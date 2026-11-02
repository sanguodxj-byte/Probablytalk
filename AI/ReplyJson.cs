using System;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 回包解析：提示词末尾单独一节约定 JSON 形状，这里只负责把键读出来。
// 键名与代码要识别的取值一律英文（text / bargain / credit / memory，act 与 credit 也是英文标识），
// 中文只出现在台词这类给玩家看的内容里；
// 解析一律宽容——缺键、多余文字、键写成字符串，都不该让整句台词丢掉。
internal static class ReplyJson
{
    internal sealed class Reply
    {
        public string Line;
        public string Action;
        public int Percent;
        public string Credit;
        public string Memory;
        public string Opening;
        public string Chat;
        public string Summary;
        public string Stance;
    }

    internal static Reply Parse(string text)
    {
        var reply = new Reply();
        if (string.IsNullOrWhiteSpace(text)) return reply;

        string source = text.Replace("```", string.Empty).Trim();
        int start = source.IndexOf('{');
        int end = source.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            // 模型没写成 JSON：整段当台词，宁可少一次动作也不要吞掉他说的话
            reply.Line = source;
            return reply;
        }

        JObject obj;
        try
        {
            obj = JObject.Parse(source.Substring(start, end - start + 1));
        }
        catch (Exception)
        {
            reply.Line = source;
            return reply;
        }

        reply.Line = Text(obj, "text");
        reply.Credit = Text(obj, "credit");
        reply.Memory = Text(obj, "memory");
        reply.Opening = Text(obj, "opening");
        reply.Chat = Text(obj, "chat");
        reply.Summary = Text(obj, "summary");
        reply.Stance = Text(obj, "stance");

        JToken price = obj["bargain"];
        JObject structured = price as JObject;
        if (structured != null)
        {
            reply.Action = Text(structured, "act");
            reply.Percent = Number(structured.Value<string>("percent"));
        }
        else if (price != null && price.Type == JTokenType.String)
        {
            // 也有人把动作和幅度挤成一句话写：拆开即可，不算判断
            string packed = (string)price;
            reply.Action = FirstWord(packed);
            reply.Percent = Number(packed);
        }
        return reply;
    }

    private static string Text(JObject obj, string key)
    {
        string value = obj.Value<string>(key);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // 幅度只取数字：写「20%」「两成」以外的花活一律不认，宁可这次不动价
    private static int Number(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        int result = 0;
        bool found = false;
        foreach (char c in value)
        {
            if (c >= '0' && c <= '9')
            {
                result = result * 10 + (c - '0');
                found = true;
            }
            else if (found)
            {
                break;
            }
        }
        return found ? result : 0;
    }

    private static string FirstWord(string value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        int space = value.IndexOf(' ');
        return (space > 0 ? value.Substring(0, space) : value).Trim();
    }
}
