using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 顾客上下文分区：商店面（当日事件与客流）→ 顾客本体 → 顾客自带货物 → 柜台与展示 → 当次交易。
// 只读游戏状态，不写任何数值字段；各区一律全量下发，不设条数与体积上限
// （静默裁剪让模型不知道自己缺了什么，只能编；体积代价交给调试面板的 token 观测）。
internal static class StoreContext
{
    internal static JObject Build(StoreClient client, int day)
    {
        return Build(client, day, null);
    }

    // transcript 只给没有 messages 通道的调用方（离店总结）：柜台交互的往来记录已作为 messages
    // 逐条发出，再复制一份进上下文就是把同一段对话计费两次
    internal static JObject Build(StoreClient client, int day, IReadOnlyList<KeyValuePair<string, string>> transcript)
    {
        JObject context = new JObject();
        AddShop(context, day);
        if (client == null) return context;
        // 性格必须在发请求之前定型：玩家第一次开口（或熟客预生成）时按类型池 roll 一次，
        // 之后跨天、跨存档都不变。放在这里是因为预生成、柜台对话、离店总结三条路都经过 Build
        CustomerArchive.EnsurePersona(client);
        AddClient(context, client, transcript);
        AddClientGoods(context);
        AddFront(context);
        AddDeal(context, client);
        RegularManager.AppendSpeechContext(context, client.identifier);
        return context;
    }

    // 商店面：天数、客流/秩序/满意度与当日特殊事件（缺水、停电、种子短缺等都由 StoreEventManager 提供）
    private static void AddShop(JObject context, int day)
    {
        JObject shop = new JObject { ["day"] = day };
        StoreStation station = StoreStation.Instance;
        if (station != null)
        {
            shop["order"] = station.order;
            shop["traffic"] = station.traffic;
            shop["happiness"] = station.happiness;
            JArray events = ActiveEvents(station);
            if (events.Count > 0) shop["events"] = events;
            // 水质是掺水骗局的核心数值：两个蓄水罐的纯度都按万分比存
            shop["gutterwater"] = Math.Round(station.todayGutterflowPurity / 100f, 1);
            shop["rustwater"] = Math.Round(station.todayRustwaterPurity / 100f, 1);
        }
        AddPolicy(shop);
        AddFinance(shop);
        AddReputation(shop);
        context["shop"] = shop;
    }

    private static JArray ActiveEvents(StoreStation station)
    {
        JArray list = new JArray();
        StoreEventManager manager = station.storeEventManager;
        if (manager == null) return list;
        List<StoreEvent> active = manager.GetActiveEvents();
        if (active == null) return list;
        foreach (StoreEvent evt in active)
        {
            if (evt == null || evt.isHidden) continue;
            JObject entry = new JObject();
            string name = !string.IsNullOrWhiteSpace(evt.displayName) ? evt.displayName : evt.newsName;
            if (!string.IsNullOrWhiteSpace(name)) entry["name"] = name.Trim();
            if (!string.IsNullOrWhiteSpace(evt.newsDescription))
            {
                entry["note"] = evt.newsDescription.Trim();
            }
            if (evt.isLockDown) entry["lockdown"] = true;
            if (evt.isDurationVisible && evt.totalDuration > 0)
            {
                entry["untilDay"] = evt.startDay + evt.totalDuration;
            }
            if (entry.Count == 0) continue;
            list.Add(entry);
        }
        return list;
    }

