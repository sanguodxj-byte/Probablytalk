using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Probablytalk;

// 图标按钮的悬浮说明：挂在按钮 root 上，指针进入/移动时用游戏统一 TooltipManager 显示，移出即隐藏。
// 与 UI/CounterTalkButton 同套路（继承 TooltipInterface + SetAndShowTooltip/HideTooltip），
// 区别只是文案来自可刷新委托（跟随语言实时求值）。TooltipManager 以鼠标位置定位，适配 UI 层。
internal sealed class IconHoverTooltip : TooltipInterface, IPointerEnterHandler, IPointerMoveHandler, IPointerExitHandler
{
    public Func<string> TextProvider;

    public override string GetTooltip()
    {
        try { return TextProvider != null ? (TextProvider() ?? string.Empty) : string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    public override bool IsTooltipEnabled()
    {
        return gameObject != null && gameObject.activeInHierarchy;
    }

    public void OnPointerEnter(PointerEventData eventData) { Show(); }
    public void OnPointerMove(PointerEventData eventData) { Show(); }
    public void OnPointerExit(PointerEventData eventData) { Hide(); }

    private void OnDisable() { Hide(); }

    private void Show()
    {
        if (string.IsNullOrEmpty(GetTooltip())) return;
        if (TooltipManager._instance != null && IsTooltipEnabled())
        {
            TooltipManager._instance.SetAndShowTooltip(GetTooltip(), this);
        }
    }

    private void Hide()
    {
        if (TooltipManager._instance != null)
        {
            TooltipManager._instance.HideTooltip(this);
        }
    }
}
