using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Probablytalk;

// Player2 网页授权（RFC 8628 设备码流）：为「桌面端不可达」的玩家提供第二条通道。
//
// 流程（消息与授权统一走 https://api.player2.game/v1）：
//   1) POST /login/device/new   { client_id }            → device_code / user_code / verification_uri(_complete) / interval / expires_in
//   2) 主线程 Application.OpenURL(verification_uri_complete) 打开浏览器让玩家批准
//   3) 每 interval 秒轮询 POST /login/device/token { client_id, device_code, grant_type }：
//        HTTP 200 且含 p2Key → 成功，回调交给上层写回配置；
//        HTTP 400           → 玩家尚未批准，继续等；
//        其它状态码         → 直接失败
//
// 线程模型与 Player2Client 一致：HTTP 在线程池，Unity API（OpenURL/事件）经 MainThreadPump 回主线程。
// 状态变化只经 Changed 事件通知 UI，上层不得轮询。
internal sealed class Player2Auth : IDisposable
{
    // 授权端点：与桌面端本地 API 无关，固定指向公开网页 API
    private const string DeviceNewUrl = "https://api.player2.game/v1/login/device/new";
    private const string DeviceTokenUrl = "https://api.player2.game/v1/login/device/token";
    private const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    // 轮询间隔下限与默认有效期：官方默认 interval=5s，这里再兜一层下限防过快轮询
    private const int DefaultIntervalSeconds = 5;
    private const int MinIntervalSeconds = 3;
    private const int DefaultExpiresSeconds = 600;
    private const int HttpTimeoutSeconds = 15;

    private readonly ModLog _log;
    private readonly HttpClient _http = new HttpClient();
    private readonly object _lock = new object();
    private CancellationTokenSource _cts;

    // 是否正在授权中（UI 据此切换按钮态）
    public bool IsAuthenticating { get; private set; }

    // 浏览器待打开的批准页（verification_uri_complete，缺失时回退 verification_uri）
    public string ApprovalUrl { get; private set; }

    // 展示给玩家的用户码（网页端手动输入用）
    public string UserCode { get; private set; }

    // 最近一次失败原因；成功或重新开始时清空
    public string LastError { get; private set; }

    // 状态变化通知：以主线程派发，UI 可直接刷新
    public event Action Changed;

    public Player2Auth(ModLog log)
    {
        _log = log;
    }

