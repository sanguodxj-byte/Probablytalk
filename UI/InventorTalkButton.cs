using System;
using UnityEngine;

namespace Probablytalk;

// 博士工作台对话按钮：位于博士（Dr. Jackson）场景柜台左侧，
// 与店铺场景的柜台对话按钮（CounterTalkButton）处于完全相同的世界坐标位置。
// 提示词侧无需任何改动：博士场景 currentClientInstance 的 identifier 为 inventor，
// ClientSpeech.InteractiveSystemPrompt 等已有 IsInventor 分支（杰克逊博士固定人设）。
// 场景互斥：店铺按钮挂铃铛父级（storeInteractables 隐藏时一并消失），
// 本按钮挂 MapUIManager.upgradeStoreScene，两边各管各的可见性。
internal sealed class InventorTalkButton : UnityEngine.MonoBehaviour
{
    public CustomerChatUI Owner;
    public static InventorTalkButton Instance { get; private set; }

    private SpriteRenderer spriteRenderer;
    private BoxCollider2D collider2d;
    private AudioSource audioSource;
    private Sprite spriteNormal;
    private Sprite spriteHover;
    private Sprite spriteClick;
    private bool hovering;
    private bool pressing;
    private bool isVisible;

    // 博士场景在场判定：playerLocation 只有 Arrive()/OnArriveStore() 两处写，
    // INVENTOR 即玩家身在博士工作台
    internal static bool IsInventorScene()
    {
        PlayerStore store = PlayerStore.Instance;
        return store != null && store.playerLocation == PlayerStore.PlayerLocation.INVENTOR;
    }

