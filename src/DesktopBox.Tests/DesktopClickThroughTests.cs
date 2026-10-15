using System.IO;
using FluentAssertions;

namespace DesktopBox.Tests;

/// <summary>桌面空白处的"鼠标穿透":我们只在**真的拖出框**时才把桌面层窗口的区域铺满整屏。
/// 曾经的毛病是在"左键按下"那一下就铺满 —— 光标下的窗口当场从 SHELLDLL_DefView 变成我们,
/// 那一下点击被我们吞掉,松手前区域又收回去,于是 Wallpaper Engine 的交互按钮只收到一个
/// "没有按下的抬起",点了毫无反应(悬停照常,所以看起来像"悬停有效、点击无效")。</summary>
public class DesktopClickThroughTests
{
    private static string ReadRepositoryFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(parts);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate repository file.", Path.Combine(parts));
    }

    private static string MethodBody(string code, string signature, string nextSignature)
    {
        var start = code.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"应该找得到 {signature}");
        var end = code.IndexOf(nextSignature, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start);
        return code[start..end];
    }

    [Fact]
    public void PressingOnBlankDesktop_DoesNotExpandTheRegion()
    {
        var code = ReadRepositoryFile("src", "DesktopBox", "Views", "DesktopLayerWindow.cs");
        var press = MethodBody(code, "private void OnHookLeftButtonDown", "private void OnHookButtonDown");

        press.Should().NotContain("ApplyRegion(full: true)",
            "按下的那一下铺满整屏 = 把这次点击从桌面/壁纸手里抢走");
    }

    [Fact]
    public void DraggingOutAMarquee_ExpandsThenRestoresTheRegion()
    {
        var code = ReadRepositoryFile("src", "DesktopBox", "Views", "DesktopLayerWindow.cs");
        var move = MethodBody(code, "private void OnHookDragMove", "private void OnHookDragEnd");

        move.Should().Contain("ApplyRegion(full: true)", "真的拖出框了才铺满,否则框画不到空白处");
        move.Should().Contain("MinimumHorizontalDragDistance", "要等超过拖动阈值才算拖动,不然单击也会铺");

        var end = MethodBody(code, "private void OnHookDragEnd", "/// <summary>把小图标拖出去");
        end.Should().Contain("ApplyRegion(full: false)", "松手要把空白处还给 explorer");
    }
}
