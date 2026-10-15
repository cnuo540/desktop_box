using System.Collections.Generic;

namespace DesktopBox.Services;

/// <summary>桌面图标"按属性隐藏"的收尾服务 —— 现在只用于<b>迁移</b>。
///
/// 早期版本给已收纳的桌面项加 <c>Hidden</c> 属性、并关掉资源管理器的"显示隐藏项目";
/// 现在改成"关掉 explorer 的桌面图标层 + 自绘桌面层"(见 DesktopLayerWindow)后,
/// 那些痕迹都要还回去 —— 文件属性恢复原样、"显示隐藏项目"恢复成用户原来的值。
///
/// 系统图标(此电脑/回收站)的显隐仍走注册表,和桌面图标层不是一套机制,所以留在这里。</summary>
public interface IDesktopIconLayoutService
{
    /// <summary>把所有"按属性隐藏"的项恢复原属性(并拉回旧版遗留在屏幕外的图标)。返回恢复的数量。</summary>
    int RestoreAll();

    /// <summary>把某一项恢复原属性(它不再"已收纳"时用)。返回是否改动过。</summary>
    bool Restore(string path);

    /// <summary>还原资源管理器"显示隐藏项目"到用户原来的设置(从没改过则什么都不做)。</summary>
    void SetExplorerShowHidden(bool show);

    /// <summary>系统图标(此电脑/回收站/控制面板/网络)显隐 —— 这几个走注册表,和文件图标不是一套。</summary>
    void SetSystemIconsVisible(bool visible);
}
