using System;
using System.Collections.Generic;

namespace Probablytalk;

// 玩家配置：走游戏自带 ModConfig 系统（ModHelper.GetConfig 取实例并从磁盘载入，
// Define 注册条目时应用已存值，配置窗口应用后由游戏落盘），mod 不自管配置文件。
//
// 这里只承载「需要自由文本输入」的设置——ModConfig 是唯一提供单行输入框的扩展面：
//   Player2  —— 连接模型三件套：
//                · 本地桌面端地址（留空则探测 127.0.0.1:4315 / 4316；自定义端口在此填写完整地址）
//                · 网页/云端 API 地址（桌面端不可达时走的第二条通道）
//                · 网页授权 P2 Key（连接窗口设备码授权成功回写，也可手工粘贴）
//   Fallback —— 自备 OpenAI 兼容端点（提供商预设 / 端点 / 密钥 / 模型），两条通道都不可达时兜底
//   Prompt   —— 角色指令（默认内置，可覆盖）
// 生成节奏与议价门槛（每日上限 / 单句长度 / 超时 + 议价四项）是数值滑条，
// 已收进模组自己的齿轮设置窗口，见 SpeechSettings + SettingsUI.OpenSettingsWindow。
// 连接窗口由顾客对话窗口里的「设置」按钮唤出（无热键）。
//
// 分组名由 ModConfigWindow 当原始文本渲染、不做本地化查表，因此固定用英文中性词；
// 标签与悬浮说明传惰性回调，配置窗口打开时（Localization 已载入）才求值，跟随游戏语言实时显示。
public sealed class Player2Config
{
    private const string SectionLocal = "Player2";
    private const string SectionByo = "Fallback";
    private const string SectionPrompt = "Prompt";

    // BYO 提供商预设：除 Custom 外的选项选中后即用内置端点、忽略手填端点，
    // 与 RimTalk 的「提供商 → 基础URL」一致；Custom 表示改用手填端点
    internal const string ProviderCustom = "Custom";

    private static readonly string[] Providers = { ProviderCustom, "OpenAI", "DeepSeek", "SiliconFlow", "Ollama", "OpenRouter" };

    // 本地桌面端地址覆盖值；留空表示自动发现
    private readonly ConfigEntry<string> _localBaseUrl;
    // 网页/云端 API 根地址：桌面端不可达时经网页授权通道请求
    private readonly ConfigEntry<string> _cloudBaseUrl;
    // 网页授权 P2 Key：连接窗口授权成功后经 SetP2Key 回写，也可手工粘贴
    private readonly ConfigEntry<string> _p2Key;
    private readonly ConfigEntry<string> _provider;
    private readonly ConfigEntry<string> _fallbackEndpoint;
    private readonly ConfigEntry<string> _fallbackApiKey;
    private readonly ConfigEntry<string> _model;
    private readonly ConfigEntry<string> _prompt;

