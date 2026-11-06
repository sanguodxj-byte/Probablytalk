using System;
using System.Collections.Generic;
using System.Text;

namespace Probablytalk;

// 文风：只管"这句怎么说"——用词域、态度、说多说少。不管"说什么"，
// 所以不收任何剧情演绎类的东西（场景节奏、环境与感官细描、旁白与动作描写），
// 那些是多轮共写预设的轴，套进来只会把一句柜台对答写成叙述段。
//
// 默认走 auto：按 职业(ClienteKind) + 好感(affinity) + 外向性(Ocean.Extraversion) 现拼一条。
// 这三样都是模组已经算好的既有事实，文风只是把它们落到措辞上；
// 性格里其余四项（开放/尽责/宜人/神经质）留在 PersonaPool 管"他要什么、怎么判断"，
// 外向性移到这里管"他说多少"，一处一件事，不重复下发。
//
// 注入位置：方括号一条，压在最后一条消息的末尾（越靠后影响越大），
// 且与玩家那句话同属这一轮，不单独占一条消息——单开一轮时模型最后读到的是指令本身，
// 会去回答指令卡而不是回对方的话，产出一句不接任何刺激却满足格式的台词。
internal static class StyleBook
{
    internal sealed class Pack
    {
        public readonly string Key;
        public readonly string Label;
        public readonly string Directive;

        internal Pack(string key, string label, string directive)
        {
            Key = key;
            Label = label;
            Directive = directive;
        }
    }

    internal const string AutoKey = "auto";
    internal const string DefaultKey = AutoKey;

    // 手工档：留着做对照与覆盖用（auto 之外才生效）
    internal static readonly Pack[] Packs =
    {
        new Pack(AutoKey, "自动（按职业与好感）", string.Empty),
        new Pack("street", "街头口语",
            "按街面上的口气说：短句、口语、想到哪说到哪；不用书面词，不用翻译腔，不复述资料里的形容词"),
        new Pack("plain", "简短直白",
            "能短就短，一个词能答完就不说两个；不解释、不安抚、不总结自己刚说了什么"),
        new Pack("custom", "自定义", string.Empty),
    };

    // 职业 → 用词域。措辞取自游戏自己的顾客固定台词，归纳见
    // codewiki/sources/customer-lines.summary.zh.md（语料 1085 条，missing=0）
    private static readonly Dictionary<ClientKind, string> KindDomain = new Dictionary<ClientKind, string>
    {
        { ClientKind.Scav, "先说来路再说货：从哪儿捡的、成色怎样、整批要不要；客气但不装粗" },
        { ClientKind.Lower, "先说要什么或缺什么，带一句自己的难处，不绕弯" },
        { ClientKind.Middle, "先报身份或用途，再讲条件和折扣；同行相称，不套近乎" },
        { ClientKind.Upper, "句子整齐带地位感；敏感事用省略绕开，不解释自己是谁" },
        { ClientKind.BlackMarket, "点到为止：只说有没有、值多少，不带来路，也不解释" },
        { ClientKind.Tourist, "好奇外行：多问，拿别处见闻作比，语气轻快" },
        { ClientKind.Security, "公事口吻：短句、命令式，只问事实，不带称呼" },
        { ClientKind.Default, "平常口气：直接说买什么或卖什么" },
    };

    internal static Pack Find(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return Packs[0];
        string wanted = key.Trim();
        foreach (Pack pack in Packs)
        {
            if (string.Equals(pack.Key, wanted, StringComparison.OrdinalIgnoreCase)) return pack;
        }
        return Packs[0];
    }

    internal static string NextKey(string key)
    {
        for (int i = 0; i < Packs.Length; i++)
        {
            if (string.Equals(Packs[i].Key, (key ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
                return Packs[(i + 1) % Packs.Length].Key;
        }
        return Packs[0].Key;
    }

    // 好感 → 态度与称呼松紧
    private static string AffinityClause(int affinity)
    {
        if (affinity >= 70) return "当自己是老主顾：可以先提上次的事或开个玩笑，句子长一点也无妨";
        if (affinity >= 40) return "就事论事：不带称呼，也不冷场";
        return "生分：短句，不带称呼，不接闲话，答完就想走";
    }

    // 外向性 → 说多少。这条从 PersonaPool 移过来，那边不再写，避免两处复写
    private static string VerbosityClause(int extraversion)
    {
        if (extraversion >= 65) return "话头可以往外多抛一句";
        if (extraversion <= 35) return "一句顶一句，不多半句";
        return null;
    }

    // auto 档的成品：职业域 + 好感态度 + 简繁
    internal static string Auto(ClientKind kind, int affinity, int extraversion)
    {
        var parts = new List<string>();
        string domain;
        if (KindDomain.TryGetValue(kind, out domain) && !string.IsNullOrWhiteSpace(domain)) parts.Add(domain);
        string temper = AffinityClause(affinity);
        if (!string.IsNullOrWhiteSpace(temper)) parts.Add(temper);
        string verbosity = VerbosityClause(extraversion);
        if (!string.IsNullOrWhiteSpace(verbosity)) parts.Add(verbosity);
        return string.Join("；", parts.ToArray());
    }

    internal static string Keys()
    {
        var list = new List<string>();
        foreach (Pack pack in Packs) list.Add(pack.Key);
        return string.Join(", ", list.ToArray());
    }

    // 渲染成一条方括号指令；没内容就不注入，别在提示词里挂空括号
    internal static string Render(string key, string custom, ClientKind kind, int affinity, int extraversion)
    {
        Pack pack = Find(key);
        string body;
        if (pack.Key == AutoKey) body = Auto(kind, affinity, extraversion);
        else if (pack.Key == "custom") body = (custom ?? string.Empty).Trim();
        else body = pack.Directive;
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        return "[" + body + "]";
    }

    // 兼容旧签名（桥与设置窗列档时用），不带顾客时按默认口径
    internal static string Render(string key, string custom)
    {
        return Render(key, custom, ClientKind.Default, 50, 50);
    }
}
