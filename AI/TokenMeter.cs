using System.Text;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 端点未回报 usage 时的粗估，只给调试面板看体积涨到多大，不参与任何判断或裁剪。
// 口径：CJK 字符（含全角标点与假名/汉字）按 1 字 1 token，其余按 4 字符 1 token，
// 与常见 BPE 分词器同量级；准确值一律以端点回报的 usage 为准。
internal static class TokenMeter
{
    internal static int Estimate(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int wide = 0;
        int other = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                // 成对代理（表情等）算一个码点，别按两个字符重复计费
                wide++;
                i++;
            }
            else if (c >= 0x2E80)
            {
                wide++;
            }
            else
            {
                other++;
            }
        }
        return wide + (other + 3) / 4;
    }

    // 请求侧估算：逐条累加 messages 的 content，不拼出整串再数
    internal static int EstimateMessages(JArray messages)
    {
        if (messages == null) return 0;
        int total = 0;
        foreach (JToken message in messages)
        {
            total += Estimate((string)message["content"] ?? string.Empty);
        }
        return total;
    }

    internal static int CountContentChars(JArray messages)
    {
        if (messages == null) return 0;
        var sb = new StringBuilder();
        foreach (JToken message in messages)
        {
            sb.Append((string)message["content"] ?? string.Empty);
        }
        return sb.Length;
    }
}
