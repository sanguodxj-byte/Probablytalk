using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using TMPro;
using UnityEngine;

namespace Probablytalk;

// 设置窗口集：把 mod 的界面入口按用途拆成三个独立窗口（RimTalk 式），由顾客对话窗口左下角三个方形图标按钮分别唤出：
//   扳手   → API 配置：连接（桌面端/网页地址、P2 密钥、设备码授权）+ 兜底端点（提供商/端点/密钥/模型）
//   三横线 → 调试：连接诊断（本地/网页/兜底/生效通道/在线态）、最近错误、信息，及「检测 / 刷新」动作
//   齿轮   → 设置：角色指令 + 生成节奏（每日上限 / 单句长度 / 超时）+ 议价门槛（四项）
//            —— 原游戏原生「选项」菜单里本 mod 的那一组整体搬到这里，原生菜单不再登记条目
// （第四个记事本图标开顾客名册，窗口由 CustomerRosterUI 自己承载，不在本文件）
// 无热键；窗口内容可滚动。
//
// 数据写入：文本/枚举项经 Player2Config（底层同一份 ModConfig ConfigEntry），节奏经 SpeechSettings
// （同一份 speech_settings.txt）；与模组设置窗口、原生「选项」菜单共用同一份数据，不存在双写。
internal sealed class SettingsUI : MonoBehaviour
{
    private const string ApiWindowId = "probablytalk_api";
    private const string DebugWindowId = "probablytalk_debug";
    private const string GearWindowId = "probablytalk_settings";

    private string modId;
    private Player2Config config;
    private Player2Client client;
    private Player2Auth auth;
    private ModLog log;

    private CustomUIWindow apiWindow;
    private CustomUIWindow debugWindow;
    private CustomUIWindow gearWindow;

    private CustomUIElement authButton;
    private CustomUIElement authHint;
    private CustomUIElement copyAuthButton;
    private CustomUIElement detectButton;
    private CustomUIElement endpointInput;
    private CustomUIElement fetchModelsButton;

    // 调试窗口动态元素
    private CustomUIElement dbgStatsLabel;
    private CustomUIElement clearButton;
    private CustomUIElement copyButton;
    private CustomUIElement detailMetaLabel;
    private CustomUIElement detailErrorLabel;
    private CustomUIElement detailPromptInput;
    private CustomUIElement detailResponseInput;

    private bool detecting;
    private bool fetchingModels;
    private static List<string> cachedModels;
    private static ApiLogEntry selectedLog;

    public static SettingsUI Instance { get; private set; }

    public void Setup(string modId, Player2Config config, Player2Client client, Player2Auth auth, ModLog log)
    {
        this.modId = modId;
        this.config = config;
        this.client = client;
        this.auth = auth;
        this.log = log;
        if (auth != null) auth.Changed += OnAuthChanged;
        ApiLogTracker.Changed += OnLogChanged;
        Instance = this;
    }

    private void OnDestroy()
    {
        if (auth != null) auth.Changed -= OnAuthChanged;
        ApiLogTracker.Changed -= OnLogChanged;
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (apiWindow != null && !apiWindow.IsAlive) DetachApi();
        if (debugWindow != null && !debugWindow.IsAlive) DetachDebug();
        if (gearWindow != null && !gearWindow.IsAlive) DetachGear();
    }

