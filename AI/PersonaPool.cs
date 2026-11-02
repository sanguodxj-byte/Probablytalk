using System.Collections.Generic;
using UnityEngine;

namespace Probablytalk;

// 顾客类型：决定抽哪个性格池。判据来自游戏自己的字段——tag 比 faction 更具体，优先认 tag
internal enum ClientKind
{
    Scav,
    Lower,
    Middle,
    Upper,
    BlackMarket,
    Tourist,
    Security,
    Default,
}

// 性格池：在顾客第一次进入对话管线之前，由代码按类型池加权抽一个人格原型落到档案上。
// 与 RimTalk 的做法同源（它在 Hediff_Persona 里从命名原型池 RandomElement 抽一条），
// 区别是我们按顾客类型分池给权重，让「黑市客多半冷硬」「游客多半好奇」这种倾向真的成立，
// 而不是所有人共享一个均匀池。
// 抽样用 identifier 派生的确定性随机：不需要落盘也不会读档后变脸，分布仍服从权重。
internal static class PersonaPool
{
    private sealed class Archetype
    {
        public readonly string Name;
        public readonly int Weight;
        public readonly int O;
        public readonly int C;
        public readonly int E;
        public readonly int A;
        public readonly int N;

        public Archetype(string name, int weight, int o, int c, int e, int a, int n)
        {
            Name = name;
            Weight = weight;
            O = o;
            C = c;
            E = e;
            A = a;
            N = n;
        }
    }

    // 同一类型的池子内部差异要小、跨类型差异要大：类型决定倾向，原型决定个体
    private static readonly Dictionary<ClientKind, Archetype[]> Pools = new Dictionary<ClientKind, Archetype[]>
    {
        [ClientKind.Scav] = new[]
        {
            // 捡破烂的：低开放、高尽责，只认熟货、逐件挑毛病，但不轻易发火
            new Archetype("抠门的老手", 30, 25, 80, 35, 35, 45),
            new Archetype("闷头捡货的", 30, 20, 75, 20, 45, 40),
            new Archetype("见货眼开的", 25, 45, 45, 55, 40, 55),
            new Archetype("念旧的", 15, 30, 60, 30, 60, 50),
        },
        [ClientKind.Lower] = new[]
        {
            // 下层市民：手头紧、神经质偏高，先问价、爱抱怨
            new Archetype("精打细算的", 35, 30, 70, 40, 45, 65),
            new Archetype("爱抱怨的", 30, 30, 40, 55, 35, 75),
            new Archetype("认命的", 20, 25, 55, 25, 60, 55),
            new Archetype("硬撑面子的", 15, 35, 50, 50, 30, 60),
        },
        [ClientKind.Middle] = new[]
        {
            // 中产：尽责与亲和都偏高，挑剔但讲道理、在意体面
            new Archetype("体面挑剔的", 35, 50, 70, 45, 40, 45),
            new Archetype("讲道理的", 30, 55, 65, 50, 65, 35),
            new Archetype("摆架子的", 20, 40, 55, 60, 25, 50),
            new Archetype("犹豫不决的", 15, 45, 50, 35, 55, 65),
        },
        [ClientKind.Upper] = new[]
        {
            // 上层：低亲和、低神经质，傲慢且不慌
            new Archetype("傲慢的主顾", 40, 45, 55, 60, 15, 30),
            new Archetype("讲究排场的", 30, 55, 45, 70, 25, 40),
            new Archetype("冷淡的老钱", 30, 40, 65, 35, 20, 25),
        },
        [ClientKind.BlackMarket] = new[]
        {
            // 黑市：低亲和、高尽责、低神经质，冷静算计、话里有话
            new Archetype("冷漠的精算", 35, 40, 80, 35, 15, 30),
            new Archetype("话里有话的", 30, 55, 65, 45, 20, 40),
            new Archetype("随时翻脸的", 20, 35, 45, 55, 10, 70),
            new Archetype("老练的生意人", 15, 50, 70, 60, 30, 35),
        },
        [ClientKind.Tourist] = new[]
        {
            // 游客：高开放、高外向、低神经质，少见多怪
            new Archetype("什么都新鲜的", 40, 85, 35, 75, 70, 30),
            new Archetype("爱打听的", 30, 75, 40, 85, 60, 40),
            new Archetype("小心谨慎的旅人", 20, 60, 60, 40, 55, 55),
            new Archetype("想捡便宜的", 10, 65, 45, 65, 45, 45),
        },
        [ClientKind.Security] = new[]
        {
            // 执法：低开放、高尽责，公事公办、带审视
            new Archetype("公事公办的", 50, 25, 80, 40, 35, 30),
            new Archetype("多疑的", 30, 30, 70, 30, 20, 55),
            new Archetype("看人下菜的", 20, 35, 60, 55, 25, 45),
        },
        [ClientKind.Default] = new[]
        {
            // 认不出类型：给中性池，个体差异靠原型与抖动撑开
            new Archetype("普通顾客", 40, 50, 50, 50, 50, 50),
            new Archetype("急性子", 20, 45, 40, 65, 40, 70),
            new Archetype("慢性子", 20, 45, 65, 30, 60, 30),
            new Archetype("和气生财", 20, 50, 55, 55, 75, 35),
        },
    };

