using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Probablytalk;

// 顾客对话：柜台"对话"按钮（原版遣散按钮的镜像位，同绘制逻辑）→ 输入行 → AI 回复走原版气泡。
// 无右键交互；本模组 UI 为对话窗口 + 左下角四个方形图标入口（扳手=API 配置 / 三横线=调试 / 齿轮=设置 / 记事本=顾客名册）。
internal sealed class CustomerChatUI : MonoBehaviour
{
    private const string WindowId = "probablytalk_customer";

    private string modId;
    private Player2Client client;
    private ModLog log;

    private CustomUIWindow window;
    private CustomUIElement inputElement;
    private CustomUIElement sendButton;
    private CustomUIElement apiButton;
    private CustomUIElement debugButton;
    private CustomUIElement gearButton;
    private CustomUIElement rosterButton;
    private StoreClient boundClient;

    // 图标按钮的初始方形边长；实际由 SyncSendButtonHeight 同步为与输入框等高（正方形）
    private const float IconSize = 34f;

    // API 请求中的旋转指示器（圆圈箭头）：挂在窗口根、定位到标题文本右侧，请求期间缓慢旋转
    private GameObject spinner;
    private float spinnerAngle;
    private const float SpinnerSize = 20f;
    private const float SpinnerDegPerSec = 110f;
    private const float SpinnerGap = 8f;
    private bool busy;
    private bool syncSendHeight;
    private CounterTalkButton counterButton;

    // 柜前空出来的起始时刻：连续空置 3 秒才判定顾客已离柜并自动关窗
    private float nullClientSince;

    // 每位顾客的交互轮次（identifier -> user/assistant 序列），同顾客跨日延续，全量作为 messages 下发
    private static readonly Dictionary<string, List<KeyValuePair<string, string>>> Turns = new Dictionary<string, List<KeyValuePair<string, string>>>();

    public static CustomerChatUI Instance { get; private set; }

    public bool IsBusy => busy;
    public bool IsInputOpen => window != null && window.IsAlive && window.IsVisible;

    public void Setup(string modId, Player2Client client, ModLog log)
    {
        this.modId = modId;
        this.client = client;
        this.log = log;
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        if (window != null && !window.IsAlive) DetachWindow();
        CloseWindowIfClientLeft();
        EnsureCounterButton();
        UpdateCounterButtonVisibility();
        PollEnterToSend();
        SyncSendButtonHeight();
        UpdateSpinner();
    }

    // 看门狗：这是 modal 纸面板，玩家开着它时世界点击全被挡住。
    // 顾客一旦离柜（成交、被赶、剧情推进），窗口必须自动消失，否则平板/铃铛就再也点不动了。
    // 等回包期间（busy）不动；柜前空出来要连续 3 秒才判定为「人走了」，避免开窗首帧的空档误关
    private void CloseWindowIfClientLeft()
    {
        if (busy) return;
        if (window == null || !window.IsAlive)
        {
            nullClientSince = 0f;
            return;
        }
        if (boundClient == null)
        {
            CloseWindow();
            return;
        }
        PlayerStore store = PlayerStore.Instance;
        StoreClient current = store != null && store.currentClientInstance != null
            ? store.currentClientInstance.GetClientBlueprint()
            : null;
        if (current != null && current.identifier == boundClient.identifier)
        {
            nullClientSince = 0f;
            return;
        }
        if (current != null)
        {
            // 柜前换了个人：立刻关，别让旧窗口挡着新客人
            if (log != null) log.Log("customer chat window closed: another client is at the counter");
            CloseWindow();
            return;
        }
        if (nullClientSince <= 0f)
        {
            nullClientSince = Time.unscaledTime;
            return;
        }
        if (Time.unscaledTime - nullClientSince < 3f) return;
        if (log != null) log.Log("customer chat window auto-closed: client left the counter");
        CloseWindow();
    }

