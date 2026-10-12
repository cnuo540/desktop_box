using System.Collections.Generic;

namespace DesktopBox.Services;

/// <summary>桌面图标"按项"显隐:把<b>已收纳</b>的桌面项图标挪到可视区外,需要时再挪回原位。
/// 系统只有整层的"显示/隐藏桌面图标",没有按项开关;桌面整理类软件的做法就是挪图标位置 ——
/// 纯视图数据,<b>不动任何文件</b>,而且未收纳/新建的项照常显示。</summary>
public interface IDesktopIconLayoutService
{
    /// <summary>当前被挪走的项数(仅用于日志/诊断)。</summary>
    int ParkedCount { get; }

    /// <summary>把给定的桌面项挪出可视区(已经挪过的跳过、读不到原位置的跳过)。返回真正挪走的数量。</summary>
    int Park(IEnumerable<string> paths);

    /// <summary>把某一项挪回原位(它不再"已收纳"时用,例如从盒子里拖回桌面)。返回是否挪回。</summary>
    bool Restore(string path);

    /// <summary>把所有被挪走的项挪回原位。返回挪回的数量。</summary>
    int RestoreAll();

    /// <summary>系统图标(此电脑/回收站/控制面板/网络)显隐 —— 这几个走注册表,和文件图标不是一套机制。</summary>
    void SetSystemIconsVisible(bool visible);
}
