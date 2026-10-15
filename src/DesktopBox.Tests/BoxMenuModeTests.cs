using System.IO;
using FluentAssertions;

namespace DesktopBox.Tests;

/// <summary>盒子右键菜单的形态:齿轮按钮 = 完整菜单(含合并/拆分/删除盒子/清空盒子);
/// 空白处/标题栏右键 = 精简版。这里锁住实现方式,防止"显隐状态被上一次打开记住"那类回退。</summary>
public class BoxMenuModeTests
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

    [Fact]
    public void TheFourStructuralActions_AreTaggedInTheXaml()
    {
        var xaml = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");

        foreach (var key in new[] { "box.menu.clear", "box.menu.mergeIn", "box.menu.split", "box.menu.delete" })
        {
            var index = xaml.IndexOf($"{{DynamicResource {key}}}", StringComparison.Ordinal);
            index.Should().BeGreaterThanOrEqualTo(0, $"{key} 应该在菜单里");

            var lineEnd = xaml.IndexOf('>', index);
            lineEnd.Should().BeGreaterThan(index);
            xaml[index..lineEnd].Should().Contain("Tag=\"structural\"", $"{key} 要被标成结构性操作");
        }
    }

    [Fact]
    public void VisibilityIsDecidedPerOpen_NotViaContextMenuOpening()
    {
        var code = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml.cs");
        var xaml = ReadRepositoryFile("src", "DesktopBox", "Controls", "BoxControl.xaml");

        // 两条打开路径各自显式设置:齿轮按钮 → 完整;右键 → 精简
        code.Should().Contain("ApplyMenuMode(BoxMenuMode.Full)");
        code.Should().Contain("ApplyMenuMode(BoxMenuMode.Compact)");

        // 不能再依赖 ContextMenuOpening 来恢复显隐:齿轮按钮是程序化打开,那个事件不一定触发,
        // 结果就是"上次右键折叠过,齿轮里就再也看不到那几项"(实测踩过)
        xaml.Should().NotContain("ContextMenuOpening=");
    }
}