    // 顾客本体：身份、意图、支付能力、买卖偏好、议价态度，以及被本管线覆盖掉的原版台词
    private static void AddClient(JObject context, StoreClient client, IReadOnlyList<KeyValuePair<string, string>> transcript)
    {
        JObject body = new JObject
        {
            ["name"] = client.displayName,
            // 身份只在这里判定一次：简报、人格池、提示词都读同一个结论，不留给模型挑
            ["identity"] = PersonaPool.KindName(PersonaPool.KindOf(client)),
            ["faction"] = FactionText(client.clientFaction),
            ["intent"] = client.clientIntent.ToString(),
            ["budget"] = client.GetBudget(),
            ["cash"] = client.clientCash,
        };
        AddList(body, "tags", client.tag);
        // 想买/不想买的 id 解析成显示名：模型拿到 "压缩食品" 才有意义，拿到 canned_food 只能瞎猜
        AddResolved(body, "wants", client.clientBuyingIdList);
        AddList(body, "wantsTags", client.clientBuyingTagList);
        AddList(body, "wantFeatures", client.clientItemFeatureBuying);
        AddResolved(body, "refuses", client.clientBlackIdList);
        AddList(body, "refusesTags", client.clientBlackTagList);
        body["bargain"] = new JObject
        {
            ["impatience"] = client.clientImpatience,
            ["available"] = client.isBargainAvailable,
            ["rejectsBadBargain"] = client.alwaysRejectBadBargain,
        };
        // 语气锚点优先取请求时刻缓存的原版台词：熟客的开场白已被本模组改写，
        // 直接读 plainText 会拿到模型自己上一轮的产出
        string line = ClientSpeech.CachedOriginalLine(client.identifier);
        if (string.IsNullOrWhiteSpace(line))
        {
            Dialogue main = client.mainDialogue;
            line = main != null ? main.plainText : null;
        }
        if (!string.IsNullOrWhiteSpace(line))
        {
            body["originalLine"] = line.Trim();
        }
        // 上一次离店的总结回灌：这是「对话影响以后对话」的闭环落点
        string summary, stance;
        int lastDay;
        if (CustomerArchive.TryGetPastVisit(client.identifier, out summary, out stance, out lastDay))
        {
            JObject past = new JObject
            {
                ["summary"] = summary,
                ["day"] = lastDay,
            };
            if (!string.IsNullOrWhiteSpace(stance)) past["stance"] = stance;
            body["pastVisit"] = past;
        }
        body["chatty"] = CustomerArchive.ChattyOf(client.identifier);
        // 原型名 + 大五人格五项原始分数：原型名给一个整体印象，分数给可执行的倾向，
        // 两者一起下发，模型不必从分数反推「这是个什么货色」
        string archetype = CustomerArchive.ArchetypeOf(client.identifier);
        if (!string.IsNullOrWhiteSpace(archetype)) body["archetype"] = archetype;
        CustomerArchive.OceanScores ocean = CustomerArchive.OceanOf(client.identifier);
        body["ocean"] = new JObject
        {
            ["openness"] = ocean.Openness,
            ["conscientiousness"] = ocean.Conscientiousness,
            ["extraversion"] = ocean.Extraversion,
            ["agreeableness"] = ocean.Agreeableness,
            ["neuroticism"] = ocean.Neuroticism,
        };
        AddTranscript(body, transcript);
        AddPriceBias(body, client);
        AddStanding(body, client);
        AddTradeRules(body, client);
        AddHabit(body, client);
        AddAug(body, client);
        AddAttraction(body, client);
        AddOrders(body, client);
        body["tradeRepMultiplier"] = Math.Round(client.tradeReputationMultiplier, 2);
        context["client"] = body;
    }

    // 顾客自带货物：柜台上归属非玩家的物品就是本轮他拿来卖的东西（成交前才会在柜上）
    private static void AddClientGoods(JObject context)
    {
        EmporiumEntry entry = EmporiumEntry.Instance;
        if (entry == null) return;
        List<GameItem> items = entry.GetAllNonOwnedItem();
        if (items == null || items.Count == 0) return;
        context["clientGoods"] = CountedItems(items);
    }

    // 柜台与招牌：柜台取柜台区全部物品里的玩家自有部分（在售），招牌取展示柜（临街可见的陈列）
    private static void AddFront(JObject context)
    {
        EmporiumEntry entry = EmporiumEntry.Instance;
        if (entry == null) return;

        List<GameItem> counter = new List<GameItem>();
        List<GameItem> counters = entry.GetCountersItems();
        if (counters != null)
        {
            foreach (GameItem item in counters)
            {
                if (item == null) continue;
                if (!GeneralHelper.IsItemOwned(item)) continue;
                counter.Add(item);
            }
        }
        if (counter.Count > 0) context["counter"] = CountedItems(counter);

        GameGridInventory showcase = entry.showcaseElement;
        if (showcase != null && showcase.childItems != null && showcase.childItems.Count > 0)
        {
            context["showcase"] = CountedItems(showcase.childItems);
        }
    }

