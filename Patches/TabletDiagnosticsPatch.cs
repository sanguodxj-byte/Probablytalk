using HarmonyLib;

namespace Probablytalk;

// 平板（交易界面）点击诊断：StoreTablet.OnClick 是唯一的入口，它先走教程推进再切面板。
// 这两行日志用来定位「聊完天后平板点不开」到底是哪一环被挡住的：
//   - 日志出现但面板没开 → 问题在 NegociationUIManager 内部（面板被别的窗口压住/Refresh 抛异常）
//   - 日志完全没出现     → 世界点击被挡住（多半是本模组的对话窗没关，或 IClickable 被别的碰撞体截走）
// 只写日志，不改行为。
[HarmonyPatch]
internal static class TabletDiagnosticsPatch
{
    [HarmonyPatch(typeof(StoreTablet), "OnClick")]
    [HarmonyPrefix]
    private static void BeforeTabletClick()
    {
        if (ModEntry.Log == null) return;
        Dialogue current = DialogUIManager.Instance != null ? DialogUIManager.Instance.GetCurrentDialog() : null;
        bool chatOpen = CustomerChatUI.Instance != null && CustomerChatUI.Instance.IsInputOpen;
        ModEntry.Log.Log("tablet click: dialog=" + (current != null ? current.title : "none")
            + ", chatInputOpen=" + chatOpen
            + ", interactPaused=" + Interactable.isPaused);
    }

    [HarmonyPatch(typeof(StoreTablet), "OnClick")]
    [HarmonyPostfix]
    private static void AfterTabletClick()
    {
        if (ModEntry.Log == null) return;
        bool open = NegociationUIManager.Instance != null && NegociationUIManager.Instance.IsOpen;
        ModEntry.Log.Log("tablet click done: negotiationOpen=" + open);
    }
}
