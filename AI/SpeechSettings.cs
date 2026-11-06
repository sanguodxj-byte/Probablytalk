using System;
using UnityEngine;

namespace Probablytalk;

// 生成节奏与议价门槛的持久化存储：每日请求上限 / 单句长度 / 请求超时 + 议价四项。
//
// 为什么单独一份存储：这七项由模组自己的设置窗口承载（见 SettingsUI 的齿轮窗口），而 ModConfig
// 的条目一旦 Define 就必然出现在模组设置窗口里、没有「隐藏条目」的口子。为遵守「每个概念只定义
// 一处」，它们不再注册进 ModConfig，改由这里自管一份跨存档落盘文件。
//
// 为什么不用 JSON：本项目编译面里没有 JsonUtility，而 Newtonsoft 反序列化进 private 嵌套类需要
// 公开构造。落盘内容只有几个整数，用最朴素的 key=value 文本最省事、零依赖。
//
// 值域与默认值刻意与原 ModConfig 条目完全一致（12 / 400 / 120，区间 0..60 / 80..2000 / 10..600），
// 保证从旧版升级过来的玩家手感不变；每日请求上限仍以 0 表示关闭 AI 发言。
internal static class SpeechSettings
{
    internal const int DefaultDailyRequestCap = 12;

    // 议价上下限的默认值：与 BargainBridge 的原硬编码一致，改这里等于改全模组默认手感
    internal const int DefaultBargainMinPercent = 1;
    internal const int DefaultBargainMaxPercent = 90;
    internal const int DefaultBargainMaxCumulative = 60;
    internal const int DefaultBargainMaxRounds = 3;
    internal const int DefaultLineLength = 400;
    internal const int DefaultTimeoutSeconds = 120;
    internal const int DefaultBubbleTimeoutSeconds = 15;

    private const string FileName = "speech_settings.txt";

    private sealed class Data
    {
        public int dailyCap = DefaultDailyRequestCap;
        public int lineLength = DefaultLineLength;
        public int timeoutSeconds = DefaultTimeoutSeconds;
        public int bubbleTimeoutSeconds = DefaultBubbleTimeoutSeconds;

        public int bargainMin = DefaultBargainMinPercent;
        public int bargainMax = DefaultBargainMaxPercent;
        public int bargainCumulative = DefaultBargainMaxCumulative;
        public int bargainRounds = DefaultBargainMaxRounds;

        // 文风包：单独模块，键名见 StyleBook；自定义文本走 styleCustom
        public string stylePack = StyleBook.DefaultKey;
        public string styleCustom = string.Empty;
    }

    private static string _modId;
    private static Data _data = new Data();

    // 每个游戏日最多发起的 AI 发言请求数；0 等价于关闭 AI 发言
    public static int DailyRequestCap
    {
        get => _data.dailyCap;
        set
        {
            int clamped = Mathf.Clamp(value, 0, 60);
            if (clamped == _data.dailyCap) return;
            _data.dailyCap = clamped;
            Save();
        }
    }

    // 单条 AI 台词保留的最大字符数
    public static int LineLength
    {
        get => _data.lineLength;
        set
        {
            int clamped = Mathf.Clamp(value, 80, 2000);
            if (clamped == _data.lineLength) return;
            _data.lineLength = clamped;
            Save();
        }
    }

    // 单次请求的超时秒数
    public static int TimeoutSeconds
    {
        get => _data.timeoutSeconds;
        set
        {
            int clamped = Mathf.Clamp(value, 10, 600);
            if (clamped == _data.timeoutSeconds) return;
            _data.timeoutSeconds = clamped;
            Save();
        }
    }

    // 当前文风包键名。写入时就归一：未知键当场落成默认档，
    // 不然设置里存着 "nosuchpack"、提示词里用的却是默认档，两处各说各话
    public static string StylePack
    {
        get => _data.stylePack;
        set
        {
            string key = StyleBook.Find(value).Key;
            if (string.Equals(key, _data.stylePack, StringComparison.OrdinalIgnoreCase)) return;
            _data.stylePack = key;
            Save();
        }
    }

    // 自定义文风正文：只在 stylePack=custom 时被读
    public static string StyleCustom
    {
        get => _data.styleCustom;
        set
        {
            string text = value ?? string.Empty;
            if (text.Length > 600) text = text.Substring(0, 600);
            if (text == _data.styleCustom) return;
            _data.styleCustom = text;
            Save();
        }
    }

