// AxisSnap.cs
//
// 磁吸对齐。设计移植自 suda-win-web
// (https://github.com/yiran168/suda-win-web) src/editor/snap.ts，
// 但**去掉了锁存滞后**：每帧独立判断 —— 源点进入吸附范围（2 点内）立即对齐参考线，
// 鼠标一离开范围元素立刻回到鼠标位置。吸附跟手、零粘滞，不会出现
// "元素卡在线上、鼠标已经挪走"的滞后感。
//
// 吸附范围 2 点（≈0.25mm）很小，边界处的吸/放切换在视觉上几乎不可察觉。

using System;
using System.Collections.Generic;

namespace QrintPrint.Models;

public readonly record struct SnapResult(double Correction, double? Guide);

/// <summary>单轴磁吸（X 或 Y 各持有一个实例）。无锁存，每帧独立最近匹配</summary>
public sealed class AxisSnapLock
{
    private const double CaptureDistanceDots = 2;

    public void Reset() { }

    /// <summary>
    /// 给定一组源点（如组合框的左/中/右边）与目标参考线，返回本次位移的修正量与参考线位置。
    /// pointerMovement 参数保留（兼容调用方），不再用于锁存判断。
    /// </summary>
    public SnapResult Apply(IReadOnlyList<double> sources, IReadOnlyList<double> targets, double pointerMovement)
    {
        if (sources.Count == 0 || targets.Count == 0)
        {
            return new SnapResult(0, null);
        }

        // 每帧独立最近匹配：范围内取修正量最小的一对，离开范围即返回 0（完全跟手）
        double? bestCorrection = null;
        double bestTarget = 0;
        for (int si = 0; si < sources.Count; si++)
        {
            double source = sources[si];
            for (int ti = 0; ti < targets.Count; ti++)
            {
                double correction = targets[ti] - source;
                if (Math.Abs(correction) <= CaptureDistanceDots
                    && (bestCorrection is null || Math.Abs(correction) < Math.Abs(bestCorrection.Value)))
                {
                    bestCorrection = correction;
                    bestTarget = targets[ti];
                }
            }
        }
        return bestCorrection is { } c ? new SnapResult(c, bestTarget) : new SnapResult(0, null);
    }
}
