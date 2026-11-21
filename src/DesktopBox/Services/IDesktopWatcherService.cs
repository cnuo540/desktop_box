using System;

namespace DesktopBox.Services;

/// <summary>桌面目录里发生的变化类型。</summary>
public enum DesktopChangeKind
{
    /// <summary>新出现的文件/文件夹(唯一会触发"自动收纳"的类型)。</summary>
    Created,
    /// <summary>被删除(或被移出桌面)。盒子里若还引用着它,要把引用撤掉。</summary>
    Deleted,
    /// <summary>原地改名。盒子里的引用要跟着改到新路径,否则点开就是"目标当前不可用"。</summary>
    Renamed
}

/// <summary>一次桌面变化。<paramref name="OldPath"/> 只在 <see cref="DesktopChangeKind.Renamed"/> 时有值。</summary>
public readonly record struct DesktopItemChange(DesktopChangeKind Kind, string Path, string? OldPath = null);

/// <summary>盯着桌面目录,把"桌面上发生了什么"报出来。
///
/// 为什么需要它:Explorer 不会自动画出别的程序在桌面目录里新建的文件 ——
/// 实测在桌面目录里写入一个文件后桌面视图毫无变化,补一条单项 shell 通知才立刻出现。
/// 所以"拖到桌面的文件要看得见"必须由我们自己去补这一条通知。
///
/// 之所以要区分类型(v1.7.37):新增要自动收纳、删除要撤引用、改名要跟着改路径 ——
/// 三件事都得知道"是哪种变化"才能做对。</summary>
public interface IDesktopWatcherService : IDisposable
{
    /// <summary>桌面目录发生了变化(已做去抖:同一批操作合并成一批上报,同一路径后到的覆盖先到的)。</summary>
    event EventHandler<DesktopItemChange>? DesktopItemChanged;
}
