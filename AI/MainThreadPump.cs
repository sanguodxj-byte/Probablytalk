using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace Probablytalk;

// Unity 主线程队列：后台线程只投递 Action，Update 每帧消费。
// 所有跨线程回调（chat 结果、在线事件翻转）必须经它进主线程，后台路径不得触碰 Unity API。
internal sealed class MainThreadPump : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
    private static MainThreadPump _instance;

    public static void Enqueue(Action action)
    {
        if (action == null) return;
        // 没有泵就没有消费者：直接丢弃，避免队列残留到下次启用时被新泵重放旧回调
        if (_instance == null) return;
        Queue.Enqueue(action);
    }

    // DontDestroyOnLoad：mod 启用一次后泵跨场景存活，OnDisable 时由 DestroyInstance 回收
    public static void EnsureCreated()
    {
        if (_instance != null) return;
        var go = new GameObject("ProbablytalkMainThreadPump");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _instance = go.AddComponent<MainThreadPump>();
    }

    public static void DestroyInstance()
    {
        if (_instance == null) return;
        UnityEngine.Object.Destroy(_instance.gameObject);
        _instance = null;
        // 清空残留回调：否则下次 EnsureCreated 后，新泵会执行上一会话的 Action
        while (Queue.TryDequeue(out _)) { }
    }

    private void Update()
    {
        while (Queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }
    }
}
