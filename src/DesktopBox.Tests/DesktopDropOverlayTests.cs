using System.Windows;
using DesktopBox.Controls;
using DesktopBox.Models;
using FluentAssertions;

namespace DesktopBox.Tests;

public class DesktopDropOverlayTests
{
    [Fact]
    public void BuildExclusionRects_InflatesAndClipsBoxBoundsInsideOverlay()
    {
        var overlay = new Rect(0, 0, 500, 400);
        var excluded = new[]
        {
            new Rect(100, 80, 240, 180),
            new Rect(-20, 10, 30, 40),
            new Rect(600, 600, 20, 20)
        };

        var rects = DesktopDropOverlay.BuildExclusionRects(overlay, excluded, padding: 8);

        rects.Should().HaveCount(2);
        rects[0].X.Should().Be(92);
        rects[0].Y.Should().Be(72);
        rects[0].Width.Should().Be(256);
        rects[0].Height.Should().Be(196);
        rects[1].X.Should().Be(0);
        rects[1].Y.Should().Be(2);
        rects[1].Width.Should().Be(18);
        rects[1].Height.Should().Be(56);
    }

    [Fact]
    public void BuildExclusionRects_IgnoresEmptyAndOffscreenBounds()
    {
        var rects = DesktopDropOverlay.BuildExclusionRects(
            new Rect(0, 0, 300, 200),
            new[]
            {
                new Rect(10, 10, 0, 20),
                new Rect(500, 10, 50, 50)
            });

        rects.Should().BeEmpty();
    }

    /// <summary>卷起的盒子在屏幕只占标题栏那么高,挖洞必须按实际高度来。
    /// 按模型里存的完整高度挖洞时,卷起后腾出来的那片空白会被漏给 explorer ——
    /// 拖到那里它就当成"把文件复制到同一个文件夹",弹「源文件名和目标文件名相同」。</summary>
    [Fact]
    public void OccupiedBoxRects_CollapsedBoxOnlyOccupiesItsHeader()
    {
        var box = Box(10, 20, 240, 300, collapsed: true);

        var rects = DesktopDropOverlay.OccupiedBoxRects(new[] { box });

        rects.Should().ContainSingle();
        rects[0].Should().Be(new Rect(10, 20, 240, BoxCollapse.DefaultHeaderHeight));
    }

    [Fact]
    public void OccupiedBoxRects_HoverExpandedAndNormalBoxKeepFullHeight()
    {
        var hoverExpanded = Box(10, 20, 240, 300, collapsed: true);
        hoverExpanded.IsHoverExpanded = true;
        var normal = Box(10, 20, 240, 300, collapsed: false);

        DesktopDropOverlay.OccupiedBoxRects(new[] { hoverExpanded, normal })
            .Should().AllBeEquivalentTo(new Rect(10, 20, 240, 300));
    }

    [Fact]
    public void OccupiedBoxRects_SkipsBoxesWithNoArea()
    {
        DesktopDropOverlay.OccupiedBoxRects(new[] { Box(0, 0, 0, 0, collapsed: false) })
            .Should().BeEmpty();
    }

    private static ViewModels.BoxViewModel Box(double x, double y, double w, double h, bool collapsed)
    {
        var vm = new ViewModels.BoxViewModel(new Models.Box { X = x, Y = y, Width = w, Height = h, IsCollapsed = collapsed });
        vm.CollapsedHeight = BoxCollapse.DefaultHeaderHeight;
        return vm;
    }
}
