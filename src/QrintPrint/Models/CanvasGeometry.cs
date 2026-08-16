// CanvasGeometry.cs
//
// 画布编辑器的旋转几何辅助（纯数学，不碰 UI）。
// 设计移植自 suda-win-web (https://github.com/yiran168/suda-win-web)
// src/model/document.ts 的 elementCorners / visualBounds / hitTestElement：
//   元素几何用 DotX/DotY（左上角）+ DotW/DotH + Rotation（绕中心顺时针旋转），
//   所有包围盒 / 命中检测都走旋转感知 —— 选中框、手柄、磁吸与渲染共用同一套几何。
//
// 旋转方向约定：与 WPF RotateTransform 一致，正角度 = 屏幕坐标顺时针。

using System;

namespace QrintPrint.Models;

public static class CanvasGeometry
{
    /// <summary>旋转四角的 AABB（视觉外框，画布坐标）</summary>
    public static (double Left, double Top, double Right, double Bottom) VisualBounds(CanvasElement el)
    {
        var corners = ElementCorners(el);
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var (x, y) in corners)
        {
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return (minX, minY, maxX, maxY);
    }

    /// <summary>元素旋转后的四角（画布坐标，顺序: 左上/右上/右下/左下）</summary>
    public static (double X, double Y)[] ElementCorners(CanvasElement el)
    {
        double rad = el.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double cx = el.DotX + el.DotW / 2;
        double cy = el.DotY + el.DotH / 2;
        double hw = el.DotW / 2;
        double hh = el.DotH / 2;

        return new[]
        {
            (cx + (-hw * cos - -hh * sin), cy + (-hw * sin + -hh * cos)),
            (cx + ( hw * cos - -hh * sin), cy + ( hw * sin + -hh * cos)),
            (cx + ( hw * cos -  hh * sin), cy + ( hw * sin +  hh * cos)),
            (cx + (-hw * cos -  hh * sin), cy + (-hw * sin +  hh * cos)),
        };
    }

    /// <summary>元素中心（画布坐标）</summary>
    public static (double X, double Y) ElementCenter(CanvasElement el)
        => (el.DotX + el.DotW / 2, el.DotY + el.DotH / 2);

    /// <summary>组合视觉外框（多选/全选用）</summary>
    public static (double Left, double Top, double Right, double Bottom) GroupBounds(System.Collections.Generic.IEnumerable<CanvasElement> elements)
    {
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;
        foreach (var el in elements)
        {
            var b = VisualBounds(el);
            minX = Math.Min(minX, b.Left); maxX = Math.Max(maxX, b.Right);
            minY = Math.Min(minY, b.Top); maxY = Math.Max(maxY, b.Bottom);
            any = true;
        }
        return any ? (minX, minY, maxX, maxY) : (0, 0, 0, 0);
    }

    /// <summary>
    /// 点 (x, y) 是否命中旋转后的元素。
    /// 反旋转到元素本地坐标系，落在 [0, DotW) × [0, DotH) 内即命中。
    /// </summary>
    public static bool HitTestElement(CanvasElement el, double x, double y)
    {
        double rad = el.Rotation * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        double cx = el.DotX + el.DotW / 2;
        double cy = el.DotY + el.DotH / 2;
        double vx = x - cx;
        double vy = y - cy;
        double lx = vx * cos + vy * sin + el.DotW / 2;
        double ly = -vx * sin + vy * cos + el.DotH / 2;
        return lx >= 0 && ly >= 0 && lx < el.DotW && ly < el.DotH;
    }

    /// <summary>
    /// 把屏幕位移换算进元素本地坐标系（旋转元素八向缩放用）。
    /// 返回 (本地 X 位移, 本地 Y 位移)。
    /// </summary>
    public static (double X, double Y) ToLocalDelta(double dx, double dy, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        double cos = Math.Cos(rad);
        double sin = Math.Sin(rad);
        // 世界位移反旋转 = 本地位移
        return (dx * cos + dy * sin, -dx * sin + dy * cos);
    }

    /// <summary>包围盒与矩形 (rx, ry, rw, rh) 是否相交（框选用，按 AABB 判断）</summary>
    public static bool Intersects((double Left, double Top, double Right, double Bottom) b,
        double rx, double ry, double rw, double rh)
        => b.Left < rx + rw && b.Right > rx && b.Top < ry + rh && b.Bottom > ry;
}