    // ================= API 配置 =================
    public void OpenApiWindow()
    {
        if (CustomUIManager.Instance == null) return;
        if (apiWindow != null && apiWindow.IsAlive) apiWindow.Close();

        CustomUIBuilder b = MakeWindow(ApiWindowId, "api_title", "API Configuration", 540f, 420f, DetachApi);
        apiWindow = b.GetWindow();

        // 首行 Player2：单独起，仅提供检测桌面端和网页授权
        SectionHeader(b, "lbl_player2", "Player2", "sec_player2");
        b.BeginRow(8f, TextAnchor.MiddleLeft);
        b.AddButton(Loc("btn_detect", "Detect desktop app"), OnDetect, "conn_detect");
        detectButton = b.Last;
        detectButton.SetFlexible(1f, 0f);

        b.AddButton(Loc("btn_authorize", "Authorize via web"), OnAuthorize, "conn_auth");
        authButton = b.Last;
        authButton.SetFlexible(1f, 0f);
        b.End();
        b.AddSpace(6f);

        // 批准链接：游戏扫描器禁止 mod 自动打开动态 URL（process-start 硬拒绝），
        // 授权开始时在这里显示验证码与完整链接，由玩家复制后手动在浏览器打开
        b.BeginRow(6f, TextAnchor.MiddleLeft);
        b.AddLabel(string.Empty, "auth_hint");
        authHint = b.Last;
        authHint.SetVisible(false);
        b.AddFlexibleSpace();
        b.AddButton(Loc("btn_copy_link", "Copy link"), OnCopyAuthLink, "btn_copy_link");
        copyAuthButton = b.Last;
        copyAuthButton.SetFixedWidth(110f);
        copyAuthButton.SetVisible(false);
        b.End();
        b.AddSpace(10f);

        // 提供商
        b.BeginRow(6f, TextAnchor.MiddleLeft);
        b.AddLabel(Loc("setting_provider_label", "Provider"), "lbl_provider");
        b.AddFlexibleSpace();
        b.AddDropdown(Player2Config.ProviderOptions, config.ProviderIndex, OnProviderChanged, "provider");
        b.Last.SetFixedWidth(320f);
        b.End();

        // 端点：支持自动补全到完整 URL
        b.BeginRow(6f, TextAnchor.MiddleLeft);
        b.AddLabel(Loc("setting_endpoint_label", "Endpoint"), "lbl_endpoint");
        b.AddFlexibleSpace();
        b.AddInput(string.Empty, config.FallbackEndpointRaw, v => config.SetFallbackEndpointRaw(v), "endpoint");
        endpointInput = b.Last;
        endpointInput.SetFixedWidth(320f);
        if (endpointInput.input != null)
        {
            endpointInput.input.onEndEdit.AddListener(OnEndpointEndEdit);
        }
        b.End();

        // 密钥
        b.BeginRow(6f, TextAnchor.MiddleLeft);
        b.AddLabel(Loc("setting_apikey_label", "API Key"), "lbl_apikey");
        b.AddFlexibleSpace();
        b.AddInput(string.Empty, config.FallbackApiKey, v => config.SetFallbackApiKey(v), "apikey");
        b.Last.SetFixedWidth(320f);
        b.End();

        // 模型：输入框 + 获取按钮，获取成功后改为下拉列表
        BuildModelRow(b);

        b.End();
        CloseRow(b, CloseApi);
        b.Center();
        b.Show();
        CustomUIBuilder.RebuildLayout(apiWindow.ContentRoot);
        RefreshApiButtons();
    }

    private void BuildModelRow(CustomUIBuilder b)
    {
        b.BeginRow(6f, TextAnchor.MiddleLeft);
        b.AddLabel(Loc("setting_model_label", "Model"), "lbl_model");
        b.AddFlexibleSpace();

        if (cachedModels != null && cachedModels.Count > 0)
        {
            int selectedIdx = cachedModels.IndexOf(config.ModelRaw);
            if (selectedIdx < 0) selectedIdx = 0;
            b.AddDropdown(cachedModels, selectedIdx, idx =>
            {
                if (idx >= 0 && idx < cachedModels.Count)
                {
                    config.SetModel(cachedModels[idx]);
                }
            }, "model_dropdown");
            b.Last.SetFixedWidth(230f);
        }
        else
        {
            b.AddInput(string.Empty, config.ModelRaw, v => config.SetModel(v), "model");
            b.Last.SetFixedWidth(230f);
        }

        string fetchText = fetchingModels ? Loc("btn_fetching", "Fetching...") : Loc("btn_fetch_models", "Fetch");
        b.AddButton(fetchText, OnFetchModelsClicked, "btn_fetch");
        fetchModelsButton = b.Last;
        fetchModelsButton.SetFixedWidth(84f);
        fetchModelsButton.SetInteractable(!fetchingModels);
        b.End();
    }

