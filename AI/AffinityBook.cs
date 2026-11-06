using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 常客好感度（0-100，50 中性）。只有 RegularManager 认定的常客参与，含三件事：
//   1) 离店评估：把本次到店的对话与交易事实发给模型，只取 delta（-20..20），夹取后落档；
//   2) 价格标签：按好感给「顾客卖货给玩家」加一次 TemporaryBuying 折价/抬价，最高 ±10%，
//      等同于议价过一遍，不经过议价 UI、不消耗议价轮次；
//   3) 语境：把 affinity 与档位词注入发言上下文，语气由模型自己演，不写任何映射。
// 明确不做：不影响玩家卖货方向的价格；不把 reason 回灌对话；不改原版买/卖价修正字段。
internal static class AffinityBook
{
    internal const int Neutral = 50;
    internal const int DeltaLimit = 20;
    internal const int MaxPriceBiasPercent = 10;

    // 价格标签的 identifier：与议价特征分开，便于离店时整体摘除，也让玩家一眼看到来源
    internal const string PriceFeatureId = "affinityPrice";

    // 离店评估只取最近若干行：本次到店的对话一定在末尾，不必为新旧顾客另建流水
    private const int TranscriptTail = 12;

    private static Player2Client _client;
    private static ModLog _log;

    // 当前挂过价格钩子的顾客：离店时必须解绑，否则每次到访都会叠加一个 handler
    private static StoreClient _priced;
    private static Action<GameItem> _pricedHook;

    internal static void Setup(Player2Client client, ModLog log)
    {
        _client = client;
        _log = log;
    }

    internal static void Shutdown()
    {
        DetachPrice();
        RemovePriceFeatures();
        _client = null;
        _log = null;
    }

    // 读档/新档：柜台会整体重建，旧特征与旧 hook 一并清掉
    internal static void ResetSession()
    {
        DetachPrice();
        RemovePriceFeatures();
    }

    // 档位词：只给模型一个可读标签，数值本身也一并下发
    internal static string LabelOf(string identifier)
    {
        int affinity = CustomerArchive.AffinityOf(identifier);
        if (affinity < 30) return Loc("affinity_cold", "冷淡");
        if (affinity < 70) return Loc("affinity_neutral", "平常");
        if (affinity < 90) return Loc("affinity_warm", "亲近");
        return Loc("affinity_bond", "铁杆");
    }

    // 好感 → 顾客售价偏移：50 为 0，每 5 点好感 1%，上限 ±10%
    internal static int PriceBias(string identifier)
    {
        int affinity = CustomerArchive.AffinityOf(identifier);
        int bias = (int)Math.Round((affinity - Neutral) / 5.0);
        return Math.Max(-MaxPriceBiasPercent, Math.Min(MaxPriceBiasPercent, bias));
    }

    // 顾客到柜：只有「顾客卖货给玩家」方向打价格标签，玩家卖货方向一律不动
    internal static void OnClientArrived(StoreClient client)
    {
        try
        {
            DetachPrice();
            RemovePriceFeatures();

            if (client == null || string.IsNullOrEmpty(client.identifier)) return;
            if (!RegularManager.IsRegular(client.identifier)) return;
            if (!client.IsClientSelling()) return;

            int bias = PriceBias(client.identifier);
            if (bias == 0) return;

            ApplyPriceFeatures(bias);
            int percent = bias;
            _priced = client;
            _pricedHook = item =>
            {
                if (item == null || GeneralHelper.IsItemOwned(item)) return;
                if (item.FindItemFeatureByID(PriceFeatureId) != null) return;
                item.AddItemFeature(CreatePriceFeature(percent));
            };
            client.onItemAddedToCounters += _pricedHook;
            if (PlayerStore.Instance != null) PlayerStore.Instance.RefreshCounterItem();
            if (_log != null) _log.Log("affinity price tag " + bias + "% for " + client.identifier);
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("affinity price tag failed: " + ex.Message);
        }
    }

