using System;
using UnityEngine;

namespace Probablytalk;

// 生成节奏的界面承载：把三个滑条注入游戏原生「选项」菜单的一个 mod 分组（GameOptionRegistry）。
//
// 相比 ModConfig 窗口，这条路多拿到两样东西：
//   1) 分组标题走本地化查表——Section.label 是 Func<string>，会按当前语言实时求值（ModConfig 的分组名不可本地化）；
//   2) 真正的反应式联动——UI 每次改动后调 RefreshRules() 重算 interactable/noteVisible：
//      每日请求上限为 0（关闭 AI 发言）时，另两个滑条灰显，并在分组上方弹出提示。
//
// 文本类设置（自备端点/API 密钥/角色指令）没有原生输入控件（Kind 里没有 Input），仍留在 ModConfig 窗口。
internal static class SpeechOptions
{
    // 组内选项/分组的 id 后缀；注册进注册表时统一加「modId:」前缀，与 ModHelper.GameOptionId 同规则。
    // 分组前缀无法走 ModHelper（GameOptionSectionId 是私有方法），这里按同一规则显式复刻。
    private const string SectionSuffix = "ai_speech";
    private const string IdDailyCap = "daily_cap";
    private const string IdLineLength = "line_length";
    private const string IdTimeout = "timeout";
    private const string IdBargainMin = "bargain_min";
    private const string IdBargainMax = "bargain_max";
    private const string IdBargainCumulative = "bargain_cumulative";
    private const string IdBargainRounds = "bargain_rounds";

    private static string _modId;
    private static string _sectionId;
    private static string _fullDailyCap;

    // 在游戏「选项」菜单里登记本 mod 的分组；Init 期调用
    public static void Register(string modId)
    {
        _modId = modId;
        _sectionId = modId + ":" + SectionSuffix;
        _fullDailyCap = modId + ":" + IdDailyCap;

        GameOptionRegistry.Section section = GameOptionRegistry.GetOrAddSection(
            _sectionId,
            () => Loc("group_speech_label", "AI Customer Speech"),
            modId);
        // 分组提示：只在每日上限为 0 时显示；ModConfig 窗口没有这种条件横幅
        section.note = () => Loc("group_speech_note", "Daily request cap is 0: no AI speech is requested.");
        section.noteVisible = pending => PendingFloat(pending, _fullDailyCap) <= 0f;

        AddSlider(IdDailyCap, "opt_cap_label", "Daily Request Cap", "opt_cap_tooltip",
            "Max AI speech requests per in-game day. 0 turns AI speech off. Default: 12.",
            0f, 60f, false,
            () => SpeechSettings.DailyRequestCap, v => SpeechSettings.DailyRequestCap = Mathf.RoundToInt(v));
        AddSlider(IdLineLength, "opt_linelen_label", "Line Length Cap", "opt_linelen_tooltip",
            "Max characters kept from a single AI line. Default: 400.",
            80f, 2000f, true,
            () => SpeechSettings.LineLength, v => SpeechSettings.LineLength = Mathf.RoundToInt(v));
        AddSlider(IdTimeout, "opt_timeout_label", "Request Timeout (s)", "opt_timeout_tooltip",
            "Per-request timeout in seconds. Default: 120.",
            10f, 600f, true,
            () => SpeechSettings.TimeoutSeconds, v => SpeechSettings.TimeoutSeconds = Mathf.RoundToInt(v));

        // 议价门槛：提示词里的数字与实际夹取用的是同一份设置，模型看到的上下限就是真正生效的
        AddSlider(IdBargainMin, "opt_bargain_min_label", "Bargain Min Percent", "opt_bargain_min_tooltip",
            "Lower bound of one bargain step; lower asks are raised to it. Default: 1.",
            1f, 50f, false,
            () => SpeechSettings.BargainMinPercent, v => SpeechSettings.BargainMinPercent = Mathf.RoundToInt(v));
        AddSlider(IdBargainMax, "opt_bargain_max_label", "Bargain Max Percent", "opt_bargain_max_tooltip",
            "Upper bound of one bargain step; higher asks are capped to it. Default: 90.",
            5f, 90f, false,
            () => SpeechSettings.BargainMaxPercent, v => SpeechSettings.BargainMaxPercent = Mathf.RoundToInt(v));
        AddSlider(IdBargainCumulative, "opt_bargain_cum_label", "Total Bargain Cap", "opt_bargain_cum_tooltip",
            "Total movement allowed for one customer across rounds. Default: 60.",
            5f, 90f, false,
            () => SpeechSettings.BargainMaxCumulative, v => SpeechSettings.BargainMaxCumulative = Mathf.RoundToInt(v));
        AddSlider(IdBargainRounds, "opt_bargain_rounds_label", "Bargain Rounds", "opt_bargain_rounds_tooltip",
            "Bargain rounds one customer allows per day. Default: 3.",
            1f, 5f, false,
            () => SpeechSettings.BargainMaxRounds, v => SpeechSettings.BargainMaxRounds = Mathf.RoundToInt(v));
    }

    // 卸载本 mod 的全部分组与条目；OnDisable 期调用
    public static void Unregister()
    {
        if (!string.IsNullOrEmpty(_modId)) GameOptionRegistry.RemoveBySource(_modId);
        _modId = null;
    }

    private static void AddSlider(string suffix, string labelKey, string labelFallback, string tipKey, string tipFallback,
        float min, float max, bool gateByCap, Func<float> get, Action<float> set)
    {
        GameOptionRegistry.Entry entry = GameOptionRegistry.Slider(
            _modId + ":" + suffix,
            () => Loc(labelKey, labelFallback),
            get,
            set,
            min,
            max,
            true,
            () => Loc(tipKey, tipFallback),
            null);
        entry.source = _modId;
        // 只有单句长度与超时随「每日上限=0」灰显；上限滑条本身不能被自己关掉
        if (gateByCap)
        {
            entry.interactable = pending => PendingFloat(pending, _fullDailyCap) > 0f;
        }
        GameOptionRegistry.Add(_sectionId, entry);
    }

    // 反应式回调拿的是「待应用值」快照，改动未点应用时也即时反映；异常一律当 0，退化成「已关闭」这一保守判定
    private static float PendingFloat(GameOptionRegistry.IPendingValues pending, string optionId)
    {
        try
        {
            return pending != null ? pending.GetFloat(optionId) : 0f;
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    // 查表回退链：本地化值 → 英文兜底字面量；键缺失时 GetLocalized 原样返回 key，这里转为可读英文
    private static string Loc(string key, string fallback)
    {
        if (string.IsNullOrEmpty(_modId)) return fallback;
        string value = ModHelper.GetLocalized(_modId, key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }
}