    private void OnEndpointEndEdit(string text)
    {
        string normalized = Player2Config.NormalizeEndpointUrl(text);
        config.SetFallbackEndpoint(normalized);
        if (endpointInput != null && endpointInput.input != null)
        {
            endpointInput.input.text = normalized;
        }
    }

    private void OnFetchModelsClicked()
    {
        if (fetchingModels || client == null) return;
        string endpoint = config.EffectiveFallbackEndpoint();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            SetButtonTempText(fetchModelsButton, Loc("btn_fetch_empty", "Endpoint empty"), Loc("btn_fetch_models", "Fetch"), 1.8f);
            return;
        }

        fetchingModels = true;
        if (fetchModelsButton != null)
        {
            fetchModelsButton.SetText(Loc("btn_fetching", "Fetching..."));
            fetchModelsButton.SetInteractable(false);
        }

        client.FetchModelsAsync(endpoint, config.FallbackApiKey).ContinueWith(t =>
        {
            MainThreadPump.Enqueue(() =>
            {
                fetchingModels = false;
                bool ok = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result != null && t.Result.Count > 0;
                if (ok)
                {
                    cachedModels = t.Result;
                    if (!cachedModels.Contains(config.ModelRaw))
                    {
                        config.SetModel(cachedModels[0]);
                    }
                    if (apiWindow != null && apiWindow.IsAlive)
                    {
                        OpenApiWindow();
                    }
                }
                else
                {
                    if (fetchModelsButton != null)
                    {
                        fetchModelsButton.SetInteractable(true);
                        SetButtonTempText(fetchModelsButton, Loc("btn_fetch_fail", "Failed"), Loc("btn_fetch_models", "Fetch"), 2f);
                    }
                }
            });
        });
    }

    // ================= 调试（模仿 RimTalk 架构） =================
    public void OpenDebugWindow()
    {
        if (CustomUIManager.Instance == null) return;
        if (debugWindow != null && debugWindow.IsAlive) debugWindow.Close();

        CustomUIBuilder b = MakeWindow(DebugWindowId, "dbg_title", "Debug", 740f, 520f, DetachDebug);
        debugWindow = b.GetWindow();

        // 顶栏：统计指标与控制按钮
        b.BeginRow(6f, TextAnchor.MiddleLeft);
        string channelStatus = client != null && client.IsOnline ? Loc("dbg_online", "Online") : Loc("dbg_offline", "Offline");
        string stats = $"{Loc("dbg_total_calls", "Calls")}: {ApiLogTracker.TotalCalls} | {TokenTotals()} | {channelStatus}";
        b.AddLabel(stats, "dbg_stats_label");
        dbgStatsLabel = b.Last;
        b.AddFlexibleSpace();

        b.AddButton(Loc("dbg_refresh", "Refresh"), OnDebugRefreshClicked, "dbg_refresh_btn");
        b.Last.SetFixedWidth(84f);

        b.AddButton(Loc("dbg_clear", "Clear"), OnDebugClearClicked, "dbg_clear_btn");
        clearButton = b.Last;
        clearButton.SetFixedWidth(84f);
        b.End();
        b.AddSpace(6f);

        // 上栏：请求历史列表
        SectionHeader(b, "dbg_history_header", "Request History", "sec_history");
        IReadOnlyList<ApiLogEntry> entries = ApiLogTracker.Entries;
        if (entries == null || entries.Count == 0)
        {
            b.AddLabel(Loc("dbg_no_records", "(No requests recorded yet)"), "lbl_no_records");
        }
        else
        {
            int displayCount = Mathf.Min(entries.Count, 25);
            for (int i = 0; i < displayCount; i++)
            {
                ApiLogEntry entry = entries[i];
                b.BeginRow(4f, TextAnchor.MiddleLeft);
                string tokenPart = TokenLabel(entry);
                string timing = entry.ElapsedMs + "ms" + (string.IsNullOrEmpty(tokenPart) ? string.Empty : " | " + tokenPart);
                string rowTitle = $"[{entry.Timestamp:HH:mm:ss}] {entry.Channel} ({timing}) - {entry.State}";
                bool isSelected = selectedLog != null && selectedLog.Id == entry.Id;
                string btnLabel = isSelected ? $"> {rowTitle}" : rowTitle;
                b.AddButton(btnLabel, () => SelectLogEntry(entry), "log_" + entry.Id);
                b.Last.SetFlexible(1f, 0f);
                b.End();
            }
        }
        b.AddSpace(10f);

        // 下栏：选中记录详情面板
        SectionHeader(b, "dbg_detail_header", "Details", "sec_details");
        b.BeginRow(6f, TextAnchor.MiddleLeft);
        b.AddLabel(string.Empty, "lbl_detail_meta");
        detailMetaLabel = b.Last;
        b.AddFlexibleSpace();
        b.AddButton(Loc("dbg_copy", "Copy"), CopySelectedLog, "btn_copy_log");
        copyButton = b.Last;
        copyButton.SetFixedWidth(84f);
        b.End();

        b.AddLabel(string.Empty, "lbl_detail_error");
        detailErrorLabel = b.Last;

        b.AddLabel(Loc("dbg_prompt", "Prompt:"), "lbl_prompt_title");
        b.AddInput(string.Empty, string.Empty, null, "det_prompt");
        detailPromptInput = b.Last;
        if (detailPromptInput.input != null)
        {
            detailPromptInput.input.lineType = TMP_InputField.LineType.MultiLineNewline;
            detailPromptInput.input.readOnly = true;
        }
        detailPromptInput.SetPreferredSize(-1f, 70f);

        b.AddLabel(Loc("dbg_response", "Response:"), "lbl_resp_title");
        b.AddInput(string.Empty, string.Empty, null, "det_response");
        detailResponseInput = b.Last;
        if (detailResponseInput.input != null)
        {
            detailResponseInput.input.lineType = TMP_InputField.LineType.MultiLineNewline;
            detailResponseInput.input.readOnly = true;
        }
        detailResponseInput.SetPreferredSize(-1f, 70f);

        // 默认显示选中项或首项
        if (selectedLog == null && entries != null && entries.Count > 0)
        {
            selectedLog = entries[0];
        }
        UpdateDetailPanel();

        b.End();
        CloseRow(b, CloseDebug);
        b.Center();
        b.Show();
        CustomUIBuilder.RebuildLayout(debugWindow.ContentRoot);
    }

    private void SelectLogEntry(ApiLogEntry entry)
    {
        selectedLog = entry;
        UpdateDetailPanel();
    }

    private void UpdateDetailPanel()
    {
        if (selectedLog == null)
        {
            detailMetaLabel?.SetText(Loc("dbg_select_hint", "(Select a request to inspect details)"));
            detailErrorLabel?.SetText(string.Empty);
            if (detailPromptInput?.input != null) detailPromptInput.input.text = string.Empty;
            if (detailResponseInput?.input != null) detailResponseInput.input.text = string.Empty;
            return;
        }

        string detailToken = TokenLabel(selectedLog);
        detailMetaLabel?.SetText($"[{selectedLog.Timestamp:yyyy-MM-dd HH:mm:ss}] {selectedLog.Channel} - {selectedLog.State} "
            + $"({selectedLog.ElapsedMs}ms | {selectedLog.PromptChars} {Loc("dbg_chars", "chars")}"
            + (string.IsNullOrEmpty(detailToken) ? string.Empty : " | " + detailToken) + ")");
        detailErrorLabel?.SetText(string.IsNullOrEmpty(selectedLog.Error) ? string.Empty : $"{Loc("dbg_error", "Error")}: {selectedLog.Error}");
        if (detailPromptInput?.input != null) detailPromptInput.input.text = selectedLog.PromptText ?? string.Empty;
        if (detailResponseInput?.input != null) detailResponseInput.input.text = selectedLog.ResponseText ?? string.Empty;
    }

    private void CopySelectedLog()
    {
        if (selectedLog == null) return;
        var sb = new StringBuilder();
        sb.AppendLine($"[{selectedLog.Timestamp:yyyy-MM-dd HH:mm:ss}] {selectedLog.Channel} - {selectedLog.State} ({selectedLog.ElapsedMs}ms)");
        string copyToken = TokenLabel(selectedLog);
        sb.AppendLine($"{selectedLog.PromptChars} prompt chars | {selectedLog.ResponseChars} response chars"
            + (string.IsNullOrEmpty(copyToken) ? string.Empty : " | " + copyToken));
        if (!string.IsNullOrEmpty(selectedLog.Error)) sb.AppendLine($"Error: {selectedLog.Error}");
        sb.AppendLine("--- Prompt ---");
        sb.AppendLine(selectedLog.PromptText);
        sb.AppendLine("--- Response ---");
        sb.AppendLine(selectedLog.ResponseText);
        GUIUtility.systemCopyBuffer = sb.ToString();
        SetButtonTempText(copyButton, Loc("dbg_copied", "Copied!"), Loc("dbg_copy", "Copy"), 1.5f);
    }

    private void OnDebugRefreshClicked()
    {
        client?.RefreshOnlineState();
        if (debugWindow != null && debugWindow.IsAlive)
        {
            OpenDebugWindow();
        }
    }

    private void OnDebugClearClicked()
    {
        ApiLogTracker.Clear();
        selectedLog = null;
        SetButtonTempText(clearButton, Loc("dbg_cleared", "Cleared"), Loc("dbg_clear", "Clear"), 1.2f);
        if (debugWindow != null && debugWindow.IsAlive)
        {
            OpenDebugWindow();
        }
    }

    private void OnLogChanged()
    {
        if (debugWindow != null && debugWindow.IsAlive)
        {
            OpenDebugWindow();
        }
    }

    // token 口径要分清来源：端点回报 usage 的是实测值，估算值一律加 ~ 前缀，
    // 免得把 TokenMeter 的字符粗估当成实测消耗读
    private static string TokenLabel(ApiLogEntry entry)
    {
        if (entry == null || (entry.PromptTokens <= 0 && entry.CompletionTokens <= 0)) return string.Empty;
        string mark = entry.TokensEstimated ? "~" : string.Empty;
        return $"{LocStatic("dbg_tokens", "tokens")} {mark}{entry.PromptTokens}->{mark}{entry.CompletionTokens}";
    }

    private static string TokenTotals()
    {
        string text = $"{LocStatic("dbg_tokens", "tokens")} {ApiLogTracker.TotalPromptTokens}->{ApiLogTracker.TotalCompletionTokens}";
        if (ApiLogTracker.TotalEstimated > 0)
        {
            text += $" ({ApiLogTracker.TotalEstimated} {LocStatic("dbg_tokens_est", "estimated")})";
        }
        return text;
    }

    // ================= 设置（齿轮） =================
    // 游戏原生「选项」菜单里本 mod 的那一组已整体搬进这里：七项滑条、随每日上限灰显的联动、
    // 以及上限为 0 时的提示横幅，都在这一屏里；原生菜单不再登记任何条目。
    private readonly List<CustomUIElement> capGated = new List<CustomUIElement>();
    private CustomUIElement capNote;

    public void OpenSettingsWindow()
    {
        if (CustomUIManager.Instance == null) return;
        if (gearWindow != null && gearWindow.IsAlive) gearWindow.Close();

        CustomUIBuilder b = MakeWindow(GearWindowId, "set_title", "Settings", 540f, 460f, DetachGear);
        gearWindow = b.GetWindow();
        capGated.Clear();
        capNote = null;

        // 提示词
        SectionHeader(b, "section_prompt", "Role Instruction", "sec_prompt");
        b.AddInput(string.Empty, config.PromptRaw, OnPromptChanged, "prompt");
        CustomUIElement promptInput = b.Last;
        if (promptInput.input != null)
        {
            promptInput.input.lineType = TMP_InputField.LineType.MultiLineNewline;
            promptInput.input.richText = false;
            promptInput.input.characterLimit = 4000;
        }
        promptInput.SetFlexible(1f, 0f);
        promptInput.SetPreferredSize(-1f, 90f);
        b.AddSpace(12f);

        // 生成节奏
        SectionHeader(b, "section_speech", "Speech Pacing", "sec_speech");
        LabeledSlider(b, "opt_cap_label", "Daily Request Cap", "opt_cap_tooltip", "Max AI speech requests per in-game day. 0 turns AI speech off.",
            0f, 60f, SpeechSettings.DailyRequestCap,
            v => { SpeechSettings.DailyRequestCap = Mathf.RoundToInt(v); ApplyCapGate(); }, "cap");
        Gate(LabeledSlider(b, "opt_linelen_label", "Line Length Cap", "opt_linelen_tooltip", "Max characters kept from a single AI line.",
            80f, 2000f, SpeechSettings.LineLength,
            v => SpeechSettings.LineLength = Mathf.RoundToInt(v), "linelen"));
        Gate(LabeledSlider(b, "opt_timeout_label", "Request Timeout (s)", "opt_timeout_tooltip", "Per-request timeout in seconds.",
            10f, 600f, SpeechSettings.TimeoutSeconds,
            v => SpeechSettings.TimeoutSeconds = Mathf.RoundToInt(v), "timeout"));

        // 上限为 0 的提示横幅：紧跟生成节奏一组，默认藏起来，由 ApplyCapGate 现算现显
        b.BeginRow(4f, TextAnchor.MiddleLeft);
        b.AddLabel(string.Empty, "cap_note");
        capNote = b.Last;
        capNote.SetFlexible(1f, 0f);
        b.End();
        b.AddSpace(10f);

        // 议价门槛：提示词里下发的数字与这里同一份设置，模型看到的上下限就是真正生效的
        SectionHeader(b, "section_bargain", "Bargain Limits", "sec_bargain");
        LabeledSlider(b, "opt_bargain_min_label", "Bargain Min Percent", "opt_bargain_min_tooltip", "Lower bound of one bargain step.",
            1f, 50f, SpeechSettings.BargainMinPercent,
            v => SpeechSettings.BargainMinPercent = Mathf.RoundToInt(v), "bargain_min");
        LabeledSlider(b, "opt_bargain_max_label", "Bargain Max Percent", "opt_bargain_max_tooltip", "Upper bound of one bargain step.",
            5f, 90f, SpeechSettings.BargainMaxPercent,
            v => SpeechSettings.BargainMaxPercent = Mathf.RoundToInt(v), "bargain_max");
        LabeledSlider(b, "opt_bargain_cum_label", "Total Bargain Cap", "opt_bargain_cum_tooltip", "Total movement allowed for one customer.",
            5f, 90f, SpeechSettings.BargainMaxCumulative,
            v => SpeechSettings.BargainMaxCumulative = Mathf.RoundToInt(v), "bargain_cum");
        LabeledSlider(b, "opt_bargain_rounds_label", "Bargain Rounds", "opt_bargain_rounds_tooltip", "Bargain rounds one customer allows per day.",
            1f, 5f, SpeechSettings.BargainMaxRounds,
            v => SpeechSettings.BargainMaxRounds = Mathf.RoundToInt(v), "bargain_rounds");

        b.End();
        CloseRow(b, CloseGear);
        b.Center();
        b.Show();
        ApplyCapGate();
        CustomUIBuilder.RebuildLayout(gearWindow.ContentRoot);
    }

    private void Gate(CustomUIElement element)
    {
        if (element != null) capGated.Add(element);
    }

    // 每日上限=0 等价于关闭 AI 发言：单句长度与超时这时没有对象可作用，灰掉比留着更诚实；
    // 上限滑条自己不能被关掉
    private void ApplyCapGate()
    {
        bool on = SpeechSettings.DailyRequestCap > 0;
        foreach (CustomUIElement element in capGated) element?.SetInteractable(on);
        capNote?.SetText(on ? string.Empty : Loc("group_speech_note", "Daily request cap is 0: no AI speech is requested."));
    }

    private CustomUIElement LabeledSlider(CustomUIBuilder b, string labelKey, string labelFallback,
        string tipKey, string tipFallback, float min, float max, float initial, Action<float> onChanged, string id)
    {
        b.BeginRow(4f, TextAnchor.MiddleLeft);
        b.AddLabel(Loc(labelKey, labelFallback), "lbl_" + id);
        b.AddFlexibleSpace();
        b.AddSlider(min, max, initial, onChanged, true, id);
        CustomUIElement slider = b.Last;
        slider.SetFixedWidth(280f);
        slider.SetTooltip(Loc(tipKey, tipFallback) + "  [" + Mathf.RoundToInt(min) + "-" + Mathf.RoundToInt(max) + "]");
        b.End();
        return slider;
    }

    // ================= 构建辅助 =================
    private CustomUIBuilder MakeWindow(string id, string titleKey, string titleFallback, float w, float h, Action onClose)
    {
        CustomUIBuilder b = CustomUIManager.Instance
            .CreateWindow(id, Loc(titleKey, titleFallback), null)
            .SetSize(w, h)
            .SetCloseOnEscape(true)
            .OnClose(onClose);
        b.BeginScrollFlexible();
        return b;
    }

    private static void SectionHeader(CustomUIBuilder b, string key, string fallback, string id)
    {
        b.BeginRow(4f, TextAnchor.MiddleLeft);
        b.AddHeader(LocStatic(key, fallback), id);
        b.End();
        b.AddSpace(2f);
    }

    private void CloseRow(CustomUIBuilder b, Action onClose)
    {
        b.AddSpace(4f);
        b.BeginRow(12f, TextAnchor.MiddleCenter);
        b.AddFlexibleSpace();
        b.AddButton(Loc("btn_close", "Close"), onClose, "set_close");
        b.Last.SetFixedWidth(180f);
        b.AddFlexibleSpace();
        b.End();
    }

    private void OnProviderChanged(int index)
    {
        IList<string> options = Player2Config.ProviderOptions;
        if (index < 0 || index >= options.Count) return;
        config.SetProvider(options[index]);
        if (apiWindow != null && apiWindow.IsAlive)
        {
            OpenApiWindow();
        }
    }

    private void OnPromptChanged(string value)
    {
        config.SetPrompt(value);
    }

    private void OnAuthChanged()
    {
        RefreshApiButtons();
    }

    private void SetButtonTempText(CustomUIElement btn, string tempText, string normalText, float seconds = 2f)
    {
        if (btn == null) return;
        btn.SetText(tempText);
        StartCoroutine(RestoreButtonTextCoroutine(btn, normalText, seconds));
    }

    private System.Collections.IEnumerator RestoreButtonTextCoroutine(CustomUIElement btn, string normalText, float seconds)
    {
        yield return new WaitForSecondsRealtime(seconds);
        if (btn != null) btn.SetText(normalText);
    }

    private void RefreshApiButtons()
    {
        if (apiWindow == null || !apiWindow.IsAlive) return;
        bool authing = auth != null && auth.IsAuthenticating;
        string authLabel = authing
            ? Loc("btn_authorize_busy", "Authorizing...")
            : (client != null && client.HasCloudKey ? Loc("btn_auth_done", "Authorized") : Loc("btn_authorize", "Authorize via web"));
        authButton?.SetText(authLabel);
        authButton?.SetInteractable(!authing);

        bool showAuth = authing && auth != null && !string.IsNullOrEmpty(auth.ApprovalUrl);
        if (authHint != null)
        {
            string code = auth != null ? auth.UserCode : null;
            string hint = string.IsNullOrEmpty(code)
                ? Loc("auth_link_hint", "Copy the link and open it in a browser")
                : Loc("auth_code_hint", "Code") + " " + code + " — "
                    + Loc("auth_link_hint", "Copy the link and open it in a browser");
            authHint.SetText(hint);
            authHint.SetVisible(showAuth);
        }
        copyAuthButton?.SetVisible(showAuth);

        detectButton?.SetText(detecting
            ? Loc("btn_detect_busy", "Detecting...")
            : Loc("btn_detect", "Detect desktop app"));
        detectButton?.SetInteractable(!detecting);

        fetchModelsButton?.SetInteractable(!fetchingModels);
    }

    private void OnAuthorize()
    {
        if (auth == null || auth.IsAuthenticating) return;
        bool started = auth.Start(key =>
        {
            config.SetP2Key(key);
            client.RefreshOnlineState();
            RefreshApiButtons();
        });
        if (started)
        {
            SetButtonTempText(authButton, Loc("btn_auth_opened", "Browser opened..."), Loc("btn_authorize", "Authorize via web"), 3f);
        }
        RefreshApiButtons();
    }

    private void OnCopyAuthLink()
    {
        if (auth == null || string.IsNullOrEmpty(auth.ApprovalUrl)) return;
        GUIUtility.systemCopyBuffer = auth.ApprovalUrl;
        if (copyAuthButton != null)
        {
            SetButtonTempText(copyAuthButton, Loc("btn_copied", "Copied"), Loc("btn_copy_link", "Copy link"), 2f);
        }
    }

    private void OnDetect()
    {
        if (client == null || detecting) return;
        detecting = true;
        if (detectButton != null)
        {
            detectButton.SetText(Loc("btn_detect_busy", "Detecting..."));
            detectButton.SetInteractable(false);
        }
        client.DetectLocal(msg =>
        {
            detecting = false;
            if (detectButton != null)
            {
                detectButton.SetInteractable(true);
                bool ok = client.DesktopOnline;
                string resultText = ok ? Loc("btn_detect_ok", "Connected") : Loc("btn_detect_fail", "Not detected");
                SetButtonTempText(detectButton, resultText, Loc("btn_detect", "Detect desktop app"), 2.5f);
            }
        });
    }

    private void CloseApi()
    {
        if (apiWindow != null && apiWindow.IsAlive) apiWindow.Close();
        DetachApi();
    }

    private void CloseDebug()
    {
        if (debugWindow != null && debugWindow.IsAlive) debugWindow.Close();
        DetachDebug();
    }

    private void CloseGear()
    {
        if (gearWindow != null && gearWindow.IsAlive) gearWindow.Close();
        DetachGear();
    }

    private void DetachApi()
    {
        apiWindow = null;
        authButton = null;
        detectButton = null;
        endpointInput = null;
        fetchModelsButton = null;
    }

    private void DetachDebug()
    {
        debugWindow = null;
    }

    private void DetachGear()
    {
        gearWindow = null;
    }

    // 本地化查表：本地化值优先，键缺失时回退英文兜底，避免 UI 出现裸键名
    private string Loc(string key, string fallback)
    {
        if (string.IsNullOrEmpty(modId)) return fallback;
        string value = ModHelper.GetLocalized(modId, key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }

    // 静态区（SectionHeader）用：无实例字段，按当前实例的 modId 查表
    private static string LocStatic(string key, string fallback)
    {
        string id = Instance != null ? Instance.modId : null;
        if (string.IsNullOrEmpty(id)) return fallback;
        string value = ModHelper.GetLocalized(id, key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }
}