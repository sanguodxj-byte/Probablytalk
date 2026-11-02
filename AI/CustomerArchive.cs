using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 顾客档案：凡是产生过对话的顾客都留一条记录（谁、第几天、谈过几次、成交几次、离店时的总结）。
// 记录只存对话与结论，不存价格与库存；持久化走 ModHelper.GetModData/SetModData（键 talk:<identifier>:<field>），
// 与常客档案同一套规矩：读档回放、新档逐键清理。
// 离店总结在顾客离开柜台时发起：前缀取到顾客与当次上下文（自有货与柜台此时还在），
// 回调落主线程只写内存与存档，失败静默——总结是锦上添花，失败不影响游戏。
internal static class CustomerArchive
{
    private const string KeyPrefix = "talk:";
    private const string DataField = "data";
    // 档案条数上限是存档体积约束，不裁任何单条内容，也不进请求上下文
    private const int MaxRecords = 300;

    internal sealed class Record
    {
        public string Identifier;
        public string Name;
        public string Faction;
        public string Summary;
        public string Stance;

        // 人格原型名（如「冷漠的精算」）：由 PersonaPool 按顾客类型抽池决定，比裸分数更好读
        public string Archetype;

        // 性格定型锁：一旦为 true，这份性格就跟着存档定死，任何情况下都不再重抽
        public bool PersonaLocked;


        // 大五人格：语气与让步尺度都从这里派生，话量也由外向性折算，不再单存 chatty
        public OceanScores Ocean = new OceanScores();

        // 常客记忆摘要（一句话，长度由提示词口径约束）：原先单独存在 regular:<id>:memory，
        // 现在与 summary 同源，避免同一顾客有两套 AI 文本
        public string Memory;

        public int FirstDay;
        public int LastDay;
        public int LastTalkDay;
        public int Talks;
        public int Deals;

        // 本会话内的往来记录（玩家/顾客各自的台词），只供离店总结取材，不落盘
        public List<KeyValuePair<string, string>> Transcript = new List<KeyValuePair<string, string>>();
    }

    // 大五人格（Big Five / OCEAN，五因素模型）：人格心理学中最通用的一套维度，
    // 五项各 0-100、50 为中性。用它而不是自由文本标签，是因为数值能被提示词直接读成行为倾向，
    // 也不会每次总结换个说法导致性格天天变
    internal sealed class OceanScores
    {
        public int Openness = 50;
        public int Conscientiousness = 50;
        public int Extraversion = 50;
        public int Agreeableness = 50;
        public int Neuroticism = 50;
    }

    private static string _modId;
    private static Player2Client _client;
    private static ModLog _log;

    private static readonly Dictionary<string, Record> Records = new Dictionary<string, Record>();

    // 同日已发起总结的键（identifier#day）：离开流程可能被重复触发，避免同一顾客反复请求
    private static readonly HashSet<string> Summarized = new HashSet<string>();

    // 离店总结的角色指令：summary/stance 两键。性格不在这里产出——
    // 它由 PersonaPool 在首次请求前按类型池 roll 定好，模型无权改
    private const string SummaryPrompt =
        "给这位顾客写档案。输出格式：只回一个 JSON 对象，不要任何别的文字。\n\n" +
        "{\"summary\":\"两三句话概括这次交流与结果\",\"stance\":\"一句话概括他对本店态度的变化\"}\n\n" +
        "总结不要编造资料里没有的事，顺着他的性子写。";

    private const string SummaryInput = "按上面的格式给这位顾客写档案。";

    public static void Setup(string modId, Player2Client client, ModLog log)
    {
        _modId = modId;
        _client = client;
        _log = log;
    }

    public static void Shutdown()
    {
        _modId = null;
        _client = null;
        _log = null;
        Records.Clear();
        Summarized.Clear();
    }

