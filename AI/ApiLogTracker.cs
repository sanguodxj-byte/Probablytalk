using System;
using System.Collections.Generic;

namespace Probablytalk;

public enum ApiLogState
{
    Pending,
    Success,
    Failed
}

public sealed class ApiLogEntry
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Timestamp { get; } = DateTime.Now;
    public string CustomerName { get; set; } = string.Empty;
    public string Channel { get; set; } = string.Empty;
    public string PromptText { get; set; } = string.Empty;
    public string ResponseText { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public long ElapsedMs { get; set; }
    public ApiLogState State { get; set; } = ApiLogState.Pending;

    // 上下文放开后唯一的观测口：请求/回包各多大、花了多少 token。
    // TokensEstimated 为 true 时这两个数是 TokenMeter 按字符估的，不是端点回报值
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public bool TokensEstimated { get; set; }
    public int PromptChars { get; set; }
    public int ResponseChars { get; set; }
}

public static class ApiLogTracker
{
    private const int MaxEntries = 100;
    private static readonly List<ApiLogEntry> _entries = new List<ApiLogEntry>();
    private static readonly object _lock = new object();

    public static event Action Changed;

    public static int TotalCalls { get; private set; }
    public static int TotalSuccess { get; private set; }
    public static int TotalFailed { get; private set; }

    // 累计 token 消耗：其中估算与回报各占多少条要分开看，混在一起报会让估算冒充实测值
    public static int TotalPromptTokens { get; private set; }
    public static int TotalCompletionTokens { get; private set; }
    public static int TotalReported { get; private set; }
    public static int TotalEstimated { get; private set; }

    public static IReadOnlyList<ApiLogEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    public static void Record(ApiLogEntry entry)
    {
        lock (_lock)
        {
            _entries.Insert(0, entry);
            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveAt(_entries.Count - 1);
            }
            TotalCalls++;
            if (entry.State == ApiLogState.Success) TotalSuccess++;
            else if (entry.State == ApiLogState.Failed) TotalFailed++;
            TotalPromptTokens += entry.PromptTokens;
            TotalCompletionTokens += entry.CompletionTokens;
            if (entry.State == ApiLogState.Success)
            {
                if (entry.TokensEstimated) TotalEstimated++;
                else TotalReported++;
            }
        }
        MainThreadPump.Enqueue(() => Changed?.Invoke());
    }

    public static void Update(ApiLogEntry entry)
    {
        lock (_lock)
        {
            if (entry.State == ApiLogState.Success) TotalSuccess++;
            else if (entry.State == ApiLogState.Failed) TotalFailed++;
        }
        MainThreadPump.Enqueue(() => Changed?.Invoke());
    }

    public static void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            TotalCalls = 0;
            TotalSuccess = 0;
            TotalFailed = 0;
            TotalPromptTokens = 0;
            TotalCompletionTokens = 0;
            TotalReported = 0;
            TotalEstimated = 0;
        }
        MainThreadPump.Enqueue(() => Changed?.Invoke());
    }
}