    // 启动一次授权；已在授权中返回 false。成功后经 onKey 回主线程交付 p2Key
    public bool Start(Action<string> onKey)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (IsAuthenticating) return false;
            IsAuthenticating = true;
            ApprovalUrl = null;
            UserCode = null;
            LastError = null;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            cts = _cts;
        }
        MainThreadPump.EnsureCreated();
        Notify();
        _ = Task.Run(() => RunAsync(cts.Token, onKey));
        return true;
    }

    // 取消进行中的授权；等待中的轮询随 CancellationToken 结束
    public void Cancel()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (!IsAuthenticating) return;
            IsAuthenticating = false;
            cts = _cts;
        }
        try { cts?.Cancel(); } catch { }
        Notify();
    }

    // 重开批准页已移除：游戏扫描器把 Application.OpenURL(动态 URL) 判为
    // process-start 硬拒绝，授权链接改由设置窗口展示、玩家手动复制打开

    public void Dispose()
    {
        Cancel();
        try { _http.Dispose(); } catch { }
    }

    private async Task RunAsync(CancellationToken token, Action<string> onKey)
    {
        try
        {
            string newBody = "{\"client_id\":\"" + Player2Client.GameClientId + "\"}";
            var (newStatus, newText) = await PostAsync(DeviceNewUrl, newBody, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            if (newStatus != 200 || string.IsNullOrEmpty(newText))
            {
                Fail("device/new HTTP " + newStatus);
                return;
            }

            JObject json;
            try { json = JObject.Parse(newText); }
            catch (Exception) { Fail("device/new unparsable response"); return; }

            // 同时兼容 snake_case 与 camelCase：官方文档与不同版本返回字段大小写并不统一
            string deviceCode = Str(json, "device_code", "deviceCode");
            string userCode = Str(json, "user_code", "userCode");
            string approval = Str(json, "verification_uri_complete", "verificationUriComplete");
            if (string.IsNullOrEmpty(approval)) approval = Str(json, "verification_uri", "verificationUri");
            int interval = Math.Max(MinIntervalSeconds, Int(json, DefaultIntervalSeconds, "interval"));
            int expires = Int(json, DefaultExpiresSeconds, "expires_in", "expiresIn");

            if (string.IsNullOrEmpty(deviceCode) || string.IsNullOrEmpty(approval))
            {
                Fail("device/new missing fields");
                return;
            }

            lock (_lock)
            {
                UserCode = userCode;
                ApprovalUrl = approval;
            }
            Notify();

            string tokenBody = "{\"client_id\":\"" + Player2Client.GameClientId
                + "\",\"device_code\":\"" + deviceCode
                + "\",\"grant_type\":\"" + DeviceGrantType + "\"}";

            DateTime deadline = DateTime.UtcNow.AddSeconds(expires > 0 ? expires : DefaultExpiresSeconds);
            while (!token.IsCancellationRequested)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    Fail("authorization timed out");
                    return;
                }

                try { await Task.Delay(TimeSpan.FromSeconds(interval), token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                if (token.IsCancellationRequested) return;

                var (status, text) = await PostAsync(DeviceTokenUrl, tokenBody, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return;

                if (status == 200 && !string.IsNullOrEmpty(text))
                {
                    string key = null;
                    try { key = Str(JObject.Parse(text), "p2Key", "p2_key"); }
                    catch (Exception) { }
                    if (!string.IsNullOrEmpty(key))
                    {
                        Succeed(key, onKey);
                        return;
                    }
                }

                // 400 = 玩家还没在网页点批准，属于正常等待
                if (status == 400) continue;

                Fail("token HTTP " + status);
                return;
            }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) Fail(ex.Message);
        }
    }

    private void Succeed(string key, Action<string> onKey)
    {
        lock (_lock)
        {
            IsAuthenticating = false;
            LastError = null;
        }
        if (_log != null) _log.Log("Player2 web authorization succeeded");
        Notify();
        // 回写配置涉及 ModConfig 落盘与 UI 刷新，必须回主线程
        MainThreadPump.Enqueue(() =>
        {
            try { onKey?.Invoke(key); }
            catch (Exception ex) { if (_log != null) _log.Warning("Player2 auth callback failed: " + ex.Message); }
        });
    }

    private void Fail(string message)
    {
        lock (_lock)
        {
            IsAuthenticating = false;
            LastError = message;
        }
        if (_log != null) _log.Warning("Player2 web authorization failed: " + message);
        Notify();
    }

    private async Task<(int status, string text)> PostAsync(string url, string jsonBody, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(HttpTimeoutSeconds));
        using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ((int)response.StatusCode, body);
    }

    private void Notify()
    {
        MainThreadPump.Enqueue(() =>
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { if (_log != null) _log.Warning("Player2 auth Changed handler failed: " + ex.Message); }
        });
    }

    // 取第一个非空字段：同时兼容 snake_case / camelCase，字段缺失或为空则继续找下一个
    private static string Str(JObject obj, params string[] names)
    {
        foreach (string name in names)
        {
            JToken token = obj?[name];
            if (token != null && token.Type != JTokenType.Null)
            {
                string text = token.ToString();
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        return null;
    }

    private static int Int(JObject obj, int fallback, params string[] names)
    {
        foreach (string name in names)
        {
            JToken token = obj?[name];
            if (token != null && token.Type != JTokenType.Null && int.TryParse(token.ToString(), out int value))
            {
                return value;
            }
        }
        return fallback;
    }
}