    public Player2Config(string modId)
    {
        ModConfig config = ModHelper.GetConfig(modId);

        _localBaseUrl = config.Define("LocalBaseUrl", string.Empty, SectionLocal,
            () => Loc(modId, "setting_localbase_label", "Desktop App Address"),
            tooltip: () => Loc(modId, "setting_localbase_tooltip",
                "Address of the Player2 desktop app. Leave empty to auto-detect (reads the app's api.port file, "
                + "falling back to " + Player2Client.DefaultBaseUrl + " / 4316)."));

        _cloudBaseUrl = config.Define("CloudBaseUrl", Player2Client.DefaultCloudBaseUrl, SectionLocal,
            () => Loc(modId, "setting_cloudbase_label", "Web API Address"),
            tooltip: () => Loc(modId, "setting_cloudbase_tooltip",
                "Player2 web/cloud API root, used when the desktop app is unreachable. It needs a web "
                + "authorization key. Default: " + Player2Client.DefaultCloudBaseUrl));

        _p2Key = config.Define("P2Key", string.Empty, SectionLocal,
            () => Loc(modId, "setting_p2key_label", "Web Authorization Key"),
            tooltip: () => Loc(modId, "setting_p2key_tooltip",
                "P2 Key obtained through the web authorization window (open it with the Settings button in the "
                + "customer chat window), or pasted manually. Leave empty to use the desktop app only."));

        _provider = config.Define("ByoProvider", ProviderCustom, SectionByo,
            () => Loc(modId, "setting_provider_label", "Provider Preset"),
            choices: Providers,
            tooltip: () => Loc(modId, "setting_provider_tooltip",
                "Built-in OpenAI-compatible preset. Choose Custom to use the endpoint field below."));
        _fallbackEndpoint = config.Define("ByoEndpoint", string.Empty, SectionByo,
            () => Loc(modId, "setting_endpoint_label", "Custom Endpoint"),
            tooltip: () => Loc(modId, "setting_endpoint_tooltip",
                "OpenAI-compatible base URL (without /chat/completions). Used only when the preset above is Custom."));
        _fallbackApiKey = config.Define("ByoApiKey", string.Empty, SectionByo,
            () => Loc(modId, "setting_apikey_label", "API Key"),
            tooltip: () => Loc(modId, "setting_apikey_tooltip",
                "Bearer key for the fallback endpoint. Leave empty for keyless servers such as Ollama."));
        _model = config.Define("Model", string.Empty, SectionByo,
            () => Loc(modId, "setting_model_label", "Model"),
            tooltip: () => Loc(modId, "setting_model_tooltip",
                "Model name sent to the fallback endpoint only. Leave empty to let the server pick."));

        _prompt = config.Define("SystemPrompt", ClientSpeech.DefaultSystemPrompt, SectionPrompt,
            () => Loc(modId, "setting_prompt_label", "Role Instruction"),
            tooltip: () => Loc(modId, "setting_prompt_tooltip",
                "Overrides the built-in role instruction only. The JSON reply format is appended automatically, "
                + "so this text never needs to describe it."));
    }

    // 本地 Player2 桌面应用的地址覆盖值；为空表示自动发现
    public string LocalBaseUrl => (_localBaseUrl.Value ?? string.Empty).Trim();

    // 网页/云端 API 根地址；留空回退默认，去掉尾部斜杠以便与固定路径拼接
    public string CloudBaseUrl
    {
        get
        {
            string value = (_cloudBaseUrl.Value ?? string.Empty).Trim();
            return string.IsNullOrEmpty(value) ? Player2Client.DefaultCloudBaseUrl : value.TrimEnd('/');
        }
    }

    // 网页授权 P2 Key；为空表示尚未授权、网页通道不可用
    public string P2Key => (_p2Key.Value ?? string.Empty).Trim();

    // 连接窗口设备码授权成功后的回写口：底层 ConfigEntry.Set 会同步落盘到 ModConfigs/<modId>.json，
    // 因此设置项与运行期取值共用同一份数据，不存在双写
    public void SetP2Key(string key)
    {
        _p2Key.Set(key ?? string.Empty);
    }

    // 实际生效的兜底端点：预设优先；Custom 时用手填值（可能为空 → 不启用兜底）
    public string EffectiveFallbackEndpoint()
    {
        string preset = PresetEndpoint(_provider.Value);
        return !string.IsNullOrEmpty(preset) ? preset : (_fallbackEndpoint.Value ?? string.Empty).Trim();
    }

    // 兜底端点是否可用：只看有无端点；密钥对 Ollama 等无鉴权服务可留空
    public bool HasFallback => !string.IsNullOrWhiteSpace(EffectiveFallbackEndpoint());

    // 自备 key：仅在兜底端点可用时随请求发送；为空则不发 Authorization 头
    public string FallbackApiKey => (_fallbackApiKey.Value ?? string.Empty).Trim();

    // 仅兜底通道发送；本地与网页通道都不发送 model 字段，模型由 Player2 侧决定
    public string Model => _model.Value;

