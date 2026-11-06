using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 顾客验货能力组件：只管两件事——他这类人当场能看穿什么门道、他已经对质过什么。
//
// 能力不抄表、也不去探测具体哪件货，而是从游戏自己的委托读回档位名：
// StoreClient.canClientExposeFeature 是 Func<ItemFeature, StoreClient, bool>，
// 由 StoreClient.CompleteClientCreation 按 tag/势力赋值（SCAV→Scav、下层与黑市→LL、
// 游客→Tourist、安保→Sec、上层→UL、其余→Basic），个别原型在工厂里直接覆盖
// （营业许可证客与 Lou 收账→TradeBoard、四号通缉客→Odin、剧情买水者→水试纸）。
// 读档位名而不是自己判势力，游戏改了分档我们跟着变，不会两套口径分叉。
//
// 触发链（反编译核实）：StoreClient.OnItemPlaced → CanClientExposeAnyFeature → ClientExposeFeature，
// 后者对每条通过 CanClientExposeThisFeature 的特性弹对质对话、非伪人则势力好感 -4、
// 再 ItemFeature.ExposeFeature(lowkey: true, playBad: true)。
//
// 边界：CanClientExposeThisFeature 的判定里含 IsFeatureMatch()/IsRealFeatureCheaper()，
// 那等于「这件货的标签确实有假」——是玩家侧情报。所以本组件只下发能力档位与已发生的事件，
// 绝不对当前货物调用该判定，否则模型会提前知道他还没验的东西有鬼。
internal static class ClientInsight
{
    // 档位名 → 他能看穿的门道。名称对不上时只说「他会当场验货」，不编造具体能力
    private static readonly Dictionary<string, string[]> Tiers = new Dictionary<string, string[]>
    {
        ["BasicClientExposeCapacity"] = new[] { "拿手电照出水是脏水或锈水", "看出香烟有问题" },
        ["ScavClientExposeCapacity"] = new[] { "拿手电照出水是脏水或锈水", "看出香烟有问题", "看出注射器不能用", "验出化学品纯度不足" },
        ["LLClientExposeCapacity"] = new[] { "用水试纸验出纯度不对", "看出香烟有问题", "看出注射器不能用", "验出化学品纯度不足" },
        ["SecClientExposeCapacity"] = new[] { "用水试纸验出纯度不对", "看出香烟有问题", "看出注射器不能用", "验出化学品纯度不足" },
        ["RevClientExposeCapacity"] = new[] { "用水试纸验出纯度不对", "看出香烟有问题", "看出注射器不能用", "验出化学品纯度不足" },
        ["TouristClientExposeCapacity"] = new[] { "拿手电照出水是脏水或锈水", "看出注射器不能用" },
        ["ULClientExposeCapacity"] = new[] { "拿手电照出水是脏水或锈水", "看出注射器不能用", "验出化学品纯度不足" },
        ["OdinExposeCapacity"] = new[] { "用水试纸验出纯度不对", "看出香烟有问题", "看出注射器不能用", "验出化学品纯度不足" },
        ["TradeBoardExposeCapacity"] = new[] { "凡是标签与货不对板的都能当场识破" },
        ["ALLExposeCapacity"] = new[] { "用水试纸验出纯度不对", "看出香烟有问题" },
        ["ClientCanExposeWaterTestingStrip"] = new[] { "用水试纸验出纯度不对" },
    };

    private sealed class Confrontation
    {
        public int Day;
        public string Item;
        public string Claimed;
        public string Actual;
    }

    // 当日对质流水（identifier -> 记录）：只存内存，跨日随游戏清场自然失效
    private static readonly Dictionary<string, List<Confrontation>> Log = new Dictionary<string, List<Confrontation>>();

    internal static void Shutdown()
    {
        Log.Clear();
    }

    // 读档/新档：清当日对质流水，避免跨档同一天命中
    internal static void ResetSession()
    {
        Log.Clear();
    }

    // 由 ClientExposeFeature 前缀调用：此刻游戏还没揭穿，逐条记下他即将当面质问的东西
    internal static void Record(StoreClient client, GameItem item, ItemFeature feature)
    {
        try
        {
            if (client == null || string.IsNullOrEmpty(client.identifier) || item == null || feature == null) return;
            string itemName = item.GetDisplayName();
            if (string.IsNullOrWhiteSpace(itemName)) return;
            string claimed = SafeDisplay(feature, true);
            string actual = SafeDisplay(feature, false);
            var entry = new Confrontation
            {
                Day = CurrentDay(),
                Item = itemName.Trim(),
                Claimed = claimed,
                Actual = actual,
            };
            List<Confrontation> list;
            if (!Log.TryGetValue(client.identifier, out list))
            {
                list = new List<Confrontation>();
                Log[client.identifier] = list;
            }
            list.Add(entry);
            // 落进往来记录：离店总结与常客记忆据此知道他当场拆穿过什么。
            // 只对已经聊过的顾客追加——名册的口径是「谈过话才建档」，不能因为一次验货就凭空建档
            if (CustomerArchive.HasTalked(client.identifier))
            {
                CustomerArchive.AppendLine(client.identifier, "confront", Describe(entry));
            }
        }
        catch (Exception ex)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("client insight record failed: " + ex.Message);
        }
    }

    // 写进顾客本体分区：能力 + 当日已发生的对质
    internal static void Append(JObject body, StoreClient client)
    {
        if (body == null || client == null) return;
        JObject insight = new JObject();
        string capacity = CapacityOf(client);
        if (!string.IsNullOrWhiteSpace(capacity)) insight["capacity"] = capacity;
        JArray confronted = ConfrontedToday(client.identifier);
        if (confronted.Count > 0) insight["confronted"] = confronted;
        if (insight.Count > 0) body["insight"] = insight;
    }

    // 档位名直接读游戏赋的委托，读不到就说明这个原型没被赋验货能力
    private static string CapacityOf(StoreClient client)
    {
        try
        {
            Func<ItemFeature, StoreClient, bool> capacity = client.canClientExposeFeature;
            if (capacity == null) return null;
            string name = capacity.Method != null ? capacity.Method.Name : null;
            if (string.IsNullOrEmpty(name)) return "他会当场验货";
            string[] items;
            if (Tiers.TryGetValue(name, out items)) return string.Join("；", items);
            // 游戏加了新档位：只承认他会验货，具体能看穿什么不猜
            return "他会当场验货";
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static JArray ConfrontedToday(string identifier)
    {
        JArray list = new JArray();
        if (string.IsNullOrEmpty(identifier)) return list;
        List<Confrontation> stored;
        if (!Log.TryGetValue(identifier, out stored)) return list;
        int day = CurrentDay();
        foreach (Confrontation entry in stored)
        {
            if (entry == null || entry.Day != day) continue;
            list.Add(Describe(entry));
        }
        return list;
    }

    private static string Describe(Confrontation entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Actual)) return entry.Item + " 被他当场识破";
        return entry.Item + "：他指着标签说「" + entry.Claimed + "」不是真的，其实是「" + entry.Actual + "」";
    }

    // 显示文案取游戏自己的公开/真身文本；抛异常就当没有，不让上下文组装带崩管线
    private static string SafeDisplay(ItemFeature feature, bool publicDisplay)
    {
        try
        {
            string text = publicDisplay ? feature.GetPublicDisplay() : feature.GetActualDisplay();
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int CurrentDay()
    {
        StoreStation station = StoreStation.Instance;
        return station != null ? station.dayCounter : 0;
    }
}
