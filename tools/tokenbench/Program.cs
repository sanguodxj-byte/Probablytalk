using System;
using Newtonsoft.Json.Linq;
using Probablytalk;

// 离线台：链接 mod 里同一份 AI/TokenMeter.cs 跑合成夹具，期望值全部由人工按口径手算，
// 不从被测代码反推，否则等于用犯错的假设给自己盖章。
internal static class Program
{
    private static int failures;

    private static void Check(string name, int actual, int expected)
    {
        bool ok = actual == expected;
        if (!ok) failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}: {actual} (手算期望 {expected})");
    }

    private static int Main()
    {
        // 口径：CJK（含全角标点）一字一 token，其余 4 字符一 token 向上取整
        Check("纯中文四字", TokenMeter.Estimate("二手店里"), 4);
        Check("hello world=11 字符", TokenMeter.Estimate("hello world"), 3);
        Check("空串", TokenMeter.Estimate(string.Empty), 0);
        Check("null", TokenMeter.Estimate(null), 0);
        Check("单个表情=一个码点", TokenMeter.Estimate("🙂"), 1);
        Check("两个表情", TokenMeter.Estimate("🙂🙂"), 2);
        Check("顾客：你好=4 字+1 全角冒号", TokenMeter.Estimate("顾客：你好"), 5);
        Check("Identity: 中层职员=10 ascii+4 cjk", TokenMeter.Estimate("Identity: 中层职员"), 3 + 4);
        Check("Day: 12 | order 60=18 ascii", TokenMeter.Estimate("Day: 12 | order 60"), 5);

        var messages = new JArray
        {
            new JObject { ["role"] = "system", ["content"] = "你在二手店模拟游戏里扮演顾客" },
            new JObject { ["role"] = "user", ["content"] = "Day: 12 | order 60" },
            new JObject { ["role"] = "user", ["content"] = "按资料写他今天进门的发言。" },
        };
        // 逐条累加：14 cjk + 5(18 ascii) + 13 cjk（末条 12 字加一个句号）
        Check("三条消息累加", TokenMeter.EstimateMessages(messages), 14 + 5 + 13);
        Check("字符数对账=14+18+13", TokenMeter.CountContentChars(messages), 45);
        Check("空数组", TokenMeter.EstimateMessages(new JArray()), 0);
        Check("缺 content 的消息", TokenMeter.EstimateMessages(new JArray { new JObject() }), 0);

        // 观测用，不判定：一条真实形状的柜台上下文大约多少 token
        var probe = new JArray
        {
            new JObject
            {
                ["role"] = "system",
                ["content"] = "你在为二手店模拟游戏扮演柜台前的顾客\n只写顾客说出口的话，不写旁白、不写神态动作\n一次一到两句，说人话",
            },
            new JObject
            {
                ["role"] = "user",
                ["content"] = "Day: 12 | order 60 | traffic 44 | happiness 51 | gutter water 12.5%\r\n"
                    + "Identity: 中层职员 | Intent: 来买货 | Budget: 820 | cash 820\r\n"
                    + "Counter: 压缩食品 ×6 (value 30; food); 电池 ×3 (value 12; tech)\r\n"
                    + "Negotiation: he is buying from you | rounds 1/3 | percent 1-90\r\n"
                    + "Dialogue:\r\n- player: 这批货你要不要，我可以让一点\r\n- customer: 让多少先说个数",
            },
            new JObject { ["role"] = "user", ["content"] = "他刚走进店门。" },
        };
        Console.WriteLine($"观测：一条三层消息约 {TokenMeter.EstimateMessages(probe)} token / "
            + $"{TokenMeter.CountContentChars(probe)} 字符");

        Console.WriteLine(failures == 0 ? "全部通过" : failures + " 项不通过");
        return failures == 0 ? 0 : 1;
    }
}
