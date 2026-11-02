using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// Player2 客户端：chat 走两条通道，心跳负责本地在线判定与时长归因。
//   通道一 · 本地桌面端：自动发现端口（读 %APPDATA%\game.player2.client\api.port，回退 4315/4316）
//                        → GET /v1/health 探活 → POST /v1/login/web/{clientId} 换本地 p2Key
//   通道二 · 网页授权：桌面端不可达时，用连接窗口设备码授权得到的 P2 Key 走云端 API
//   通道三 · 自备端点：前两条都不通时的 OpenAI 兼容兜底（见 Player2Config.Fallback）
// 所有 HTTP 都在线程池上执行（Task.Run + ConfigureAwait(false)），Unity 主线程只经
// MainThreadPump 消费回调，任何失败都按常规路径返回 null 落日志，不抛未捕获异常。
public sealed class Player2Client
{
    // 归因标识：chat 与心跳请求必须携带它，游玩时长才能归因到本 mod 名下；它是注册标识，不是密钥
    public const string GameClientId = "01a05876-8d6b-7376-92ef-0e51b45d130c";

    // Player2 桌面应用未显式配置时的默认监听地址
    public const string DefaultBaseUrl = "http://127.0.0.1:4315";

    // 网页/云端 API 根地址（不含版本段；请求路径统一带 /v1）
    public const string DefaultCloudBaseUrl = "https://api.player2.game";

    private const string GameKeyHeader = "player2-game-key";
    private const string ChatPath = "/v1/chat/completions";
    private const string HealthPath = "/v1/health";
    private const string LocalLoginPath = "/v1/login/web/";

    // 本地探测的候选端口：先读 api.port，读不到再按这两个官方默认端口试
    private static readonly int[] LocalFallbackPorts = { 4315, 4316 };

    // 本地探测单次超时：桌面端没开时端口通常直接拒连（快），只有端口开着但不响应才会吃满这个超时
    private const int LocalProbeTimeoutSeconds = 2;

    // 本地发现结果缓存时长：避免每条发言都重新探活+登录；到期或手动检测时失效
    private static readonly TimeSpan LocalCacheTtl = TimeSpan.FromSeconds(30);

    // 并发上限：饱和时新请求立即失败而非排队，防止请求无界堆积
    private const int MaxConcurrentChats = 2;

