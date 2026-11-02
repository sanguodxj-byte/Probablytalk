using HarmonyLib;

namespace Probablytalk;

// 顾客档案挂钩：离店总结——DismissCurrentClient 的方法体会清空 currentClientInstance 并销毁柜上非自有物品，
// 所以必须在**前缀**取顾客与当次上下文（顾客自带货物那时还在）；原方法跑完不等待结果，
// 总结请求走后台，失败静默。
// 名册入口不在这里：它已收进顾客对话窗口的图标行（记事本图标，见 CustomerChatUI.OpenRoster），
// 游戏原生「选项」界面不再挂任何本 mod 的按钮。
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
}
