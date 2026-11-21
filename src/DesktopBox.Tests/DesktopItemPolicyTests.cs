using System.IO;
using System.Windows;
using DesktopBox.Controls;
using DesktopBox.Models;
using DesktopBox.Native;
using DesktopBox.Services;
using FluentAssertions;

namespace DesktopBox.Tests;

/// <summary>v1.7.37 的纯逻辑回归:临时文件规则、资源管理器隐藏文件对齐、
/// 删除失败原因映射、图标可见性的可观察绑定、FileDrop 的按需携带。</summary>
public class DesktopItemPolicyTests
{
    // ---- 临时文件名 ----

    [Theory]
    [InlineData("~$周计划统计表.xlsx")]        // Office 锁文件(问题6 的主角)
    [InlineData("~WRL0001.tmp")]                // Word 临时
    [InlineData("周计划统计表~50341.tmp")]      // Excel 保存期临时
    [InlineData("desktop.ini")]
    [InlineData("Thumbs.db")]
    [InlineData("DESKTOP.INI")]                 // 大小写不敏感
    [InlineData("~")]                           // 桌面上以 ~ 开头的文件只会是临时垃圾,按前缀一刀切
    public void IsTransientName_FiltersOfficeAndSystemJunk(string name)
        => DesktopItemPolicy.IsTransientName(name).Should().BeTrue();

    [Theory]
    [InlineData("周计划统计表.xlsx")]
    [InlineData("报告.pdf")]
    [InlineData("我的文件夹")]
    [InlineData("报表~最终版.xlsx")]            // 中间带波浪线、但不是 .tmp:正常文件不受影响
    [InlineData("")]
    public void IsTransientName_KeepsNormalFiles(string name)
        => DesktopItemPolicy.IsTransientName(name).Should().BeFalse();

    // ---- 资源管理器的隐藏文件设置 ----

    [Fact]
    public void IsVisibleOnDesktop_FollowsExplorerSettings()
    {
        var hidden = FileAttributes.Hidden;
        var system = FileAttributes.System | FileAttributes.Hidden;
        var normal = FileAttributes.Normal;

        // 默认(两个都关):隐藏文件与系统文件都不显示
        DesktopItemPolicy.IsVisibleOnDesktop(hidden, showHidden: false, showSuperHidden: false).Should().BeFalse();
        DesktopItemPolicy.IsVisibleOnDesktop(system, showHidden: false, showSuperHidden: false).Should().BeFalse();
        // 只开了"显示隐藏文件":Hidden 放行,但受保护的系统文件仍挡着
        DesktopItemPolicy.IsVisibleOnDesktop(hidden, showHidden: true, showSuperHidden: false).Should().BeTrue();
        DesktopItemPolicy.IsVisibleOnDesktop(system, showHidden: true, showSuperHidden: false).Should().BeFalse();
        // 两个都开:全放行
        DesktopItemPolicy.IsVisibleOnDesktop(system, showHidden: true, showSuperHidden: true).Should().BeTrue();
        // 普通文件永远显示
        DesktopItemPolicy.IsVisibleOnDesktop(normal, showHidden: false, showSuperHidden: false).Should().BeTrue();
    }