    // 发送按钮高度对齐左侧输入框：窗口首帧布局完成后读一次输入框实高，回写按钮并重建布局
    private void SyncSendButtonHeight()
    {
        if (!syncSendHeight) return;
        if (window == null || !window.IsAlive || inputElement == null || sendButton == null) return;
        RectTransform rect = inputElement.Rect;
        float height = rect != null ? rect.rect.height : 0f;
        if (height <= 8f) return;
        sendButton.SetPreferredSize(96f, height);
        apiButton?.SetPreferredSize(height, height);
        debugButton?.SetPreferredSize(height, height);
        gearButton?.SetPreferredSize(height, height);
        rosterButton?.SetPreferredSize(height, height);
        CustomUIBuilder.RebuildLayout(window.ContentRoot);
        PlaceSpinner();
        syncSendHeight = false;
    }

    // 柜台按钮随店铺场景重建：消失即按原版按钮数据重新生成
    private void EnsureCounterButton()
    {
        if (counterButton != null) return;
        StoreBell bell = UnityEngine.Object.FindFirstObjectByType<StoreBell>();
        if (bell == null) return;
        counterButton = CounterTalkButton.Create(bell);
        counterButton.Owner = this;
        if (log != null) log.Log("counter talk button created at " + counterButton.transform.position);
    }

    // 柜台按钮与原版铃铛一致：常驻桌面，不随顾客有无切换；
    // 能否交谈由点击入口、热键与 tooltip 各自判定
    private void UpdateCounterButtonVisibility()
    {
        if (counterButton == null) return;
        counterButton.SetVisible(true);
    }

    // 柜台按钮点击入口：只有买卖顾客在柜前才打开输入
    internal void OnCounterButtonClicked()
    {
        PlayerStore store = PlayerStore.Instance;
        StoreClient blueprint = store != null && store.currentClientInstance != null
            ? store.currentClientInstance.GetClientBlueprint()
            : null;
        if (!ClientSpeech.IsTalkCustomer(blueprint)) return;
        OpenInput(blueprint);
    }

    // 输入窗：标题为顾客名，仅一行输入 + 发送；Enter 直发
    private void OpenInput(StoreClient blueprint)
    {
        if (busy || blueprint == null) return;
        if (window != null && window.IsAlive) window.Close();
        if (CustomUIManager.Instance == null)
        {
            if (log != null) log.Warning("customer chat open failed: CustomUIManager missing");
            return;
        }
        boundClient = blueprint;

        string layerId = CustomUIManager.HasLayer(CustomUIManager.LAYER_PAPER)
            ? CustomUIManager.LAYER_PAPER
            : null;
        CustomUIBuilder builder = CustomUIManager.Instance
            .CreateWindow(WindowId, blueprint.displayName, layerId)
            .SetSize(430f, 150f)
            .SetCloseOnEscape(true)
            .OnClose(DetachWindow);
        window = builder.GetWindow();

        builder.BeginRow(6f, TextAnchor.MiddleLeft);
        builder.AddInput(string.Empty, string.Empty, null, "customer_input", null);
        inputElement = builder.Last;
        inputElement.SetFlexible(1f, 0f);
        inputElement.input.lineType = TMPro.TMP_InputField.LineType.SingleLine;
        inputElement.input.richText = false;
        inputElement.input.characterLimit = 600;
        builder.AddButton(Loc("chat_send", "Send"), Send, "customer_send");
        sendButton = builder.Last;
        sendButton.SetPreferredSize(96f, 0f);
        builder.End();

        // 四个方形图标入口，置于输入框左下角（同一对话框容器内，不外挂），左对齐：
        //   扳手 = API 配置 / 三横线 = 调试 / 齿轮 = 设置 / 记事本 = 顾客名册；均带悬浮说明
        builder.AddSpace(6f);
        builder.BeginRow(6f, TextAnchor.MiddleLeft);
        apiButton = AddIconEntry(builder, IconSprites.Wrench, "entry_api", "tooltip_api", "API Configuration", OpenApiSettings);
        debugButton = AddIconEntry(builder, IconSprites.Menu, "entry_debug", "tooltip_debug", "Debug", OpenDebugSettings);
        gearButton = AddIconEntry(builder, IconSprites.Gear, "entry_settings", "tooltip_settings", "Settings", OpenGearSettings);
        rosterButton = AddIconEntry(builder, IconSprites.Notes, "entry_roster", "tooltip_roster", "Customer Roster", OpenRoster);
        builder.AddFlexibleSpace();
        builder.End();

        builder.Center();
        builder.Show();
        CustomUIBuilder.RebuildLayout(window.ContentRoot);
        TMP_InputField input = inputElement != null ? inputElement.input : null;
        if (input != null)
        {
            // 回车直发：单行输入框按回车时 TMP 触发 onSubmit 并移出焦点，仅靠 Update 轮询可能因失焦漏发，故显式订阅
            input.onSubmit.AddListener(OnInputSubmitted);
            input.ActivateInputField();
        }
        CreateSpinner();
        syncSendHeight = true;
    }

