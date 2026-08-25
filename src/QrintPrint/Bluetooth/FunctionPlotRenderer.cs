using System.Globalization;
using QrintPrint.Models;

namespace QrintPrint.Bluetooth;

/// <summary>图上标记点(名称 + 坐标)</summary>
public sealed class PlotPoint
{
    public string Name { get; init; } = "";
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>渲染后的坐标映射(用于 UI 点击反算世界坐标)</summary>
public readonly struct PlotMapping
{
    public double XMin { get; init; }
    public double XMax { get; init; }
    public double YMin { get; init; }
    public double YMax { get; init; }
    public int PlotX { get; init; }
    public int PlotY { get; init; }
    public int PlotW { get; init; }
    public int PlotH { get; init; }
}

/// <summary>函数图像打印参数（桌面页与 HTTP 接口共用）</summary>
public sealed class FunctionPlotOptions
{
    public IReadOnlyList<string> Expressions { get; init; } = Array.Empty<string>();
    public double XMin { get; init; } = -10;
    public double XMax { get; init; } = 10;
    /// <summary>null 表示自动计算 Y 范围</summary>
    public double? YMin { get; init; }
    public double? YMax { get; init; }
    public bool ShowGrid { get; init; } = true;
    public bool ShowLegend { get; init; } = true;
    /// <summary>是否绘制坐标轴（x=0 / y=0 轴）</summary>
    public bool ShowAxes { get; init; } = true;
    /// <summary>是否绘制坐标轴刻度数值（x 底边条、y 左侧边条）</summary>
    public bool ShowAxisLabels { get; init; } = true;
    public string? Title { get; init; }
    /// <summary>图上标记点(点击曲线生成,打印时保留)</summary>
    public IReadOnlyList<PlotPoint>? Points { get; init; }
}

/// <summary>
/// 函数图像渲染器：把多个函数表达式采样绘制到 384 点宽的二值画布上。
/// 曲线按顺序交替画实线/虚线以便区分；坐标轴为实线、网格为虚线；Y 轴范围可自动计算。
/// </summary>
public static class FunctionPlotRenderer
{
    private const int MARGIN = 8;
    private const int PLOT_HEIGHT = 170;
    private const int TITLE_FONT = 13;
    private const int LEGEND_FONT = 11;

    /// <summary>渲染为二值画布；失败时 Error 非空、Canvas 为 null</summary>
    public static (byte[]? Canvas, int Width, int Height, string? Error) Render(FunctionPlotOptions opt)
    {
        var (canvas, w, h, error, _) = RenderWithMapping(opt);
        return (canvas, w, h, error);
    }

