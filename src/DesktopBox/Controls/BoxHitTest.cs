using System.Collections.Generic;
using System.Linq;
using DesktopBox.ViewModels;

namespace DesktopBox.Controls;

/// <summary>盒子命中测试:拖动中的盒子要判断"指针落在哪个别的盒子上"(拖进目标盒子 = 合并)。
/// 抽成纯函数是为了能直接单测(不依赖窗口/拖放)。</summary>
public static class BoxHitTest
{
    /// <summary>找到包含指定屏幕坐标点的盒子;多个重叠时取<b>面积最小</b>的那个
    /// (最小的那个一定是最后拖到上面/最贴身的,用户意图命中的就是它)。
    /// excludeId 用于排除正在被拖动的盒子自身。</summary>
    public static BoxViewModel? FindAt(IEnumerable<BoxViewModel> boxes, double screenX, double screenY,
        System.Guid? excludeId = null)
    {
        BoxViewModel? best = null;
        double bestArea = double.MaxValue;

        foreach (var box in boxes)
        {
            if (excludeId.HasValue && box.Id == excludeId.Value)
                continue;
            if (box.Width <= 0 || box.Height <= 0)
                continue;
            if (screenX < box.X || screenX > box.X + box.Width)
                continue;
            if (screenY < box.Y || screenY > box.Y + box.Height)
                continue;

            var area = box.Width * box.Height;
            if (area < bestArea)
            {
                bestArea = area;
                best = box;
            }
        }

        return best;
    }

    /// <summary>清掉所有盒子的"合并目标"高亮(拖动结束/取消时调用)。</summary>
    public static void ClearMergeTargets(IEnumerable<BoxViewModel> boxes)
    {
        foreach (var box in boxes.Where(b => b.IsMergeTarget))
            box.IsMergeTarget = false;
    }
}
