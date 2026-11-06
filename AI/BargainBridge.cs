using System;
using System.Collections.Generic;

namespace Probablytalk;

// 议价执行层：只做「标签 → 游戏接口」的翻译，不做任何判断。
//
// 明确边界：
//   - 不夹取百分比：模型给多少就传多少（上下限由模型遵守，规矩只写在提示词 PromptBook 里说一次）。
//   - 不校验买卖方向：kind 与 intent 是否匹配由模型自己保证；传错方向时游戏自身会静默不生效。
//   - 不拦截轮次与累计：轮数与累计上限是给模型看的规矩，不是代码的门槛。
//   - 不重置游戏内部标志、不摘除旧特性：那属于替模型补逻辑，已移除。
// 这里唯一保留的是「kind 字符串映射不到任何游戏方法」时不调用（无法翻译，不是判断）。
//
// 计数只用于回显事实（本轮已用几轮、累计报过多少），不参与任何放行/拦截。
internal static class BargainBridge
{
    private sealed class State
    {
        public int Day;
        public int Rounds;
        public int DiscountApplied;
        public int MarkupApplied;
    }

    private static readonly Dictionary<string, State> States = new Dictionary<string, State>();

    // 给模型看的规矩值：仅用于上下文与提示词展示，代码不据此拦截
    internal static int MaxRoundsPerClient => SpeechSettings.BargainMaxRounds;

    internal static int MaxCumulativePercent => SpeechSettings.BargainMaxCumulative;

    internal static int MaxPercent => SpeechSettings.BargainMaxPercent;

    internal static int MinPercent => SpeechSettings.BargainMinPercent;

    internal static void Shutdown()
    {
        States.Clear();
    }

    // 读档/新档：清轮次与幅度，避免上一档的同日谈判记录被复用
    internal static void ResetSession()
    {
        States.Clear();
    }

    internal static int RoundsUsed(string identifier)
    {
        State state;
        return !string.IsNullOrEmpty(identifier) && States.TryGetValue(identifier, out state) ? state.Rounds : 0;
    }

    internal static int DiscountSoFar(string identifier)
    {
        State state;
        return !string.IsNullOrEmpty(identifier) && States.TryGetValue(identifier, out state) ? state.DiscountApplied : 0;
    }

    internal static int MarkupSoFar(string identifier)
    {
        State state;
        return !string.IsNullOrEmpty(identifier) && States.TryGetValue(identifier, out state) ? state.MarkupApplied : 0;
    }

    // 原样转发：kind 决定调哪个游戏方法，percent 不做任何处理。
    // 返回是否成功发起调用（不代表游戏一定接受，接受与否由游戏自己的规则决定）
    internal static bool Apply(StoreClient client, string kind, int percent)
    {
        if (client == null || string.IsNullOrWhiteSpace(kind)) return false;
        BargainUIManager manager = BargainUIManager.Instance;
        if (manager == null) return false;

        string tag = kind.Trim().ToLowerInvariant();
        State state = StateOf(client.identifier);
        bool accepted;
        try
        {
            switch (tag)
            {
                case "discount":
                    accepted = manager.CanOfferDiscount();
                    if (accepted) manager.OfferDiscount(percent);
                    break;
                case "markup":
                    accepted = manager.CanOfferMarkup();
                    if (accepted) manager.OfferMarkup(percent);
                    break;
                case "buyingmarkup":
                    accepted = manager.CanOfferBuyingMarkup();
                    if (accepted) manager.OfferBuyingMarkup(percent);
                    break;
                case "buyingdiscount":
                    accepted = manager.CanDemandBuyingDiscount();
                    if (accepted) manager.DemandBuyingDiscount(percent);
                    break;
                default:
                    // 标签无法映射到任何游戏方法：不执行（这不是判断，只是翻译失败）
                    if (ModEntry.Log != null) ModEntry.Log.Log("bargain skipped: unknown kind '" + kind + "'");
                    return false;
            }
        }
        catch (Exception ex)
        {
            if (ModEntry.Log != null) ModEntry.Log.Warning("bargain apply failed: " + ex.Message);
            return false;
        }

        // 只记录事实：游戏 CanOffer* 为 false 时什么都没发生，不能把这次计入轮次/幅度
        if (!accepted)
        {
            if (ModEntry.Log != null) ModEntry.Log.Log("bargain not accepted: " + tag + " " + percent + "%");
            return false;
        }

        state.Rounds++;
        if (tag == "discount" || tag == "buyingdiscount") state.DiscountApplied += percent;
        else state.MarkupApplied += percent;
        if (ModEntry.Log != null)
        {
            ModEntry.Log.Log("bargain forwarded: " + tag + " " + percent + "%"
                + " (round " + state.Rounds + ", discount " + state.DiscountApplied
                + "%, markup " + state.MarkupApplied + "%)");
        }
        return true;
    }

    private static State StateOf(string identifier)
    {
        int day = CurrentDay();
        State state;
        if (string.IsNullOrEmpty(identifier)) identifier = "unknown";
        if (!States.TryGetValue(identifier, out state) || state.Day != day)
        {
            state = new State { Day = day };
            States[identifier] = state;
        }
        return state;
    }

    // 买卖方向：这是事实（游戏自己的判定），供模型决定用哪两个 kind，代码不据此拦截
    internal static string DirectionOf(StoreClient client)
    {
        if (client == null) return "none";
        bool buying = client.IsClientBuying();
        bool selling = client.IsClientSelling();
        if (buying && selling) return "both";
        if (buying) return "playerSells";
        return selling ? "playerBuys" : "none";
    }

    private static int CurrentDay()
    {
        StoreStation station = StoreStation.Instance;
        return station != null ? station.dayCounter : 0;
    }
}
