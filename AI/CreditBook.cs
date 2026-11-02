using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 赊账账本：顾客预算不够时由玩家点头挂账，差额从他的预算里补上让这笔生意当场成立，债务记在这里。
// 存档级：写 PlayerStore 的 modData（键 credit:<identifier>:<field>），换档换账本、新档清空。
// 收账走同一条对话通道：顾客下次来店时账本金额已进上下文，玩家一句「还钱」即可结算，
// 从他当前预算里扣、加到玩家现金上，扣多少由账本与他的钱袋共同封顶。
internal static class CreditBook
{
    private const string KeyPrefix = "credit:";

    // 单笔赊账上限与单人累计上限：只约束本 mod 的挂账额度，不改游戏任何经济数值
    internal const int MaxPerDeal = 500;
    internal const int MaxTotal = 2000;

    internal sealed class Entry
    {
        public string Identifier;
        public string Name;
        public int Amount;
        public int Day;
    }

    private static string _modId;
    private static ModLog _log;

    private static readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>();

    // 本轮已垫付但还没成交的额度：只有当顾客真的完成交易才转成债务。
    // 顾客拿了额度却没买（掉头走人）时不记账——额度加在他的预算上，人走了实例即销毁，不留痕迹
    private static readonly Dictionary<string, int> Pending = new Dictionary<string, int>();

    public static void Setup(string modId, ModLog log)
    {
        _modId = modId;
        _log = log;
    }

    public static void Shutdown()
    {
        _modId = null;
        _log = null;
        Entries.Clear();
        Pending.Clear();
    }