    /// <summary>渲染并返回坐标映射(供 UI 点击反算世界坐标)</summary>
    public static (byte[]? Canvas, int Width, int Height, string? Error, PlotMapping Mapping) RenderWithMapping(FunctionPlotOptions opt)
    {
        if (opt is null || opt.Expressions is null || opt.Expressions.Count == 0)
            return (null, 0, 0, "至少需要一个函数表达式", default);

        if (!double.IsFinite(opt.XMin) || !double.IsFinite(opt.XMax) || opt.XMax <= opt.XMin)
            return (null, 0, 0, "X 范围无效（需 xMax 大于 xMin）", default);

        // 编译全部表达式,任一失败即整体报错
        var funcs = new List<Func<double, double>>();
        for (int i = 0; i < opt.Expressions.Count; i++)
        {
            string expr = opt.Expressions[i].Trim();
            if (string.IsNullOrEmpty(expr)) continue;
            if (!FunctionEvaluator.TryCompile(expr, out var fn, out string? err))
                return (null, 0, 0, $"第 {i + 1} 个函数“{expr}”语法错误：{err}", default);
            funcs.Add(fn!);
        }
        if (funcs.Count == 0)
            return (null, 0, 0, "至少需要一个函数表达式", default);

        bool showTicks = opt.ShowAxisLabels;
        int marginLeft = showTicks ? 26 : MARGIN;

        // 顶部标题/图例先行渲染,以便确定画布总高
        int width = QringProtocol.WIDTH_DOTS;
        int textMaxW = width - marginLeft - MARGIN;
        var headerBars = new List<(byte[] Bin, int W, int H)>();
        if (!string.IsNullOrWhiteSpace(opt.Title))
        {
            var bin = RenderTextLine(opt.Title.Trim(), TITLE_FONT, textMaxW);
            if (bin is not null) headerBars.Add(bin.Value);
        }
        if (opt.ShowLegend)
        {
            for (int i = 0; i < funcs.Count; i++)
            {
                string expr = opt.Expressions[i].Trim();
                var item = RenderTextLine($"f{i + 1}(x) = {expr}", LEGEND_FONT, textMaxW);
                if (item is not null) headerBars.Add(item.Value);
            }
        }
        // 标记点坐标列入图例区
        if (opt.Points is not null && opt.Points.Count > 0)
        {
            foreach (var p in opt.Points)
            {
                string label = $"{p.Name}({p.X.ToString("0.##", CultureInfo.InvariantCulture)}, {p.Y.ToString("0.##", CultureInfo.InvariantCulture)})";
                var item = RenderTextLine(label, LEGEND_FONT, textMaxW);
                if (item is not null) headerBars.Add(item.Value);
            }
        }

        int headerH = 0;
        foreach (var (_, _, h) in headerBars) headerH += h + 1;
        if (headerBars.Count > 0) headerH -= 1;

        int plotW = width - marginLeft - MARGIN;
        int plotY = MARGIN + headerH + 4;
        int tickH = showTicks ? 12 : 0;
        int canvasH = plotY + PLOT_HEIGHT + MARGIN + tickH;
        var canvas = Compositor.CreateBinaryCanvas(width, canvasH);

        // 顶部标题/图例居中叠印
        int topY = MARGIN;
        foreach (var (bin, w, h) in headerBars)
        {
            int ox = (width - w) / 2;
            Compositor.BlitBinary(canvas, width, canvasH, bin, w, h, ox, topY);
            topY += h + 1;
        }

        // Y 范围:手动指定或自动采样计算(并纳入标记点)
        double yMin = opt.YMin ?? double.NaN;
        double yMax = opt.YMax ?? double.NaN;
        if (!double.IsFinite(yMin) || !double.IsFinite(yMax) || yMax <= yMin)
        {
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var fn in funcs)
            {
                for (int px = 0; px <= plotW; px++)
                {
                    double xw = opt.XMin + (px / (double)plotW) * (opt.XMax - opt.XMin);
                    double v = SafeEval(fn, xw);
                    if (!double.IsFinite(v)) continue;
                    lo = Math.Min(lo, v);
                    hi = Math.Max(hi, v);
                }
            }
            if (opt.Points is not null)
            {
                foreach (var p in opt.Points)
                {
                    if (!double.IsFinite(p.Y)) continue;
                    lo = Math.Min(lo, p.Y);
                    hi = Math.Max(hi, p.Y);
                }
            }
            if (!double.IsFinite(lo) || !double.IsFinite(hi) || hi - lo < 1e-9)
            {
                lo = -1;
                hi = 1;
            }
            double pad = (hi - lo) * 0.1;
            yMin = Math.Min(lo - pad, 0);
            yMax = Math.Max(hi + pad, 0);
            if (yMax <= yMin)
            {
                double mid = (yMin + yMax) / 2;
                yMin = mid - 1;
                yMax = mid + 1;
            }
        }

        // 网格(虚线)
        if (opt.ShowGrid)
        {
            double step = NiceStep(opt.XMax - opt.XMin, 8);
            for (double v = Math.Ceiling(opt.XMin / step) * step; v <= opt.XMax + step * 0.5; v += step)
            {
                int col = XToCol(v, opt.XMin, opt.XMax, marginLeft, plotW);
                DrawDashedVLine(canvas, width, canvasH, col, plotY, plotY + PLOT_HEIGHT);
            }
            step = NiceStep(yMax - yMin, 8);
            for (double v = Math.Ceiling(yMin / step) * step; v <= yMax + step * 0.5; v += step)
            {
                int row = YToRow(v, yMin, yMax, plotY, PLOT_HEIGHT);
                DrawDashedHLine(canvas, width, canvasH, row, marginLeft, marginLeft + plotW);
            }
        }

        // 坐标轴:y=0 的水平轴、x=0 的垂直轴(受开关控制,只画在绘图区内)
        if (opt.ShowAxes)
        {
            if (yMin <= 0 && yMax >= 0)
            {
                int row0 = YToRow(0, yMin, yMax, plotY, PLOT_HEIGHT);
                for (int xx = marginLeft; xx <= marginLeft + plotW; xx++)
                    SetPixel(canvas, width, canvasH, xx, row0, dashed: false);
            }
            if (opt.XMin <= 0 && opt.XMax >= 0)
            {
                int col0 = XToCol(0, opt.XMin, opt.XMax, marginLeft, plotW);
                for (int yy = plotY; yy <= plotY + PLOT_HEIGHT; yy++)
                    SetPixel(canvas, width, canvasH, col0, yy, dashed: false);
            }
        }