    // 当次交易：易货条件、批发折扣、服务费与多样性限制；全是本单特有信息，缺了模型只能泛泛而谈
    private static void AddDeal(JObject context, StoreClient client)
    {
        JObject deal = new JObject();
        AddBarter(deal, client);
        if (client.isOfferWholesale) deal["wholesaleDiscount"] = client.wholesaleDiscount;
        if (client.serviceCost > 0) deal["serviceCost"] = client.serviceCost;
        if (client.isRequireVariety)
        {
            deal["requiresVariety"] = true;
            AddList(deal, "varietyOffered", client.varietyTrackingList);
        }
        if (client.isMultiBuyDisabled) deal["multiBuyDisabled"] = true;
        if (client.isBusinessComplete) deal["dealClosed"] = true;
        deal["negotiation"] = Negotiation(client);
        context["deal"] = deal;
    }

    // 议价与赊账的本轮状态：模型据此知道还能不能再压、这单能挂多少账，
    // 不必每轮试探到被游戏拒绝才停下
    private static JObject Negotiation(StoreClient client)
    {
        string identifier = client != null ? client.identifier : null;
        // 方向决定哪两个 kind 合法：顾客买你的货 = 你让利/提价；顾客卖给你 = 你加价收/压价收。
        // 模型据此选 kind，写错方向会被 BargainBridge 直接丢弃
        string direction = BargainBridge.DirectionOf(client);
        // 前半是事实（游戏方向、已发生的轮次与幅度），后半是给模型遵守的规矩：
        // 代码不夹取、不拦截、不纠正，全部由模型自己守住这些数字
        JObject negotiation = new JObject
        {
            ["direction"] = direction,
            ["roundsUsed"] = BargainBridge.RoundsUsed(identifier),
            ["roundsMax"] = BargainBridge.MaxRoundsPerClient,
            ["discountApplied"] = BargainBridge.DiscountSoFar(identifier),
            ["markupApplied"] = BargainBridge.MarkupSoFar(identifier),
            ["minPercent"] = BargainBridge.MinPercent,
            ["maxPercent"] = BargainBridge.MaxPercent,
            ["maxCumulative"] = BargainBridge.MaxCumulativePercent,
        };
        int debt = CreditBook.DebtOf(identifier);
        if (debt > 0)
        {
            negotiation["owes"] = debt;
            negotiation["owesSinceDay"] = CreditBook.SinceDay(identifier);
        }
        negotiation["creditLine"] = CreditBook.MaxPerDeal;
        return negotiation;
    }

    // 易货内容：BarterOffer 的条件里混着 GameItem 引用与委托，不能整体序列化，
    // 只取游戏自己给玩家看的提示文案与条件显示名
    private static void AddBarter(JObject deal, StoreClient client)
    {
        BarterOffer offer = client.barterOffer;
        if (offer == null) return;
        JObject barter = new JObject();
        if (!string.IsNullOrWhiteSpace(offer.barterTextBody))
        {
            barter["text"] = offer.barterTextBody.Trim();
        }
        JArray wants = new JArray();
        JArray gives = new JArray();
        if (offer.conditions != null)
        {
            foreach (BarterCondition condition in offer.conditions)
            {
                if (condition == null) continue;
                if (!string.IsNullOrWhiteSpace(condition.requiredDisplay)) wants.Add(condition.requiredDisplay.Trim());
                if (!string.IsNullOrWhiteSpace(condition.offeredDisplay)) gives.Add(condition.offeredDisplay.Trim());
            }
        }
        if (wants.Count > 0) barter["wants"] = wants;
        if (gives.Count > 0) barter["gives"] = gives;
        if (barter.Count > 0) deal["barter"] = barter;
    }

    // 往来记录全文：只给不走 messages 的调用（离店总结）用，逐条带角色，不截断
    private static void AddTranscript(JObject body, IReadOnlyList<KeyValuePair<string, string>> transcript)
    {
        if (transcript == null || transcript.Count == 0) return;
        JArray lines = new JArray();
        foreach (KeyValuePair<string, string> turn in transcript)
        {
            if (string.IsNullOrWhiteSpace(turn.Value)) continue;
            lines.Add(new JObject
            {
                ["role"] = turn.Key,
                ["text"] = turn.Value.Trim(),
            });
        }
        if (lines.Count > 0) body["dialogue"] = lines;
    }

