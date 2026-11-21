using System.IO;

namespace DesktopBox.Services;

public class DesktopScannerService : IDesktopScannerService
{
    public List<string> ScanDesktop()
    {
        var result = new List<string>();
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),   // 用户桌面
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) // 公共桌面 C:\Users\Public\Desktop
        };

        foreach (var d in dirs.Distinct().Where(Directory.Exists))
        {
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(d))
                {
                    // 过滤集中在 DesktopItemPolicy:临时文件(~$、*.tmp)与资源管理器不显示的隐藏文件
                    // 既不画在桌面层、也不收进盒子 —— 否则会留下一堆"目标当前不可用"的死引用。
                    if (!DesktopItemPolicy.ShouldShow(entry)) continue;
                    result.Add(entry);
                }
            }
            catch (Exception ex)
            {
                App.LogError(ex, "DesktopScanner.ScanDesktop");
            }
        }
        return result;
    }
}
