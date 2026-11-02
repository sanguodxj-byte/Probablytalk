using HarmonyLib;

namespace Probablytalk;

// 顾客档案挂钩：
//   1) 离店总结——DismissCurrentClient 的方法体会清空 currentClientInstance 并销毁柜上非自有物品，
//      所以必须在**前缀**取顾客与当次上下文（顾客自带货物那时还在）；原方法跑完不等待结果，
//      总结请求走后台，失败静默。
//   2) 名册入口——挂在游戏原生「选项」界面（GameOptionUIManager.OpenUI 建出窗口 id "game_options"）。
//      GameOptionRegistry 只有 Toggle/Slider/Dropdown/KeyBind 四种控件、放不下列表按钮，
//      但 CustomUIBuilder(manager, window) 构造器是 public，可在窗口建好后按 id 取回并追加一个入口按钮。
[HarmonyPatch]
internal static class ArchivePatches
{
    [HarmonyPatch(typeof(PlayerStore), "DismissCurrentClient")]
    [HarmonyPrefix]
    private static void BeforeDismissCurrentClient()
    {
        PlayerStore store = PlayerStore.Instance;
        if (store == null || store.currentClientInstance == null) return;
        StoreClient client = store.currentClientInstance.GetClientBlueprint();
        // 未成交就离店：清掉本轮挂起的赊账额度。垫付只加在当时那个实例的预算上，
        // 实例销毁即无痕；不清则同一 identifier 下次赊账会叠加，成交时重复记账
        CreditBook.DiscardPending(client != null ? client.identifier : null);
        CustomerArchive.SummarizeOnLeave(client);
    }

    [HarmonyPatch(typeof(GameOptionUIManager), "OpenUI")]
    [HarmonyPostfix]
    private static void AfterOpenOptionsUI()
    {
        if (CustomUIManager.Instance == null) return;
        CustomUIWindow window = CustomUIManager.Instance.GetWindow("game_options");
        if (window == null || !window.IsAlive) return;
        CustomerRosterUI.AttachToOptionsWindow(window, ModEntry.ModId);
    }
}