    public static void LoadAll()
    {
        try
        {
            Entries.Clear();
            Pending.Clear();
            if (string.IsNullOrEmpty(_modId)) return;
            foreach (string key in ModHelper.GetModDataKeys(_modId))
            {
                if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix)) continue;
                string rest = key.Substring(KeyPrefix.Length);
                int split = rest.LastIndexOf(':');
                if (split <= 0 || split >= rest.Length - 1) continue;
                string identifier = rest.Substring(0, split);
                string field = rest.Substring(split + 1);
                Entry entry = GetOrCreate(identifier);
                string value = ModHelper.GetModData(_modId, key);
                int parsed;
                switch (field)
                {
                    case "amount": if (int.TryParse(value, out parsed)) entry.Amount = parsed; break;
                    case "day": if (int.TryParse(value, out parsed)) entry.Day = parsed; break;
                    case "name": entry.Name = value; break;
                }
            }
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Credit book load failed: " + ex.Message);
        }
    }

    public static void OnNewGameStarted()
    {
        try
        {
            if (!string.IsNullOrEmpty(_modId))
            {
                foreach (string key in ModHelper.GetModDataKeys(_modId))
                {
                    if (!string.IsNullOrEmpty(key) && key.StartsWith(KeyPrefix))
                    {
                        ModHelper.RemoveModData(_modId, key);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Credit book clear failed: " + ex.Message);
        }
        Entries.Clear();
        Pending.Clear();
    }

    internal static int DebtOf(string identifier)
    {
        Entry entry;
        return !string.IsNullOrEmpty(identifier) && Entries.TryGetValue(identifier, out entry) ? entry.Amount : 0;
    }

    internal static int SinceDay(string identifier)
    {
        Entry entry;
        return !string.IsNullOrEmpty(identifier) && Entries.TryGetValue(identifier, out entry) ? entry.Day : 0;
    }

    // 赊账：只补「他买不起的那部分」。顾客本来就付得起时不记账（没有赊的意义），返回 0。
    // 差额按当前正在谈的那件货的议价估值算，算不出来才退回模型给的金额。
    // 这里只垫付并挂起，账目要等 CompleteBusiness 之后才落到账本上（见 CommitPending）
    internal static int Stage(StoreClient client, int requested)
    {
        try
        {
            if (client == null || string.IsNullOrEmpty(client.identifier)) return 0;
            int budget = client.GetBudget();
            int shortfall = Shortfall(client, budget);
            if (shortfall <= 0) shortfall = Math.Min(Math.Max(0, requested), MaxPerDeal);
            if (shortfall <= 0) return 0;

            int owed = DebtOf(client.identifier);
            int room = Math.Max(0, MaxTotal - owed);
            int amount = Math.Min(shortfall, Math.Min(MaxPerDeal, room));
            if (amount <= 0) return 0;

            client.ModBudget(amount);
            int staged;
            Pending.TryGetValue(client.identifier, out staged);
            Pending[client.identifier] = staged + amount;
            if (_log != null) _log.Log("credit staged " + amount + " for " + client.identifier);
            return amount;
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Credit stage failed: " + ex.Message);
            return 0;
        }
    }

    // 成交确认（StoreClient.CompleteBusiness 后缀）：把挂起额度转成债务，写存档
    internal static void CommitPending(string identifier, string name)
    {
        try
        {
            if (string.IsNullOrEmpty(identifier)) return;
            int staged;
            if (!Pending.TryGetValue(identifier, out staged) || staged <= 0) return;
            Pending.Remove(identifier);

            Entry entry = GetOrCreate(identifier);
            entry.Name = string.IsNullOrEmpty(name) ? entry.Name : name;
            entry.Amount = Math.Min(MaxTotal, entry.Amount + staged);
            entry.Day = CurrentDay();
            Save(entry);
            if (_log != null) _log.Log("credit committed " + staged + " for " + identifier + " (debt " + entry.Amount + ")");
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Credit commit failed: " + ex.Message);
        }
    }

    // 离店/未成交：丢弃本轮挂起的额度。垫付只加在当时那个顾客实例的预算上，
    // 实例销毁即无痕；若不清掉，同一 identifier 下次赊账会叠加并重复记账
    internal static void DiscardPending(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return;
        Pending.Remove(identifier);
    }

    // 收账：从顾客当前预算里扣，扣多少由债务与他手上的钱共同封顶，扣到的钱进玩家现金。
    // 钱不够就只收一部分，账本留余额——不做自动催收，什么时候要账由玩家在对话里决定
    internal static int Collect(StoreClient client)
    {
        try
        {
            if (client == null || string.IsNullOrEmpty(client.identifier)) return 0;
            Entry entry;
            if (!Entries.TryGetValue(client.identifier, out entry) || entry.Amount <= 0) return 0;
            int affordable = Math.Max(0, client.GetBudget());
            int amount = Math.Min(entry.Amount, affordable);
            if (amount <= 0) return 0;

            client.ModBudget(-amount);
            PlayerStore store = PlayerStore.Instance;
            if (store != null) store.playerCash += amount;
            entry.Amount -= amount;
            entry.Day = CurrentDay();
            if (entry.Amount <= 0) Remove(entry.Identifier); else Save(entry);
            if (_log != null) _log.Log("credit collected " + amount + " from " + client.identifier);
            return amount;
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Credit collect failed: " + ex.Message);
            return 0;
        }
    }

    internal static List<Entry> Snapshot()
    {
        List<Entry> list = new List<Entry>();
        foreach (Entry entry in Entries.Values)
        {
            if (entry.Amount > 0) list.Add(entry);
        }
        list.Sort((a, b) => b.Amount.CompareTo(a.Amount));
        return list;
    }

    internal static string SnapshotJson()
    {
        JArray list = new JArray();
        foreach (Entry entry in Snapshot())
        {
            list.Add(new JObject
            {
                ["identifier"] = entry.Identifier,
                ["name"] = entry.Name,
                ["amount"] = entry.Amount,
                ["day"] = entry.Day,
            });
        }
        return list.ToString(Formatting.None);
    }

    // 差额：当前正在谈的那件货值多少，减去他手上还有多少。拿不到协商物件时返回 0，交给调用方兜底
    private static int Shortfall(StoreClient client, int budget)
    {
        NegociationUIManager ui = NegociationUIManager.Instance;
        GameItem item = ui != null ? ui.currentNegociatedItem : null;
        if (item == null) return 0;
        long price;
        try
        {
            price = item.GetNegociatedValue();
        }
        catch (Exception)
        {
            return 0;
        }
        if (price <= budget) return 0;
        return (int)Math.Min(int.MaxValue, price - budget);
    }

    private static Entry GetOrCreate(string identifier)
    {
        Entry entry;
        if (!Entries.TryGetValue(identifier, out entry))
        {
            entry = new Entry { Identifier = identifier };
            Entries[identifier] = entry;
        }
        return entry;
    }

    private static void Save(Entry entry)
    {
        if (string.IsNullOrEmpty(_modId)) return;
        ModHelper.SetModData(_modId, KeyPrefix + entry.Identifier + ":amount", entry.Amount.ToString());
        ModHelper.SetModData(_modId, KeyPrefix + entry.Identifier + ":day", entry.Day.ToString());
        ModHelper.SetModData(_modId, KeyPrefix + entry.Identifier + ":name", entry.Name ?? string.Empty);
    }

    private static void Remove(string identifier)
    {
        Entries.Remove(identifier);
        if (string.IsNullOrEmpty(_modId)) return;
        ModHelper.RemoveModData(_modId, KeyPrefix + identifier + ":amount");
        ModHelper.RemoveModData(_modId, KeyPrefix + identifier + ":day");
        ModHelper.RemoveModData(_modId, KeyPrefix + identifier + ":name");
    }

    private static int CurrentDay()
    {
        StoreStation station = StoreStation.Instance;
        return station != null ? station.dayCounter : 0;
    }
}