    // 回车提交回调：与轮询互为兜底；busy/空文本守卫避免重复发送
    private void OnInputSubmitted(string value)
    {
        Send();
    }

    // 三个入口分别打开 API 配置 / 调试 / 设置窗口（原先单一「设置」按钮）
    private void OpenApiSettings() { OpenEntry(SettingsUI.Instance, 0); }
    private void OpenDebugSettings() { OpenEntry(SettingsUI.Instance, 1); }
    private void OpenGearSettings() { OpenEntry(SettingsUI.Instance, 2); }

    // 名册：两个窗口都是 modal 纸面板，同时开着会把世界点击锁死，所以先收掉输入窗再开名册
    private void OpenRoster()
    {
        if (window != null && window.IsAlive) CloseWindow();
        CustomerRosterUI.Toggle(modId);
    }

    private void OpenEntry(SettingsUI settings, int which)
    {
        if (settings == null)
        {
            if (log != null) log.Warning("settings open failed: settings UI not available");
            return;
        }
        if (which == 0) settings.OpenApiWindow();
        else if (which == 1) settings.OpenDebugWindow();
        else settings.OpenSettingsWindow();
    }

    // 构建一个方形图标按钮并挂悬浮说明；尺寸随后由 SyncSendButtonHeight 统一同步为方形
    private CustomUIElement AddIconEntry(CustomUIBuilder builder, Sprite icon, string id, string tipKey, string tipFallback, Action onClick)
    {
        builder.AddIconButton(icon, IconSize, onClick, null, id);
        CustomUIElement element = builder.Last;
        if (element != null && element.root != null)
        {
            IconHoverTooltip tip = element.root.AddComponent<IconHoverTooltip>();
            tip.TextProvider = () => Loc(tipKey, tipFallback);
        }
        return element;
    }

    // 发送：上下文（顾客画像）并入系统提示，轮次历史按序进请求；回复走原版气泡
    public void Send()
    {
        if (busy || boundClient == null) return;
        TMP_InputField input = inputElement != null ? inputElement.input : null;
        if (input == null) return;
        string text = input.text;
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        input.text = string.Empty;

        if (!client.IsOnline)
        {
            if (log != null) log.Warning("customer chat send failed: player2 offline");
            return;
        }

        // 玩家这一句就是管线入口：先领当日额度，到顶或上限为 0 时不发请求、原文留在输入框里
        if (!ClientSpeech.TryConsumeRequest())
        {
            if (log != null) log.Warning("customer chat send blocked: daily request cap reached");
            input.text = text;
            return;
        }

        busy = true;
        sendButton?.SetInteractable(false);
        inputElement?.SetInteractable(false);

        string identifier = boundClient.identifier;
        List<KeyValuePair<string, string>> turns = GetTurns(identifier);
        turns.Add(new KeyValuePair<string, string>("user", text));

        // 产生对话即入档：名字与势力一并带上，名册里不必再回查游戏对象
        CustomerArchive.RecordTalk(identifier, boundClient.displayName, boundClient.clientFaction, null);
        CustomerArchive.AppendLine(identifier, "player", text);

        string systemPrompt = ClientSpeech.InteractiveSystemPrompt(boundClient)
            + "\n" + ClientSpeech.InteractiveContext(boundClient);
        client.Chat(systemPrompt, turns, (ok, reply) =>
        {
            OnChatResult(identifier, ok, reply);
        });
    }