    // 同名物品聚成一条并计数：货架上一格一件，逐条平铺会浪费上下文。
    // 每条带单价估值与该物品的可见状态（锈蚀/凹痕/过期…），这是讨价还价时唯一能落到实处的依据
    private static JArray CountedItems(List<GameItem> items)
    {
        List<string> order = new List<string>();
        Dictionary<string, int> counts = new Dictionary<string, int>();
        Dictionary<string, GameItem> samples = new Dictionary<string, GameItem>();
        foreach (GameItem item in items)
        {
            if (item == null) continue;
            string name = item.GetDisplayName();
            if (string.IsNullOrEmpty(name)) continue;
            name = name.Trim();
            if (!counts.ContainsKey(name))
            {
                order.Add(name);
                counts[name] = 0;
                samples[name] = item;
            }
            counts[name]++;
        }
        JArray array = new JArray();
        foreach (string name in order)
        {
            JObject row = new JObject { ["name"] = name };
            int count = counts[name];
            if (count > 1) row["count"] = count;
            GameItem sample = samples[name];
            long value = SafeValue(sample);
            if (value > 0) row["value"] = value;
            AddList(row, "types", sample.itemTypes);
            JArray features = VisibleFeatures(sample);
            if (features.Count > 0) row["state"] = features;
            array.Add(row);
        }
        return array;
    }

    // 物品估值：走游戏自己的议价估值，异常时按无价处理，不让上下文组装把整条管线带崩
    private static long SafeValue(GameItem item)
    {
        try
        {
            return item != null ? item.GetNegociatedValue() : 0L;
        }
        catch (Exception)
        {
            return 0L;
        }
    }

