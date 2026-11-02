using System;
using System.Linq;
using Probablytalk;
using UnityEngine;

public class ModEntry : IMod
{
    private ModManifest manifest;
    private ModLog log;
    private Player2Config config;
    private Player2Client client;
    private Player2Auth auth;
    private GameObject uiHost;

    private static ModEntry active;

    // 验收桥读取口：ProbablyBridge 经静态成员取运行期实例与快照，避免反射 internal 类型
    public static ModLog Log => active != null ? active.log : null;
    public static Player2Client ActiveClient => active != null ? active.client : null;
    public static string ModId => active != null && active.manifest != null ? active.manifest.ID : null;
    public static string RegularsSnapshot() => active != null ? RegularManager.SnapshotJson() : null;
    public static string SpeechSnapshot() => active != null ? ClientSpeech.SnapshotJson() : null;
    public static string TalksSnapshot() => active != null ? CustomerArchive.SnapshotJson() : null;
    public static string CreditsSnapshot() => active != null ? CreditBook.SnapshotJson() : null;

    public void Init(ModManifest manifest)
    {
        this.manifest = manifest;
        active = this;
        log = new ModLog(manifest);
        config = new Player2Config(manifest.ID);
        client = new Player2Client(config, log);
        ClientSpeech.Setup(config, client, log);
        RegularManager.Setup(manifest.ID, log);
        CustomerArchive.Setup(manifest.ID, client, log);
        CreditBook.Setup(manifest.ID, log);
        // 生成节奏设置：载入持久化值；界面承载在模组的齿轮设置窗口（见 SettingsUI），
        // 不再登记进游戏原生「选项」菜单
        SpeechSettings.Load(manifest.ID);
        // 连接通道：创建授权器（网页设备码流按需启动，由顾客对话窗口的「设置」按钮触发）
        auth = new Player2Auth(log);
        log.Log("Init");
    }

    public void OnEnable()
    {
        ModHook.OnGameLoadedLate += OnGameLoadedLate;
        ModHook.OnNewGameStarted += OnNewGameStarted;
        ModHook.OnGenerateCustomerNormal += OnGenerateCustomerNormal;
        // 常客调度先于发言扫描订阅：VeryLate 内先入队到期常客，随后的发言包预生成才覆盖到熟客
        ModHook.OnGenerateCustomerVeryLate += RegularManager.OnGenerateCustomerVeryLate;
        ModHook.OnGenerateCustomerVeryLate += OnGenerateCustomerVeryLate;
        ModHook.OnShutterClosedLate += RegularManager.OnShutterClosedLate;
        ModHook.OnShutterOpenedLate += OnShutterOpenedLate;
        client.Start();
        // 顾客对话宿主：右键柜前顾客弹选项窗，回复走原版气泡；本模组无独立聊天窗口
        uiHost = new GameObject("ProbablyTalkCustomerHost");
        UnityEngine.Object.DontDestroyOnLoad(uiHost);
        uiHost.AddComponent<CustomerChatUI>().Setup(manifest.ID, client, log);
        // 连接窗口宿主：由顾客对话窗口的「设置」按钮唤出，承载网页授权与本地桌面端检测
        uiHost.AddComponent<SettingsUI>().Setup(manifest.ID, config, client, auth, log);
        log.Log("OnEnable");
    }

    public void OnDisable()
    {
        if (uiHost != null) UnityEngine.Object.Destroy(uiHost);
        uiHost = null;
        client.Stop();
        ModHook.OnGameLoadedLate -= OnGameLoadedLate;
        ModHook.OnNewGameStarted -= OnNewGameStarted;
        ModHook.OnGenerateCustomerNormal -= OnGenerateCustomerNormal;
        ModHook.OnGenerateCustomerVeryLate -= RegularManager.OnGenerateCustomerVeryLate;
        ModHook.OnGenerateCustomerVeryLate -= OnGenerateCustomerVeryLate;
        ModHook.OnShutterClosedLate -= RegularManager.OnShutterClosedLate;
        ModHook.OnShutterOpenedLate -= OnShutterOpenedLate;
        auth?.Dispose();
        auth = null;
        RegularManager.Shutdown();
        CustomerArchive.Shutdown();
        CreditBook.Shutdown();
        BargainBridge.Shutdown();
        ClientInsight.Shutdown();
        ClientSpeech.Shutdown();
        log.Log("OnDisable");
    }

    private void OnGameLoadedLate()
    {
        // 顾客档案先加载：常客档案在 LoadAll 里要把旧的 name/faction/memory 迁移过来，
        // 必须已经能看到 talk:<id>:data
        CustomerArchive.LoadAll();
        RegularManager.LoadAll();
        CreditBook.LoadAll();
        log.Log("OnGameLoadedLate");
    }

    private void OnNewGameStarted(string scenarioId)
    {
        RegularManager.OnNewGameStarted();
        CustomerArchive.OnNewGameStarted();
        CreditBook.OnNewGameStarted();
        log.Log("OnNewGameStarted: " + scenarioId);
    }

    // clientStack 可能含 null 项与 identifier 为 null 的项（ClientGenCheck 同源防御）
    private void OnGenerateCustomerNormal(StoreClientManager manager)
    {
        string identifiers = (manager?.clientStack == null)
            ? string.Empty
            : string.Join(", ", manager.clientStack
                .Where(client => client != null && client.identifier != null)
                .Select(client => client.identifier));
        log.Log("OnGenerateCustomerNormal clientStack: " + identifiers);
    }


    // 当日顾客名单定型点：全栈扫描发起预生成，管线内部只对「聊过的顾客」发请求
    private void OnGenerateCustomerVeryLate(StoreClientManager manager)
    {
        ClientSpeech.OnDayStackReady(manager);
    }

    // ModHook 只有 Early/Late 两个开卷帘事件，订阅 Late 阶段
    private void OnShutterOpenedLate()
    {
        log.Log("OnShutterOpenedLate");
    }
}