using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 对话管线，两段式：
//   1) 首次接触——顾客从未被搭过话时，开场白一律走游戏原版台词，本模组不发任何请求；
//      玩家的第一句话就是管线入口，模型只负责回应。
//   2) 熟客回访——一旦在 CustomerArchive 里留下对话记录，该顾客之后每天入队时恢复预生成发言包，
//      命中则改写开场白、并附一句闲聊。判定只有一条：CustomerArchive.HasTalked(identifier)。
// 预生成在每日名单定型（VeryLate）与入队（AddClient/AddNextClient）两个时点触发，键为 identifier+日。
// 全链路无同步等待：请求在线程池上跑，回调经 MainThreadPump 回主线程只写内存缓存，失败静默走原版。
internal static class ClientSpeech
{
    private enum PackState { Running, Success, Failed }

    private sealed class SpeechPack
    {
        public PackState State;
        public int Day;
        public string Opening;
        public string Chat;

        // 请求发出那一刻的原版台词：开场白注入会改写 mainDialogue.plainText，
        // 之后的交互对话若要「原版语气锚点」，只能从这里取，否则读到的是模型自己上一轮的话
        public string OriginalLine;
    }

    // 生成节奏的默认值：运行期值由 SpeechSettings（游戏「选项」菜单承载）提供
    internal const int DefaultMaxRequestsPerDay = 12;
    internal const int DefaultMaxLineLength = 400;

    // 交易意图白名单：只有明确来买卖的顾客才开 AI 发言；常客沉淀沿用同一白名单，剧情客不会入档。
    // 剧情(DIALOGUE/SPECIAL)、执法(INSPECTION)、房租与讨债(RENT/LOAN_SHARK)、
    // 情报/远征/收购等其他意图全部跳过；通缉客另有 isWanted 判定。
    internal static readonly HashSet<StoreClient.ClientIntent> TalkIntents = new HashSet<StoreClient.ClientIntent>
    {
        StoreClient.ClientIntent.BUY,
        StoreClient.ClientIntent.SELL,
        StoreClient.ClientIntent.SELLNBUY,
        StoreClient.ClientIntent.BARTER,
        StoreClient.ClientIntent.WHOLESALE,
    };

    private static readonly ConcurrentDictionary<string, SpeechPack> Packs = new ConcurrentDictionary<string, SpeechPack>();

    private static Player2Config _config;
    private static Player2Client _client;
    private static ModLog _log;

    // 当日已消耗的请求额度：预生成与柜台对话共用一个池子，0 上限等价于关闭 AI
    private static int _budgetDay = -1;
    private static int _budgetUsed;

    public static void Setup(Player2Config config, Player2Client client, ModLog log)
    {
        _config = config;
        _client = client;
        _log = log;
    }

    public static void Shutdown()
    {
        _config = null;
        _client = null;
        _log = null;
        Packs.Clear();
        _budgetDay = -1;
        _budgetUsed = 0;
    }

    // 读档/新档：清当日发言包与额度，避免上一档的同日缓存被复用
    internal static void ResetSession()
    {
        Packs.Clear();
        _budgetDay = -1;
        _budgetUsed = 0;
    }

    // 当日名单定型时全栈扫描：VeryLate 钩子在 ClientGenCheck 按 identifier 去重之后，栈内即当日最终名单
    public static void OnDayStackReady(StoreClientManager manager)
    {
        try
        {
            int day = CurrentDay();
            ClearOtherDays(day);
            List<StoreClient> stack = manager != null ? manager.GetClientStack() : null;
            if (stack == null) return;
            foreach (StoreClient client in stack)
            {
                TryRequest(client, day);
            }
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("ClientSpeech day stack scan failed: " + ex.Message);
        }
    }

    // 入队口补发（AddClient/AddNextClient 补丁）：只对真正入栈的顾客发请求，键去重天然防止与全栈扫描重复
    public static void OnClientEnqueued(StoreClientManager manager, StoreClient client)
    {
        try
        {
            List<StoreClient> stack = manager != null ? manager.GetClientStack() : null;
            if (stack == null || client == null || !stack.Contains(client)) return;
            TryRequest(client, CurrentDay());
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("ClientSpeech enqueue hook failed: " + ex.Message);
        }
    }

    // 主对话注入（StartMainDialogue 前缀）：StartDialogue 会同步读 plainText 显示，
    // 文本改写必须发生在它之前；未命中时原方法照常播放原版台词
    public static void ApplyOnStartMainDialogue(StoreClient client)
    {
        try
        {
            if (client == null || string.IsNullOrEmpty(client.identifier) || string.IsNullOrEmpty(client.displayName)) return;
            SpeechPack pack = GetPack(client.identifier, CurrentDay());
            if (pack != null && pack.State == PackState.Failed)
            {
                // 预生成失败：左上角报错，随后照常走原版台词
                NotifyPregenFailed();
                return;
            }
            if (pack == null || pack.State != PackState.Success || string.IsNullOrWhiteSpace(pack.Opening)) return;
            Dialogue main = client.mainDialogue;
            if (main == null) return;
            main.SetText(client.displayName, TrimLine(pack.Opening));
            AttachChatLine(main, client, pack.Chat);
        }
        catch (Exception ex)
        {
            // 前缀抛异常会中断游戏自己的对话启动，这里必须全吞，失败即原版台词
            if (_log != null) _log.Warning("ClientSpeech apply failed: " + ex.Message);
        }
    }

