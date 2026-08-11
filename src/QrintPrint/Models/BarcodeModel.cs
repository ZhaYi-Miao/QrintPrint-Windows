// BarcodeModel.cs
//
// 条码类型与输入约束。
//
// 原项目用 HarmonyOS ScanKit 的 scanCore.ScanType 枚举,
// Windows 上改用 ZXing.Net 的 ZXing.BarcodeFormat。
//
// 翻译自 QringPrint/entry/src/main/ets/model/BarcodeModel.ets
// 码制清单严格对照原文件的 CODE_TYPES 数组,提示文案保持一致。

using System.Text.RegularExpressions;
using BarcodeFormat = ZXing.BarcodeFormat;

namespace QrintPrint.Models;

public enum CodeCategory
{
    ONE_D = 0,
    TWO_D = 1,
}

/// <summary>条码类型描述</summary>
public readonly record struct CodeType(
    BarcodeFormat Format,
    string Label,
    CodeCategory Category,
    string Hint);

public static class BarcodeModel
{
    /// <summary>所有支持的码制,顺序与原项目一致</summary>
    public static readonly CodeType[] CodeTypes =
    {
        // ── 一维码 ──────────────────────────────────────────
        new(BarcodeFormat.EAN_13, "EAN-13", CodeCategory.ONE_D,
            "13 位纯数字(含校验位);也可只输 12 位,由生成器补校验位"),
        new(BarcodeFormat.EAN_8, "EAN-8", CodeCategory.ONE_D,
            "8 位纯数字(含校验位);也可只输 7 位"),
        new(BarcodeFormat.UPC_A, "UPC-A", CodeCategory.ONE_D,
            "12 位纯数字(含校验位);也可只输 11 位"),
        new(BarcodeFormat.UPC_E, "UPC-E", CodeCategory.ONE_D,
            "8 位纯数字,首位必须是 0 或 1;也可只输 6 位数据段"),
        new(BarcodeFormat.ITF, "ITF-14", CodeCategory.ONE_D,
            "14 位纯数字。ITF 按两位一组编码,位数必须是偶数"),
        new(BarcodeFormat.CODE_128, "Code 128", CodeCategory.ONE_D,
            "任意 ASCII 字符(0–127),长度不限。一维码里兼容性最好的选择"),
        new(BarcodeFormat.CODE_39, "Code 39", CodeCategory.ONE_D,
            "数字、大写字母 A–Z,以及 - . $ / + % 和空格"),
        new(BarcodeFormat.CODE_93, "Code 93", CodeCategory.ONE_D,
            "字符集同 Code 39,但编码更紧凑"),
        new(BarcodeFormat.CODABAR, "Codabar", CodeCategory.ONE_D,
            "数字与 - $ : / . +,首尾必须各带一个起止字符 A/B/C/D"),
        // ── 二维码 ──────────────────────────────────────────
        new(BarcodeFormat.QR_CODE, "QR Code", CodeCategory.TWO_D,
            "任意文本,最多约 2953 字节。最通用的二维码"),
        new(BarcodeFormat.DATA_MATRIX, "Data Matrix", CodeCategory.TWO_D,
            "任意文本,最多约 2335 字符。小尺寸下密度高,常用于工业标签"),
        new(BarcodeFormat.PDF_417, "PDF417", CodeCategory.TWO_D,
            "任意文本,最多约 1850 字符。横向长条形,常用于证件"),
        new(BarcodeFormat.AZTEC, "Aztec", CodeCategory.TWO_D,
            "任意文本,最多约 3067 字符。无需静区,常用于票务"),
    };

    public static List<CodeType> TypesOf(CodeCategory category)
    {
        var result = new List<CodeType>();
        foreach (var type in CodeTypes)
        {
            if (type.Category == category) result.Add(type);
        }
        return result;
    }

    // ── 输入校验 ────────────────────────────────────────────
    private static readonly Regex RE_DIGITS = new("^[0-9]+$", RegexOptions.Compiled);
    private static readonly Regex RE_CODE39 = new("^[0-9A-Z\\-. $/+%]+$", RegexOptions.Compiled);
    private static readonly Regex RE_CODABAR_BODY = new("^[0-9\\-$:/.+]+$", RegexOptions.Compiled);
    private static readonly Regex RE_ASCII = new("^[\\x00-\\x7F]+$", RegexOptions.Compiled);

    private static bool DigitsOnly(string content) => RE_DIGITS.IsMatch(content);

