using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Probablytalk;

// 单次补全的结果：成功/失败与「值不值得重试」分开表达，
// 重试策略留在调用方（Player2Client），各通道只负责自己那一次 HTTP 往返
internal sealed class CompletionResult
{
    public bool Ok;
    public string Text;
    public string Error;
    public bool Retryable;
    public int Status;

    public static CompletionResult Success(string text)
    {
        return new CompletionResult { Ok = true, Text = text };
    }

    public static CompletionResult Failure(string error, bool retryable, int status = 0)
    {
        return new CompletionResult { Ok = false, Error = error, Retryable = retryable, Status = status };
    }
}

// AI 通道抽象：一个实现 = 一种端点家族（本地 Player2 桌面端 / 云端 / 自备 OpenAI 兼容端点）。
// 加新通道只需再写一个实现并在 ChatClientFactory 里注册，Player2Client 主流程不用改。
internal interface IAIClient
{
    // 通道名：只用于日志与诊断
    string Name { get; }

    // 发起一次补全；不重试、不抛异常（失败一律返回 CompletionResult）
    Task<CompletionResult> CompleteAsync(HttpClient http, JArray messages, CancellationToken token);
}

// 三条通道共用的 HTTP 细节：POST JSON、解析 choices[0].message.content、把异常翻译成结果
internal static class ChatHttp
{
    public const string GameKeyHeader = "player2-game-key";

    public static async Task<CompletionResult> PostJsonAsync(HttpClient http, string url, string json,
        string gameKey, string bearerKey, int timeoutSeconds, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            if (!string.IsNullOrEmpty(gameKey))
            {
                request.Headers.TryAddWithoutValidation(GameKeyHeader, gameKey);
            }
            if (!string.IsNullOrEmpty(bearerKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearerKey);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return CompletionResult.Failure(code + " " + response.StatusCode, code == 429 || code >= 500, code);
            }
            string content = ExtractContent(body);
            return content != null
                ? CompletionResult.Success(content)
                : CompletionResult.Failure("unparseable response", false, code);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 关闭流程主动取消：不算失败，交给上层静默结束
            return CompletionResult.Failure("cancelled", false);
        }
        catch (OperationCanceledException)
        {
            return CompletionResult.Failure("chat timed out", true);
        }
        catch (HttpRequestException ex)
        {
            return CompletionResult.Failure(ex.Message, true);
        }
        catch (Exception ex)
        {
            return CompletionResult.Failure(ex.Message, false);
        }
    }

    public static string ExtractContent(string body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            JObject response = JObject.Parse(body);
            if (response["choices"]?[0]?["message"]?["content"] is JValue value && value.Type == JTokenType.String)
            {
                return (string)value;
            }
        }
        catch (JsonReaderException)
        {
        }
        return null;
    }

    public static string Body(JArray messages, string model)
    {
        var body = new JObject { ["messages"] = messages };
        if (!string.IsNullOrWhiteSpace(model)) body["model"] = model.Trim();
        return body.ToString(Formatting.None);
    }
}

// Player2 家族（本地桌面端与云端同形状）：/v1/chat/completions，不带 model，带归因 header
internal sealed class Player2EndpointClient : IAIClient
{
    private readonly string _baseUrl;
    private readonly string _bearerKey;
    private readonly int _timeoutSeconds;

    public Player2EndpointClient(string name, string baseUrl, string bearerKey, int timeoutSeconds)
    {
        Name = name;
        _baseUrl = (baseUrl ?? string.Empty).TrimEnd('/');
        _bearerKey = bearerKey;
        _timeoutSeconds = timeoutSeconds;
    }

    public string Name { get; }

    public Task<CompletionResult> CompleteAsync(HttpClient http, JArray messages, CancellationToken token)
    {
        string json = ChatHttp.Body(messages, null);
        return ChatHttp.PostJsonAsync(http, _baseUrl + "/v1/chat/completions", json,
            Player2Client.GameClientId, _bearerKey, _timeoutSeconds, token);
    }
}

// 自备 OpenAI 兼容端点：/chat/completions，带 model，密钥可留空
internal sealed class ByoEndpointClient : IAIClient
{
    private readonly string _endpoint;
    private readonly string _bearerKey;
    private readonly string _model;
    private readonly int _timeoutSeconds;

    public ByoEndpointClient(string endpoint, string bearerKey, string model, int timeoutSeconds)
    {
        _endpoint = (endpoint ?? string.Empty).TrimEnd('/');
        _bearerKey = bearerKey;
        _model = model;
        _timeoutSeconds = timeoutSeconds;
    }

    public string Name => "byo";

    public Task<CompletionResult> CompleteAsync(HttpClient http, JArray messages, CancellationToken token)
    {
        string json = ChatHttp.Body(messages, _model);
        string targetUrl = _endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? _endpoint
            : _endpoint + "/chat/completions";
        return ChatHttp.PostJsonAsync(http, targetUrl, json,
            null, _bearerKey, _timeoutSeconds, token);
    }
}