    // 对话气泡等待回复的超时秒数：超时后左上角报错并给固定回复
    public static int BubbleTimeoutSeconds
    {
        get => _data.bubbleTimeoutSeconds;
        set
        {
            int clamped = Mathf.Clamp(value, 5, 60);
            if (clamped == _data.bubbleTimeoutSeconds) return;
            _data.bubbleTimeoutSeconds = clamped;
            Save();
        }
    }

    // 议价单次幅度下限（%）：模型给的 percent 低于它会被抬到它
    public static int BargainMinPercent
    {
        get => _data.bargainMin;
        set
        {
            int clamped = Mathf.Clamp(value, 1, 50);
            if (clamped == _data.bargainMin) return;
            _data.bargainMin = clamped;
            Save();
        }
    }

    // 议价单次幅度上限（%）
    public static int BargainMaxPercent
    {
        get => _data.bargainMax;
        set
        {
            int clamped = Mathf.Clamp(value, 5, 90);
            if (clamped == _data.bargainMax) return;
            _data.bargainMax = clamped;
            Save();
        }
    }

    // 累计幅度上限（%）：多轮加码的总量天花板
    public static int BargainMaxCumulative
    {
        get => _data.bargainCumulative;
        set
        {
            int clamped = Mathf.Clamp(value, 5, 90);
            if (clamped == _data.bargainCumulative) return;
            _data.bargainCumulative = clamped;
            Save();
        }
    }

    // 每客每日议价轮次上限
    public static int BargainMaxRounds
    {
        get => _data.bargainRounds;
        set
        {
            int clamped = Mathf.Clamp(value, 1, 5);
            if (clamped == _data.bargainRounds) return;
            _data.bargainRounds = clamped;
            Save();
        }
    }

    // 从磁盘载入；文件缺失或损坏时用默认值。位置用游戏给 mod 的数据目录（跨存档持久）
    public static void Load(string modId)
    {
        _modId = modId;
        _data = LoadData(modId) ?? new Data();
    }

    private static Data LoadData(string modId)
    {
        try
        {
            string text = ModHelper.ReadModDataFileText(modId, FileName);
            if (string.IsNullOrEmpty(text)) return null;
            Data data = new Data();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                // 文风两项是文本值，不能走 int 解析；其余越界或损坏的值夹回合法区间，未知键忽略
                if (key == "stylePack")
                {
                    // 未知键名不整条丢掉：Find 会回落到默认档
                    data.stylePack = StyleBook.Find(value).Key;
                    continue;
                }
                if (key == "styleCustom")
                {
                    data.styleCustom = value;
                    continue;
                }
                if (!int.TryParse(value, out int number)) continue;
                switch (key)
                {
                    case "dailyCap": data.dailyCap = Mathf.Clamp(number, 0, 60); break;
                    case "lineLength": data.lineLength = Mathf.Clamp(number, 80, 2000); break;
                    case "timeoutSeconds": data.timeoutSeconds = Mathf.Clamp(number, 10, 600); break;
                    case "bubbleTimeoutSeconds": data.bubbleTimeoutSeconds = Mathf.Clamp(number, 5, 60); break;
                    case "bargainMin": data.bargainMin = Mathf.Clamp(number, 1, 50); break;
                    case "bargainMax": data.bargainMax = Mathf.Clamp(number, 5, 90); break;
                    case "bargainCumulative": data.bargainCumulative = Mathf.Clamp(number, 5, 90); break;
                    case "bargainRounds": data.bargainRounds = Mathf.Clamp(number, 1, 5); break;
                }
            }
            return data;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Save()
    {
        if (string.IsNullOrEmpty(_modId)) return;
        try
        {
            string text = "# Probablytalk speech settings\n"
                + "dailyCap=" + _data.dailyCap + "\n"
                + "lineLength=" + _data.lineLength + "\n"
                + "timeoutSeconds=" + _data.timeoutSeconds + "\n"
                + "bubbleTimeoutSeconds=" + _data.bubbleTimeoutSeconds + "\n"
                + "bargainMin=" + _data.bargainMin + "\n"
                + "bargainMax=" + _data.bargainMax + "\n"
                + "bargainCumulative=" + _data.bargainCumulative + "\n"
                + "bargainRounds=" + _data.bargainRounds + "\n"
                + "stylePack=" + _data.stylePack + "\n"
                + "styleCustom=" + (_data.styleCustom ?? string.Empty).Replace('\n', ' ') + "\n";
            ModHelper.WriteModDataFileText(_modId, FileName, text);
        }
        catch (Exception)
        {
            // 落盘失败不影响运行期取值：内存值仍然生效，下次改动再尝试写入
        }
    }
}