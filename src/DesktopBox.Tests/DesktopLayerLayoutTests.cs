using DesktopBox.Models;
using FluentAssertions;

namespace DesktopBox.Tests;

/// <summary>桌面层"未收纳项"的摆放规则:记过位置的吸附到网格(默认"对齐到网格"),
/// 没记过位置的自动找空位(列优先,不和别人重叠)。</summary>
public class DesktopLayerLayoutTests
{
    private static readonly Dictionary<string, (double X, double Y)> None = new();

    // 格子 75 x 100、起点 10 —— 合法格点是 (10+75c, 10+100r)
    private static List<DesktopLayerLayout.Cell> Arrange(
        IReadOnlyList<string> paths,
        Dictionary<string, (double X, double Y)>? saved = null,
        double width = 1000,
        double height = 400)
        => DesktopLayerLayout.Arrange(paths, saved ?? None, width, height, 75, 100, 10, 0);

    [Fact]
    public void ItemsWithoutSavedPosition_FillTheFirstColumnTopDown()
    {
        var cells = Arrange(new[] { "a", "b", "c" });

        cells.Should().HaveCount(3);
        cells[0].Should().Be(new DesktopLayerLayout.Cell("a", 10, 10));
        cells[1].Should().Be(new DesktopLayerLayout.Cell("b", 10, 110));
        cells[2].Should().Be(new DesktopLayerLayout.Cell("c", 10, 210));
    }

    [Fact]
    public void OnGridSavedPosition_IsKeptAsIs()
    {
        var saved = new Dictionary<string, (double X, double Y)> { ["a"] = (310, 210) };

        Arrange(new[] { "a" }, saved).Single()
            .Should().Be(new DesktopLayerLayout.Cell("a", 310, 210));
    }

    [Fact]
    public void OffGridSavedPosition_SnapsToTheNearestCell()
    {
        // 用户随手拖到一个像素位置:落盘和重画时都要吸附回网格
        var saved = new Dictionary<string, (double X, double Y)> { ["a"] = (300, 203) };

        Arrange(new[] { "a" }, saved).Single()
            .Should().Be(new DesktopLayerLayout.Cell("a", 310, 210));
    }

    [Fact]
    public void SavedPositionsAreClampedBackIntoTheLastInGridCell()
    {
        // 换分辨率/拔屏幕后老位置可能跑到屏幕外 → 吸附回"最后一个合法格点"
        var saved = new Dictionary<string, (double X, double Y)> { ["a"] = (99999, 99999) };

        var cell = Arrange(new[] { "a" }, saved).Single();
        cell.X.Should().Be(910);
        cell.Y.Should().Be(210);
        (cell.X + 75).Should().BeLessThanOrEqualTo(1000);
        (cell.Y + 100).Should().BeLessThanOrEqualTo(400);
    }

    [Fact]
    public void AutoPlacedItems_AvoidTheSlotsTakenBySavedOnes()
    {
        var saved = new Dictionary<string, (double X, double Y)> { ["a"] = (10, 10) };

        Arrange(new[] { "a", "b" }, saved).Single(c => c.Path == "b").Y
            .Should().Be(110, "第一个格子已经被记过位置的那项占了");
    }

    [Fact]
    public void Snap_LandsOnTheNearestGridCell()
    {
        var snapped = DesktopLayerLayout.Snap(47, 47, new List<(double X, double Y)>(), 75, 100, 10, 1000, 400);

        snapped.Should().Be((10, 10), "光标落在第一格里,吸附后就是第一格");
    }

    [Fact]
    public void Snap_StepsAsideWhenTheNearestCellIsTaken()
    {
        var taken = new List<(double X, double Y)> { (310, 210) };

        var snapped = DesktopLayerLayout.Snap(305, 205, taken, 75, 100, 10, 1000, 400);

        snapped.X.Should().BeOneOf(235d, 310d, 385d);
        snapped.Y.Should().BeOneOf(110d, 210d, 310d);
        snapped.Should().NotBe((310d, 210d), "那一格已经被别人占了");
    }

    [Fact]
    public void Snap_AlwaysStaysInsideTheViewport()
    {
        var snapped = DesktopLayerLayout.Snap(-500, -500, new List<(double X, double Y)>(), 75, 100, 10, 300, 250);
        snapped.Should().Be((10, 10));

        var far = DesktopLayerLayout.Snap(99999, 99999, new List<(double X, double Y)>(), 75, 100, 10, 300, 250);
        far.X.Should().Be(235);
        far.Y.Should().Be(210);
    }

    [Fact]
    public void APackedScreen_FallsBackAndNeverLeavesTheViewport()
    {
        // 屏上格子全占满时:既不能死循环,也不能把图标摆到屏幕外(宁可叠在一起)
        var paths = Enumerable.Range(0, 60).Select(i => $"item{i}").ToArray();

        var cells = Arrange(paths, null, width: 200, height: 200);

        cells.Should().HaveCount(60);
        cells.Should().OnlyContain(c => c.X >= 10 && c.Y >= 10 && c.X <= 200 && c.Y <= 200);
    }

    [Fact]
    public void BottomReserve_IsKeptFreeForTheTaskbar()
    {
        var cells = DesktopLayerLayout.Arrange(
            new[] { "a" }, None,
            width: 1000, height: 400, cellWidth: 75, cellHeight: 100, padding: 10, bottomReserve: 56);

        cells[0].Y.Should().Be(10);
        (cells[0].Y + 100).Should().BeLessThanOrEqualTo(400 - 56, "别把图标摆到任务栏底下");
    }
}