    public static InventorTalkButton Create()
    {
        GameObject go = new GameObject("ProbablyTalkInventorButton");

        // 挂到博士场景舞台下，随场景一起显隐；worldPositionStays=true 保持世界坐标
        MapUIManager mapUi = MapUIManager.Instance;
        if (mapUi == null || mapUi.upgradeStoreScene == null) return null;
        go.transform.SetParent(mapUi.upgradeStoreScene.transform, true);
        go.layer = mapUi.upgradeStoreScene.layer;

        // 位置：与店铺柜台对话按钮完全相同的世界坐标。
        // 店铺按钮即使被隐藏（storeInteractables 整体 SetActive(false)），
        // 其 transform.position 仍然可读——藏起的是渲染与碰撞，不是坐标。
        // 首选直接取店铺按钮当前坐标；店铺按钮尚未创建时用铃铛镜像位推导。
        CounterTalkButton storeButton = CounterTalkButton.Instance;
        StoreBell bell = UnityEngine.Object.FindFirstObjectByType<StoreBell>();
        Vector3 worldPos;
        if (storeButton != null)
        {
            worldPos = storeButton.transform.position;
        }
        else if (bell != null)
        {
            Vector3 bellLocal = bell.transform.localPosition;
            Vector3 mirrored = new Vector3(-bellLocal.x, bellLocal.y, bellLocal.z);
            worldPos = bell.transform.parent != null
                ? bell.transform.parent.TransformPoint(mirrored)
                : mirrored;
        }
        else
        {
            worldPos = go.transform.position;
        }
        go.transform.position = worldPos;
        if (storeButton != null)
        {
            go.transform.localScale = storeButton.transform.localScale;
        }
        else if (bell != null)
        {
            go.transform.localScale = bell.transform.localScale;
        }

        InventorTalkButton button = go.AddComponent<InventorTalkButton>();
        button.audioSource = storeButton != null ? storeButton.GetComponent<AudioSource>() : null;

        // 贴图与店铺按钮共用同一套三态资源与缓存
        button.spriteNormal = CounterTalkButton.LoadSprite("talk_button_0.png") ?? (bell != null ? bell.spriteNormal : null);
        button.spriteHover = CounterTalkButton.LoadSprite("talk_button_1.png") ?? (bell != null ? bell.spriteHover : null);
        button.spriteClick = CounterTalkButton.LoadSprite("talk_button_2.png") ?? (bell != null ? bell.spriteClick : null);

        button.spriteRenderer = go.AddComponent<SpriteRenderer>();
        button.spriteRenderer.sprite = button.spriteNormal;
        SpriteRenderer storeRenderer = storeButton != null ? storeButton.GetComponent<SpriteRenderer>() : null;
        if (storeRenderer != null)
        {
            button.spriteRenderer.sortingLayerID = storeRenderer.sortingLayerID;
            button.spriteRenderer.sortingOrder = storeRenderer.sortingOrder;
        }
        // 博士场景的舞台背景（upgradeStoreScene 内的 SpriteRenderer）渲染序高于店铺桌面，
        // 直接抄店铺按钮的 order 会被背景整块盖住。抬高到场景前景层之上，
        // 与柜面物件（sheetTablet 的渲染层同级偏上），保证贴图可见
        button.spriteRenderer.sortingOrder = 100;

        button.collider2d = go.AddComponent<BoxCollider2D>();
        BoxCollider2D storeBox = storeButton != null ? storeButton.GetComponent<BoxCollider2D>() : null;
        if (storeBox != null)
        {
            button.collider2d.size = storeBox.size;
            button.collider2d.offset = storeBox.offset;
        }
        else if (button.spriteNormal != null)
        {
            Bounds bounds = button.spriteNormal.bounds;
            button.collider2d.size = new Vector2(bounds.size.x, bounds.size.y);
        }

        Instance = button;
        return button;
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
        }
    }

    private void Update()
    {
        HandleHotkey();
        UpdateHoverByCollider();
        if (hovering && TooltipManager._instance != null && IsTooltipEnabled())
        {
            TooltipManager._instance.SetAndShowTooltip(GetTooltip(), this);
        }
    }

    private void HandleHotkey()
    {
        if (!CanUseHotkey()) return;
        if (Input.GetKeyDown(KeyCode.T))
        {
            TriggerTalk();
        }
    }

    // 与店铺按钮同款悬停判定：裸对象没有 Interactable 分发链路，自己用碰撞体点检测
    private void UpdateHoverByCollider()
    {
        if (!isVisible || collider2d == null || Camera.main == null)
        {
            if (hovering) OnPointerExit();
            return;
        }
        Vector3 world = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        bool over = collider2d.OverlapPoint(new Vector2(world.x, world.y));
        if (over && !hovering)
        {
            OnPointerEnter();
        }
        else if (!over && hovering)
        {
            OnPointerExit();
        }
    }

    private bool CanUseHotkey()
    {
        if (!isVisible) return false;
        if (!IsInventorScene()) return false;
        if (StoreUIManager.Instance != null && StoreUIManager.Instance.IsTyping()) return false;
        if (CustomerChatUI.Instance != null && CustomerChatUI.Instance.IsInputOpen) return false;
        if (UnityEngine.EventSystems.EventSystem.current != null)
        {
            var selected = UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject;
            if (selected != null && selected.GetComponentInParent<TMPro.TMP_InputField>() != null) return false;
        }
        if (Interactable.isPaused) return false;
        return true;
    }

    public string GetTooltip()
    {
        if (CustomerChatUI.Instance != null && CustomerChatUI.Instance.IsBusy)
        {
            return Loc("inte_talk_busy", "正在交谈中…");
        }
        return Loc("inte_inventor_button", "与杰克逊博士交谈 [T]");
    }

    public bool IsTooltipEnabled()
    {
        return gameObject.activeInHierarchy && isVisible;
    }

    // 原版 Interactable 分发点击；本按钮是裸对象，改由悬停 + 鼠标按键状态合成点击
    private void OnPointerEnter()
    {
        hovering = true;
        if (!pressing) SetSprite(spriteHover);
    }

    private void OnPointerExit()
    {
        hovering = false;
        pressing = false;
        SetSprite(spriteNormal);
    }

    private void TriggerTalk()
    {
        if (Owner != null)
        {
            if (audioSource != null) audioSource.Play();
            Owner.OnCounterButtonClicked();
        }
    }

    // 鼠标点击合成：悬停期间按下→按下态，松开→若仍在悬停则触发
    private void OnMouseOver()
    {
        if (!isVisible) return;
        if (Input.GetMouseButtonDown(0))
        {
            pressing = true;
            SetSprite(spriteClick);
        }
        if (Input.GetMouseButtonUp(0) && pressing)
        {
            pressing = false;
            SetSprite(hovering ? spriteHover : spriteNormal);
            TriggerTalk();
        }
    }

    private void OnMouseExit()
    {
        pressing = false;
        if (hovering) OnPointerExit();
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
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