    /// <summary>
    /// 生成前的本地校验。返回错误信息,null 表示可以生成。
    ///
    /// 这一层是为了给出人话提示 —— 直接把不合法内容丢给生成器,
    /// 拿回来的只是一个错误码,用户看不懂哪里错了。
    /// 但它不替代生成器的校验,最终以生成结果为准。
    /// </summary>
    public static string? ValidateContent(CodeType type, string content)
    {
        if (content.Length == 0) return "请输入要生成的内容";

        switch (type.Format)
        {
            case BarcodeFormat.EAN_13:
                if (!DigitsOnly(content)) return "EAN-13 只能是纯数字";
                if (content.Length != 12 && content.Length != 13)
                    return $"EAN-13 需要 12 或 13 位数字,当前 {content.Length} 位";
                return null;

            case BarcodeFormat.EAN_8:
                if (!DigitsOnly(content)) return "EAN-8 只能是纯数字";
                if (content.Length != 7 && content.Length != 8)
                    return $"EAN-8 需要 7 或 8 位数字,当前 {content.Length} 位";
                return null;

            case BarcodeFormat.UPC_A:
                if (!DigitsOnly(content)) return "UPC-A 只能是纯数字";
                if (content.Length != 11 && content.Length != 12)
                    return $"UPC-A 需要 11 或 12 位数字,当前 {content.Length} 位";
                return null;

            case BarcodeFormat.UPC_E:
                if (!DigitsOnly(content)) return "UPC-E 只能是纯数字";
                if (content.Length != 6 && content.Length != 8)
                    return $"UPC-E 需要 6 或 8 位数字,当前 {content.Length} 位";
                if (content.Length == 8 && content[0] != '0' && content[0] != '1')
                    return "UPC-E 的 8 位形式首位必须是 0 或 1";
                return null;

            case BarcodeFormat.ITF:
                if (!DigitsOnly(content)) return "ITF-14 只能是纯数字";
                if (content.Length % 2 != 0)
                    return $"ITF 按两位一组编码,位数必须是偶数,当前 {content.Length} 位";
                if (content.Length != 14)
                    return $"ITF-14 需要 14 位数字,当前 {content.Length} 位";
                return null;

            case BarcodeFormat.CODE_39:
            case BarcodeFormat.CODE_93:
                if (!RE_CODE39.IsMatch(content))
                    return "只能包含数字、大写字母 A–Z,以及 - . $ / + % 和空格";
                return null;

            case BarcodeFormat.CODABAR:
            {
                if (content.Length < 3)
                    return "Codabar 至少需要「起始字符 + 数据 + 结束字符」三位";
                char head = char.ToUpperInvariant(content[0]);
                char tail = char.ToUpperInvariant(content[^1]);
                const string valid = "ABCD";
                if (!valid.Contains(head) || !valid.Contains(tail))
                    return "Codabar 首尾必须各带一个起止字符 A/B/C/D,例如 A1234A";
                if (!RE_CODABAR_BODY.IsMatch(content[1..^1]))
                    return "Codabar 中间部分只能是数字与 - $ : / . +";
                return null;
            }

            case BarcodeFormat.CODE_128:
                if (!RE_ASCII.IsMatch(content))
                    return "Code 128 只支持 ASCII 字符(0–127),不能含中文";
                return null;

            case BarcodeFormat.QR_CODE:
                return null;

            case BarcodeFormat.DATA_MATRIX:
                if (content.Length > 2335)
                    return $"Data Matrix 最多约 2335 字符,当前 {content.Length}";
                return null;

            case BarcodeFormat.PDF_417:
                if (content.Length > 1850)
                    return $"PDF417 最多约 1850 字符,当前 {content.Length}";
                return null;

            case BarcodeFormat.AZTEC:
                if (content.Length > 3067)
                    return $"Aztec 最多约 3067 字符,当前 {content.Length}";
                return null;

            default:
                return null;
        }
    }

    /// <summary>示例内容,方便快速试打</summary>
    public static string SampleContent(CodeType type)
    {
        return type.Format switch
        {
            BarcodeFormat.EAN_13 => "6901234567892",
            BarcodeFormat.EAN_8 => "6901234",
            BarcodeFormat.UPC_A => "012345678905",
            BarcodeFormat.UPC_E => "01234565",
            BarcodeFormat.ITF => "06901234567892",
            BarcodeFormat.CODABAR => "A12345A",
            BarcodeFormat.CODE_39 => "QRING-001",
            BarcodeFormat.CODE_93 => "QRING-001",
            _ => "https://example.com",
        };
    }
}