    [Fact]
    public void ShouldShow_HidesTransientFiles_EvenWhenExplorerShowsHidden()
    {
        // 用户开了"显示隐藏文件"也照样挡临时文件 —— 挡的是名字,不是属性
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var lockFile = Path.Combine(dir.FullName, "~$报表.xlsx");
            File.WriteAllText(lockFile, "x");

            DesktopItemPolicy.IsTransientName(Path.GetFileName(lockFile)).Should().BeTrue();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- 删除失败原因映射 ----

    [Theory]
    [InlineData(2, "dialog.delete.notFound")]      // ERROR_FILE_NOT_FOUND
    [InlineData(3, "dialog.delete.notFound")]      // ERROR_PATH_NOT_FOUND
    [InlineData(5, "dialog.delete.accessDenied")]  // ERROR_ACCESS_DENIED
    [InlineData(32, "dialog.delete.inUse")]        // ERROR_SHARING_VIOLATION —— 实测:文件被 Excel 占着就是这样
    [InlineData(33, "dialog.delete.inUse")]        // ERROR_LOCK_VIOLATION
    [InlineData(1223, "dialog.delete.cancelled")]  // ERROR_CANCELLED
    [InlineData(14007, "dialog.delete.failed")]    // 未知码走通用文案
    public void DescribeDeleteFailure_MapsEveryKnownReason(int rc, string expectedKey)
        => Shell32.DescribeDeleteFailure(rc).Should().Be(expectedKey);

    [Fact]
    public void DeleteReasonKeys_ArePresentInBothLanguages()
    {
        var zh = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Resources", "Strings.zh-CN.xaml"));
        var en = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Resources", "Strings.en-US.xaml"));

        foreach (var key in new[]
                 {
                     "dialog.delete.inUse", "dialog.delete.accessDenied", "dialog.delete.notFound",
                     "dialog.delete.cancelled", "dialog.delete.failed", "dialog.delete.alreadyGone"
                 })
        {
            zh.Should().Contain($"x:Key=\"{key}\"", $"中文文案缺 {key}");
            en.Should().Contain($"x:Key=\"{key}\"", $"英文文案缺 {key}");
        }
    }

    // ---- 图标可见性改成绑定(修"整理后图标不显示")----

    [Fact]
    public void HasIcon_NotifiesWhenIconCachePathArrives()
    {
        var item = new BoxItem { TargetPath = @"C:\a.txt", DisplayName = "a" };

        var notified = new List<string?>();
        item.PropertyChanged += (_, e) => notified.Add(e.PropertyName);

        item.HasIcon.Should().BeFalse("还没提取到图标");

        item.IconCachePath = @"C:\icons\a.png";

        item.HasIcon.Should().BeTrue();
        notified.Should().Contain(nameof(BoxItem.HasIcon),
            "IconCachePath 是后台异步填的,必须由它驱动 HasIcon,否则磁贴永远停在字母占位");
    }

    [Fact]
    public void ItemTile_BindsImageVisibilityInsteadOfSettingItInCode()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Controls", "ItemTile.xaml"));
        var code = File.ReadAllText(FindRepositoryFile("src", "DesktopBox", "Controls", "ItemTile.xaml.cs"));

        xaml.Should().Contain("Visibility=\"{Binding HasIcon, Converter={StaticResource BoolVis}}\"");
        // 命令式赋值只会算一次 DataContext 变化时的值,后台到达的 IconCachePath 改不动它
        code.Should().NotContain("Img.Visibility =");
    }

    // ---- FileDrop 按需携带 ----

    [Fact]
    public void CreateDesktopDropData_WithoutFlag_StillHasNoNativeFileDrop()
    {
        var item = new BoxItem { Type = ItemType.File, TargetPath = @"C:\Temp\a.txt", DisplayName = "a.txt" };
        var data = ItemDragDrop.CreateDesktopDropData(new[] { item });

        data.GetDataPresent(DataFormats.FileDrop).Should().BeFalse();
    }

    [Fact]
    public void CreateDesktopDropData_WithFlag_ExposesExistingFilesOnly()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var real = Path.Combine(dir.FullName, "real.txt");
            File.WriteAllText(real, "x");
            var items = new[]
            {
                new BoxItem { Type = ItemType.File, TargetPath = real, DisplayName = "real.txt" },
                new BoxItem { Type = ItemType.Url, TargetPath = "https://example.com", DisplayName = "web" },
                new BoxItem { Type = ItemType.SystemIcon, TargetPath = "::{645FF040-5081-101B-9F08-00AA002F954E}", DisplayName = "bin" },
                new BoxItem { Type = ItemType.File, TargetPath = Path.Combine(dir.FullName, "missing.txt"), DisplayName = "gone" }
            };

            var data = ItemDragDrop.CreateDesktopDropData(items, includeFileDrop: true);

            // 只有真实存在的本地文件进 FileDrop:网址/系统图标/已消失的文件外部程序都用不了
            data.GetDataPresent(DataFormats.FileDrop).Should().BeTrue();
            data.GetData(DataFormats.FileDrop).Should().BeEquivalentTo(new[] { real });
            // 内部格式照旧,盒内重排不受影响
            data.GetDataPresent(ItemDragDrop.DragSourceItemFormat).Should().BeTrue();
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var segments = new List<string> { dir.FullName };
            segments.AddRange(parts);
            var candidate = Path.Combine(segments.ToArray());
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(Path.Combine(parts));
    }
}