    // 预生成失败：左上角报错，随后走原版台词（不阻断原版）
    private static void NotifyPregenFailed()
    {
        try
        {
            if (StoreUIManager.Instance == null) return;
            string message = ModHelper.GetLocalized(ModEntry.ModId, "pregen_failed");
            if (string.IsNullOrEmpty(message) || message == "pregen_failed")
            {
                message = "AI 开场白生成失败，已使用原版台词";
            }
            StoreUIManager.Instance.Notify(message, "sec");
        }
        catch (Exception ex)
        {
            if (_log != null) _log.Warning("pregen failed notify error: " + ex.Message);
        }
    }

    // 闲聊句续链只在主对话完全无后文时安全：原版推进时先执行当前句 endAction，
    // 因此仅当 nextDialogue 与 endAction 双空才挂链，否则闲聊句整体丢弃
    private static void AttachChatLine(Dialogue main, StoreClient client, string chat)
    {
        if (main.nextDialogue != null || main.endAction != null) return;
        if (string.IsNullOrWhiteSpace(chat)) return;
        Dialogue chatLine = new Dialogue();
        chatLine.SetText(client.displayName, TrimLine(chat));
        chatLine.priority = main.priority;
        main.SetNextDialogue(chatLine);
    }

    private static void TryRequest(StoreClient client, int day)
    {
        if (client == null) return;
        if (string.IsNullOrEmpty(client.identifier) || string.IsNullOrEmpty(client.displayName)) return;
        if (client.isWanted) return;
        if (!TalkIntents.Contains(client.clientIntent)) return;

        // 唯一开关：没被搭过话的顾客不预生成，他的开场白永远走原版
        if (!CustomerArchive.HasTalked(client.identifier)) return;

        string key = CacheKey(client.identifier, day);
        if (Packs.ContainsKey(key)) return;
        if (_config == null || _client == null || !_client.IsOnline) return;
        if (!TryConsumePregen()) return;

        // 原版台词必须在这一刻取：注入之后 plainText 已被本模组改写
        Dialogue planned = client.mainDialogue;
        string originalLine = planned != null ? planned.plainText : null;
        Packs[key] = new SpeechPack { State = PackState.Running, Day = day, OriginalLine = originalLine };

        string systemPrompt = SpeechSystemPrompt;
        string userInput = "按资料写他今天进门的发言。";
        string contextJson = ContextBrief.Render(
            StoreContext.Build(client, day, CustomerArchive.GetTranscript(client.identifier)), "他刚走进店门。");
        string identifier = client.identifier;
        int session = ModEntry.SessionGeneration;
        _client.Chat(systemPrompt, userInput, contextJson, SpeechTail(client), (ok, reply) =>
        {
            // 读档/新档后旧请求的回包必须丢弃，否则会写进新档的发言包缓存
            if (session != ModEntry.SessionGeneration) return;
            SpeechPack pack = new SpeechPack { Day = day, OriginalLine = originalLine };
            if (ok && TryParseReply(reply, out string opening, out string chat))
            {
                pack.State = PackState.Success;
                pack.Opening = opening;
                pack.Chat = chat;
            }
            else
            {
                pack.State = PackState.Failed;
            }
            Packs[key] = pack;
        });
    }

    private static SpeechPack GetPack(string identifier, int day)
    {
        return Packs.TryGetValue(CacheKey(identifier, day), out SpeechPack pack) ? pack : null;
    }

    // 已缓存的原版台词：交互对话取语气锚点用。取不到返回 null，调用方回退到当下 plainText。
    // 熟客的开场白已被本模组改写，只有缓存里还留着游戏原句
    internal static string CachedOriginalLine(string identifier)
    {
        SpeechPack pack = GetPack(identifier, CurrentDay());
        return pack != null ? pack.OriginalLine : null;
    }

    // 清掉非当日键：缓存只在当日内存有效，跨日靠新键按需重新预生成
    private static void ClearOtherDays(int day)
    {
        foreach (KeyValuePair<string, SpeechPack> entry in Packs)
        {
            if (entry.Value == null || entry.Value.Day != day)
            {
                Packs.TryRemove(entry.Key, out _);
            }
        }
    }

    private static string CacheKey(string identifier, int day)
    {
        return identifier + "+" + day;
    }

    private static int CurrentDay()
    {
        StoreStation station = StoreStation.Instance;
        return station != null ? station.dayCounter : 0;
    }

    // 运行时读配置：设置改动后立即生效；未注入配置时回退内置默认
    private static int DailyCap => _config != null ? SpeechSettings.DailyRequestCap : DefaultMaxRequestsPerDay;
    private static int LineLength => _config != null ? SpeechSettings.LineLength : DefaultMaxLineLength;
    private static string Prompt => _config != null ? _config.SystemPrompt : DefaultSystemPrompt;

