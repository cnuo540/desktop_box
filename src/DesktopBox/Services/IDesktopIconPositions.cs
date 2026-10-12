using System;

namespace DesktopBox.Services;

/// <summary>桌面图标位置访问(抽象出来便于单测):路径 → 图标项句柄 → 读/写位置。
/// 位置是桌面视图坐标(与屏幕像素同系)。纯视图数据 —— 只改图标摆放,不动任何文件。</summary>
public interface IDesktopIconPositions
{
    /// <summary>把"直接位于桌面"的项路径解析成图标项句柄。失败返回 false。</summary>
    bool TryParse(string path, out IntPtr item);

    /// <summary>释放 TryParse 得到的句柄。</summary>
    void Free(IntPtr item);

    /// <summary>读取图标当前位置。</summary>
    bool TryGet(IntPtr item, out int x, out int y);

    /// <summary>把图标移动到指定位置。</summary>
    bool TrySet(IntPtr item, int x, int y);
}