    private void OnChatResult(string identifier, bool ok, string reply)
    {
        busy = false;
        sendButton?.SetInteractable(true);
        inputElement?.SetInteractable(true);

        // 这个窗口是 modal 纸面板：只要它还活着，世界点击（平板、铃铛、柜台）就全被挡住。
        // 因此不论走哪条分支——顾客已离柜、请求失败、议价/记账抛异常——都必须在 finally 里关掉它
        try
        {
            // 回复气泡只在原顾客仍在柜前时显示；人已离柜则本轮作废
            PlayerStore store = PlayerStore.Instance;
            StoreClient current = store != null && store.currentClientInstance != null
                ? store.currentClientInstance.GetClientBlueprint()
                : null;
            if (current == null || boundClient == null || current.identifier != boundClient.identifier) return;

            if (!ok || string.IsNullOrWhiteSpace(reply))
            {
                if (log != null) log.Warning("customer chat failed: " + (client.LastError ?? "unknown"));
                return;
            }
            ReplyJson.Reply parsed = ReplyJson.Parse(reply);
            string text = TextLimit.Truncate(parsed.Line ?? string.Empty, ClientSpeech.InteractiveLineLength);
            if (!string.IsNullOrWhiteSpace(text))
            {
                if (Turns.TryGetValue(identifier, out List<KeyValuePair<string, string>> turns))
                {
                    turns.Add(new KeyValuePair<string, string>("assistant", text));
                }
                CustomerArchive.AppendLine(identifier, "customer", text);
            }

            // 交接顺序很重要：先关掉我们这个 modal 窗口，再把控制权交给游戏。
            // 否则游戏自己弹出的「接受/拒绝」结算对话会与仍开着的窗口抢前台，玩家点不动 → 看起来像卡死。
            Dialogue before = DialogUIManager.Instance != null ? DialogUIManager.Instance.GetCurrentDialog() : null;
            CloseWindow();

            // 交易动作：模型把玩家这句话解析成 bargain（议价）与 credit（赊账/收账）
            ApplyReplyEffects(parsed);
            if (log != null) log.Log("deal actions applied");

            Dialogue after = DialogUIManager.Instance != null ? DialogUIManager.Instance.GetCurrentDialog() : null;
            // 游戏结算对话（接受/拒绝/走人）会抢前台：模型那句不能直接盖上去，
            // 但也不能丢——挂在结算对话之后播，玩家仍能看到顾客的完整回应
            if (after != null && after != before)
            {
                if (!string.IsNullOrWhiteSpace(text) && boundClient != null
                    && after.nextDialogue == null && after.endAction == null)
                {
                    Dialogue extra = new Dialogue();
                    extra.SetText(boundClient.displayName, text);
                    extra.priority = after.priority;
                    after.SetNextDialogue(extra);
                    if (log != null) log.Log("game dialogue takes over, our line chained after it");
                }
                else if (log != null)
                {
                    log.Log("game dialogue takes over, our line skipped");
                }
                return;
            }

            // 模型只给了标记、没写台词：动作照做，但不要弹一个空气泡
            if (string.IsNullOrWhiteSpace(text))
            {
                if (log != null) log.Log("customer reply had no spoken line, bubble skipped");
                return;
            }
            Dialogue bubble = new Dialogue();
            bubble.SetText(boundClient.displayName, text);
            DialogUIManager.Instance.StartDialogue(bubble);
        }
        catch (Exception ex)
        {
            if (log != null) log.Warning("customer chat result failed: " + ex.Message);
        }
        finally
        {
            CloseWindow();
        }
    }

