using HarmonyLib;

namespace Probablytalk;

// 验货事件钩子：游戏在 ClientExposeFeature 里逐条判定并当场揭穿，这里用同一个判定把
// 「他即将质问哪件货、标签写着什么、真身是什么」抄一份给上下文组件。
// 前缀跑在游戏揭穿之前，此时 CanClientExposeThisFeature 仍为 true，与游戏循环看到的集合一致；
// 该判定只读，不改任何状态。
[HarmonyPatch(typeof(StoreClient), "ClientExposeFeature")]
internal static class ClientInsightPatches
{
    [HarmonyPrefix]
    private static void BeforeClientExpose(StoreClient __instance, GameItem gameItem)
    {
        if (__instance == null || gameItem == null || gameItem.itemFeatures == null) return;
        foreach (ItemFeature feature in gameItem.itemFeatures)
        {
            if (feature == null) continue;
            if (!__instance.CanClientExposeThisFeature(feature)) continue;
            ClientInsight.Record(__instance, gameItem, feature);
        }
    }
}
