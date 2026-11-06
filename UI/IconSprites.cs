using System;
using UnityEngine;

namespace Probablytalk;

// 聊天窗口左下角三个方形图标按钮的贴图加载：走 ModHelper 读取模组自带文件，
// 不直接调用 System.IO.File —— 扫描器只按调用类型判定能力，直接读会被记成 files；
// 缺失返回 null。与 UI/CounterTalkButton 的贴图加载同套路（运行时 LoadImage + Sprite.Create），
// 区别只是这里给 CustomUI 的 Image/Button 用（UI 层，非世界空间 SpriteRenderer）。
internal static class IconSprites
{
    private static Sprite _wrench;
    private static Sprite _menu;
    private static Sprite _gear;
    private static Sprite _notes;
    private static Sprite _spinner;

    // API 配置（扳手）
    public static Sprite Wrench { get { if (_wrench == null) _wrench = Load("icon_wrench.png"); return _wrench; } }
    // 调试（三横线）
    public static Sprite Menu { get { if (_menu == null) _menu = Load("icon_menu.png"); return _menu; } }
    // 设置（齿轮）
    public static Sprite Gear { get { if (_gear == null) _gear = Load("icon_gear.png"); return _gear; } }
    // 顾客名册（记事本）
    public static Sprite Notes { get { if (_notes == null) _notes = Load("icon_notes.png"); return _notes; } }
    // API 请求中的旋转指示（圆圈箭头）：同法加载，缺失返回 null
    public static Sprite Spinner { get { if (_spinner == null) _spinner = Load("icon_spinner.png"); return _spinner; } }

    private static Sprite Load(string fileName)
    {
        try
        {
            string modId = ModEntry.ModId;
            if (string.IsNullOrEmpty(modId)) return null;
            byte[] bytes = ModHelper.ReadModFileBytes(modId, "Textures/" + fileName);
            if (bytes == null || bytes.Length == 0) return null;
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            if (!tex.LoadImage(bytes))
            {
                // 解码失败：销毁临时纹理，别每次重试都留一张
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            return Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
        }
        catch (Exception)
        {
            return null;
        }
    }
}