    // 回包是一个 JSON 对象（台词 + 改价 + 欠账 + 记忆）。这里只把中文动作词翻译成游戏接口，
    // 不做夹取、不校验方向、不纠正；游戏自己决定接受还是拒绝
    private void ApplyReplyEffects(ReplyJson.Reply parsed)
    {
        if (parsed == null || boundClient == null) return;

        // 三段各自隔离：任何一段抛异常，都不该连带影响另外两段与后续播报
        try
        {
            // act 是英文协议标识（discount / markup / buyingMarkup / buyingDiscount），
            // 与议价层的 kind 同名，直接透传，不做任何翻译或判断
            if (!string.IsNullOrWhiteSpace(parsed.Action) && parsed.Percent > 0)
            {
                bool applied = BargainBridge.Apply(boundClient, parsed.Action, parsed.Percent);
                if (log != null)
                {
                    log.Log("customer chat bargain " + (applied ? "forwarded" : "skipped")
                        + ": " + parsed.Action + " " + parsed.Percent + "%");
                }
                if (applied)
                {
                    // 记进往来：离店总结据此知道这单价格被谈过
                    CustomerArchive.AppendLine(boundClient.identifier, "deal",
                        "议价 " + parsed.Action + " " + parsed.Percent + "%");
                }
            }
        }
        catch (Exception ex)
        {
            if (log != null) log.Warning("customer chat bargain failed: " + ex.Message);
        }

        // 记忆摘要：写进常客档案，下次预生成的开场白会带上
        try
        {
            if (!string.IsNullOrWhiteSpace(parsed.Memory))
            {
                RegularManager.OnMemoryProduced(boundClient.identifier, parsed.Memory);
            }
        }
        catch (Exception ex)
        {
            if (log != null) log.Warning("customer chat memory failed: " + ex.Message);
        }

        try
        {
            string credit = parsed.Credit != null ? parsed.Credit.Trim().ToLowerInvariant() : null;
            if (credit == "lend")
            {
                int amount = CreditBook.Stage(boundClient, 0);
                if (amount > 0) CustomerArchive.AppendLine(boundClient.identifier, "credit", "赊账 " + amount);
            }
            else if (credit == "collect")
            {
                int amount = CreditBook.Collect(boundClient);
                if (amount > 0) CustomerArchive.AppendLine(boundClient.identifier, "credit", "还款 " + amount);
            }
        }
        catch (Exception ex)
        {
            if (log != null) log.Warning("customer chat credit failed: " + ex.Message);
        }
    }


    // 关窗必须绝对可靠：这个面板是 modal，销毁失败就等于把整个世界点击锁死。
    // 因此销毁本身异常也要吞掉并把引用清空，看门狗下一帧才不会重复踩同一个坑
    private void CloseWindow()
    {
        try
        {
            if (window != null && window.IsAlive) window.Close();
        }
        catch (Exception ex)
        {
            if (log != null) log.Warning("customer chat close failed: " + ex.Message);
        }
        finally
        {
            DetachWindow();
        }
    }

    private void DetachWindow()
    {
        window = null;
        inputElement = null;
        sendButton = null;
        apiButton = null;
        debugButton = null;
        gearButton = null;
        rosterButton = null;
        spinner = null;
        syncSendHeight = false;
    }

