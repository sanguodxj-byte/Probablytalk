using HarmonyLib;

namespace Probablytalk;

// 常客行为挂钩：主对话启动记实到访，成交收尾记最近成交日。两个后缀都只读 identifier，
// 记账口径与异常处理在 RegularManager 内；StartMainDialogue 同时被发言注入前缀挂接，
// 前后缀互不影响
[HarmonyPatch]
internal static class RegularPatches
{
    [HarmonyPatch(typeof(StoreClient), "StartMainDialogue")]
    [HarmonyPostfix]
    private static void AfterStartMainDialogue(StoreClient __instance)
    {
        RegularManager.OnClientArrived(__instance != null ? __instance.identifier : null);
        // 顾客到柜：常客且方向为「顾客卖货」时挂好感度价格标签（其余方向一律不动）
        AffinityBook.OnClientArrived(__instance);
    }

    [HarmonyPatch(typeof(StoreClient), "CompleteBusiness")]
    [HarmonyPostfix]
    private static void AfterCompleteBusiness(StoreClient __instance)
    {
        RegularManager.OnDealCompleted(__instance != null ? __instance.identifier : null);
        // 赊账此刻才算数：只有真的成交，挂起的垫付额度才转成债务
        if (__instance != null)
        {
            CreditBook.CommitPending(__instance.identifier, __instance.displayName);
        }
    }
}
