using System.IO;
using Microsoft.Win32;

namespace DesktopBox.Services;

/// <summary>桌面项"画不画 / 收不收"的判定规则(纯逻辑集中在这里,便于单测)。
///
/// 两条规则:
/// ① <b>对齐资源管理器的"隐藏文件"设置</b>(HKCU\...\Explorer\Advanced 的 Hidden / ShowSuperHidden):
///    explorer 不显示的,我们也不画、不收 —— 否则我们画出来的桌面和用户在资源管理器里看到的对不上。
///    默认(不显示隐藏文件)时,Office 的 ~$ 锁文件、Thumbs.db、desktop.ini 这类 Hidden 项自动消失。
/// ② <b>临时文件永远不收</b>:~$ 锁文件、~xxx.tmp、name~12345.tmp、desktop.ini、Thumbs.db。
///    它们寿命只有几秒到几分钟,收进盒子只会留下一堆"目标当前不可用"的死引用。
///    (即使用户打开了"显示隐藏文件"也照挡 —— 挡的是名字,不是属性。)</summary>
public static class DesktopItemPolicy
{
    /// <summary>资源管理器设置的缓存时长:每次重画都会问一遍,不能每次都读注册表。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);
    private static DateTime _cacheAtUtc = DateTime.MinValue;
    private static bool _showHidden;
    private static bool _showSuperHidden;

    // ---- 规则①:临时文件名(纯函数) ----

    /// <summary>是不是"不该进盒子"的临时文件名。传文件名(不含路径)。</summary>
    public static bool IsTransientName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        // 资源管理器自己的两个:即使用户开了"显示隐藏文件",也不会希望它们进盒子/占桌面
        if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) return true;
        if (fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)) return true;

        // Office 锁文件 ~$xxx.xlsx、Word 临时 ~WRL0001.tmp
        if (fileName.StartsWith("~", StringComparison.Ordinal)) return true;

        // Excel 保存期临时文件:周计划统计表~50341.tmp(带 ~ 的 .tmp)
        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            && fileName.Contains('~'))
            return true;

        return false;
    }

    // ---- 规则②:资源管理器的隐藏文件设置(纯函数) ----

    /// <summary>按资源管理器设置判断这个属性组合该不该显示。</summary>
    public static bool IsVisibleOnDesktop(FileAttributes attributes, bool showHidden, bool showSuperHidden)
    {
        if ((attributes & FileAttributes.Hidden) != 0 && !showHidden) return false;
        if ((attributes & FileAttributes.System) != 0 && !showSuperHidden) return false;
        return true;
    }

    // ---- 资源管理器设置读取(带短缓存) ----

    /// <summary>读 HKCU\...\Explorer\Advanced:Hidden(1=显示隐藏文件)与 ShowSuperHidden(1=显示受保护的系统文件)。
    /// 读不到按 Windows 默认值(都不显示)处理 —— 宁可少画,不可和 explorer 对不上。</summary>
    public static (bool ShowHidden, bool ShowSuperHidden) ReadExplorerSetting()
    {
        var now = DateTime.UtcNow;
        if (now - _cacheAtUtc < CacheTtl)
            return (_showHidden, _showSuperHidden);

        // Hidden: 1 = 显示, 2 = 不显示(默认 2);ShowSuperHidden: 1 = 显示, 0 = 不显示(默认 0)
        _showHidden = ReadAdvancedValue("Hidden", 2) == 1;
        _showSuperHidden = ReadAdvancedValue("ShowSuperHidden", 0) == 1;
        _cacheAtUtc = now;
        return (_showHidden, _showSuperHidden);
    }

    private static int ReadAdvancedValue(string name, int fallback)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue(name) is int value ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>清掉设置缓存(测试用;真实场景 3 秒后自动过期)。</summary>
    public static void InvalidateCache()
    {
        _cacheAtUtc = DateTime.MinValue;
        _showHidden = false;
        _showSuperHidden = false;
    }

    // ---- 组合判定 ----

    /// <summary>这一项该不该画在桌面上/收进盒子(两个入口共用:ScanDesktop 是桌面层与一键整理的共同来源)。
    /// 读不到属性一律当"不可见"——多半是文件刚被删,画出来也是死引用。</summary>
    public static bool ShouldShow(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var fileName = Path.GetFileName(path);
        if (IsTransientName(fileName)) return false;

        var (showHidden, showSuperHidden) = ReadExplorerSetting();
        // 用户把两类隐藏文件都打开了:除了临时文件(上面已挡)全都显示,省一次属性读取
        if (showHidden && showSuperHidden) return true;

        try
        {
            return IsVisibleOnDesktop(File.GetAttributes(path), showHidden, showSuperHidden);
        }
        catch
        {
            return false;
        }
    }
}
