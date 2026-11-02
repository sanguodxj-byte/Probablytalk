using HarmonyLib;

namespace Probablytalk;

// 发言包挂钩：入队口补发与主对话注入。
// ModLoader 为 mod 程序集自动 PatchAll（new Harmony("mod." + ID) 后 PatchAll(assembly)），
// 本类不建 Harmony 实例；AddClient/AddNextClient 均无重载，按方法名定位无歧义。
// 预生成只对「已被玩家搭过话」的顾客生效（判定在 ClientSpeech.TryRequest 内），
// 因此新客的主对话前缀永远命中不到包，原版台词照常播放。
[HarmonyPatch]
internal static class ClientSpeechPatches
{
    // 入队口补发：postfix 时目标已按屏蔽规则决定是否入栈，由管线查栈过滤
    [HarmonyPatch(typeof(StoreClientManager), "AddClient")]
    [HarmonyPostfix]
    private static void AfterAddClient(StoreClientManager __instance, StoreClient storeClient)
    {
        ClientSpeech.OnClientEnqueued(__instance, storeClient);
    }

    [HarmonyPatch(typeof(StoreClientManager), "AddNextClient")]
    [HarmonyPostfix]
    private static void AfterAddNextClient(StoreClientManager __instance, StoreClient storeClient)
    {
        ClientSpeech.OnClientEnqueued(__instance, storeClient);
    }

    // 注入必须是前缀：StartMainDialogue 的方法体即 StartDialogue(mainDialogue)，
    // 后者同步读 plainText 显示，改写文本必须发生在它之前
    [HarmonyPatch(typeof(StoreClient), "StartMainDialogue")]
    [HarmonyPrefix]
    private static void BeforeStartMainDialogue(StoreClient __instance)
    {
        ClientSpeech.ApplyOnStartMainDialogue(__instance);
    }
}
