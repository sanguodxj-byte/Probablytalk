using System;
using System.Collections.Generic;
using System.Text;

namespace Probablytalk;

// 一条提示词条目：有 id、有文本。
internal sealed class PromptEntry
{
    public string Id;
    public string Text;

    public PromptEntry(string id, string text)
    {
        Id = id;
        Text = text;
    }
}

// 提示词手册：结构照 RimTalk 的三段式——角色与规则、输出格式、资料（由 ContextBrief 以 profile 行给出）。
// 确定性内容（身份、性子、买卖方向、价格门槛）一律由代码判定后落进资料或按方向裁剪，
// 提示词里不给选项、不让模型挑，也不解释「资料是什么」。
internal static class PromptBook
{
    // 台词长度上限：只在这一处定义，渲染进两条输出契约（不复写第二份数字，
    // 两处写同一个数就会互相打架）。放在 tail 里是因为格式契约要贴着生成点、
    // 压在成坨资料前面会被模型忽略。
    internal const int MaxLineChars = 50;

    // 语言跟随游戏当前语言：避免玩家切成英文后模型仍说中文
    internal static string LanguageName()
    {
        string code = LocHelper.GetCurrentLocaleCode();
        if (string.IsNullOrEmpty(code)) return "简体中文";
        if (code.StartsWith("zh")) return "简体中文";
        if (code.StartsWith("en")) return "English";
        return code;
    }

    internal static PromptEntry BaseInstruction()
    {
        return new PromptEntry("base", string.Join("\n", new[]
        {
            "你在为二手店模拟游戏扮演柜台前的顾客",
            "",
            "只写顾客说出口的话，不写旁白、不写神态动作",
            "语言固定用" + LanguageName() + "，人名与物品名保留原文不翻译",
            "一次一到两句",
            "不报具体价格数字，不承诺成交或让利",
            "资料里写他看穿的门道与已经发生的对质，都是既成事实：只能接着演，不能否认、撤回或阻止",
        }));
    }

    // 柜台对话的输出格式：按买卖方向裁剪，只列这一单用得上的动作
    internal static PromptEntry InteractiveFormat(string direction)
    {
        int min = Math.Min(BargainBridge.MinPercent, BargainBridge.MaxPercent);
        int max = BargainBridge.MaxPercent;
        int cumulative = BargainBridge.MaxCumulativePercent;
        int rounds = BargainBridge.MaxRoundsPerClient;

        bool sells = direction == "playerSells" || direction == "both";
        bool buys = direction == "playerBuys" || direction == "both";

        var sample = new StringBuilder("{\"text\":\"顾客说出口的话\"");
        if (sells || buys)
        {
            sample.Append(",\"bargain\":{\"act\":\"")
                .Append(sells ? "discount" : "buyingDiscount")
                .Append("\",\"percent\":20}");
        }
        sample.Append(",\"credit\":\"lend\"}");

        var lines = new StringBuilder();
        lines.Append("输出格式：只回一个 JSON 对象，不要任何别的文字。\n\n");
        lines.Append(sample);
        lines.Append("\n\ntext：一到两句、不超过 " + MaxLineChars + " 字，必须有。");

        if (sells || buys)
        {
            var actions = new List<string>();
            if (sells)
            {
                actions.Add("discount（玩家卖货给你时降价，你少付）");
                actions.Add("markup（玩家卖货给你时抬价，你多付）");
            }
            if (buys)
            {
                actions.Add("buyingDiscount（你卖货给玩家时他少付，你少收）");
                actions.Add("buyingMarkup（你卖货给玩家时他多付，你多收）");
            }
            lines.Append("\nbargain：只在谈价时加。act 取 ")
                .Append(string.Join(" 或 ", actions.ToArray()))
                .Append("；percent 是幅度百分比，不是折后价（打八折写 20），")
                .Append("单次 ").Append(min).Append("-").Append(max)
                .Append("，累计不超过 ").Append(cumulative)
                .Append("%，这单最多谈 ").Append(rounds).Append(" 轮，已谈几轮见 Negotiation。");
        }

        lines.Append("\ncredit：只在谈欠账时加，取 lend（同意赊账）。");
        lines.Append("\nmove：只在你决定不接受当前条件、转身离开时加，取值 refuse。");
        lines.Append("\nmove 与 bargain 不要同时出现。");
        lines.Append("\n玩家这句话没有明确要求改价、赊账或成交时，不要带 bargain 或 credit。");
        lines.Append("\n用不到的键省掉。");

        return new PromptEntry("format", lines.ToString());
    }

    // 熟客开场白的输出格式
    internal static PromptEntry SpeechFormat()
    {
        return new PromptEntry("speech_format", string.Join("\n", new[]
        {
            "输出格式：只回一个 JSON 对象，不要任何别的文字。",
            "",
            "{\"opening\":\"一到两句\",\"chat\":\"一到两句，与买卖无关\"}",
            "",
            "opening 与 chat 都要有，各自不超过 " + MaxLineChars + " 字。",
            "Original line 是他平时的口吻，接近但别照抄；从 His goods / Counter / Events 里挑一处点题，不要罗列。",
        }));
    }