        // 曲线(实线/虚线交替便于区分多条函数)
        for (int i = 0; i < funcs.Count; i++)
        {
            PlotCurve(canvas, width, canvasH, funcs[i],
                opt.XMin, opt.XMax, yMin, yMax,
                marginLeft, plotY, plotW, PLOT_HEIGHT, dashed: i % 2 == 1);
        }

        // 标记点:十字 + 点旁名称
        if (opt.Points is not null && opt.Points.Count > 0)
        {
            foreach (var p in opt.Points)
            {
                int px = XToCol(p.X, opt.XMin, opt.XMax, marginLeft, plotW);
                int py = YToRow(p.Y, yMin, yMax, plotY, PLOT_HEIGHT);
                DrawCross(canvas, width, canvasH, px, py);
                var label = RenderTextLine(p.Name, LEGEND_FONT, 40);
                if (label is not null)
                {
                    var (lb, lw, lh) = label.Value;
                    int lx = px + 3;
                    int ly = py - lh - 2;
                    if (lx + lw > width - 1) lx = px - lw - 3;
                    if (lx < 0) lx = 0;
                    if (ly < 0) ly = py + 3;
                    Compositor.BlitBinary(canvas, width, canvasH, lb, lw, lh, lx, ly);
                }
            }
        }

        // 刻度数值: x 轴底边条 + y 轴左侧边条
        if (showTicks)
        {
            double stepX = NiceStep(opt.XMax - opt.XMin, 7);
            int tickRow = plotY + PLOT_HEIGHT + 2;
            for (double v = Math.Ceiling(opt.XMin / stepX) * stepX; v <= opt.XMax + stepX * 0.5; v += stepX)
            {
                if (Math.Abs(v) < stepX * 0.02) continue; // 0 由坐标轴标出,避免重叠
                var tb = RenderTextLine(FormatTick(v), 9, 60);
                if (tb is null) continue;
                var (binX, tw, th) = tb.Value;
                int cx = XToCol(v, opt.XMin, opt.XMax, marginLeft, plotW);
                int ox = cx - tw / 2;
                if (ox < 0) ox = 0;
                if (ox + tw > width) ox = width - tw;
                if (tickRow + th > canvasH) continue;
                Compositor.BlitBinary(canvas, width, canvasH, binX, tw, th, ox, tickRow);
            }

            double stepY = NiceStep(yMax - yMin, 6);
            for (double v = Math.Ceiling(yMin / stepY) * stepY; v <= yMax + stepY * 0.5; v += stepY)
            {
                if (Math.Abs(v) < stepY * 0.02) continue; // 0 由坐标轴标出
                var text = RenderTextLine(FormatTick(v), 9, 44);
                if (text is null) continue;
                var (tbY, tyw, tyh) = text.Value;
                int row = YToRow(v, yMin, yMax, plotY, PLOT_HEIGHT);
                int ox = marginLeft - tyw - 2;
                int oy = row - tyh / 2;
                if (ox < 0) ox = 0;
                if (oy < 0) oy = 0;
                if (oy + tyh > plotY + PLOT_HEIGHT) oy = plotY + PLOT_HEIGHT - tyh;
                if (ox + tyw > marginLeft - 1) continue;
                Compositor.BlitBinary(canvas, width, canvasH, tbY, tyw, tyh, ox, oy);
            }
        }

        return (canvas, width, canvasH, null, new PlotMapping
        {
            XMin = opt.XMin,
            XMax = opt.XMax,
            YMin = yMin,
            YMax = yMax,
            PlotX = marginLeft,
            PlotY = plotY,
            PlotW = plotW,
            PlotH = PLOT_HEIGHT,
        });
    }

    /// <summary>画十字标记(中心 + 上下左右各 2 像素)</summary>
    private static void DrawCross(byte[] canvas, int w, int h, int cx, int cy)
    {
        for (int d = -2; d <= 2; d++)
        {
            SetPixel(canvas, w, h, cx + d, cy, dashed: false);
            SetPixel(canvas, w, h, cx, cy + d, dashed: false);
        }
    }

    /// <summary>求值但把异常视为非有限值(如 sqrt 负数域),曲线在该处自然断开</summary>
    private static double SafeEval(Func<double, double> fn, double x)
    {
        try { return fn(x); }
        catch { return double.NaN; }
    }

    /// <summary>X 世界坐标 → 画布列</summary>
    private static int XToCol(double xw, double xmin, double xmax, int plotX, int plotW)
    {
        double t = (xw - xmin) / (xmax - xmin);
        return plotX + (int)Math.Round(t * plotW);
    }

