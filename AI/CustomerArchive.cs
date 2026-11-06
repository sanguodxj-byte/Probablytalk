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
    // 顾客档案只增不删：不再设条数上限（决策见 docs/decisions/2026-11-02-上下文不设上限.md）

    internal sealed class Record
    {
        public string Identifier;
        public string Name;
        public string Faction;

        // 人格原型名（如「冷漠的精算」）：由 PersonaPool 按顾客类型抽池决定，比裸分数更好读
        public string Archetype;

        // 性格定型锁：一旦为 true，这份性格就跟着存档定死，任何情况下都不再重抽
        public bool PersonaLocked;


        // 大五人格：语气与让步尺度都从这里派生，话量也由外向性折算，不再单存 chatty
        public OceanScores Ocean = new OceanScores();

        public int FirstDay;
        public int LastDay;
        public int LastTalkDay;
        public int Talks;
        public int Deals;

        // 好感度（0-100，仅常客参与）：50 中性，越低越厌恶，越高越有好感。
        // 数值只由离店评估的 delta 与代码的价格标签使用，AffinityDay 标记最近一次评估日
        public int Affinity = 50;
        public int AffinityDay;

        // 对话往来记录（玩家/顾客各自的台词与事件），完整持久化落盘作为顾客记忆
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
    }

    public static void LoadAll()
    {
        try
        {
            Records.Clear();
            if (string.IsNullOrEmpty(_modId)) return;
            foreach (string key in ModHelper.GetModDataKeys(_modId))
            {
                string identifier;
                string field;
                if (!TrySplitKey(key, out identifier, out field) || field != DataField) continue;
                try
                {
                    TryApplyJson(GetOrCreate(identifier), ModHelper.GetModData(_modId, key));
                }
                catch (Exception ex)
                {
                    // 逐条隔离：一条坏档案不能让其余顾客全部不加载
                    if (_log != null) _log.Warning("Customer archive entry failed: " + identifier + " - " + ex.Message);
                }
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
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Customer archive record failed: " + ex.Message);
        }
    }

    // 交互对话逐句追加：玩家一句、顾客一句，全量落盘持久化作为记忆
    public static void AppendLine(string identifier, string role, string text)
    {
        if (string.IsNullOrEmpty(identifier) || string.IsNullOrWhiteSpace(text)) return;
        Record record = GetOrCreate(identifier);
        AppendLine(record, role, text);
        Save(record);
    }

    // 顾客离柜处理：记录成交数据，不发起任何 LLM 总结请求（记忆完全由完整往来承担）
    public static void OnClientLeave(StoreClient client)
    {
        try
        {
            if (client == null || string.IsNullOrEmpty(client.identifier)) return;
            Record record;
            if (!Records.TryGetValue(client.identifier, out record) || record.Talks <= 0) return;
            int day = CurrentDay();
            if (record.LastTalkDay != day) return;
            if (client.isBusinessComplete) record.Deals++;
            Save(record);
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("Customer archive on leave failed: " + ex.Message);
        }
    }

    // 兼容原调用名
    public static void SummarizeOnLeave(StoreClient client) => OnClientLeave(client);

    // 好感度（0-100）：只有常客参与，缺档时按中性 50 返回
    internal static int AffinityOf(string identifier)
    {
        Record record;
        return !string.IsNullOrEmpty(identifier) && Records.TryGetValue(identifier, out record)
            ? Math.Max(0, Math.Min(100, record.Affinity))
            : 50;
    }

    internal static int AffinityDayOf(string identifier)
    {
        Record record;
        return !string.IsNullOrEmpty(identifier) && Records.TryGetValue(identifier, out record)
            ? record.AffinityDay
            : 0;
    }

    // 最近一次说话的游戏日：用来确认「本次到店确实聊过」，只买卖不搭话的到访不重复评估
    internal static int LastTalkDayOf(string identifier)
    {
        Record record;
        return !string.IsNullOrEmpty(identifier) && Records.TryGetValue(identifier, out record)
            ? record.LastTalkDay
            : 0;
    }

    // 只由离店评估调用：夹取后落盘，并记下评估日，保证同一顾客每天只评估一次
    internal static void SetAffinity(string identifier, int value, int day)
    {
        if (string.IsNullOrEmpty(identifier)) return;
        Record record = GetOrCreate(identifier);
        record.Affinity = Math.Max(0, Math.Min(100, value));
        record.AffinityDay = day;
        Save(record);
    }

    public static List<KeyValuePair<string, string>> GetTranscript(string identifier)
    {
        if (string.IsNullOrEmpty(identifier)) return null;
        if (Records.TryGetValue(identifier, out Record record))
        {
            return record.Transcript;
        }
        return null;
    }

    // 性格定型：顾客第一次进入对话管线时由代码抽池决定，随即写锁落档。
    // 锁一旦写下，这份性格就跟着存档定死——跨天、读档、重进游戏都不重抽，任何路径都改不动它
    internal static void EnsurePersona(StoreClient client)
    {
        try
        {
            if (client == null || string.IsNullOrEmpty(client.identifier)) return;
            // 博士不 roll：固定人设（InventorInstruction），随机原型/大五人格会跟他打架
            if (PromptBook.IsInventor(client)) return;
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

    // 是否被玩家主动搭过话：预生成发言包的唯一开关——新客不预生成，聊过一次之后再来才恢复
    internal static bool HasTalked(string identifier)
    {
        Record record;
        return !string.IsNullOrEmpty(identifier) && Records.TryGetValue(identifier, out record) && record.Talks > 0;
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
            JObject item = new JObject
            {
                ["identifier"] = record.Identifier,
                ["name"] = record.Name,
                ["faction"] = record.Faction,
                ["firstDay"] = record.FirstDay,
                ["lastDay"] = record.LastDay,
                ["talks"] = record.Talks,
                ["deals"] = record.Deals,
                ["archetype"] = record.Archetype,
                ["personaLocked"] = record.PersonaLocked,
                ["affinity"] = record.Affinity,
                ["affinityDay"] = record.AffinityDay,
                ["ocean"] = new JObject
                {
                    ["openness"] = record.Ocean.Openness,
                    ["conscientiousness"] = record.Ocean.Conscientiousness,
                    ["extraversion"] = record.Ocean.Extraversion,
                    ["agreeableness"] = record.Ocean.Agreeableness,
                    ["neuroticism"] = record.Ocean.Neuroticism,
                },
            };
            if (record.Transcript != null && record.Transcript.Count > 0)
            {
                JArray trans = new JArray();
                foreach (KeyValuePair<string, string> line in record.Transcript)
                {
                    trans.Add(new JObject { ["role"] = line.Key, ["text"] = line.Value });
                }
                item["transcript"] = trans;
            }
            list.Add(item);
        }
        return list.ToString(Formatting.None);
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
        JObject obj = new JObject
        {
            ["name"] = record.Name,
            ["faction"] = record.Faction,
            ["archetype"] = record.Archetype,
            ["personaLocked"] = record.PersonaLocked,
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
            ["affinity"] = record.Affinity,
            ["affinityDay"] = record.AffinityDay,
        };

        if (record.Transcript != null && record.Transcript.Count > 0)
        {
            JArray trans = new JArray();
            foreach (KeyValuePair<string, string> line in record.Transcript)
            {
                if (string.IsNullOrWhiteSpace(line.Value)) continue;
                trans.Add(new JObject
                {
                    ["role"] = line.Key ?? "customer",
                    ["text"] = line.Value.Trim(),
                });
            }
            obj["transcript"] = trans;
        }

        return obj.ToString(Formatting.None);
    }

    private static bool TryApplyJson(Record record, string json)
    {
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            JObject obj = JObject.Parse(json);
            record.Name = obj.Value<string>("name") ?? record.Name;
            record.Faction = obj.Value<string>("faction") ?? record.Faction;
            record.Archetype = obj.Value<string>("archetype") ?? record.Archetype;
            record.PersonaLocked = obj.Value<bool?>("personaLocked") ?? record.PersonaLocked;
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
            int? affinity = obj.Value<int?>("affinity");
            if (affinity.HasValue) record.Affinity = Math.Max(0, Math.Min(100, affinity.Value));
            record.AffinityDay = obj.Value<int?>("affinityDay") ?? record.AffinityDay;

            record.Transcript.Clear();
            JArray trans = obj["transcript"] as JArray;
            if (trans != null)
            {
                foreach (JToken token in trans)
                {
                    JObject item = token as JObject;
                    if (item == null) continue;
                    string role = item.Value<string>("role") ?? "customer";
                    string text = item.Value<string>("text");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        record.Transcript.Add(new KeyValuePair<string, string>(role, text.Trim()));
                    }
                }
            }

            return true;
        }
        catch (Exception)
        {
            // 单条档案字段损坏（类型不符/转换异常）不能中止整批加载
            return false;
        }
    }

    private static int ReadScore(JObject obj, string key)
    {
        int? value = obj.Value<int?>(key);
        return value.HasValue ? Math.Max(0, Math.Min(100, value.Value)) : 50;
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
