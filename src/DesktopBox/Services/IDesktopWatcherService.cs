using System;

namespace DesktopBox.Services;

/// <summary>盯着桌面目录,把"新出现的桌面项"报出来。
///
/// 为什么需要它:Explorer 不会自动画出别的程序在桌面目录里新建的文件 ——
/// 实测在桌面目录里写入一个文件后桌面视图毫无变化,补一条单项 shell 通知才立刻出现。
/// 所以"拖到桌面的文件要看得见"必须由我们自己去补这一条通知。</summary>
public interface IDesktopWatcherService : IDisposable
{
    /// <summary>桌面上出现了新的文件/文件夹(已做去抖,同一批拖入只报一次)。</summary>
    event EventHandler<string>? DesktopItemAppeared;
}
