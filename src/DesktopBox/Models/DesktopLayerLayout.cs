using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopBox.Models;

/// <summary>桌面层里"未收纳项"的摆放规则(纯逻辑,好单测):
/// 用户拖过、记下位置的按记录放(夹回可视区,换分辨率/拔屏幕也不会跑没影);
/// 没记过位置的自动找空位(列优先,和 explorer 的自动排列观感一致)。</summary>
internal static class DesktopLayerLayout
{
    /// <summary>一个项的落点(左上角,单位与调用方一致)。</summary>
    public readonly record struct Cell(string Path, double X, double Y);

    public static List<Cell> Arrange(
        IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, (double X, double Y)> saved,
        double width,
        double height,
        double cellWidth,
        double cellHeight,
        double padding,
        double bottomReserve)
    {
        var result = new List<Cell>(paths?.Count ?? 0);
        if (paths is null || paths.Count == 0) return result;

        var cellW = Math.Max(1, cellWidth);
        var cellH = Math.Max(1, cellHeight);
        var usableHeight = Math.Max(cellH, height - bottomReserve - padding * 2);
        var rows = Math.Max(1, (int)Math.Floor(usableHeight / cellH));
        var maxX = Math.Max(padding, width - padding - cellW);
        var maxY = Math.Max(padding, height - bottomReserve - cellH);

        var placed = new List<Cell>(paths.Count);
        var pending = new List<string>();

        // 先摆"记得位置"的,它们优先占位;记的位置也吸附到网格(explorer 的"对齐到网格"是默认行为)
        foreach (var path in paths)
        {
            if (saved is not null && saved.TryGetValue(path, out var point))
            {
                var snapped = Snap(point.X, point.Y, placed.Select(c => (c.X, c.Y)).ToList(),
                    cellW, cellH, padding, maxX, maxY);
                var cell = new Cell(path, snapped.X, snapped.Y);
                result.Add(cell);
                placed.Add(cell);
            }
            else
            {
                pending.Add(path);
            }
        }

        var columns = Math.Max(1, (int)Math.Floor((maxX - padding) / cellW) + 1);
        foreach (var path in pending)
        {
            var slot = NextFreeSlot(placed, padding, cellW, cellH, rows, columns, maxX, maxY);
            var cell = new Cell(path, slot.X, slot.Y);
            result.Add(cell);
            placed.Add(cell);
        }

        return result;
    }

    /// <summary>把自由坐标吸附到最近的格子(等价于 explorer 的"对齐到网格"):
    /// 四舍五入到最近的格点,夹回可视区;那格已被别人占了就往外一圈圈找最近的空格子。
    /// 全占满时退回最近格子(宁可叠在一起,也不能跑到屏幕外)。</summary>
    public static (double X, double Y) Snap(
        double x,
        double y,
        IReadOnlyCollection<(double X, double Y)> taken,
        double cellWidth,
        double cellHeight,
        double padding,
        double maxX,
        double maxY)
    {
        var cellW = Math.Max(1, cellWidth);
        var cellH = Math.Max(1, cellHeight);
        var maxColumn = Math.Max(0, (int)Math.Floor((maxX - padding) / cellW));
        var maxRow = Math.Max(0, (int)Math.Floor((maxY - padding) / cellH));

        var column = Math.Clamp((int)Math.Round((x - padding) / cellW, MidpointRounding.AwayFromZero), 0, maxColumn);
        var row = Math.Clamp((int)Math.Round((y - padding) / cellH, MidpointRounding.AwayFromZero), 0, maxRow);

        for (var ring = 0; ring <= maxColumn + maxRow + 1; ring++)
        {
            for (var dc = -ring; dc <= ring; dc++)
            {
                for (var dr = -ring; dr <= ring; dr++)
                {
                    if (Math.Max(Math.Abs(dc), Math.Abs(dr)) != ring) continue;   // 只看这一圈
                    var c = column + dc;
                    var r = row + dr;
                    if (c < 0 || c > maxColumn || r < 0 || r > maxRow) continue;

                    var candidate = (X: padding + c * cellW, Y: padding + r * cellH);
                    if (!IsTaken(taken, candidate.X, candidate.Y)) return candidate;
                }
            }
        }

        return (padding + column * cellW, padding + row * cellH);
    }

    private static bool IsTaken(IReadOnlyCollection<(double X, double Y)> taken, double x, double y)
        => taken is not null && taken.Any(t => Math.Abs(t.X - x) < 1 && Math.Abs(t.Y - y) < 1);

    /// <summary>找一个空着的格子:列优先(一列自上而下排满再换右一列)。整屏都占满时退回第一个格子
    /// (宁可叠在一起,也不能死循环或把图标摆到屏幕外)。</summary>
    private static (double X, double Y) NextFreeSlot(
        List<Cell> placed, double padding, double cellW, double cellH, int rows, int columns, double maxX, double maxY)
    {
        for (var column = 0; column < columns; column++)
        {
            var x = Math.Min(padding + column * cellW, maxX);
            for (var row = 0; row < rows; row++)
            {
                var y = Math.Min(padding + row * cellH, maxY);
                if (!placed.Any(c => Math.Abs(c.X - x) < 1 && Math.Abs(c.Y - y) < 1))
                    return (x, y);
            }
        }

        return (padding, padding);
    }
}
