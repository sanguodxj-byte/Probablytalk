using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 常客档案系统：把反复到访且有近期成交的顾客沉淀为档案，按保守节奏调度回访。
// 沉淀只看原生跨日计数 StoreClientManager.GetCustomerTotalVisitedCount 与本会话记录的最近成交日；
// 回访经 StoreClientListDict.CreateStoreClient(identifier) 重建顾客后 AddClient 入队，
// 工厂未注册的 identifier 回退为随机散客，必须按 identifier 相等校验拦截；
// 档案持久化只走 ModHelper.SetModData/GetModData（键 regular:<identifier>:<field>）。
internal static class RegularManager
{
    // 关系等级四档：0 陌生 → 1 熟客 → 2 老友 → 3 盟友
    internal const int TierStranger = 0;
    internal const int TierRegular = 1;
    internal const int TierOldFriend = 2;
    internal const int TierAlly = 3;

    // 晋升门槛按档案建立后的实到访次数（Visits）计，只升不降；实到访=顾客走到柜台启动主对话
    private const int TierRegularVisits = 3;
    private const int TierOldFriendVisits = 8;
    private const int TierAllyVisits = 15;

    // 沉淀门槛：原生跨日到访计数达到 3 才有资格；闭店结算时当日到访已由
    // PlayerStore.GetNextClient 经 TrackClientVisit 入账，直接按原生值判断
    private const int SedimentVisitsThreshold = 3;

    // 沉淀概率（RNG.Roll 百分比）与近期成交窗口（天）；任一不满足不沉淀
    private const int SedimentChancePercent = 25;
    private const int RecentDealDays = 3;

    // 活跃档案数上限：到顶后当日不再沉淀新档案
    private const int MaxActiveRegulars = 4;

    // 每日常客入队名额；剧情/通缉/随机客都在 VeryLate 之前入栈，常客只追加队尾
    private const int DailyRegularCap = 1;

    // 回访间隔随机区间 [2,5) 即 2-4 天（UnityEngine.Random.Range 整数上界不含）
    private const int VisitIntervalMinDays = 2;
    private const int VisitIntervalMaxDays = 5;

    // 缺席退场：距上次实到访达 7 天（到期日被原生冷却压住的天数一并计入）转 left
    private const int AbsentLeftDays = 7;

    // AI 记忆摘要长度上限：一句话量级，控制发言上下文体积
    private const int MaxMemoryLength = 200;

    private const string StatusActive = "active";
    private const string StatusLeft = "left";
    private const string KeyPrefix = "regular:";

    private sealed class RegularProfile
    {
        public string Identifier;
        public string DisplayName;
        public string Faction;
        public int Tier;
        public string PrefsJson;
        public string Memory;
        public string Status;
        public int Visits;
        public int LastVisitDay;
        public int NextVisitDay;
    }

    private static string _modId;
    private static ModLog _log;

    private static readonly Dictionary<string, RegularProfile> Profiles = new Dictionary<string, RegularProfile>();

    // 当日名单定型时的候选客快照：闭店沉淀评估只读其 identifier/faction/白名单等字符串字段
    private static readonly List<StoreClient> DayCandidates = new List<StoreClient>();

    // 本会话内的最近成交日（identifier -> day）：仅内存，读档后重置，沉淀窗口因此只紧不松
    private static readonly Dictionary<string, int> DealDays = new Dictionary<string, int>();

    public static void Setup(string modId, ModLog log)
    {
        _modId = modId;
        _log = log;
    }

    public static void Shutdown()
    {
        _modId = null;
        _log = null;
        Profiles.Clear();
        DayCandidates.Clear();
        DealDays.Clear();
    }

    // 读档回放：PlayerStore.LoadGame 在 modData 反序列化完成后才触发 OnGameLoadedLate，
    // 此处 GetModDataKeys 返回的已是本存档数据
    public static void LoadAll()
    {
        try
        {
            Profiles.Clear();
            DayCandidates.Clear();
            DealDays.Clear();
            if (string.IsNullOrEmpty(_modId)) return;
            foreach (string key in ModHelper.GetModDataKeys(_modId))
            {
                if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix)) continue;
                string rest = key.Substring(KeyPrefix.Length);
                // 字段名是固定枚举且不含冒号，取最后一个冒号切分，
                // identifier 自带冒号时仍能完整还原
                int split = rest.LastIndexOf(':');
                if (split <= 0 || split >= rest.Length - 1) continue;
                string identifier = rest.Substring(0, split);
                string field = rest.Substring(split + 1);
                RegularProfile profile = GetOrCreate(identifier);
                ApplyField(profile, field, ModHelper.GetModData(_modId, key));
            }