    // 离店：先摘价格标签，再按需发起一次评估。评估失败只留日志，好感保持不变
    internal static void OnClientLeave(StoreClient client)
    {
        DetachPrice();
        RemovePriceFeatures();

        try
        {
            if (client == null || string.IsNullOrEmpty(client.identifier)) return;
            if (!RegularManager.IsRegular(client.identifier)) return;
            if (_client == null || !_client.IsOnline) return;

            int day = CurrentDay();
            if (CustomerArchive.AffinityDayOf(client.identifier) == day) return;
            // 今天没搭过话（纯买卖）：不拿上一次的旧记录重复打分
            if (CustomerArchive.LastTalkDayOf(client.identifier) != day) return;

            List<KeyValuePair<string, string>> transcript = CustomerArchive.GetTranscript(client.identifier);
            if (transcript == null || transcript.Count == 0) return;

            string identifier = client.identifier;
            string body = BuildReviewInput(client, transcript);
            if (string.IsNullOrWhiteSpace(body)) return;

            List<KeyValuePair<string, string>> turns = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("user", body),
            };
            int session = ModEntry.SessionGeneration;
            if (_log != null) _log.Log("affinity review requested for " + identifier);

            _client.Chat(PromptBook.AffinityReviewPrompt(), turns, null, (ok, reply) =>
            {
                if (session != ModEntry.SessionGeneration) return;
                if (!ok || string.IsNullOrWhiteSpace(reply))
                {
                    if (_log != null) _log.Warning("affinity review failed for " + identifier);
                    return;
                }
                ApplyReview(identifier, reply, day);
            });
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("affinity review failed: " + ex.Message);
        }
    }

    private static string BuildReviewInput(StoreClient client, List<KeyValuePair<string, string>> transcript)
    {
        StringBuilder builder = new StringBuilder();
        builder.Append("当前好感度：").Append(CustomerArchive.AffinityOf(client.identifier)).Append('\n');
        builder.Append("本次到店记录：\n");
        int start = Math.Max(0, transcript.Count - TranscriptTail);
        for (int i = start; i < transcript.Count; i++)
        {
            KeyValuePair<string, string> line = transcript[i];
            if (string.IsNullOrWhiteSpace(line.Value)) continue;
            string who;
            switch (line.Key)
            {
                case "player": who = "玩家"; break;
                case "credit": who = "赊账"; break;
                case "deal": who = "交易"; break;
                case "confront": who = "对质"; break;
                default: who = "顾客"; break;
            }
            builder.Append(who).Append('：').Append(line.Value.Trim()).Append('\n');
        }

        builder.Append("交易事实：").Append(client.isBusinessComplete ? "本次成交" : "本次未成交");
        int rounds = BargainBridge.RoundsUsed(client.identifier);
        if (rounds > 0)
        {
            builder.Append("；议价 ").Append(rounds).Append(" 轮，累计降价 ")
                .Append(BargainBridge.DiscountSoFar(client.identifier)).Append("%，累计加价 ")
                .Append(BargainBridge.MarkupSoFar(client.identifier)).Append('%');
        }
        int debt = CreditBook.DebtOf(client.identifier);
        if (debt > 0) builder.Append("；欠款 ").Append(debt);
        builder.Append('\n');
        return builder.ToString();
    }

    private static void ApplyReview(string identifier, string reply, int day)
    {
        try
        {
            string json = ExtractJson(reply);
            if (json == null) return;
            JObject obj = JObject.Parse(json);
            int delta = obj.Value<int?>("delta") ?? 0;
            string reason = obj.Value<string>("reason");
            delta = Math.Max(-DeltaLimit, Math.Min(DeltaLimit, delta));

            int current = CustomerArchive.AffinityOf(identifier);
            int next = Math.Max(0, Math.Min(100, current + delta));
            CustomerArchive.SetAffinity(identifier, next, day);
            if (_log != null)
            {
                _log.Log("affinity " + current + " -> " + next + " (" + delta + "): " + (reason ?? string.Empty));
            }
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("affinity review apply failed: " + ex.Message);
        }
    }

    // 模型偶尔把 JSON 包在 ``` 或解说文字里：只截首个 '{' 到末个 '}'
    private static string ExtractJson(string reply)
    {
        int start = reply.IndexOf('{');
        int end = reply.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return reply.Substring(start, end - start + 1);
    }

    private static void DetachPrice()
    {
        if (_priced != null && _pricedHook != null)
        {
            try
            {
                _priced.onItemAddedToCounters -= _pricedHook;
            }
            catch (Exception)
            {
                // 顾客对象可能已销毁：字段照样清空，不影响后续
            }
        }
        _priced = null;
        _pricedHook = null;
    }

    private static void ApplyPriceFeatures(int bias)
    {
        EmporiumEntry entry = EmporiumEntry.Instance;
        if (entry == null) return;
        if (entry.frontInvinvElement != null) ApplyToItems(entry.frontInvinvElement.childItems, bias);
        if (entry.backInvinvElement != null) ApplyToItems(entry.backInvinvElement.childItems, bias);
    }

    private static void ApplyToItems(List<GameItem> items, int bias)
    {
        if (items == null) return;
        foreach (GameItem item in items)
        {
            if (item == null || GeneralHelper.IsItemOwned(item)) continue;
            if (item.FindItemFeatureByID(PriceFeatureId) != null) continue;
            item.AddItemFeature(CreatePriceFeature(bias));
        }
    }

    private static void RemovePriceFeatures()
    {
        EmporiumEntry entry = EmporiumEntry.Instance;
        if (entry == null) return;
        if (entry.frontInvinvElement != null) RemoveFromItems(entry.frontInvinvElement.childItems);
        if (entry.backInvinvElement != null) RemoveFromItems(entry.backInvinvElement.childItems);
        // 摘掉特征后必须重算，否则柜台上的货还挂着折后价
        if (PlayerStore.Instance != null) PlayerStore.Instance.RefreshCounterItem();
    }

    private static void RemoveFromItems(List<GameItem> items)
    {
        if (items == null) return;
        foreach (GameItem item in items)
        {
            if (item == null) continue;
            if (item.FindItemFeatureByID(PriceFeatureId) != null) item.RemoveItemFeatureByID(PriceFeatureId);
        }
    }

    // 与 ItemFeatureList.BargainBuyingDiscount 同构：TemporaryBuying + 负 valueMod 表示顾客让价；
    // 只换 identifier 与显示文本，价格计算完全走游戏自己的链路
    private static ItemFeature CreatePriceFeature(int bias)
    {
        ItemFeature feature = new ItemFeature(ItemFeature.FeatureType.TemporaryBuying);
        feature.identifier = PriceFeatureId;
        feature.category = PriceFeatureId;
        feature.valueStage = ItemFeature.ValueStage.Final;
        string label = Loc("affinity_price_label", "Affinity");
        feature.SetPublicDisplay(label);
        feature.SetActualDisplay(label);
        feature.isFeatureMatch = true;
        feature.SetPreExposedValueMod(-bias);
        return feature;
    }

    private static int CurrentDay()
    {
        StoreStation station = StoreStation.Instance;
        return station != null ? station.dayCounter : 0;
    }

    private static string Loc(string key, string fallback)
    {
        string modId = ModEntry.ModId;
        if (string.IsNullOrEmpty(modId)) return fallback;
        string value = ModHelper.GetLocalized(modId, key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }
}