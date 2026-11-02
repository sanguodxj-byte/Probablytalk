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
            "一次一到两句，说人话",
            "不报具体价格数字，不承诺成交或让利",
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
        sample.Append(",\"credit\":\"lend\",\"memory\":\"一句话\"}");

        var lines = new StringBuilder();
        lines.Append("输出格式：只回一个 JSON 对象，不要任何别的文字。\n\n");
        lines.Append(sample);
        lines.Append("\n\ntext：一到两句，必须有。");

        if (sells || buys)
        {
            var actions = new List<string>();
            if (sells)
            {
                actions.Add("discount（卖给他的东西更便宜）");
                actions.Add("markup（卖给他的东西更贵）");
            }
            if (buys)
            {
                actions.Add("buyingDiscount（收他的东西时少付）");
                actions.Add("buyingMarkup（收他的东西时多付）");
            }
            lines.Append("\nbargain：只在谈价时加。act 取 ")
                .Append(string.Join(" 或 ", actions.ToArray()))
                .Append("；percent 是幅度百分比，不是折后价（打八折写 20），")
                .Append("单次 ").Append(min).Append("-").Append(max)
                .Append("，累计不超过 ").Append(cumulative)
                .Append("%，这单最多谈 ").Append(rounds).Append(" 轮，已谈几轮见 Negotiation。");
        }

        lines.Append("\ncredit：只在谈欠账时加，取 lend（同意赊账）或 collect（收账）。");
        lines.Append("\nmemory：每次一句，他这次透露的偏好或近况。");
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
            "{\"opening\":\"一到两句\",\"chat\":\"一到两句，与买卖无关\",\"memory\":\"一句他的偏好或近况\"}",
            "",
            "opening 与 chat 都要有，memory 一句。",
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

    internal static string InteractivePrompt(string direction)
    {
        return Compose(BaseInstruction(), InteractiveFormat(direction));
    }

    internal static string SpeechPrompt()
    {
        return Compose(BaseInstruction(), SpeechFormat());
    }
}
