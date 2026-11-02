using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Probablytalk;

// 柜台对话按钮：位于柜台左侧，与右侧铃铛按钮关于柜台中轴镜像对称；实现 TooltipInterface 与热键 T 触发对话。
internal sealed class CounterTalkButton : TooltipInterface, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
{
    public CustomerChatUI Owner;
    public static CounterTalkButton Instance { get; private set; }

    private SpriteRenderer spriteRenderer;
    private BoxCollider2D collider2d;
    private AudioSource audioSource;
    private Sprite spriteNormal;
    private Sprite spriteHover;
    private Sprite spriteClick;
    private bool hovering;
    private bool pressing;
    private bool isVisible;

    private GameObject talkCalloutObj;
    private GameObject talkShortcutObj;
    private GameObject talkShortcutDescObj;
    private bool shortcutExpanded;

    public static CounterTalkButton Create(StoreBell bell)
    {
        GameObject go = new GameObject("ProbablyTalkCounterButton");
        if (bell.transform.parent != null)
        {
            go.transform.SetParent(bell.transform.parent, false);
        }

        // 位置：关于柜台视图中轴（x = 0）与右侧铃铛按钮水平镜像对称
        Vector3 bellLocalPos = bell.transform.localPosition;
        go.transform.localPosition = new Vector3(-bellLocalPos.x, bellLocalPos.y, bellLocalPos.z);
        go.transform.localScale = bell.transform.localScale;
        go.layer = bell.gameObject.layer;

        CounterTalkButton button = go.AddComponent<CounterTalkButton>();
        button.audioSource = bell.audioSource;

        // 加载重绘的三态贴图资源；缺失时回退铃铛贴图。
        // 走 ModHelper 读模组文件，不直接碰 System.IO——扫描器按调用类型判定 files 能力
        button.spriteNormal = LoadSprite("talk_button_0.png") ?? bell.spriteNormal;
        button.spriteHover = LoadSprite("talk_button_1.png") ?? bell.spriteHover;
        button.spriteClick = LoadSprite("talk_button_2.png") ?? bell.spriteClick;

        button.spriteRenderer = go.AddComponent<SpriteRenderer>();
        button.spriteRenderer.sprite = button.spriteNormal;
        SpriteRenderer bellRenderer = bell.buttonRenderer;
        if (bellRenderer != null)
        {
            button.spriteRenderer.sortingLayerID = bellRenderer.sortingLayerID;
            button.spriteRenderer.sortingOrder = bellRenderer.sortingOrder;
        }

        button.collider2d = go.AddComponent<BoxCollider2D>();
        BoxCollider2D bellBox = bell.GetComponent<BoxCollider2D>();
        if (bellBox != null)
        {
            button.collider2d.size = bellBox.size;
            button.collider2d.offset = bellBox.offset;
        }
        else if (button.spriteNormal != null)
        {
            Bounds bounds = button.spriteNormal.bounds;
            button.collider2d.size = new Vector2(bounds.size.x, bounds.size.y);
        }

        Instance = button;
        return button;
    }

