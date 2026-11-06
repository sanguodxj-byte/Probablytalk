using HarmonyLib;

namespace Probablytalk;

// 顾客档案挂钩：顾客离店前缀触发。DismissCurrentClient 的方法体会清空 currentClientInstance
// 并销毁柜上非自有物品，因此必须在前缀执行。
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
        CreditBook.DiscardPending(client != null ? client.identifier : null);
        CustomerArchive.OnClientLeave(client);
        // 离店先摘好感度价格标签，再发起一次常客好感评估（失败只留日志）
        AffinityBook.OnClientLeave(client);
    }
}