    internal static string Compose(params PromptEntry[] entries)
    {
        var builder = new StringBuilder();
        foreach (PromptEntry entry in entries)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.Text)) continue;
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(entry.Text);
        }
        return builder.ToString();
    }

    // 输出格式不进 system，而是作为最后一条消息单独发出（见 Player2Client.BuildMessages 的 tail）：
    // 压在成坨的场景资料前面时，模型读到的是资料、生成时想不起格式，回包经常是散文而非 JSON
    internal static string InteractivePrompt(string direction)
    {
        return Compose(BaseInstruction());
    }

    internal static string SpeechPrompt()
    {
        return Compose(BaseInstruction());
    }

    // 常客离店时的一次性好感度评估：独立段落，不参与任何角色扮演提示词。
    // 输入只有当前好感度、本次对话与交易事实；输出只允许 delta 与 reason，
    // reason 仅进日志，不回灌给顾客，避免模型拿自己的评价当台词素材。
    internal static string AffinityReviewPrompt()
    {
        return string.Join("\n", new[]
        {
            "你是本店顾客关系评估器",
            "输入包含：该顾客当前好感度（0-100）、本次到店玩家与顾客的对话记录、本次交易事实（是否成交、议价被接受或被拒绝、是否赊账）",
            "你只评估这些行为让顾客对店主的观感变化，输出一个整数增量",
            "1. 只输出 JSON：{\"delta\": 整数, \"reason\": \"短语\"}",
            "2. delta 取 -20 到 20 的整数",
            "3. 正面：被尊重、被公平对待、交易顺利、被照顾",
            "4. 负面：被欺骗、被压价过狠、被无视、被当面对质、赊账不还",
            "5. 没有明显情绪线索时输出 0",
            "6. 不要复述对话，不要输出 Markdown 或计算过程",
            "7. reason 不超过 15 个字，只用于日志，不会回灌给顾客",
        });
    }

    // 文风指令：按柜前这位的职业、好感与外向性现拼（StyleBook 的 auto 档），
    // 手工档与自定义档仍照设置原样下发
    internal static PromptEntry StyleDirective(StoreClient client)
    {
        if (client == null) return null;
        ClientKind kind = PersonaPool.KindOf(client);
        int affinity = CustomerArchive.AffinityOf(client.identifier);
        CustomerArchive.OceanScores ocean = CustomerArchive.OceanOf(client.identifier);
        int extraversion = ocean != null ? ocean.Extraversion : 50;
        string text = StyleBook.Render(SpeechSettings.StylePack, SpeechSettings.StyleCustom,
            kind, affinity, extraversion);
        return string.IsNullOrEmpty(text) ? null : new PromptEntry("style", text);
    }

    // tail 的组合顺序：文风在前、输出格式最后。两者都跟着玩家那句话进同一条消息，
    // 不再单开一轮——单开一轮时模型最后读到的是指令，就会去回答指令卡而不是回玩家的话
    // 博士（发明家）不是普通顾客：固定人设，不参与议价与赊账话术
    internal static bool IsInventor(StoreClient client)
    {
        if (client == null || string.IsNullOrEmpty(client.identifier)) return false;
        return client.identifier == "inventor" || client.identifier == "inventorStorage";
    }

    internal static PromptEntry InventorInstruction()
    {
        return new PromptEntry("inventor", string.Join("\n", new[]
        {
            "你是杰克逊博士（Dr. Jackson），在柜台后接待店主",
            "说话冷淡、精确，技术口吻，不寒暄，不客套",
            "你卖升级模块与发明家库存，也收材料",
            "只写你说出口的话，不写旁白与动作",
            "语言固定用" + LanguageName() + "，人名与物品名保留原文",
            "一次一到两句，不报具体价格数字，不承诺成交",
        }));
    }

    internal static PromptEntry InventorFormat()
    {
        return new PromptEntry("inventor_format", string.Join("\n", new[]
        {
            "输出格式：只回一个 JSON 对象，不要任何别的文字。",
            "",
            "{\"text\":\"你的一句话\"}",
            "",
            "text 必须有，一到两句；不带 bargain、credit、move。",
        }));
    }

    internal static string InteractiveTail(string direction, StoreClient client)
    {
        // 博士是固定人设（InventorInstruction 已定死说话方式），
        // StyleDirective 按职业池现拼的文风会跟"冷淡、精确、不寒暄"打架
        if (IsInventor(client)) return Compose(InventorFormat());
        return Compose(StyleDirective(client), InteractiveFormat(direction));
    }

    internal static string SpeechTail(StoreClient client)
    {
        return Compose(StyleDirective(client), SpeechFormat());
    }
}