    public static void LoadAll()
    {
        try
        {
            Records.Clear();
            Summarized.Clear();
            if (string.IsNullOrEmpty(_modId)) return;
            foreach (string key in ModHelper.GetModDataKeys(_modId))
            {
                string identifier;
                string field;
                if (!TrySplitKey(key, out identifier, out field) || field != DataField) continue;
                TryApplyJson(GetOrCreate(identifier), ModHelper.GetModData(_modId, key));
            }
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Customer archive load failed: " + ex.Message);
        }
    }

    private static bool TrySplitKey(string key, out string identifier, out string field)
    {
        identifier = null;
        field = null;
        if (string.IsNullOrEmpty(key) || !key.StartsWith(KeyPrefix)) return false;
        string rest = key.Substring(KeyPrefix.Length);
        int split = rest.LastIndexOf(':');
        if (split <= 0 || split >= rest.Length - 1) return false;
        identifier = rest.Substring(0, split);
        field = rest.Substring(split + 1);
        return true;
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
            if (_log != null) _log.Warning("Customer archive clear failed: " + ex.Message);
        }
        Records.Clear();
        Summarized.Clear();
    }

    // 产生对话即入档：发言包命中（开场白被替换）与柜台交互发送都走这里
    public static void RecordTalk(string identifier, string name, string faction, string line)
    {
        try
        {
            if (string.IsNullOrEmpty(identifier)) return;
            int day = CurrentDay();
            Record record = GetOrCreate(identifier);
            if (!string.IsNullOrEmpty(name)) record.Name = name;
            if (!string.IsNullOrEmpty(faction)) record.Faction = faction;
            if (record.FirstDay <= 0) record.FirstDay = day;
            record.LastDay = day;
            record.LastTalkDay = day;
            record.Talks++;
            if (!string.IsNullOrWhiteSpace(line)) AppendLine(record, "customer", line);
            Save(record);
            TrimOverflow();
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Customer archive record failed: " + ex.Message);
        }
    }

    // 交互对话逐句追加：玩家一句、顾客一句，全量保留供离店总结取材
    public static void AppendLine(string identifier, string role, string text)
    {
        if (string.IsNullOrEmpty(identifier) || string.IsNullOrWhiteSpace(text)) return;
        AppendLine(GetOrCreate(identifier), role, text);
    }

    // 离店总结入口：调用方须在游戏清空柜台前调用（前缀），此时顾客自带货物仍可读。
    // 两道门槛：今天没在这里说过话的顾客不做总结（路过不入档），同一顾客同日只总结一次
    public static void SummarizeOnLeave(StoreClient client)
    {
        try
        {
            if (client == null || _client == null || string.IsNullOrEmpty(client.identifier)) return;
            Record record;
            if (!Records.TryGetValue(client.identifier, out record) || record.Talks <= 0) return;
            int day = CurrentDay();
            if (record.LastTalkDay != day) return;
            if (!Summarized.Add(record.Identifier + "#" + day)) return;
            if (client.isBusinessComplete) record.Deals++;
            Save(record);

            string contextJson = ContextBrief.Render(
                StoreContext.Build(client, CurrentDay(), record.Transcript), "这单生意刚结束，他离开了柜台。");
            string identifier = record.Identifier;
            _client.Chat(SummaryPrompt, SummaryInput, contextJson, (ok, reply) =>
            {
                if (!ok || !TryParseSummary(reply, out string summary, out string stance)) return;
                Record target;
                if (!Records.TryGetValue(identifier, out target)) return;
                target.Summary = summary.Trim();
                if (!string.IsNullOrWhiteSpace(stance))
                {
                    target.Stance = stance.Trim();
                }

                Save(target);
            });
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Customer archive summary failed: " + ex.Message);
        }
    }

    // 性格定型：顾客第一次进入对话管线时由代码抽池决定，随即写锁落档。
    // 锁一旦写下，这份性格就跟着存档定死——跨天、读档、重进游戏都不重抽，任何路径都改不动它
    internal static void EnsurePersona(StoreClient client)
    {
        try
        {
            if (client == null || string.IsNullOrEmpty(client.identifier)) return;
            Record record = GetOrCreate(client.identifier);
            if (record.PersonaLocked) return;
            ClientKind kind = PersonaPool.KindOf(client);
            PersonaPool.Roll(client.identifier, kind, out string archetype, out OceanScores ocean);
            record.Archetype = archetype;
            record.Ocean = ocean;
            record.PersonaLocked = true;
            if (string.IsNullOrEmpty(record.Name)) record.Name = client.displayName;
            if (string.IsNullOrEmpty(record.Faction)) record.Faction = client.clientFaction;
            Save(record);
            if (_log != null)
            {
                _log.Log("persona locked: " + kind + " / " + record.Archetype
                    + " O" + record.Ocean.Openness + " C" + record.Ocean.Conscientiousness
                    + " E" + record.Ocean.Extraversion + " A" + record.Ocean.Agreeableness
                    + " N" + record.Ocean.Neuroticism);
            }
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Persona roll failed: " + ex.Message);
        }
    }

    // 人格原型名：注入上下文，比裸分数更好读
    internal static string ArchetypeOf(string identifier)
    {
        Record record;
        if (string.IsNullOrEmpty(identifier) || !Records.TryGetValue(identifier, out record)) return null;
        return record.Archetype;
    }

    // 常客档案补全：名字与派系只在档案里存一份，RegularManager 不再各存一套
    internal static void Ensure(string identifier, string name, string faction)
    {
        try
        {
            if (string.IsNullOrEmpty(identifier)) return;
            Record record = GetOrCreate(identifier);
            bool changed = false;
            if (!string.IsNullOrEmpty(name) && record.Name != name)
            {
                record.Name = name;
                changed = true;
            }
            if (!string.IsNullOrEmpty(faction) && record.Faction != faction)
            {
                record.Faction = faction;
                changed = true;
            }
            if (changed) Save(record);
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Customer archive ensure failed: " + ex.Message);
        }
    }

    // 常客记忆：与 summary 同一份档案，读档后仍在
    internal static string MemoryOf(string identifier)
    {
        Record record;
        if (string.IsNullOrEmpty(identifier) || !Records.TryGetValue(identifier, out record)) return null;
        return record.Memory;
    }

    // 名字与派系：RegularManager 不再单独落盘，统一从这里取
    internal static string NameOf(string identifier)
    {
        Record record;
        if (string.IsNullOrEmpty(identifier) || !Records.TryGetValue(identifier, out record)) return null;
        return record.Name;
    }

    internal static string FactionOf(string identifier)
    {
        Record record;
        if (string.IsNullOrEmpty(identifier) || !Records.TryGetValue(identifier, out record)) return null;
        return record.Faction;
    }

    internal static void SetMemory(string identifier, string memory)
    {
        try
        {
            if (string.IsNullOrEmpty(identifier) || string.IsNullOrWhiteSpace(memory)) return;
            Record record = GetOrCreate(identifier);
            string text = memory.Trim();
            if (record.Memory == text) return;
            record.Memory = text;
            Save(record);
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Customer memory failed: " + ex.Message);
        }
    }

    // 是否被玩家主动搭过话：预生成发言包的唯一开关——新客不预生成，聊过一次之后再来才恢复
    internal static bool HasTalked(string identifier)
    {
        Record record;
        return !string.IsNullOrEmpty(identifier) && Records.TryGetValue(identifier, out record) && record.Talks > 0;
    }

    // 上一次离店的总结：回灌进下一次对话的上下文，让「说过的话」真的影响「以后怎么说话」
    internal static bool TryGetPastVisit(string identifier, out string summary, out string stance, out int lastDay)
    {
        summary = null;
        stance = null;
        lastDay = 0;
        Record record;
        if (string.IsNullOrEmpty(identifier) || !Records.TryGetValue(identifier, out record)) return false;
        if (string.IsNullOrWhiteSpace(record.Summary)) return false;
        summary = record.Summary;
        stance = record.Stance;
        lastDay = record.LastDay;
        return true;
    }

    // 大五人格：没有档案时返回全 50（中性），提示词据此按普通人演
    internal static OceanScores OceanOf(string identifier)
    {
        Record record;
        if (string.IsNullOrEmpty(identifier) || !Records.TryGetValue(identifier, out record)) return new OceanScores();
        return record.Ocean ?? new OceanScores();
    }

    // 话量档位由外向性折算，不再单独存档：同一件事只留一处来源
    internal static int ChattyOf(string identifier)
    {
        int extraversion = OceanOf(identifier).Extraversion;
        if (extraversion >= 67) return 2;
        if (extraversion <= 33) return 0;
        return 1;
    }

    // 名册数据：按最近到访倒序，界面只读
    public static List<Record> Snapshot()
    {
        List<Record> list = new List<Record>(Records.Values);
        list.Sort((a, b) =>
        {
            int byDay = b.LastDay.CompareTo(a.LastDay);
            return byDay != 0 ? byDay : string.CompareOrdinal(a.Identifier, b.Identifier);
        });
        return list;
    }

    // 名册只列真正说过话的顾客：性格在首次请求前就定型落盘，那些记录还不算「产生过对话」
    public static List<Record> Roster()
    {
        List<Record> list = new List<Record>();
        foreach (Record record in Snapshot())
        {
            if (record.Talks > 0) list.Add(record);
        }
        return list;
    }

    internal static string SnapshotJson()
    {
        JArray list = new JArray();
        foreach (Record record in Roster())
        {
            list.Add(new JObject
            {
                ["identifier"] = record.Identifier,
                ["name"] = record.Name,
                ["faction"] = record.Faction,
                ["firstDay"] = record.FirstDay,
                ["lastDay"] = record.LastDay,
                ["talks"] = record.Talks,
                ["deals"] = record.Deals,
                ["summary"] = record.Summary,
                ["stance"] = record.Stance,
                ["archetype"] = record.Archetype,
            ["personaLocked"] = record.PersonaLocked,
                ["ocean"] = new JObject
                {
                    ["openness"] = record.Ocean.Openness,
                    ["conscientiousness"] = record.Ocean.Conscientiousness,
                    ["extraversion"] = record.Ocean.Extraversion,
                    ["agreeableness"] = record.Ocean.Agreeableness,
                    ["neuroticism"] = record.Ocean.Neuroticism,
                },
            });
        }
        return list.ToString(Formatting.None);
    }

    private static bool TryParseSummary(string reply, out string summary, out string stance)
    {
        summary = null;
        stance = null;
        if (string.IsNullOrWhiteSpace(reply)) return false;
        ReplyJson.Reply parsed = ReplyJson.Parse(reply);
        summary = parsed.Summary;
        stance = parsed.Stance;
        // 模型漏了总结键、只写了一段话：把台词位当总结收下，别让这次档案白跑
        if (string.IsNullOrWhiteSpace(summary)) summary = parsed.Line;
        return !string.IsNullOrWhiteSpace(summary);
    }

    private static void AppendLine(Record record, string role, string text)
    {
        record.Transcript.Add(new KeyValuePair<string, string>(role, text.Trim()));
    }

    private static Record GetOrCreate(string identifier)
    {
        Record record;
        if (!Records.TryGetValue(identifier, out record))
        {
            record = new Record { Identifier = identifier };
            Records[identifier] = record;
        }
        return record;
    }

    private static void Save(Record record)
    {
        if (string.IsNullOrEmpty(_modId) || record == null) return;
        ModHelper.SetModData(_modId, KeyPrefix + record.Identifier + ":" + DataField, Serialize(record));
    }

    // 单键 JSON：v 是存档结构版本，新增字段直接加键，读档按缺省值回落
    private static string Serialize(Record record)
    {
        OceanScores ocean = record.Ocean ?? new OceanScores();
        return new JObject
        {
            ["name"] = record.Name,
            ["faction"] = record.Faction,
            ["summary"] = record.Summary,
            ["stance"] = record.Stance,
            ["archetype"] = record.Archetype,
            ["memory"] = record.Memory,
            ["ocean"] = new JObject
            {
                ["openness"] = ocean.Openness,
                ["conscientiousness"] = ocean.Conscientiousness,
                ["extraversion"] = ocean.Extraversion,
                ["agreeableness"] = ocean.Agreeableness,
                ["neuroticism"] = ocean.Neuroticism,
            },
            ["firstDay"] = record.FirstDay,
            ["lastDay"] = record.LastDay,
            ["lastTalkDay"] = record.LastTalkDay,
            ["talks"] = record.Talks,
            ["deals"] = record.Deals,
        }.ToString(Formatting.None);
    }

    private static bool TryApplyJson(Record record, string json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            JObject obj = JObject.Parse(json);
            record.Name = obj.Value<string>("name") ?? record.Name;
            record.Faction = obj.Value<string>("faction") ?? record.Faction;
            record.Summary = obj.Value<string>("summary") ?? record.Summary;
            record.Stance = obj.Value<string>("stance") ?? record.Stance;
            record.Archetype = obj.Value<string>("archetype") ?? record.Archetype;
            record.PersonaLocked = obj.Value<bool?>("personaLocked") ?? record.PersonaLocked;
            record.Memory = obj.Value<string>("memory") ?? record.Memory;
            JObject ocean = obj["ocean"] as JObject;
            if (ocean != null)
            {
                record.Ocean = new OceanScores
                {
                    Openness = ReadScore(ocean, "openness"),
                    Conscientiousness = ReadScore(ocean, "conscientiousness"),
                    Extraversion = ReadScore(ocean, "extraversion"),
                    Agreeableness = ReadScore(ocean, "agreeableness"),
                    Neuroticism = ReadScore(ocean, "neuroticism"),
                };
            }
            record.FirstDay = obj.Value<int?>("firstDay") ?? record.FirstDay;
            record.LastDay = obj.Value<int?>("lastDay") ?? record.LastDay;
            record.LastTalkDay = obj.Value<int?>("lastTalkDay") ?? record.LastTalkDay;
            record.Talks = obj.Value<int?>("talks") ?? record.Talks;
            record.Deals = obj.Value<int?>("deals") ?? record.Deals;
            return true;
        }
        catch (JsonReaderException)
        {
            return false;
        }
    }

    private static int ReadScore(JObject obj, string key)
    {
        int? value = obj.Value<int?>(key);
        return value.HasValue ? Math.Max(0, Math.Min(100, value.Value)) : 50;
    }

    // 记录数上限：超限先丢「只在存档里、本会话没谈过」的最旧一条，避免删掉正在用的条目
    private static void TrimOverflow()
    {
        if (Records.Count <= MaxRecords) return;
        List<Record> sorted = Snapshot();
        for (int i = sorted.Count - 1; i >= 0 && Records.Count > MaxRecords; i--)
        {
            Record candidate = sorted[i];
            if (candidate.Transcript.Count > 0) continue;
            Records.Remove(candidate.Identifier);
            RemoveAll(candidate.Identifier);
        }
    }

    private static void RemoveAll(string identifier)
    {
        if (string.IsNullOrEmpty(_modId)) return;
        ModHelper.RemoveModData(_modId, KeyPrefix + identifier + ":" + DataField);
    }

    private static int CurrentDay()
    {
        StoreStation station = StoreStation.Instance;
        return station != null ? station.dayCounter : 0;
    }
}