    /// <summary>Y 世界坐标 → 画布行(图像上下翻转)</summary>
    private static int YToRow(double yw, double ymin, double ymax, int plotY, int plotH)
    {
        double t = (yw - ymin) / (ymax - ymin);
        return plotY + plotH - (int)Math.Round(t * plotH);
    }

    /// <summary>网格步长取"美观"的 1/2/2.5/5×10^n</summary>
    private static double NiceStep(double range, int targetLines)
    {
        double raw = range / targetLines;
        if (raw <= 0) return 1;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double norm = raw / mag;
        double step = norm <= 1 ? 1 : norm <= 2 ? 2 : norm <= 2.5 ? 2.5 : norm <= 5 ? 5 : 10;
        return step * mag;
    }

    /// <summary>刻度数值短格式: 0 输出 "0", 其余保留最多 2 位有效小数</summary>
    private static string FormatTick(double v)
    {
        if (v == 0) return "0";
        double a = Math.Abs(v);
        if (a >= 10000) return v.ToString("0.#e0", CultureInfo.InvariantCulture);
        if (a >= 100) return v.ToString("0", CultureInfo.InvariantCulture);
        if (a >= 1) return v.ToString("0.#", CultureInfo.InvariantCulture);
        return v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static void DrawDashedVLine(byte[] canvas, int w, int h, int x, int y0, int y1)
    {
        if (x < 0 || x >= w) return;
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
        {
            if (((y / 3) % 2) == 0) canvas[y * w + x] = 1;
        }
    }

    private static void DrawDashedHLine(byte[] canvas, int w, int h, int y, int x0, int x1)
    {
        if (y < 0 || y >= h) return;
        for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++)
        {
            if (((x / 3) % 2) == 0) canvas[y * w + x] = 1;
        }
    }

/// <summary>沿 x 像素列采样并连线绘制一条曲线;dashed=true 时画虚线(用于区分多条函数)</summary>
    private static void PlotCurve(byte[] canvas, int w, int h, Func<double, double> fn,
        double xmin, double xmax, double ymin, double ymax,
        int plotX, int plotY, int plotW, int plotH, bool dashed)
    {
        bool hasPrev = false;
        int prevCol = 0, prevRow = 0;
        for (int px = 0; px <= plotW; px++)
        {
            double xw = xmin + (px / (double)plotW) * (xmax - xmin);
            double yv = SafeEval(fn, xw);
            int col = plotX + px;
            int row = YToRow(yv, ymin, ymax, plotY, plotH);

            // 相邻采样行跨度>1 视为陡坡/断点:不再连线,只落像素点,避免"y 轴无限延伸"的竖直线
            if (hasPrev && Math.Abs(row - prevRow) <= 1)
                SetPixel(canvas, w, h, col, row, dashed);
            else
                SetPixel(canvas, w, h, col, row, dashed);

            hasPrev = true;
            prevCol = col;
            prevRow = row;
        }
    }

    private static void SetPixel(byte[] canvas, int w, int h, int x, int y, bool dashed)
    {
        if (x < 0 || x >= w || y < 0 || y >= h) return;
        if (dashed && ((x / 4) % 2) == 1) return;
        canvas[y * w + x] = 1;
    }

    /// <summary>Bresenham 画线段;dashed 时按 x 方向周期性跳过形成虚线</summary>
    private static void LineTo(byte[] canvas, int w, int h, int x0, int y0, int x1, int y1, bool dashed)
    {
        int dx = Math.Abs(x1 - x0);
        int dy = -Math.Abs(y1 - y0);
        int sx = x0 < x1 ? 1 : -1;
        int sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        int x = x0, y = y0;
        while (true)
        {
            SetPixel(canvas, w, h, x, y, dashed);
            if (x == x1 && y == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    /// <summary>把单行文本渲染为二值位图(宽受限自动换行),失败返回 null</summary>
    private static (byte[] Bin, int W, int H)? RenderTextLine(string text, int fontSize, int maxWidth)
    {
        try
        {
            var options = new RasterEncoder.TextRenderOptions
            {
                FontSize = fontSize,
                Bold = false,
                Italic = false,
                Underline = false,
                LetterSpacing = 0,
                LineSpacing = 2,
                Margin = 0,
            };
            using var img = RasterEncoder.RenderTextToImageIn(text, options, maxWidth);
            var gray = RasterEncoder.ImageToGrayRaw(img);
            var bin = Dither.DitherToBinary(gray, DitherMode.NONE, RasterEncoder.THRESHOLD_TEXT);
            return (bin, gray.Width, gray.Height);
        }
        catch
        {
            return null;
        }
    }
}