    // 角色指令：覆盖值为空白时回退内置指令，避免玩家清空后请求带空 system
    public string SystemPrompt
    {
        get
        {
            string value = _prompt.Value;
            return string.IsNullOrWhiteSpace(value) ? ClientSpeech.DefaultSystemPrompt : value;
        }
    }

    // ---- 设置面板（自建窗口）读写口：与 ModConfig 条目共用同一份 ConfigEntry，无双写 ----

    // 网页/云端地址的原始存储值（空 = 用默认），供输入框回显，不用 getter 的默认回退
    public string CloudBaseUrlRaw => (_cloudBaseUrl.Value ?? string.Empty).Trim();
    public void SetLocalBaseUrl(string value) => _localBaseUrl.Set(value ?? string.Empty);
    public void SetCloudBaseUrl(string value) => _cloudBaseUrl.Set(value ?? string.Empty);

    // 提供商预设列表与当前索引（供下拉框回显）；顺序与 Providers 一致
    public static IList<string> ProviderOptions => Providers;
    public int ProviderIndex
    {
        get
        {
            int index = Array.IndexOf(Providers, _provider.Value);
            return index < 0 ? 0 : index;
        }
    }
    public void SetProvider(string value) => _provider.Set(string.IsNullOrEmpty(value) ? ProviderCustom : value);

    // 自备端点原始值（预设为 Custom 时生效）
    public string FallbackEndpointRaw => (_fallbackEndpoint.Value ?? string.Empty).Trim();
    public void SetFallbackEndpointRaw(string value) => _fallbackEndpoint.Set(value ?? string.Empty);
    public void SetFallbackEndpoint(string value) => _fallbackEndpoint.Set(NormalizeEndpointUrl(value));
    public void SetFallbackApiKey(string value) => _fallbackApiKey.Set(value ?? string.Empty);

    // 模型原始值（仅兜底通道发送）
    public string ModelRaw => _model.Value ?? string.Empty;
    public void SetModel(string value) => _model.Set(value ?? string.Empty);

    // 角色指令原始值（空 = 用内置），供输入框回显；SystemPrompt getter 会给默认回退
    public string PromptRaw => _prompt.Value ?? string.Empty;
    public void SetPrompt(string value) => _prompt.Set(value ?? string.Empty);

    public static string NormalizeEndpointUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        string url = input.Trim().TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }
        if (url.EndsWith("/v1/chat", StringComparison.OrdinalIgnoreCase))
        {
            return url + "/completions";
        }
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return url + "/chat/completions";
        }
        return url + "/v1/chat/completions";
    }

    public static string DeriveModelsUrl(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return string.Empty;
        string url = endpoint.Trim().TrimEnd('/');
        if (url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return url.Substring(0, url.Length - "/chat/completions".Length) + "/models";
        }
        if (url.EndsWith("/v1/chat", StringComparison.OrdinalIgnoreCase))
        {
            return url.Substring(0, url.Length - "/v1/chat".Length) + "/v1/models";
        }
        if (url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            return url + "/models";
        }
        return url + "/v1/models";
    }

    private static string PresetEndpoint(string provider)
    {
        switch (provider)
        {
            case "OpenAI": return "https://api.openai.com/v1/chat/completions";
            case "DeepSeek": return "https://api.deepseek.com/v1/chat/completions";
            case "SiliconFlow": return "https://api.siliconflow.cn/v1/chat/completions";
            case "Ollama": return "http://127.0.0.1:11434/v1/chat/completions";
            case "OpenRouter": return "https://openrouter.ai/api/v1/chat/completions";
            default: return null;
        }
    }

    // 查表回退链：本地化值 → 英文兜底字面量。
    // GetLocalized 在键缺失时原样返回 key，这里显式转为可读英文，避免 UI 出现裸键名
    private static string Loc(string modId, string key, string fallback)
    {
        string value = ModHelper.GetLocalized(modId, key);
        return string.IsNullOrEmpty(value) || value == key ? fallback : value;
    }
}