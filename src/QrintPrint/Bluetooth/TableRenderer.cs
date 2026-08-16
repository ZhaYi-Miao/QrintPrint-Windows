// TableRenderer.cs
//
// 表格元素渲染：把行列数据排版成位图（白底 + 黑网格线 + 单元格文本）。
// 用 WPF DrawingVisual 离屏渲染（与 RasterEncoder.RenderTextToImageIn 同思路），
// 输出 ImageSharp 位图，后续走灰度 → 二值化管线。
//
// 数据格式：TableData 为逗号分隔的文本，\n 分行，例如：
//   "科目,成绩\n语文,92\n数学,95"
// 列宽权重：逗号分隔正数，如 "20,30,10"，留空自动均分。

using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using WpfPoint = System.Windows.Point;
using SixLabors.ImageSharp.PixelFormats;

namespace QrintPrint.Bluetooth;

public static class TableRenderer
{
    private const double CellPadding = 3; // 单元格内边距（像素）

    /// <summary>
    /// 表格数据 → 位图。
    /// boxWidth 为目标宽度（点），行高按各单元格文本高度自适应，总高度由内容决定。
    /// </summary>
    public static Image<Rgba32> RenderTableToImage(
        string data, int rows, int cols, string colWeights,
        int fontSize, int boxWidth)
    {
        rows = Math.Max(1, rows);
        cols = Math.Max(1, cols);
        int width = Math.Max(20, boxWidth);

        var cells = ParseTable(data, rows, cols);
        var weights = ParseWeights(colWeights, cols);
        double totalWeight = 0;
        foreach (var w in weights) totalWeight += w;
        if (totalWeight <= 0) totalWeight = cols;
        var colWidths = new double[cols];
        for (int c = 0; c < cols; c++)
        {
            colWidths[c] = weights[c] > 0 ? width * weights[c] / totalWeight : width / (double)cols;
        }

        var typeface = new Typeface(
            SystemFonts.MessageFontFamily,
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        // 先量每行高度（该行所有单元格文本的最大高度 + 内边距）
        var rowHeights = new double[rows];
        for (int r = 0; r < rows; r++)
        {
            double maxH = fontSize;
            for (int c = 0; c < cols; c++)
            {
                if (string.IsNullOrEmpty(cells[r][c])) continue;
                var ft = new FormattedText(
                    cells[r][c], CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, fontSize, Brushes.Black, 1.0);
                if (ft.Height > maxH) maxH = ft.Height;
            }
            rowHeights[r] = Math.Ceiling(maxH) + CellPadding * 2;
        }

        double totalH = 0;
        foreach (var h in rowHeights) totalH += h;
        int height = Math.Max(1, (int)Math.Ceiling(totalH) + 1);

        // 离屏渲染
        var dv = new DrawingVisual();
        var pen = new Pen(Brushes.Black, 1);
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));

            double y = 0;
            for (int r = 0; r < rows; r++)
            {
                double x = 0;
                for (int c = 0; c < cols; c++)
                {
                    string text = cells[r][c];
                    if (!string.IsNullOrEmpty(text))
                    {
                        var ft = new FormattedText(
                            text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                            typeface, fontSize, Brushes.Black, 1.0);
                        // 单元格文本：左对齐，垂直居中
                        double ty = y + Math.Max(0, (rowHeights[r] - ft.Height) / 2);
                        dc.DrawText(ft, new WpfPoint(x + CellPadding, ty));
                    }
                    x += colWidths[c];
                }
                y += rowHeights[r];
                // 行底线
                dc.DrawLine(pen, new WpfPoint(0, y), new WpfPoint(width, y));
            }
            // 列线（含外框竖线）
            double cx = 0;
            for (int c = 0; c < cols; c++)
            {
                cx += colWidths[c];
                dc.DrawLine(pen, new WpfPoint(cx, 0), new WpfPoint(cx, height));
            }
            // 外框横线（顶/底）
            dc.DrawLine(pen, new WpfPoint(0, 0), new WpfPoint(width, 0));
            dc.DrawLine(pen, new WpfPoint(0, height - 1), new WpfPoint(width, height - 1));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var pixels = new byte[width * height * 4];
        rtb.CopyPixels(pixels, width * 4, 0);

        var img = new Image<Rgba32>(width, height);
        img.ProcessPixelRows(accessor =>
        {
            for (int yy = 0; yy < height; yy++)
            {
                var row = accessor.GetRowSpan(yy);
                int srcBase = yy * width * 4;
                for (int xx = 0; xx < width; xx++)
                {
                    int p = srcBase + xx * 4;
                    row[xx] = new Rgba32(pixels[p + 2], pixels[p + 1], pixels[p], pixels[p + 3]);
                }
            }
        });
        return img;
    }

    /// <summary>解析表格数据：\n 分行、逗号分列（支持 CSV 引号转义）。不足的行/列补空串</summary>
    public static string[][] ParseTable(string data, int rows, int cols)
    {
        var result = new string[rows][];
        var lines = data.Replace("\r", "").Split('\n');
        for (int r = 0; r < rows; r++)
        {
            result[r] = new string[cols];
            string line = r < lines.Length ? lines[r] : "";
            var parts = ParseCsvLine(line);
            for (int c = 0; c < cols; c++)
            {
                result[r][c] = c < parts.Length ? parts[c].Trim() : "";
            }
        }
        return result;
    }

    /// <summary>把一行单元格拼成 CSV 行（含逗号/引号/换行的单元格用引号包裹并转义）</summary>
    public static string BuildCsvLine(IReadOnlyList<string> cells)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < cells.Count; i++)
        {
            if (i > 0) sb.Append(',');
            string v = cells[i];
            if (v.Contains(',') || v.Contains('"') || v.Contains('\n'))
            {
                sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
            }
            else
            {
                sb.Append(v);
            }
        }
        return sb.ToString();
    }

    /// <summary>解析一行 CSV（支持引号包裹的字段与 "" 转义）</summary>
    private static string[] ParseCsvLine(string line)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        cur.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cur.Append(ch);
                }
            }
            else
            {
                if (ch == '"')
                {
                    inQuotes = true;
                }
                else if (ch == ',')
                {
                    result.Add(cur.ToString());
                    cur.Clear();
                }
                else
                {
                    cur.Append(ch);
                }
            }
        }
        result.Add(cur.ToString());
        return result.ToArray();
    }

    /// <summary>解析列宽权重（逗号分隔正数）。非法/不足补 0，空串全部 0（调用方按均分处理）</summary>
    private static double[] ParseWeights(string colWeights, int cols)
    {
        var result = new double[cols];
        if (string.IsNullOrWhiteSpace(colWeights)) return result;
        var parts = colWeights.Split(',');
        for (int c = 0; c < cols && c < parts.Length; c++)
        {
            if (double.TryParse(parts[c].Trim(), out double w) && w > 0)
            {
                result[c] = w;
            }
        }
        return result;
    }
}
