namespace DesktopBox.Controls;

/// <summary>按住"已经选中的那一条"时该怎么处理选中态。盒内磁贴与桌面层小图标两处共用。</summary>
public static class SelectionClickPolicy
{
    /// <summary>这次按下要不要先把多选塌成一条?
    /// 多选之后按住其中一条往外拖 = 整组一起拖(explorer 也是这么干的),所以<b>不能</b>在按下时就塌 ——
    /// 一塌,拖拽载荷里就只剩被按住的那条,表现就是"框选一堆只拖出来一个"。
    /// 塌陷要推迟到"松手时并没有拖动"再做。返回 true = 这次按下先别动选中态。</summary>
    public static bool DeferCollapseOnPress(bool ctrl, bool pressedIsSelected, int selectedCount)
        => !ctrl && pressedIsSelected && selectedCount > 1;
}