            // 名字/派系/记忆统一从顾客档案回填，regular 侧只负责调度状态
            foreach (RegularProfile profile in Profiles.Values)
            {
                string name = CustomerArchive.NameOf(profile.Identifier);
                string faction = CustomerArchive.FactionOf(profile.Identifier);
                string memory = CustomerArchive.MemoryOf(profile.Identifier);
                if (!string.IsNullOrEmpty(name)) profile.DisplayName = name;
                if (!string.IsNullOrEmpty(faction)) profile.Faction = faction;
                if (!string.IsNullOrEmpty(memory)) profile.Memory = memory;
            }
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Regular load failed: " + ex.Message);
        }
    }

    // 新开档清空：新档 modData 本身为空，这里按规范逐键移除并复位内存态
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
            if (_log != null) _log.Warning("Regular clear failed: " + ex.Message);
        }
        Profiles.Clear();
        DayCandidates.Clear();
        DealDays.Clear();
    }

    // 到访调度：OnGenerateCustomerVeryLate 是发言管线的当日名单定型点，此处先入队到期常客，
    // 随后的全栈扫描才把常客一并纳入发言包预生成；被屏蔽/重复拦截时到期日保持不变，次日重试
    public static void OnGenerateCustomerVeryLate(StoreClientManager manager)
    {
        try
        {
            if (manager == null) return;
            int day = CurrentDay();
            if (day <= 0) return;
            ScheduleDue(manager, day);
            RegisterDayStack(manager, day);
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Regular schedule failed: " + ex.Message);
        }
    }

    // 闭店结算：沉淀评估用当日实到访后的原生计数，退场检查覆盖被逮捕与连续缺席
    public static void OnShutterClosedLate()
    {
        try
        {
            int day = CurrentDay();
            if (day <= 0) return;
            Sediment(day);
            Depart(day);
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Regular end of day failed: " + ex.Message);
        }
    }

    // 成交记录（StoreClient.CompleteBusiness 后缀入口）：只记最近成交日供沉淀窗口判断
    public static void OnDealCompleted(string identifier)
    {
        try
        {
            if (string.IsNullOrEmpty(identifier)) return;
            int day = CurrentDay();
            if (day <= 0) return;
            DealDays[identifier] = day;
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Regular deal record failed: " + ex.Message);
        }
    }

    // 实到访记账（StoreClient.StartMainDialogue 后缀入口）：主对话启动即顾客走到柜台，
    // Visits、LastVisitDay 与档位只在此处累加，早晨入栈登记不计数；
    // 同日重复触发（对话重开）按 LastVisitDay 去重
    public static void OnClientArrived(string identifier)
    {
        try
        {
            if (string.IsNullOrEmpty(identifier)) return;
            int day = CurrentDay();
            if (day <= 0) return;
            RegularProfile profile;
            if (!Profiles.TryGetValue(identifier, out profile) || profile.Status != StatusActive) return;
            if (profile.LastVisitDay == day) return;
            profile.LastVisitDay = day;
            profile.Visits++;
            profile.Tier = Math.Max(profile.Tier, ComputeTier(profile.Visits));
            SaveProfile(profile);
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Regular arrival failed: " + ex.Message);
        }
    }

    // 发言包顺带产出的记忆摘要：只落常客档案，超长截断
    public static void OnMemoryProduced(string identifier, string memory)
    {
        try
        {
            if (string.IsNullOrEmpty(identifier) || string.IsNullOrWhiteSpace(memory)) return;
            RegularProfile profile;
            if (!Profiles.TryGetValue(identifier, out profile)) return;
            string trimmed = TextLimit.Truncate(memory.Trim(), MaxMemoryLength);
            if (profile.Memory == trimmed) return;
            profile.Memory = trimmed;
            // 记忆与 summary 同源：写进顾客档案，不再是 regular 的独立字段
            CustomerArchive.SetMemory(identifier, trimmed);
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Regular memory failed: " + ex.Message);
        }
    }

    // 发言上下文追加：仅活跃常客附加关系档位、常客到访数、偏好快照与记忆摘要
    public static void AppendSpeechContext(JObject context, string identifier)
    {
        try
        {
            if (context == null || string.IsNullOrEmpty(identifier)) return;
            RegularProfile profile;
            if (!Profiles.TryGetValue(identifier, out profile) || profile.Status != StatusActive) return;
            JObject regular = new JObject
            {
                ["tier"] = profile.Tier,
                ["visits"] = profile.Visits,
                ["memory"] = CustomerArchive.MemoryOf(identifier) ?? profile.Memory ?? string.Empty,
            };
            if (!string.IsNullOrEmpty(profile.PrefsJson))
            {
                try
                {
                    JObject prefs = JObject.Parse(profile.PrefsJson);
                    // 特征偏好已由发言上下文按实时值顶层发送（首访客同样带），
                    // 档案快照里不再重复一份，避免同一字段出现两套口径
                    prefs.Remove("wantsFeatures");
                    if (prefs.Count > 0) regular["prefs"] = prefs;
                }
                catch (JsonReaderException)
                {
                }
            }
            context["regular"] = regular;
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Regular speech context failed: " + ex.Message);
        }
    }

    private static void ScheduleDue(StoreClientManager manager, int day)
    {
        List<StoreClient> stack = manager.GetClientStack();
        if (stack == null) return;
        int queued = 0;
        List<RegularProfile> due = Profiles.Values
            .Where(p => p.Status == StatusActive && day >= p.NextVisitDay)
            .OrderBy(p => p.NextVisitDay).ThenBy(p => p.Identifier)
            .ToList();
        foreach (RegularProfile profile in due)
        {
            if (queued >= DailyRegularCap) break;
            if (StoreClientManager.IsClientOnCooldown(profile.Identifier)) continue;
            if (stack.Any(c => c != null && c.identifier == profile.Identifier)) continue;
            StoreClient client = StoreClientListDict.CreateStoreClient(profile.Identifier);
            if (client == null || client.isWanted) continue;
            if (client.identifier != profile.Identifier) continue;
            manager.AddClient(client);
            // AddClient 被告密者/禁售规则拦截时静默不入栈，以实收为准
            if (stack.Contains(client)) queued++;
        }
    }

    // 当日名单定型登记：常客只刷新调度与资料（下次回访间隔、显示名、偏好快照），
    // 候选客留存供闭店沉淀；到访计数以实到柜为准，早晨入栈不等于走到柜台
    private static void RegisterDayStack(StoreClientManager manager, int day)
    {
        DayCandidates.Clear();
        List<StoreClient> stack = manager.GetClientStack();
        if (stack == null) return;
        HashSet<string> seen = new HashSet<string>();
        foreach (StoreClient client in stack)
        {
            if (client == null || string.IsNullOrEmpty(client.identifier) || !seen.Add(client.identifier)) continue;
            DayCandidates.Add(client);
            RegularProfile profile;
            if (!Profiles.TryGetValue(client.identifier, out profile) || profile.Status != StatusActive) continue;
            profile.NextVisitDay = day + NextInterval();
            if (!string.IsNullOrEmpty(client.displayName)) profile.DisplayName = client.displayName;
            if (!string.IsNullOrEmpty(client.clientFaction)) profile.Faction = client.clientFaction;
            CustomerArchive.Ensure(profile.Identifier, profile.DisplayName, profile.Faction);
            profile.PrefsJson = BuildPrefsJson(client);
            SaveProfile(profile);
        }
    }

    private static void Sediment(int day)
    {
        StoreClientManager manager = PlayerStore.Instance != null ? PlayerStore.Instance.storeClientManager : null;
        if (manager == null)
        {
            DayCandidates.Clear();
            return;
        }
        int activeCount = Profiles.Values.Count(p => p.Status == StatusActive);
        foreach (StoreClient client in DayCandidates)
        {
            if (activeCount >= MaxActiveRegulars) break;
            if (client == null || string.IsNullOrEmpty(client.identifier)) continue;
            if (Profiles.ContainsKey(client.identifier)) continue;
            if (!ClientSpeech.TalkIntents.Contains(client.clientIntent)) continue;
            if (manager.arrestedClients != null && manager.arrestedClients.Contains(client.identifier)) continue;
            if (StoreClientManager.GetCustomerTotalVisitedCount(client.identifier) < SedimentVisitsThreshold) continue;
            int dealDay;
            if (!DealDays.TryGetValue(client.identifier, out dealDay) || day - dealDay > RecentDealDays) continue;
            if (!RNG.Roll(SedimentChancePercent)) continue;
            // 档位计数只认建档后的实到访，建档时清零；LastVisitDay 取最近成交日（成交发生在柜台），
            // 建档前的柜台行为不重复入账
            RegularProfile profile = new RegularProfile
            {
                Identifier = client.identifier,
                DisplayName = !string.IsNullOrEmpty(client.displayName)
                    ? client.displayName
                    : StoreClientListDict.GetClientDisplayName(client.identifier),
                Faction = client.clientFaction,
                Tier = TierStranger,
                PrefsJson = BuildPrefsJson(client),
                Memory = string.Empty,
                Status = StatusActive,
                Visits = 0,
                LastVisitDay = dealDay,
                NextVisitDay = day + NextInterval(),
            };
            Profiles[profile.Identifier] = profile;
            activeCount++;
            // 建档即把名字/派系写进顾客档案，regular 侧不再单独存
            CustomerArchive.Ensure(profile.Identifier, profile.DisplayName, profile.Faction);
            SaveProfile(profile);
        }
        DayCandidates.Clear();
    }

    private static void Depart(int day)
    {
        StoreClientManager manager = PlayerStore.Instance != null ? PlayerStore.Instance.storeClientManager : null;
        foreach (RegularProfile profile in Profiles.Values.ToList())
        {
            if (profile.Status != StatusActive) continue;
            bool arrested = manager != null && manager.arrestedClients != null
                && manager.arrestedClients.Contains(profile.Identifier);
            bool absent = day - profile.LastVisitDay >= AbsentLeftDays;
            if (!arrested && !absent) continue;
            profile.Status = StatusLeft;
            SaveProfile(profile);
        }
    }

    private static RegularProfile GetOrCreate(string identifier)
    {
        RegularProfile profile;
        if (!Profiles.TryGetValue(identifier, out profile))
        {
            profile = new RegularProfile
            {
                Identifier = identifier,
                Status = StatusActive,
            };
            Profiles[identifier] = profile;
        }
        return profile;
    }

    private static void ApplyField(RegularProfile profile, string field, string value)
    {
        int parsed;
        switch (field)
        {
            case "tier": if (int.TryParse(value, out parsed)) profile.Tier = parsed; break;
            case "prefs": profile.PrefsJson = value; break;
            case "status": if (!string.IsNullOrEmpty(value)) profile.Status = value; break;
            case "visits": if (int.TryParse(value, out parsed)) profile.Visits = parsed; break;
            case "lastVisit": if (int.TryParse(value, out parsed)) profile.LastVisitDay = parsed; break;
            case "nextVisit": if (int.TryParse(value, out parsed)) profile.NextVisitDay = parsed; break;
        }
    }

    private static void SaveProfile(RegularProfile profile)
    {
        // 只落调度状态：名字/派系/记忆统一存在 CustomerArchive（talk:<id>:data），
        // 这里再存一份只会在两边漂移
        SaveField(profile, "tier", profile.Tier.ToString());
        SaveField(profile, "prefs", profile.PrefsJson);
        SaveField(profile, "status", profile.Status);
        SaveField(profile, "visits", profile.Visits.ToString());
        SaveField(profile, "lastVisit", profile.LastVisitDay.ToString());
        SaveField(profile, "nextVisit", profile.NextVisitDay.ToString());
    }

    private static void SaveField(RegularProfile profile, string field, string value)
    {
        if (string.IsNullOrEmpty(_modId)) return;
        ModHelper.SetModData(_modId, KeyPrefix + profile.Identifier + ":" + field, value ?? string.Empty);
    }

    private static int ComputeTier(int visits)
    {
        if (visits >= TierAllyVisits) return TierAlly;
        if (visits >= TierOldFriendVisits) return TierOldFriend;
        if (visits >= TierRegularVisits) return TierRegular;
        return TierStranger;
    }

    private static int NextInterval()
    {
        return UnityEngine.Random.Range(VisitIntervalMinDays, VisitIntervalMaxDays);
    }

    // 偏好快照：三条白名单全部为 string 列表（clientItemFeatureBuying 存特征 id 如 discountedBuy20）
    private static string BuildPrefsJson(StoreClient client)
    {
        JObject prefs = new JObject();
        AddList(prefs, "wantsIds", client.clientBuyingIdList);
        AddList(prefs, "wantsTags", client.clientBuyingTagList);
        AddList(prefs, "wantsFeatures", client.clientItemFeatureBuying);
        return prefs.ToString(Formatting.None);
    }

    private static void AddList(JObject prefs, string name, List<string> items)
    {
        if (items == null || items.Count == 0) return;
        prefs[name] = new JArray(items);
    }

    // 桥接读取口：档案快照序列化，供 ProbablyBridge 验收工具只读查看
    internal static string SnapshotJson()
    {
        JArray list = new JArray();
        foreach (RegularProfile profile in Profiles.Values)
        {
            list.Add(new JObject
            {
                ["identifier"] = profile.Identifier,
                ["displayName"] = CustomerArchive.NameOf(profile.Identifier) ?? profile.DisplayName,
                ["faction"] = CustomerArchive.FactionOf(profile.Identifier) ?? profile.Faction,
                ["tier"] = profile.Tier,
                ["status"] = profile.Status,
                ["visits"] = profile.Visits,
                ["lastVisitDay"] = profile.LastVisitDay,
                ["nextVisitDay"] = profile.NextVisitDay,
                ["memory"] = CustomerArchive.MemoryOf(profile.Identifier) ?? profile.Memory,
            });
        }
        return list.ToString(Formatting.None);
    }

    private static int CurrentDay()
    {
        StoreStation station = StoreStation.Instance;
        return station != null ? station.dayCounter : 0;
    }
}
