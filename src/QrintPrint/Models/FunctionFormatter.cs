using System.Text;
using System.Text.RegularExpressions;

namespace QrintPrint.Models;

/// <summary>
/// 把用户输入的表达式规范化为"数学排版"文本用于显示:
/// x^2 → x²、x^3 → x³、pi → π、sqrt( → √(、* → ·、/ → ÷。
/// 仅做文本层美化,不改变语义。
/// </summary>
public static class FunctionFormatter
{
    private static readonly Regex Power2 = new(@"\^2(?!\d)", RegexOptions.Compiled);
    private static readonly Regex Power3 = new(@"\^3(?!\d)", RegexOptions.Compiled);
    private static readonly Regex PiWord = new(@"\bpi\b", RegexOptions.Compiled);

    /// <summary>返回规范化显示文本;若输入为空返回空串</summary>
    public static string Format(string? expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return string.Empty;
        string s = expr.Trim();

        var sb = new StringBuilder(s);
        // 先把 Unicode 已有字符保留,统一替换可识别的 ASCII 写法
        sb.Replace("sqrt(", "√(");
        sb.Replace('*', '·');
        sb.Replace('/', '÷');

        string text = sb.ToString();
        text = Power2.Replace(text, "²");
        text = Power3.Replace(text, "³");
        text = PiWord.Replace(text, "π");
        text = ReplaceAbs(text);

        return text;
    }

    /// <summary>把 abs(...) 替换为 |...|,支持嵌套(从外向内递归处理)</summary>
    private static string ReplaceAbs(string s)
    {
        int i = s.IndexOf("abs(", StringComparison.Ordinal);
        if (i < 0) return s;
        int depth = 0;
        int j = i + 3; // 指向 '('
        for (; j < s.Length; j++)
        {
            if (s[j] == '(') depth++;
            else if (s[j] == ')') { depth--; if (depth == 0) break; }
        }
        if (j >= s.Length) return s; // 括号不匹配,保留原样
        string inner = s[(i + 4)..j];
        inner = ReplaceAbs(inner);
        string replaced = s[..i] + "|" + inner + "|" + s[(j + 1)..];
        return ReplaceAbs(replaced);
    }
}