    private static string TrimLine(string text)
    {
        return TextLimit.Truncate(text.Trim(), LineLength);
    }

    // 柜台对话领额度：每次都消耗一格，到顶或上限为 0 时返回 false
    internal static bool TryConsumeRequest()
    {
        if (!TryConsume(out string reason))
        {
            if (_log != null) _log.Log("customer chat blocked: " + reason);
            return false;
        }
        return true;
    }

    // 预生成领额度：给柜台对话预留至少一格，避免一次开门就把额度吃光、玩家想聊却聊不了
    internal static bool TryConsumePregen()
    {
        int cap = DailyCap;
        if (cap <= 0) return false;
        int day = CurrentDay();
        if (_budgetDay != day)
        {
            _budgetDay = day;
            _budgetUsed = 0;
        }
        int reserved = cap > 1 ? 1 : 0;
        if (_budgetUsed >= cap - reserved) return false;
        _budgetUsed++;
        return true;
    }

    private static bool TryConsume(out string reason)
    {
        reason = null;
        int cap = DailyCap;
        if (cap <= 0)
        {
            reason = "daily request cap is 0";
            return false;
        }
        int day = CurrentDay();
        if (_budgetDay != day)
        {
            _budgetDay = day;
            _budgetUsed = 0;
        }
        if (_budgetUsed >= cap)
        {
            reason = "daily request cap reached (" + cap + ")";
            return false;
        }
        _budgetUsed++;
        return true;
    }

    internal static int RequestsUsedToday
    {
        get
        {
            int day = CurrentDay();
            return _budgetDay == day ? _budgetUsed : 0;
        }
    }

    // 内置角色指令（玩家可在「Role Instruction」中覆盖）：只描述人设与说话规矩，
    // 语言与回复格式由固定的契约追加，玩家覆盖指令不会破坏解析
    // 只留一句场景设定：说话规矩统一由 PromptBook 的 Base Instruction 承担，
    // 两层各说一遍会让玩家覆盖后出现两套说法
    internal const string DefaultSystemPrompt = "你在一款二手店模拟游戏里扮演柜台前的顾客。";

    // 提示词由 PromptBook 分块组装：角色 / 性格 / 输出形状 / 议价 / 账务 / 记忆 / 上下文说明。
    // 这里只负责把玩家可覆盖的角色指令放在最前面，条目顺序与内容都由 PromptBook 决定
    internal static string SpeechSystemPrompt =>
        Prompt + Environment.NewLine + PromptBook.SpeechPrompt();

    // 输出契约与文风作为最后一条消息发出，不混进 system；文风按这位顾客现算
    internal static string SpeechTail(StoreClient client) => PromptBook.SpeechTail(client);

    // 议价方向由游戏状态判定后传进提示词：只列出这一单可用的标记，模型不做方向选择
    internal static string InteractiveSystemPrompt(StoreClient client) =>
        PromptBook.IsInventor(client)
            ? PromptBook.Compose(PromptBook.InventorInstruction())
            : Prompt + Environment.NewLine + PromptBook.InteractivePrompt(BargainBridge.DirectionOf(client));

    internal static string InteractiveTail(StoreClient client) =>
        PromptBook.InteractiveTail(BargainBridge.DirectionOf(client), client);

    internal static int InteractiveLineLength => LineLength;

    // 交互对话上下文 = 分区上下文：往来记录已经作为 messages 逐条发出，不在这里复制第二份
    internal static string InteractiveContext(StoreClient client)
    {
        string scene = PromptBook.IsInventor(client)
            ? "玩家在杰克逊博士的工作台前跟他说话。"
            : "玩家在柜台后跟他说话。";
        return ContextBrief.Render(StoreContext.Build(client, CurrentDay()), scene);
    }

    // 顾客资格：只有来买卖的才算；通缉客与剧情拜访不算
    internal static bool IsTalkCustomer(StoreClient client)
    {
        return client != null && !client.isWanted && TalkIntents.Contains(client.clientIntent);
    }

    // 只认 JSON 的 opening/chat 两字段；解析失败返回 false，调用方静默回退原版
    private static bool TryParseReply(string reply, out string opening, out string chat)
    {
        ReplyJson.Reply parsed = ReplyJson.Parse(reply);
        opening = parsed.Opening;
        chat = parsed.Chat;
        return !string.IsNullOrWhiteSpace(opening);
    }

    // 桥接读取口：发言包状态快照，供 ProbablyBridge 验收工具只读查看
    internal static string SnapshotJson()
    {
        JArray list = new JArray();
        foreach (KeyValuePair<string, SpeechPack> pair in Packs)
        {
            SpeechPack pack = pair.Value;
            list.Add(new JObject
            {
                ["identifier"] = pair.Key,
                ["day"] = pack.Day,
                ["state"] = pack.State.ToString(),
                ["opening"] = pack.Opening,
            });
        }
        return list.ToString(Formatting.None);
    }
}
