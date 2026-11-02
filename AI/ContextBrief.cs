using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 上下文简报：把 StoreContext 采集的结构化数据译成 RimTalk 那种 profile 行——「标签: 值」紧凑排列。
// 标签用英文（结构），值用游戏里的实际内容（中文货名、中文身份）。
// 游戏里凡是能被玩家看见或感知的系统，这里都有一行：店况与水质、价格政策、财务、声望、
// 顾客的立场/规矩/癖好/伪人/诱惑源/订单、物品的类型与真伪、这单的条件与议价进度。
internal static class ContextBrief
{
    internal static string Render(JObject context, string scene)
    {
        if (context == null) return string.Empty;
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(scene)) lines.Add("Scene: " + scene);

        AddShop(lines, context["shop"] as JObject);
        AddClient(lines, context["client"] as JObject);
        AddGoods(lines, "His goods", context["clientGoods"] as JArray);
        AddGoods(lines, "Counter", context["counter"] as JArray);
        AddGoods(lines, "Showcase", context["showcase"] as JArray);
        AddDeal(lines, context["deal"] as JObject);
        AddRegular(lines, context["regular"] as JObject);
        return string.Join("\n", lines);
    }

    private static void AddShop(List<string> lines, JObject shop)
    {
        if (shop == null) return;

        var stats = new StringBuilder("Day: ").Append(Int(shop, "day"));
        if (shop["order"] != null) stats.Append(" | order ").Append(Int(shop, "order"));
        if (shop["traffic"] != null) stats.Append(" | traffic ").Append(Int(shop, "traffic"));
        if (shop["happiness"] != null) stats.Append(" | happiness ").Append(Int(shop, "happiness"));
        if (shop["gutterwater"] != null) stats.Append(" | gutter water ").Append(Dec(shop, "gutterwater")).Append("%");
        if (shop["rustwater"] != null) stats.Append(" | rust water ").Append(Dec(shop, "rustwater")).Append("%");
        lines.Add(stats.ToString());

        JArray events = shop["events"] as JArray;
        if (events != null && events.Count > 0)
        {
            var parts = new List<string>();
            foreach (JToken token in events)
            {
                JObject evt = token as JObject;
                if (evt == null) continue;
                string name = Str(evt, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;
                var text = new StringBuilder(name);
                string note = Str(evt, "note");
                if (!string.IsNullOrWhiteSpace(note)) text.Append("（").Append(note).Append("）");
                if (Bool(evt, "lockdown")) text.Append(" [lockdown]");
                if (evt["untilDay"] != null) text.Append(" [until day ").Append(Int(evt, "untilDay")).Append("]");
                parts.Add(text.ToString());
            }
            if (parts.Count > 0) lines.Add("Events: " + string.Join("；", parts));
        }

        JObject finance = shop["finance"] as JObject;
        var money = new List<string>();
        string storeName = Str(shop, "storeName");
        if (!string.IsNullOrWhiteSpace(storeName)) money.Add("store " + storeName);
        if (finance != null)
        {
            if (finance["rent"] != null)
            {
                var rent = new StringBuilder("rent ").Append(Int(finance, "rent"));
                if (finance["rentDueIn"] != null) rent.Append(" due in ").Append(Int(finance, "rentDueIn")).Append("d");
                money.Add(rent.ToString());
            }
            if (finance["loan"] != null)
            {
                var loan = new StringBuilder("loan ").Append(Int(finance, "loan"));
                if (finance["loanDueIn"] != null) loan.Append(" due in ").Append(Int(finance, "loanDueIn")).Append("d");
                money.Add(loan.ToString());
            }
        }
        if (money.Count > 0) lines.Add("Store: " + string.Join(" | ", money));

        JObject policy = shop["policy"] as JObject;
        if (policy != null)
        {
            var parts = new List<string>
            {
                "retail markup " + Int(policy, "retailMarkup") + "%",
                "contraband " + Int(policy, "contrabandLow") + "/" + Int(policy, "contrabandMid") + "/"
                    + Int(policy, "contrabandHigh") + "%",
            };
            if (policy["garbage"] != null) parts.Add("garbage " + Int(policy, "garbage"));
            if (Bool(policy, "powerOff")) parts.Add("power off");
            if (policy["dealMakerBonus"] != null) parts.Add("deal-maker +" + Int(policy, "dealMakerBonus"));
            lines.Add("Policy: " + string.Join(" | ", parts));
        }

        JArray reputation = shop["reputation"] as JArray;
        if (reputation != null && reputation.Count > 0)
        {
            var parts = new List<string>();
            foreach (JToken token in reputation)
            {
                JObject rep = token as JObject;
                if (rep == null) continue;
                parts.Add(Str(rep, "faction") + " " + Int(rep, "strength"));
            }
            if (parts.Count > 0) lines.Add("Reputation: " + string.Join(" | ", parts));
        }
    }

    private static void AddClient(List<string> lines, JObject client)
    {
        if (client == null) return;

        lines.Add("Name: " + (Str(client, "name") ?? "?"));
        string identity = Str(client, "identity");
        string faction = Str(client, "faction");
        string who = identity;
        if (!string.IsNullOrWhiteSpace(faction) && !string.Equals(faction, identity))
        {
            who = string.IsNullOrWhiteSpace(who) ? faction : who + "（" + faction + "）";
        }
        if (!string.IsNullOrWhiteSpace(who)) lines.Add("Identity: " + who);

        string intent = IntentText(Str(client, "intent"));
        if (!string.IsNullOrWhiteSpace(intent)) lines.Add("Intent: " + intent);

        var budget = new StringBuilder("Budget: ").Append(Int(client, "budget"));
        if (client["cash"] != null) budget.Append(" | cash ").Append(Int(client, "cash"));
        lines.Add(budget.ToString());

        JObject bias = client["priceBias"] as JObject;
        if (bias != null)
        {
            var parts = new List<string>();
            if (bias["buy"] != null) parts.Add("buy " + Signed(Int(bias, "buy")) + "%");
            if (bias["sell"] != null) parts.Add("sell " + Signed(Int(bias, "sell")) + "%");
            lines.Add("Price bias: " + string.Join(" | ", parts));
        }

        string wants = Join(client["wants"] as JArray);
        if (!string.IsNullOrWhiteSpace(wants)) lines.Add("Wants: " + wants);
        string wantsTags = Join(client["wantsTags"] as JArray);
        if (!string.IsNullOrWhiteSpace(wantsTags)) lines.Add("Also accepts categories: " + wantsTags);
        string refuses = Join(client["refuses"] as JArray);
        if (!string.IsNullOrWhiteSpace(refuses)) lines.Add("Refuses: " + refuses);

        JObject bargain = client["bargain"] as JObject;
        if (bargain != null)
        {
            var temper = new StringBuilder("Temper: 耐心 ").Append(Int(bargain, "impatience"));
            temper.Append(Bool(bargain, "available") ? "，可以还价" : "，不接还价");
            if (Bool(bargain, "rejectsBadBargain")) temper.Append("，太离谱会翻脸");
            lines.Add(temper.ToString());
        }

        JObject standing = client["standing"] as JObject;
        string realName = Str(client, "realName");
        if (standing != null || !string.IsNullOrWhiteSpace(realName))
        {
            var parts = new List<string>();
            if (standing != null)
            {
                if (Bool(standing, "wanted")) parts.Add("wanted");
                if (standing["reward"] != null) parts.Add("reward " + Int(standing, "reward"));
                if (Bool(standing, "stealthWanted")) parts.Add("stealth wanted");
                if (Bool(standing, "security")) parts.Add("station security");
                if (Bool(standing, "sentenceOfficer")) parts.Add("sentence officer");
                if (Bool(standing, "reported")) parts.Add("already reported");
            }
            if (!string.IsNullOrWhiteSpace(realName)) parts.Add("real name " + realName);
            if (parts.Count > 0) lines.Add("Standing: " + string.Join(" | ", parts));
        }

        JObject rules = client["tradeRules"] as JObject;
        if (rules != null)
        {
            var parts = new List<string>();
            if (Bool(rules, "noContraband")) parts.Add("refuses contraband");
            if (Bool(rules, "contrabandMarkup")) parts.Add("charges contraband markup");
            if (Bool(rules, "noRetailMarkup")) parts.Add("no retail markup");
            if (Bool(rules, "noReturn")) parts.Add("no returns");
            if (Bool(rules, "noMultiBuy")) parts.Add("no multi-buy");
            if (parts.Count > 0) lines.Add("Trade rules: " + string.Join(" | ", parts));
        }

        JObject habit = client["habit"] as JObject;
        if (habit != null)
        {
            var parts = new List<string>();
            if (Bool(habit, "resalePurpose")) parts.Add("buys to resell");
            if (Bool(habit, "addict")) parts.Add("addict");
            if (Bool(habit, "exposesChems")) parts.Add("tests chemicals");
            if (Bool(habit, "refusesHazardWater")) parts.Add("refuses hazardous water label");
            if (habit["chemicalGrade"] != null) parts.Add("accepts chemical grade " + Int(habit, "chemicalGrade"));
            if (habit["perfume"] != null) parts.Add("perfume influence " + Int(habit, "perfume"));
            if (parts.Count > 0) lines.Add("Habit: " + string.Join(" | ", parts));
        }

        JObject aug = client["aug"] as JObject;
        if (aug != null)
        {
            var text = new StringBuilder("Aug: ");
            if (Bool(aug, "scanned"))
            {
                text.Append("scanned, tier ").Append(Int(aug, "tier"));
            }
            else
            {
                // 没被扫描就只给破绽，不给身份
                text.Append("idle tell ").Append(Str(aug, "tell"));
            }
            lines.Add(text.ToString());
        }

        var attraction = new List<string>();
        string attracted = Str(client, "attractedBy");
        if (!string.IsNullOrWhiteSpace(attracted)) attraction.Add("item " + attracted);
        string attractedType = Str(client, "attractedType");
        if (!string.IsNullOrWhiteSpace(attractedType)) attraction.Add("type " + attractedType);
        string eventSource = Str(client, "eventSource");
        if (!string.IsNullOrWhiteSpace(eventSource)) attraction.Add("event " + eventSource);
        if (attraction.Count > 0) lines.Add("Came for: " + string.Join(" | ", attraction));

        JObject gun = client["gunOrder"] as JObject;
        if (gun != null)
        {
            var text = new StringBuilder("Gun order: tier ").Append(Int(gun, "tier"));
            if (gun["reward"] != null) text.Append(" | reward ").Append(Int(gun, "reward"));
            if (Bool(gun, "best")) text.Append(" | best");
            if (Bool(gun, "service")) text.Append(" | service job");
            string request = Str(gun, "request");
            if (!string.IsNullOrWhiteSpace(request)) text.Append(" | ").Append(request);
            lines.Add(text.ToString());
        }

        JObject procurement = client["procurement"] as JObject;
        if (procurement != null)
        {
            var text = new StringBuilder("Procurement: ");
            if (procurement["reward"] != null) text.Append("reward ").Append(Int(procurement, "reward")).Append(" | ");
            string offer = Str(procurement, "offer");
            if (!string.IsNullOrWhiteSpace(offer)) text.Append(offer);
            string collect = Str(procurement, "collect");
            if (!string.IsNullOrWhiteSpace(collect)) text.Append(" / ").Append(collect);
            lines.Add(text.ToString());
        }

        JObject hireling = client["hireling"] as JObject;
        if (hireling != null)
        {
            var text = new StringBuilder("Hireling: ").Append(Str(hireling, "name"));
            if (hireling["cost"] != null) text.Append(" | cost ").Append(Int(hireling, "cost"));
            string state = Str(hireling, "state");
            if (!string.IsNullOrWhiteSpace(state)) text.Append(" | ").Append(state);
            lines.Add(text.ToString());
        }

        if (client["tradeRepMultiplier"] != null)
        {
            lines.Add("Trade rep multiplier: x" + Dec(client, "tradeRepMultiplier"));
        }

        string archetype = Str(client, "archetype");
        JObject ocean = client["ocean"] as JObject;
        if (ocean != null)
        {
            CustomerArchive.OceanScores scores = new CustomerArchive.OceanScores
            {
                Openness = Int(ocean, "openness"),
                Conscientiousness = Int(ocean, "conscientiousness"),
                Extraversion = Int(ocean, "extraversion"),
                Agreeableness = Int(ocean, "agreeableness"),
                Neuroticism = Int(ocean, "neuroticism"),
            };
            string nature = PersonaPool.Describe(scores, archetype);
            if (!string.IsNullOrWhiteSpace(nature)) lines.Add("Personality: " + nature);
        }

        string recent = Join(client["recentLines"] as JArray, " / ");
        if (!string.IsNullOrWhiteSpace(recent)) lines.Add("Recent lines: " + recent);

        string original = Str(client, "originalLine");
        if (!string.IsNullOrWhiteSpace(original)) lines.Add("Original line: " + original);

        JObject past = client["pastVisit"] as JObject;
        if (past != null)
        {
            var visit = new StringBuilder("Last visit: day ").Append(Int(past, "day"));
            string summary = Str(past, "summary");
            if (!string.IsNullOrWhiteSpace(summary)) visit.Append(" — ").Append(summary);
            string stance = Str(past, "stance");
            if (!string.IsNullOrWhiteSpace(stance)) visit.Append(" | attitude: ").Append(stance);
            lines.Add(visit.ToString());
        }
    }

    private static void AddGoods(List<string> lines, string label, JArray items)
    {
        if (items == null || items.Count == 0) return;
        var parts = new List<string>();
        foreach (JToken token in items)
        {
            JObject item = token as JObject;
            if (item == null) continue;
            string name = Str(item, "name");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var text = new StringBuilder(name);
            int count = Int(item, "count");
            if (count > 1) text.Append(" ×").Append(count);

            var notes = new List<string>();
            long value = Long(item, "value");
            if (value > 0) notes.Add("value " + value);
            string types = Join(item["types"] as JArray, "/");
            if (!string.IsNullOrWhiteSpace(types)) notes.Add(types);
            string features = Features(item["state"] as JArray);
            if (!string.IsNullOrWhiteSpace(features)) notes.Add(features);
            if (notes.Count > 0) text.Append(" (").Append(string.Join("; ", notes)).Append(")");
            parts.Add(text.ToString());
        }
        if (parts.Count > 0) lines.Add(label + ": " + string.Join("; ", parts));
    }

    // 特性行：已揭穿的打 [exposed]，有伪装来源的打 [fake]，让人一眼看出这货有鬼
    private static string Features(JArray features)
    {
        if (features == null || features.Count == 0) return null;
        var parts = new List<string>();
        foreach (JToken token in features)
        {
            JObject feature = token as JObject;
            if (feature == null) continue;
            string text = Str(feature, "text");
            if (string.IsNullOrWhiteSpace(text)) continue;
            var marks = new List<string>();
            if (Bool(feature, "exposed")) marks.Add("exposed");
            if (Bool(feature, "fake")) marks.Add("fake");
            parts.Add(marks.Count > 0 ? text + " [" + string.Join(", ", marks) + "]" : text);
        }
        return parts.Count > 0 ? string.Join(" / ", parts) : null;
    }

    private static void AddDeal(List<string> lines, JObject deal)
    {
        if (deal == null) return;

        JObject barter = deal["barter"] as JObject;
        if (barter != null)
        {
            var text = new StringBuilder("Barter: ");
            string body = Str(barter, "text");
            if (!string.IsNullOrWhiteSpace(body)) text.Append(body);
            string wants = Join(barter["wants"] as JArray);
            string gives = Join(barter["gives"] as JArray);
            if (!string.IsNullOrWhiteSpace(wants)) text.Append(" | wants ").Append(wants);
            if (!string.IsNullOrWhiteSpace(gives)) text.Append(" | gives ").Append(gives);
            lines.Add(text.ToString());
        }

        var terms = new List<string>();
        if (deal["wholesaleDiscount"] != null) terms.Add("wholesale " + Int(deal, "wholesaleDiscount") + "%");
        if (deal["serviceCost"] != null) terms.Add("service fee " + Int(deal, "serviceCost"));
        if (Bool(deal, "requiresVariety")) terms.Add("variety required");
        if (Bool(deal, "multiBuyDisabled")) terms.Add("no multi-buy");
        if (Bool(deal, "dealClosed")) terms.Add("deal closed");
        if (terms.Count > 0) lines.Add("Terms: " + string.Join(" | ", terms));

        JObject negotiation = deal["negotiation"] as JObject;
        if (negotiation == null) return;

        var line = new StringBuilder("Negotiation: ");
        string direction = DirectionText(Str(negotiation, "direction"));
        if (!string.IsNullOrWhiteSpace(direction)) line.Append(direction).Append(" | ");
        line.Append("rounds ").Append(Int(negotiation, "roundsUsed")).Append("/").Append(Int(negotiation, "roundsMax"));
        line.Append(" | discount ").Append(Int(negotiation, "discountApplied")).Append("%");
        line.Append(" | markup ").Append(Int(negotiation, "markupApplied")).Append("%");
        line.Append(" | percent ").Append(Int(negotiation, "minPercent")).Append("-").Append(Int(negotiation, "maxPercent"));
        line.Append(" | total cap ").Append(Int(negotiation, "maxCumulative")).Append("%");
        lines.Add(line.ToString());

        if (negotiation["owes"] != null || negotiation["creditLine"] != null)
        {
            var credit = new StringBuilder("Credit: ");
            if (negotiation["owes"] != null)
            {
                credit.Append("owes ").Append(Int(negotiation, "owes"))
                    .Append(" since day ").Append(Int(negotiation, "owesSinceDay"));
            }
            if (negotiation["creditLine"] != null)
            {
                if (credit.Length > "Credit: ".Length) credit.Append(" | ");
                credit.Append("line ").Append(Int(negotiation, "creditLine"));
            }
            lines.Add(credit.ToString());
        }
    }

    private static void AddRegular(List<string> lines, JObject regular)
    {
        if (regular == null) return;
        var text = new StringBuilder("Regular: tier ").Append(Int(regular, "tier"))
            .Append(" | visits ").Append(Int(regular, "visits"));
        string memory = Str(regular, "memory");
        if (!string.IsNullOrWhiteSpace(memory)) text.Append(" | memory ").Append(memory);
        lines.Add(text.ToString());
    }

    private static string IntentText(string intent)
    {
        switch (intent)
        {
            case "BUY": return "来买货";
            case "SELL": return "来卖货";
            case "SELLNBUY": return "既买也卖";
            case "BARTER": return "来易货";
            case "WHOLESALE": return "来批发";
            default: return null;
        }
    }

    private static string DirectionText(string direction)
    {
        switch (direction)
        {
            case "playerSells": return "he is buying from you";
            case "playerBuys": return "he is selling to you";
            case "both": return "he is buying and selling";
            default: return null;
        }
    }

    private static string Signed(int value)
    {
        return value > 0 ? "+" + value : value.ToString();
    }

    private static string Str(JObject obj, string key)
    {
        return obj != null ? obj.Value<string>(key) : null;
    }

    private static int Int(JObject obj, string key)
    {
        if (obj == null) return 0;
        int? value = obj.Value<int?>(key);
        return value ?? 0;
    }

    private static double Dec(JObject obj, string key)
    {
        if (obj == null) return 0;
        double? value = obj.Value<double?>(key);
        return value ?? 0;
    }

    private static long Long(JObject obj, string key)
    {
        if (obj == null) return 0L;
        long? value = obj.Value<long?>(key);
        return value ?? 0L;
    }

    private static bool Bool(JObject obj, string key)
    {
        if (obj == null) return false;
        bool? value = obj.Value<bool?>(key);
        return value ?? false;
    }

    private static string Join(JArray array, string separator = "、")
    {
        if (array == null || array.Count == 0) return null;
        var parts = new List<string>();
        foreach (JToken token in array)
        {
            if (token == null) continue;
            string text = token.Type == JTokenType.String ? ((string)token) : token.ToString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            parts.Add(text.Trim());
        }
        return parts.Count > 0 ? string.Join(separator, parts) : null;
    }
}
