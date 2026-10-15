using System.Windows;
using System.Windows.Media;

namespace DesktopBox.Controls;

/// <summary>WPF 拖放落点的一个坑:命中判定用的是"鼠标正下方那个元素",而 <c>AllowDrop</c> <b>不继承</b>。
/// 所以要让"整块区域都能接住拖进来的文件",必须把整棵可视化子树逐个打开 —— 只在外层容器上设一次
/// 是不够的:把文件拖到里面的图标或空白处时,落点会停在那些没开 AllowDrop 的子元素上,直接被拒
/// (系统显示禁止光标)。</summary>
internal static class DropTargets
{
    public static void EnableOnSubtree(DependencyObject root)
    {
        if (root is UIElement element) element.AllowDrop = true;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            EnableOnSubtree(VisualTreeHelper.GetChild(root, i));
    }
}
