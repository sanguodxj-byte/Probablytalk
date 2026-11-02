namespace Probablytalk;

// 定长截断：切点前一字符为高代理时整体回退一位，输出不以孤立代理项结尾
// （模型回包可能含非 BMP 字符，按 char 数硬切会产生残缺代理对）
internal static class TextLimit
{
    public static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        int cut = maxLength;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text.Substring(0, cut);
    }
}