    // 本地化查表：本地化值优先，键缺失时回退英文兜底，避免按钮出现裸键名
    private string Loc(string key, string fallback)
    {
        string value = ModHelper.GetLocalized(modId, key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }

    private static List<KeyValuePair<string, string>> GetTurns(string identifier)
    {
        if (!Turns.TryGetValue(identifier, out List<KeyValuePair<string, string>> turns))
        {
            turns = new List<KeyValuePair<string, string>>();
            Turns[identifier] = turns;
        }
        return turns;
    }

    // 创建旋转指示器挂件：贴图或窗口缺失时静默跳过；颜色对齐标题文字，保证可见
    private void CreateSpinner()
    {
        if (window == null || !window.IsAlive) return;
        if (IconSprites.Spinner == null) return;
        RectTransform rootRect = window.Rect;
        if (rootRect == null) return;
        spinner = new GameObject("pt_spinner", typeof(RectTransform), typeof(Image));
        RectTransform rect = spinner.transform as RectTransform;
        rect.SetParent(rootRect, false);
        rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = new Vector2(SpinnerSize, SpinnerSize);
        rect.anchoredPosition = new Vector2(120f, -(SpinnerSize * 0.5f + 8f));
        Image img = spinner.GetComponent<Image>();
        img.sprite = IconSprites.Spinner;
        img.preserveAspect = true;
        img.raycastTarget = false;
        if (window.frame != null && window.frame.titleText != null) img.color = window.frame.titleText.color;
        spinner.SetActive(false);
    }

    // 指示器锚到标题文本右缘：标题右缘世界坐标换算到窗口根局部坐标，再转成左上锚点偏移
    private void PlaceSpinner()
    {
        if (spinner == null) return;
        if (window == null || !window.IsAlive) return;
        RectTransform rootRect = window.Rect;
        if (rootRect == null) return;
        RectTransform rect = spinner.transform as RectTransform;
        Vector2 anchored = new Vector2(120f, -(SpinnerSize * 0.5f + 8f));
        CustomUIWindowFrame frame = window.frame;
        if (frame != null && frame.titleText != null && frame.titleText.rectTransform != null)
        {
            RectTransform titleRect = frame.titleText.rectTransform;
            float tw = titleRect.rect.width;
            float th = titleRect.rect.height;
            if (tw <= 0f) tw = frame.titleText.preferredWidth;
            float xmax = (1f - titleRect.pivot.x) * tw;
            float ymid = (0.5f - titleRect.pivot.y) * th;
            Vector3 rightWorld = titleRect.TransformPoint(new Vector3(xmax, ymid, 0f));
            Vector3 local = rootRect.InverseTransformPoint(rightWorld);
            float leftEdge = -rootRect.rect.width * rootRect.pivot.x;
            float topEdge = rootRect.rect.height * (1f - rootRect.pivot.y);
            anchored = new Vector2(local.x - leftEdge + SpinnerGap + SpinnerSize * 0.5f, local.y - topEdge);
        }
        rect.anchoredPosition = anchored;
    }

    // 仅请求期间显示并缓慢旋转（unscaled 时间，暂停时不冻结）；其余时间隐藏
    private void UpdateSpinner()
    {
        if (spinner == null) return;
        bool show = busy;
        if (spinner.activeSelf != show) spinner.SetActive(show);
        if (!show) return;
        spinnerAngle = (spinnerAngle + SpinnerDegPerSec * Time.unscaledDeltaTime) % 360f;
        spinner.transform.localRotation = Quaternion.Euler(0f, 0f, -spinnerAngle);
    }
    private void PollEnterToSend()
    {
        if (busy || window == null || !window.IsAlive || !window.IsVisible) return;
        TMP_InputField input = inputElement != null ? inputElement.input : null;
        if (input == null || !input.isFocused) return;
        if (Input.compositionString.Length > 0) return;
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            Send();
        }
    }

    // 桥接测试通道：等同点击柜台按钮（Probablytalk.CustomerChatUI 反射可达）。
    // 返回 false = 柜前不是买卖顾客，桥按契约上报
    public static bool OpenInputForCurrent()
    {
        PlayerStore store = PlayerStore.Instance;
        StoreClient blueprint = store != null && store.currentClientInstance != null
            ? store.currentClientInstance.GetClientBlueprint()
            : null;
        if (Instance == null || !ClientSpeech.IsTalkCustomer(blueprint)) return false;
        Instance.OpenInput(blueprint);
        return true;
    }

    // 桥接测试通道：直接以文本走发送管线
    public static void SendFromBridge(string text)
    {
        if (Instance == null || string.IsNullOrWhiteSpace(text)) return;
        PlayerStore store = PlayerStore.Instance;
        StoreClient blueprint = store != null && store.currentClientInstance != null
            ? store.currentClientInstance.GetClientBlueprint()
            : null;
        if (!ClientSpeech.IsTalkCustomer(blueprint)) return;
        Instance.OpenInput(blueprint);
        TMP_InputField input = Instance.inputElement != null ? Instance.inputElement.input : null;
        if (input != null) input.text = text;
        Instance.Send();
    }
}