    private static Sprite LoadSprite(string fileName, float ppu = 100f)
    {
        string modId = ModEntry.ModId;
        if (string.IsNullOrEmpty(modId)) return null;
        byte[] bytes = ModHelper.ReadModFileBytes(modId, "Textures/" + fileName);
        if (bytes == null || bytes.Length == 0) return null;
        try
        {
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            if (tex.LoadImage(bytes))
            {
                return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), ppu);
            }
        }
        catch
        {
        }
        return null;
    }

    public void SetVisible(bool visible)
    {
        isVisible = visible;
        if (spriteRenderer != null) spriteRenderer.enabled = visible;
        if (collider2d != null) collider2d.enabled = visible;
        if (!visible)
        {
            hovering = false;
            pressing = false;
            SetSprite(spriteNormal);
            HideTooltip();
        }
    }

    private void Update()
    {
        HandleHotkey();
        if (hovering && TooltipManager._instance != null && IsTooltipEnabled())
        {
            TooltipManager._instance.SetAndShowTooltip(GetTooltip(), this);
        }
        UpdateControlOverlayHints();
    }

    private void HandleHotkey()
    {
        if (!CanUseHotkey()) return;
        if (Input.GetKeyDown(KeyCode.T))
        {
            PlayerStore store = PlayerStore.Instance;
            StoreClient blueprint = store != null && store.currentClientInstance != null
                ? store.currentClientInstance.GetClientBlueprint()
                : null;
            if (ClientSpeech.IsTalkCustomer(blueprint))
            {
                TriggerTalk();
            }
        }
    }

    private bool CanUseHotkey()
    {
        if (!isVisible) return false;
        if (StoreUIManager.Instance != null && StoreUIManager.Instance.IsTyping()) return false;
        if (CustomerChatUI.Instance != null && CustomerChatUI.Instance.IsInputOpen) return false;
        if (Interactable.isPaused) return false;
        return true;
    }

    public override string GetTooltip()
    {
        if (CustomerChatUI.Instance != null && CustomerChatUI.Instance.IsBusy)
        {
            return Loc("inte_talk_busy", "正在交谈中…");
        }
        PlayerStore store = PlayerStore.Instance;
        StoreClient blueprint = store != null && store.currentClientInstance != null
            ? store.currentClientInstance.GetClientBlueprint()
            : null;
        if (ClientSpeech.IsTalkCustomer(blueprint))
        {
            return Loc("inte_talk_button", "与顾客交谈 [T]");
        }
        return Loc("inte_talk_cant", "暂时无法交谈");
    }

    public override bool IsTooltipEnabled()
    {
        return gameObject.activeInHierarchy && isVisible;
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        hovering = true;
        if (!pressing) SetSprite(spriteHover);
        ShowTooltip();
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        hovering = false;
        pressing = false;
        SetSprite(spriteNormal);
        HideTooltip();
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        pressing = true;
        SetSprite(spriteClick);
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        bool wasHovering = hovering;
        pressing = false;
        SetSprite(hovering ? spriteHover : spriteNormal);
        if (wasHovering)
        {
            TriggerTalk();
        }
    }

    public void TriggerTalk()
    {
        if (Owner != null)
        {
            if (audioSource != null) audioSource.Play();
            Owner.OnCounterButtonClicked();
        }
    }

    private void ShowTooltip()
    {
        if (TooltipManager._instance != null && IsTooltipEnabled())
        {
            TooltipManager._instance.SetAndShowTooltip(GetTooltip(), this);
        }
    }

    private void HideTooltip()
    {
        if (TooltipManager._instance != null)
        {
            TooltipManager._instance.HideTooltip(this);
        }
    }

    // 将对讲机操作提示整合进原版教程/操作提示遮罩（_ControlOverLay）
    private void UpdateControlOverlayHints()
    {
        StoreUIManager storeUi = StoreUIManager.Instance;
        if (storeUi == null || storeUi.controlHintOverlay == null) return;
        GameObject overlay = storeUi.controlHintOverlay;

        // 1. 柜台对讲机青色指示条：关于柜台中轴与右侧 CallCustomer 镜像对称
        if (talkCalloutObj == null)
        {
            Transform existing = overlay.transform.Find("TalkCustomer");
            if (existing != null)
            {
                talkCalloutObj = existing.gameObject;
            }
            else
            {
                Transform callCustomer = overlay.transform.Find("CallCustomer");
                if (callCustomer != null)
                {
                    talkCalloutObj = UnityEngine.Object.Instantiate(callCustomer.gameObject, overlay.transform);
                    talkCalloutObj.name = "TalkCustomer";

                    // 移除原版本地化组件，防止被覆写为「呼叫下一位顾客」
                    foreach (MonoBehaviour mb in talkCalloutObj.GetComponentsInChildren<MonoBehaviour>(true))
                    {
                        if (mb != null && mb.GetType().Name.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            UnityEngine.Object.Destroy(mb);
                        }
                    }

                    RectTransform rt = talkCalloutObj.GetComponent<RectTransform>();
                    RectTransform srcRt = callCustomer.GetComponent<RectTransform>();
                    if (rt != null && srcRt != null)
                    {
                        rt.anchorMin = srcRt.anchorMin;
                        rt.anchorMax = srcRt.anchorMax;
                        rt.pivot = srcRt.pivot;
                        // X 轴与右侧 CallCustomer 保持水平对称 (-346)；
                        // Y 轴往上移动至 -46（避开左下角快捷键面板的顶部，位于电话与对讲机之间的空旷视野内）
                        rt.anchoredPosition = new Vector2(-srcRt.anchoredPosition.x, -46f);
                        // 继承原版尺度 (2, 2, 2)，保持点阵字体与内边距完好
                        rt.localScale = srcRt.localScale;
                    }
                }
            }
        }

        if (talkCalloutObj != null)
        {
            TMP_Text text = talkCalloutObj.GetComponentInChildren<TMP_Text>();
            string hintText = Loc("inte_talk_control_hint", "与顾客交谈");
            if (text != null && text.text != hintText)
            {
                text.text = hintText;
            }
        }

        // 2. 键盘快捷键面板：补充 [ T ] 与顾客交谈 条目
        Transform shortcut = overlay.transform.Find("Shortcut");
        if (shortcut != null)
        {
            // 拓展快捷键背景框高度并向上延伸，避免向下侵占底部自动保存说明
            RectTransform shortcutRt = shortcut as RectTransform;
            if (shortcutRt != null && !shortcutExpanded)
            {
                shortcutExpanded = true;
                float deltaHeight = 44f;
                shortcutRt.sizeDelta = new Vector2(shortcutRt.sizeDelta.x, shortcutRt.sizeDelta.y + deltaHeight);
                shortcutRt.anchoredPosition = new Vector2(shortcutRt.anchoredPosition.x, 50f);
            }

            if (talkShortcutObj == null)
            {
                Transform existingShortcut = shortcut.Find("TalkShortcut");
                if (existingShortcut != null)
                {
                    talkShortcutObj = existingShortcut.gameObject;
                }
                else
                {
                    Transform closeUi = shortcut.Find("CloseUI");
                    Transform skipDialogue = shortcut.Find("SkipDialogue");
                    Transform template = closeUi ?? skipDialogue;
                    if (template != null)
                    {
                        talkShortcutObj = UnityEngine.Object.Instantiate(template.gameObject, shortcut);
                        talkShortcutObj.name = "TalkShortcut";

                        foreach (MonoBehaviour mb in talkShortcutObj.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (mb != null && mb.GetType().Name.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                UnityEngine.Object.Destroy(mb);
                            }
                        }

                        RectTransform rt = talkShortcutObj.GetComponent<RectTransform>();
                        if (rt != null)
                        {
                            // CloseUI 为 -288.1，SkipDialogue 为 -326（步长约 38），第9行递延至 -364
                            rt.anchoredPosition = new Vector2(-172.8f, -364f);
                        }

                        TMP_Text keyText = talkShortcutObj.GetComponentInChildren<TMP_Text>();
                        if (keyText != null)
                        {
                            keyText.text = "T";
                        }
                    }
                }
            }

            Transform verticalLayout = shortcut.Find("VerticalLayout");
            if (verticalLayout != null && talkShortcutDescObj == null)
            {
                Transform existingDesc = verticalLayout.Find("TalkShortcutDesc");
                if (existingDesc != null)
                {
                    talkShortcutDescObj = existingDesc.gameObject;
                }
                else
                {
                    TMP_Text[] descTexts = verticalLayout.GetComponentsInChildren<TMP_Text>();
                    if (descTexts != null && descTexts.Length > 0)
                    {
                        TMP_Text lastDesc = descTexts[descTexts.Length - 1];
                        talkShortcutDescObj = UnityEngine.Object.Instantiate(lastDesc.gameObject, verticalLayout);
                        talkShortcutDescObj.name = "TalkShortcutDesc";

                        foreach (MonoBehaviour mb in talkShortcutDescObj.GetComponentsInChildren<MonoBehaviour>(true))
                        {
                            if (mb != null && mb.GetType().Name.IndexOf("Localize", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                UnityEngine.Object.Destroy(mb);
                            }
                        }

                        RectTransform descRt = talkShortcutDescObj.GetComponent<RectTransform>();
                        RectTransform lastRt = lastDesc.rectTransform;
                        if (descRt != null && lastRt != null)
                        {
                            // 第8行 Y 坐标为 -293.98，按行距 39.85 递延至 -333.83
                            descRt.anchoredPosition = new Vector2(lastRt.anchoredPosition.x, lastRt.anchoredPosition.y - 39.85f);
                        }
                    }
                }
            }

            if (talkShortcutDescObj != null)
            {
                TMP_Text descTmp = talkShortcutDescObj.GetComponent<TMP_Text>();
                string descText = Loc("inte_talk_shortcut_desc", "与顾客交谈");
                if (descTmp != null && descTmp.text != descText)
                {
                    descTmp.text = descText;
                }
            }
        }
    }

    public void SimulateHover(bool hover)
    {
        if (hover)
        {
            OnPointerEnter(new PointerEventData(EventSystem.current));
            if (TooltipManager._instance != null && IsTooltipEnabled() && Camera.main != null)
            {
                Vector3 screenPos = Camera.main.WorldToScreenPoint(transform.position);
                TooltipManager._instance.tooltipGameobject.transform.position = screenPos + new Vector3(80f, 60f, 0f);
            }
        }
        else
        {
            OnPointerExit(new PointerEventData(EventSystem.current));
        }
    }

    private void SetSprite(Sprite sprite)
    {
        if (sprite != null && spriteRenderer != null)
        {
            spriteRenderer.sprite = sprite;
        }
    }

    private static string Loc(string key, string fallback)
    {
        string val = ModHelper.GetLocalized("Probablytalk", key);
        return string.IsNullOrEmpty(val) || val == key ? fallback : val;
    }

    private void OnDisable()
    {
        hovering = false;
        pressing = false;
        HideTooltip();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        HideTooltip();
        talkCalloutObj = null;
        talkShortcutObj = null;
        talkShortcutDescObj = null;
    }
}