    // 类型判据：tag 比 faction 更具体（同一个下层市民也可能带 SCAV），所以先看 tag
    internal static ClientKind KindOf(StoreClient client)
    {
        if (client == null) return ClientKind.Default;
        if (client.tag != null && client.tag.Contains("SCAV")) return ClientKind.Scav;
        switch (client.clientFaction)
        {
            case StoreClient.FACTION_TOURIST: return ClientKind.Tourist;
            case StoreClient.FACTION_CRIME: return ClientKind.BlackMarket;
            case StoreClient.FACTION_UPPER: return ClientKind.Upper;
            case StoreClient.FACTION_MIDDLE: return ClientKind.Middle;
            case StoreClient.FACTION_LOWER: return ClientKind.Lower;
            case StoreClient.FACTION_SECURITY: return ClientKind.Security;
        }
        if (client.tag != null && client.tag.Contains("LOWER_LEVEL_CITIZEN")) return ClientKind.Lower;
        return ClientKind.Default;
    }

    // 性子描述：把五项分数按阈值折成行为结论，直接写进资料。
    // 模型不需要看分数、也不需要知道「分高往哪端演」这种推演规则——那是代码的活
    internal static string Describe(CustomerArchive.OceanScores ocean, string archetype)
    {
        if (ocean == null) return null;
        var traits = new List<string>();
        if (ocean.Openness >= 65) traits.Add("爱听新鲜说法，对古怪货有兴趣");
        else if (ocean.Openness <= 35) traits.Add("只认熟货，嫌新花样麻烦");
        if (ocean.Conscientiousness >= 65) traits.Add("计较细节和账目");
        else if (ocean.Conscientiousness <= 35) traits.Add("马虎，随口答应");
        if (ocean.Extraversion >= 65) traits.Add("话多，会主动搭话");
        else if (ocean.Extraversion <= 35) traits.Add("寡言，只答不问");
        if (ocean.Agreeableness >= 65) traits.Add("好说话，容易让步");
        else if (ocean.Agreeableness <= 35) traits.Add("强硬，爱抬价，记仇");
        if (ocean.Neuroticism >= 65) traits.Add("容易焦虑发火，被压价就急");
        else if (ocean.Neuroticism <= 35) traits.Add("沉得住气，不当回事");
        if (traits.Count == 0) traits.Add("不温不火，平常心");
        string body = string.Join("；", traits);
        return string.IsNullOrWhiteSpace(archetype) ? body : archetype + "：" + body;
    }

    // 身份名：代码判定的唯一结论，直接写进资料，不让模型从列表里自己挑
    internal static string KindName(ClientKind kind)
    {
        switch (kind)
        {
            case ClientKind.Scav: return "捡破烂的";
            case ClientKind.Lower: return "下层市民";
            case ClientKind.Middle: return "中产顾客";
            case ClientKind.Upper: return "上层顾客";
            case ClientKind.BlackMarket: return "黑市客";
            case ClientKind.Tourist: return "游客";
            case ClientKind.Security: return "空间站安保";
            default: return null;
        }
    }

    // 抽一个人格原型：按池内权重 roll，再给五项各加减几个点，避免同类型顾客一模一样。
    // 种子取 identifier，所以同一个人在任何时候抽到的都是同一套性格，读档不会变脸
    internal static void Roll(string identifier, ClientKind kind, out string archetypeName, out CustomerArchive.OceanScores scores)
    {
        Archetype[] pool = Pools[kind];
        Rng rng = new Rng(identifier + "|" + kind);

        int total = 0;
        foreach (Archetype candidate in pool) total += candidate.Weight;
        int ticket = rng.NextInt(total);
        Archetype picked = pool[pool.Length - 1];
        foreach (Archetype candidate in pool)
        {
            ticket -= candidate.Weight;
            if (ticket < 0)
            {
                picked = candidate;
                break;
            }
        }

        archetypeName = picked.Name;
        scores = new CustomerArchive.OceanScores
        {
            Openness = Jitter(picked.O, rng),
            Conscientiousness = Jitter(picked.C, rng),
            Extraversion = Jitter(picked.E, rng),
            Agreeableness = Jitter(picked.A, rng),
            Neuroticism = Jitter(picked.N, rng),
        };
    }

    // 抖动幅度 ±7：够区分个体，不够推翻原型的倾向
    private static int Jitter(int baseValue, Rng rng)
    {
        return Mathf.Clamp(baseValue + rng.NextInt(15) - 7, 5, 95);
    }

    // xorshift：只要给定同一种子就给出同一串数，不依赖游戏 RNG 的存档种子
    private sealed class Rng
    {
        private uint _state;

        internal Rng(string seed)
        {
            uint hash = 2166136261u;
            foreach (char c in seed)
            {
                hash ^= c;
                hash *= 16777619u;
            }
            _state = hash == 0 ? 1u : hash;
        }

        private uint Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;
            return _state;
        }

        internal int NextInt(int exclusive)
        {
            if (exclusive <= 0) return 0;
            return (int)(Next() % (uint)exclusive);
        }
    }
}