    // 可见状态：未揭穿读游戏给玩家看的伪装文案，已揭穿读真身文案并标 exposed；
    // 本模组自己写的议价特性（bargain*）不外泄
    private static JArray VisibleFeatures(GameItem item)
    {
        JArray list = new JArray();
        if (item == null || item.itemFeatures == null) return list;
        foreach (ItemFeature feature in item.itemFeatures)
        {
            if (feature == null || string.IsNullOrEmpty(feature.identifier)) continue;
            if (feature.identifier.StartsWith("bargain", StringComparison.Ordinal)) continue;
            bool exposed = false;
            try
            {
                exposed = feature.IsExposed();
            }
            catch (Exception)
            {
            }
            if (!exposed && !feature.IsInitiallyShown()) continue;
            string display;
            try
            {
                display = exposed ? feature.GetActualDisplay() : feature.GetPublicDisplay();
            }
            catch (Exception)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(display)) continue;
            JObject row = new JObject { ["text"] = display.Trim() };
            if (exposed) row["exposed"] = true;
            if (feature.fakeCondition != null) row["fake"] = true;
            list.Add(row);
        }
        return list;
    }

    // 价格倾向：顾客自带的买/卖价修正（百分比），直接决定他觉得贵还是便宜
    private static void AddPriceBias(JObject body, StoreClient client)
    {
        JObject bias = new JObject();
        if (client.buyPriceModifier != 0) bias["buy"] = client.buyPriceModifier;
        if (client.sellPriceModifier != 0) bias["sell"] = client.sellPriceModifier;
        if (bias.Count > 0) body["priceBias"] = bias;
    }

    // 身份立场：通缉、安保、判决官。真名只在游戏自己有值时给出（伪装客靠它藏身）
    private static void AddStanding(JObject body, StoreClient client)
    {
        JObject standing = new JObject();
        if (client.isWanted) standing["wanted"] = true;
        if (client.isStealthWanted) standing["stealthWanted"] = true;
        if (client.reportReward > 0) standing["reward"] = client.reportReward;
        if (client.isSecurity) standing["security"] = true;
        if (client.isSentenceOfficer) standing["sentenceOfficer"] = true;
        if (client.reported) standing["reported"] = true;
        if (standing.Count > 0) body["standing"] = standing;
        if (!string.IsNullOrWhiteSpace(client.realName) && client.realName != client.displayName)
        {
            body["realName"] = client.realName;
        }
    }

    // 交易规矩：违禁品、零售加价、否认退货
    private static void AddTradeRules(JObject body, StoreClient client)
    {
        JObject rules = new JObject();
        if (client.noAcceptingContraband) rules["noContraband"] = true;
        if (client.chargesContrabandMarkup) rules["contrabandMarkup"] = true;
        if (!client.acceptRetailMarkup) rules["noRetailMarkup"] = true;
        if (!client.CanReturn) rules["noReturn"] = true;
        if (rules.Count > 0) body["tradeRules"] = rules;
    }

    // 癖好：化学品、成瘾、香氛、掺水
    private static void AddHabit(JObject body, StoreClient client)
    {
        JObject habit = new JObject();
        if (!client.IsPersonalConsumption) habit["resalePurpose"] = true;
        if (client.IsClientAddict) habit["addict"] = true;
        if (client.CanExposeChem) habit["exposesChems"] = true;
        if (client.refusesHazardousWaterLabel) habit["refusesHazardWater"] = true;
        if (client.acceptedChemicalProductGrade > 1) habit["chemicalGrade"] = client.acceptedChemicalProductGrade;
        if (client.perfumeInfluence != 0) habit["perfume"] = client.perfumeInfluence;
        if (habit.Count > 0) body["habit"] = habit;
    }

    // 伪人：扫描过才把身份写进资料，没扫描只给破绽（僵住/抽搐），不提前泄底
    private static void AddAug(JObject body, StoreClient client)
    {
        if (!client.isAug) return;
        JObject aug = new JObject();
        if (client.isAugScanned)
        {
            aug["scanned"] = true;
            aug["tier"] = client.augTier;
        }
        else
        {
            aug["tell"] = client.augIdleTell.ToString();
        }
        body["aug"] = aug;
    }

    // 诱惑：他是被哪件货/哪类货/哪场事件引来的
    private static void AddAttraction(JObject body, StoreClient client)
    {
        if (!string.IsNullOrEmpty(client.attractedItemId))
        {
            body["attractedBy"] = ResolveId(client.attractedItemId);
        }
        if (!string.IsNullOrEmpty(client.attractedType)) body["attractedType"] = client.attractedType;
        if (!string.IsNullOrEmpty(client.eventSourceId)) body["eventSource"] = client.eventSourceId;
    }

    // 订单：枪械订制、采购委托、雇工
    private static void AddOrders(JObject body, StoreClient client)
    {
        GunOrderData gun = client.gunOrderData;
        if (gun != null)
        {
            JObject order = new JObject { ["tier"] = gun.tier };
            if (gun.baseReward > 0) order["reward"] = gun.baseReward;
            if (gun.isBestOrder) order["best"] = true;
            if (gun.isServiceOrder) order["service"] = true;
            if (!string.IsNullOrWhiteSpace(gun.dialogText)) order["request"] = gun.dialogText.Trim();
            body["gunOrder"] = order;
        }
        ProcurementData procurement = client.procurementData;
        if (procurement != null)
        {
            JObject order = new JObject();
            if (procurement.initialReward > 0) order["reward"] = procurement.initialReward;
            if (!string.IsNullOrWhiteSpace(procurement.descriptionOffer)) order["offer"] = procurement.descriptionOffer.Trim();
            if (!string.IsNullOrWhiteSpace(procurement.descriptionCollect)) order["collect"] = procurement.descriptionCollect.Trim();
            if (procurement.day > 0) order["day"] = procurement.day;
            if (order.Count > 0) body["procurement"] = order;
        }
        Hireling hireling = client.hirelingData;
        if (hireling != null)
        {
            JObject hired = new JObject();
            if (!string.IsNullOrWhiteSpace(hireling.displayName)) hired["name"] = hireling.displayName;
            if (hireling.baseCost > 0) hired["cost"] = hireling.baseCost;
            hired["state"] = hireling.state.ToString();
            body["hireling"] = hired;
        }
    }

    // 价格政策与店况：零售加价、违禁品加价档、垃圾堆、供电、谈判加成
    private static void AddPolicy(JObject shop)
    {
        PlayerStore store = PlayerStore.Instance;
        if (store == null) return;
        JObject policy = new JObject
        {
            ["retailMarkup"] = store.retailMarkup,
            ["contrabandLow"] = store.contrabandMarkupLow,
            ["contrabandMid"] = store.contrabandMarkupMid,
            ["contrabandHigh"] = store.contrabandMarkupHigh,
        };
        if (store.garbageLevel > 0) policy["garbage"] = store.garbageLevel;
        if (!store.isPowerOn) policy["powerOff"] = true;
        if (store.dealMakerBargainBonus > 0) policy["dealMakerBonus"] = store.dealMakerBargainBonus;
        shop["policy"] = policy;
    }

    // 财务：店名、租金与贷款到期
    private static void AddFinance(JObject shop)
    {
        PlayerStore store = PlayerStore.Instance;
        if (store == null) return;
        if (!string.IsNullOrWhiteSpace(store.storeName)) shop["storeName"] = store.storeName;
        JObject finance = new JObject();
        if (store.rentValue > 0) finance["rent"] = store.rentValue;
        if (store.dayUntilRent > 0) finance["rentDueIn"] = store.dayUntilRent;
        if (store.loanValue > 0) finance["loan"] = store.loanValue;
        if (store.dayUntilLoan > 0) finance["loanDueIn"] = store.dayUntilLoan;
        if (finance.Count > 0) shop["finance"] = finance;
    }

    // 声望：按势力的好感度，只取有值的几条
    private static void AddReputation(JObject shop)
    {
        PlayerStore store = PlayerStore.Instance;
        if (store == null || store.storeReputations == null) return;
        JArray list = new JArray();
        foreach (StoreReputation rep in store.storeReputations)
        {
            if (rep == null) continue;
            string name = !string.IsNullOrWhiteSpace(rep.factionDisplay) ? rep.factionDisplay : rep.factionId;
            if (string.IsNullOrWhiteSpace(name)) continue;
            list.Add(new JObject { ["faction"] = name, ["strength"] = rep.strength });
        }
        if (list.Count > 0) shop["reputation"] = list;
    }


    private static void AddList(JObject parent, string name, List<string> items)
    {
        if (items == null || items.Count == 0) return;
        parent[name] = new JArray(items);
    }

    // 单个 id → 显示名：解析失败保留原 id
    private static string ResolveId(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        try
        {
            string display = GeneralHelper.GetDisplayNameFromID(id);
            if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("[")) return display.Trim();
        }
        catch (Exception)
        {
        }
        return id;
    }

    // 物品 id 列表 → 显示名列表：解析失败保留原 id，宁可难看也不丢信息
    private static void AddResolved(JObject parent, string name, List<string> ids)
    {
        if (ids == null || ids.Count == 0) return;
        JArray array = new JArray();
        foreach (string id in ids)
        {
            if (string.IsNullOrEmpty(id)) continue;
            string display;
            try
            {
                display = GeneralHelper.GetDisplayNameFromID(id);
            }
            catch (Exception)
            {
                display = null;
            }
            string text = string.IsNullOrWhiteSpace(display) || display.StartsWith("[") ? id : display.Trim();
            if (!array.Contains(text)) array.Add(text);
        }
        if (array.Count > 0) parent[name] = array;
    }

    // 势力常量（FACTION_LOWER_LEVEL 等）对模型是不可读代码，换成它能理解的阶层称呼；
    // 名册界面也复用同一份映射，避免两处措辞分叉
    internal static string FactionText(string faction)
    {
        switch (faction)
        {
            case StoreClient.FACTION_LOWER: return "下层市民";
            case StoreClient.FACTION_MIDDLE: return "中层职员";
            case StoreClient.FACTION_UPPER: return "上层权贵";
            case StoreClient.FACTION_SECURITY: return "空间站安保";
            case StoreClient.FACTION_CRIME: return "黑市";
            case StoreClient.FACTION_TOURIST: return "外来游客";
            default: return string.IsNullOrEmpty(faction) ? "无归属" : faction;
        }
    }
}
