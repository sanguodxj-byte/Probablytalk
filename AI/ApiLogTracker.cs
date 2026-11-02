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
        }
        MainThreadPump.Enqueue(() => Changed?.Invoke());
    }
}