    // 在线时单次对话最多尝试 3 次（首次 + 2 次退避重试）；离线只试 1 次，避免对不可达端口刷请求
    private const int MaxAttemptsOnline = 3;
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) };

    private const int HealthTimeoutSeconds = 5;

    // 心跳周期：官方口径每分钟一次 /v1/health，用于在线判定与分红归因
    private const int HeartbeatIntervalMs = 60000;

    private readonly Player2Config _config;
    private readonly ModLog _log;
    private readonly SemaphoreSlim _chatGate = new SemaphoreSlim(MaxConcurrentChats, MaxConcurrentChats);
    private readonly object _stateLock = new object();
    private readonly object _localLock = new object();

    private HttpClient _http;
    private CancellationTokenSource _shutdown;
    private bool _started;

    // 桌面端是否可达（心跳探测）
    private bool _desktopOnline;
    // 组合在线态：桌面端可达 或 已配置网页授权 Key（两者任一为真即可发起请求）
    private bool _online;

    // 本地发现结果缓存：命中即复用；null 表示已探测但未发现（负缓存同样有效）
    private LocalConnection _localCache;
    private bool _localCacheKnown;
    private DateTime _localCacheAt;

    // 在线判定：桌面端可达，或已配置网页授权 Key（此时允许尝试云端通道）
    public bool IsOnline => _online;

    // 桌面端是否可达（诊断用：仅供调试面板区分本地/云端通道）
    public bool DesktopOnline => _desktopOnline;

    public bool HasCloudKey => !string.IsNullOrWhiteSpace(_config.P2Key);

    // 仅在组合在线态翻转时触发，回调已切到 Unity 主线程
    public event Action<bool> OnlineChanged;

    // 最近一次失败原因；诊断用，成功时清空
    public string LastError { get; private set; }

    private sealed class LocalConnection
    {
        public string BaseUrl;
        public string Key;

        public LocalConnection(string baseUrl, string key)
        {
            BaseUrl = baseUrl;
            Key = key;
        }
    }

    public Player2Client(Player2Config config, ModLog log)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log;
    }

    // 启动心跳循环；内部异常全部吞掉，只落日志
    public void Start()
    {
        if (_started) return;
        _started = true;
        MainThreadPump.EnsureCreated();
        _shutdown = new CancellationTokenSource();
        _http = new HttpClient();
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");
        _ = Task.Run(() => HeartbeatLoopAsync(_shutdown.Token));
    }

    // 停心跳、中断在途请求、回收主线程泵；幂等
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        try { _shutdown?.Cancel(); } catch { }
        try { _shutdown?.Dispose(); } catch { }
        _shutdown = null;
        try { _http?.Dispose(); } catch { }
        _http = null;
        MainThreadPump.DestroyInstance();
    }

    // 供连接窗口在授权成功写入 P2 Key 后调用：重算组合在线态并触发在线事件
    public void RefreshOnlineState()
    {
        SetOnline(_desktopOnline || HasCloudKey);
    }

    // chat(系统提示, 用户输入, 上下文JSON)：异步返回 choices[0].message.content，失败返回 null（原因见 LastError）。
    // 整体切到线程池执行，调用方在主线程 await 也不阻塞帧。
    public Task<string> ChatAsync(string systemPrompt, string userInput, string contextJson)
    {
        return Task.Run(() => ChatCoreAsync(BuildMessages(systemPrompt, userInput, contextJson)));
    }

    // 多轮版：turns 为按发生顺序排列的完整对话历史（含本轮用户输入），Key 为角色（"user"/"assistant"），
    // Value 为该轮文本；systemPrompt 置顶。其余行为与三参版一致。
    public Task<string> ChatAsync(string systemPrompt, IReadOnlyList<KeyValuePair<string, string>> turns)
    {
        return Task.Run(() => ChatCoreAsync(BuildMessages(systemPrompt, turns)));
    }

    // 回调版：结果经 MainThreadPump 在 Unity 主线程递送；ok=false 时 text 为 null
    public void Chat(string systemPrompt, string userInput, string contextJson, Action<bool, string> onResult)
    {
        Deliver(ChatAsync(systemPrompt, userInput, contextJson), onResult);
    }

    // 多轮回调版
    public void Chat(string systemPrompt, IReadOnlyList<KeyValuePair<string, string>> turns, Action<bool, string> onResult)
    {
        Deliver(ChatAsync(systemPrompt, turns), onResult);
    }

    // 供连接窗口的「检测本地桌面端」按钮：强制重探测并回主线程返回结果说明
    public void DetectLocal(Action<string> onResult)
    {
        HttpClient http = _http;
        if (!_started || http == null)
        {
            DeliverString("client not started", onResult);
            return;
        }
        InvalidateLocalCache();
        _ = Task.Run(async () =>
        {
            CancellationToken token = _shutdown != null ? _shutdown.Token : CancellationToken.None;
            LocalConnection local = await DiscoverLocalAsync(token).ConfigureAwait(false);
            string message = local != null
                ? "Local desktop app found: " + local.BaseUrl
                    + (string.IsNullOrEmpty(local.Key) ? " (login key unavailable)" : "")
                : "No local Player2 desktop app detected.";
            DeliverString(message, onResult);
        });
    }

    private void Deliver(Task<string> task, Action<bool, string> onResult)
    {
        task.ContinueWith(t =>
        {
            bool ok = t.Status == TaskStatus.RanToCompletion && t.Result != null;
            MainThreadPump.Enqueue(() =>
            {
                try
                {
                    onResult?.Invoke(ok, ok ? t.Result : null);
                }
                catch (Exception ex)
                {
                    if (_log != null) _log.Warning("Player2 chat callback failed: " + ex.Message);
                }
            });
        });
    }

    private void DeliverString(string message, Action<string> onResult)
    {
        MainThreadPump.Enqueue(() =>
        {
            try { onResult?.Invoke(message); }
            catch (Exception ex) { if (_log != null) _log.Warning("Player2 detect callback failed: " + ex.Message); }
        });
    }

    public Task<List<string>> FetchModelsAsync(string endpoint, string apiKey)
    {
        return Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(endpoint)) return new List<string>();
            string modelsUrl = Player2Config.DeriveModelsUrl(endpoint);
            if (string.IsNullOrEmpty(modelsUrl)) return new List<string>();
            HttpClient http = _http ?? new HttpClient();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                if (!string.IsNullOrEmpty(apiKey))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var response = await http.SendAsync(request, cts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return new List<string>();
                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                JObject obj = JObject.Parse(body);
                JArray data = obj["data"] as JArray;
                if (data == null) return new List<string>();
                var list = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (JToken item in data)
                {
                    string id = (string)item["id"];
                    if (!string.IsNullOrWhiteSpace(id) && seen.Add(id.Trim()))
                    {
                        list.Add(id.Trim());
                    }
                }
                return list;
            }
            catch (Exception ex)
            {
                if (_log != null) _log.Warning("FetchModels failed: " + ex.Message);
                return new List<string>();
            }
        });
    }

    private async Task<string> ChatCoreAsync(JArray messages)
    {
        HttpClient http = _http;
        if (!_started || http == null)
        {
            SetLastError("client not started");
            return null;
        }

        var logEntry = new ApiLogEntry
        {
            PromptText = FormatMessagesPrompt(messages)
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        bool acquired = false;
        try
        {
            CancellationToken shutdownToken = _shutdown != null ? _shutdown.Token : CancellationToken.None;
            if (!await _chatGate.WaitAsync(TimeSpan.FromSeconds(15), shutdownToken).ConfigureAwait(false))
            {
                SetLastError("chat concurrency limit reached");
                logEntry.State = ApiLogState.Failed;
                logEntry.Error = "chat concurrency limit reached";
                return null;
            }
            acquired = true;

            int timeout = SpeechSettings.TimeoutSeconds;

            // 通道一 · 本地桌面端（自动发现端口 → 健康 → 本地登录换 key）
            LocalConnection local = await DiscoverLocalAsync(shutdownToken).ConfigureAwait(false);
            if (local != null)
            {
                logEntry.Channel = "local";
                IAIClient channel = new Player2EndpointClient("local", local.BaseUrl, local.Key, timeout);
                string text = await RunWithRetryAsync(channel, messages, _online, shutdownToken).ConfigureAwait(false);
                if (text != null)
                {
                    SetOnline(true);
                    logEntry.State = ApiLogState.Success;
                    logEntry.ResponseText = text;
                    return text;
                }
            }

            // 通道二 · 网页授权（云端 API + P2 Key）
            string cloudKey = _config.P2Key;
            if (!string.IsNullOrWhiteSpace(cloudKey))
            {
                logEntry.Channel = "cloud";
                IAIClient channel = new Player2EndpointClient("cloud", _config.CloudBaseUrl, cloudKey, timeout);
                string text = await RunWithRetryAsync(channel, messages, _online, shutdownToken).ConfigureAwait(false);
                if (text != null)
                {
                    SetOnline(true);
                    logEntry.State = ApiLogState.Success;
                    logEntry.ResponseText = text;
                    return text;
                }
            }

            // 通道三 · 自备 OpenAI 兼容端点（BYO 兜底）；密钥可留空（Ollama 等无鉴权服务）
            string byoEndpoint = _config.EffectiveFallbackEndpoint();
            if (!string.IsNullOrWhiteSpace(byoEndpoint))
            {
                logEntry.Channel = "byo";
                IAIClient channel = new ByoEndpointClient(byoEndpoint, _config.FallbackApiKey, _config.Model, timeout);
                string text = await RunWithRetryAsync(channel, messages, retries: false, token: shutdownToken).ConfigureAwait(false);
                if (text != null)
                {
                    logEntry.State = ApiLogState.Success;
                    logEntry.ResponseText = text;
                    return text;
                }
            }
            logEntry.State = ApiLogState.Failed;
            logEntry.Error = LastError ?? "all channels failed";
            return null;
        }
        catch (Exception ex)
        {
            SetLastError(ex.Message);
            logEntry.State = ApiLogState.Failed;
            logEntry.Error = ex.Message;
            return null;
        }
        finally
        {
            sw.Stop();
            logEntry.ElapsedMs = sw.ElapsedMilliseconds;
            ApiLogTracker.Record(logEntry);
            if (acquired) _chatGate.Release();
        }
    }

    private static string FormatMessagesPrompt(JArray messages)
    {
        if (messages == null || messages.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (JToken t in messages)
        {
            string role = (string)t["role"] ?? "unknown";
            string content = (string)t["content"] ?? string.Empty;
            sb.AppendLine($"[{role}] {content}");
        }
        return sb.ToString().Trim();
    }

    // 重试策略集中在这里：通道只报「能不能重试」，次数与退避由主流程决定
    private async Task<string> RunWithRetryAsync(IAIClient channel, JArray messages, bool retries, CancellationToken token)
    {
        int attempts = retries ? MaxAttemptsOnline : 1;
        for (int attempt = 1; ; attempt++)
        {
            CompletionResult result = await channel.CompleteAsync(_http, messages, token).ConfigureAwait(false);
            if (result.Ok)
            {
                SetLastError(null);
                return result.Text;
            }
            if (result.Error != "cancelled")
            {
                SetLastError("[" + channel.Name + "] " + result.Error);
            }
            if (!result.Retryable || attempt >= attempts) return null;
            try
            {
                await Task.Delay(RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)], token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    // 三参入口的消息序列：system 置顶，contextJson 非空时作为独立 user 消息插在用户输入之前，
    // 不拼接任何标签文字。
    private static JArray BuildMessages(string systemPrompt, string userInput, string contextJson)
    {
        var messages = new JArray();
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new JObject { ["role"] = "system", ["content"] = systemPrompt });
        }
        if (!string.IsNullOrEmpty(contextJson))
        {
            messages.Add(new JObject { ["role"] = "user", ["content"] = contextJson });
        }
        messages.Add(new JObject { ["role"] = "user", ["content"] = userInput ?? string.Empty });
        return messages;
    }

    // 多轮入口的消息序列：system 置顶后按发生顺序追加对话轮，角色原样保留。
    private static JArray BuildMessages(string systemPrompt, IReadOnlyList<KeyValuePair<string, string>> turns)
    {
        var messages = new JArray();
        if (!string.IsNullOrEmpty(systemPrompt))
        {
            messages.Add(new JObject { ["role"] = "system", ["content"] = systemPrompt });
        }
        if (turns != null)
        {
            foreach (KeyValuePair<string, string> turn in turns)
            {
                messages.Add(new JObject { ["role"] = turn.Key, ["content"] = turn.Value ?? string.Empty });
            }
        }
        return messages;
    }

    // ============================================================
    // 本地桌面端发现
    // ============================================================

    private async Task<LocalConnection> DiscoverLocalAsync(CancellationToken token)
    {
        lock (_localLock)
        {
            if (_localCacheKnown && DateTime.UtcNow - _localCacheAt < LocalCacheTtl)
            {
                return _localCache;
            }
        }

        LocalConnection found = await ProbeLocalAsync(token).ConfigureAwait(false);
        lock (_localLock)
        {
            _localCache = found;
            _localCacheKnown = true;
            _localCacheAt = DateTime.UtcNow;
        }
        return found;
    }

    private void InvalidateLocalCache()
    {
        lock (_localLock)
        {
            _localCacheKnown = false;
            _localCache = null;
        }
    }

    private async Task<LocalConnection> ProbeLocalAsync(CancellationToken token)
    {
        foreach (string root in LocalCandidateRoots())
        {
            if (token.IsCancellationRequested) return null;
            if (!await IsHealthyAsync(root, token).ConfigureAwait(false)) continue;
            string key = await TryLocalLoginAsync(root, token).ConfigureAwait(false);
            return new LocalConnection(root, key);
        }
        return null;
    }

    // 候选根地址：显式覆盖优先（只用它）；否则 api.port → 官方默认端口，去重后逐个试
    private List<string> LocalCandidateRoots()
    {
        var roots = new List<string>();
        string overrideUrl = _config.LocalBaseUrl;
        if (!string.IsNullOrWhiteSpace(overrideUrl))
        {
            roots.Add(Normalize(overrideUrl));
            return roots;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 只探测固定端口：不读 %APPDATA%\game.player2.client\api.port，
        // 模组因此不需要 files 能力；桌面端用了自定义端口时请在设置里手填地址
        foreach (int p in LocalFallbackPorts) AddPortRoots(roots, seen, p);
        return roots;
    }

    private static void AddPortRoots(List<string> roots, HashSet<string> seen, int port)
    {
        foreach (string host in new[] { "127.0.0.1", "localhost" })
        {
            string root = "http://" + host + ":" + port;
            if (seen.Add(root)) roots.Add(root);
        }
    }

    // 健康成功判定：HTTP 200 且响应含 client_version 字段
    private async Task<bool> IsHealthyAsync(string root, CancellationToken token)
    {
        HttpClient http = _http;
        if (http == null) return false;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, root + HealthPath);
            request.Headers.TryAddWithoutValidation(GameKeyHeader, GameClientId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(LocalProbeTimeoutSeconds));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return HasClientVersion(body);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // 本地登录：POST /v1/login/web/{clientId}，成功返回本地 p2Key；失败返回 null（仍视为本地可用，退化为不带头）
    private async Task<string> TryLocalLoginAsync(string root, CancellationToken token)
    {
        HttpClient http = _http;
        if (http == null) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, root + LocalLoginPath + GameClientId);
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation(GameKeyHeader, GameClientId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(LocalProbeTimeoutSeconds));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseP2Key(body);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ParseP2Key(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            var json = JObject.Parse(body);
            foreach (string name in new[] { "p2Key", "p2_key" })
            {
                JToken token = json[name];
                if (token != null && token.Type == JTokenType.String)
                {
                    string value = (string)token;
                    if (!string.IsNullOrEmpty(value)) return value;
                }
            }
        }
        catch (JsonReaderException)
        {
        }
        return null;
    }

    private static string Normalize(string url)
    {
        return (url ?? string.Empty).Trim().TrimEnd('/');
    }

    // 心跳探测的根地址：显式覆盖 → 已发现的本地地址 → 默认地址
    private string HeartbeatRoot()
    {
        string overrideUrl = _config.LocalBaseUrl;
        if (!string.IsNullOrWhiteSpace(overrideUrl)) return Normalize(overrideUrl);
        lock (_localLock)
        {
            if (_localCache != null) return _localCache.BaseUrl;
        }
        return DefaultBaseUrl;
    }

    // ============================================================
    // 心跳与状态
    // ============================================================

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await CheckHealthAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 循环级兜底：任何意外都不能让心跳线程死亡或外抛
                if (_log != null) _log.Warning("Player2 heartbeat failed: " + ex.Message);
                SetDesktopOnline(false);
            }
            try
            {
                await Task.Delay(HeartbeatIntervalMs, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task CheckHealthAsync(CancellationToken token)
    {
        HttpClient http = _http;
        if (http == null || _shutdown == null) return;
        bool desktopOnline;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, HeartbeatRoot() + HealthPath);
            request.Headers.TryAddWithoutValidation(GameKeyHeader, GameClientId);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(HealthTimeoutSeconds));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string body = response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync().ConfigureAwait(false)
                : null;
            desktopOnline = body != null && HasClientVersion(body);
        }
        catch (Exception)
        {
            if (token.IsCancellationRequested || _shutdown == null) return;
            desktopOnline = false;
        }
        SetDesktopOnline(desktopOnline);
    }

    private static bool HasClientVersion(string body)
    {
        try
        {
            return JObject.Parse(body)["client_version"] != null;
        }
        catch (JsonReaderException)
        {
            return false;
        }
    }

    private void SetDesktopOnline(bool desktopOnline)
    {
        lock (_stateLock)
        {
            _desktopOnline = desktopOnline;
        }
        SetOnline(desktopOnline || HasCloudKey);
    }

    private void SetOnline(bool online)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _online != online;
            _online = online;
        }
        if (!changed) return;
        if (_log != null) _log.Log("Player2 " + (online ? "online" : "offline"));
        MainThreadPump.Enqueue(() =>
        {
            try
            {
                OnlineChanged?.Invoke(online);
            }
            catch (Exception ex)
            {
                if (_log != null) _log.Warning("Player2 OnlineChanged handler failed: " + ex.Message);
            }
        });
    }

    private void SetLastError(string error)
    {
        LastError = error;
        if (!string.IsNullOrEmpty(error) && _log != null) _log.Warning("Player2 chat failed: " + error);
    }
}