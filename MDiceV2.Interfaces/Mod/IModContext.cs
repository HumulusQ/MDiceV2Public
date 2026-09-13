namespace MDiceV2.Interfaces.Mod;

/// <summary>
/// Mod上下文接口
/// Mod通过此接口与宿主程序（MDiceV2）交互
/// 
/// 设计原则：
/// - 仅暴露Mod需要的必要功能，避免过度开放
/// - 所有操作都是异步友好的，可在Mod中安全使用
/// - 宿主程序负责实现此接口，Mod不应自行实现
/// 
/// 注入方式：
/// Mod的构造函数应接收IModContext参数：
/// <code>
/// public class MyMod : IModPlugin
/// {
///     private readonly IModContext _context;
///     
///     public MyMod(IModContext context)
///     {
///         _context = context;
///     }
/// }
/// </code>
/// 
/// 宿主程序使用反射调用构造函数：
/// <code>
/// var constructor = modType.GetConstructor(new[] { typeof(IModContext) });
/// var modInstance = constructor.Invoke(new object[] { modContext });
/// </code>
/// </summary>
public interface IModContext
{
    /// <summary>
    /// 发送群消息
    /// </summary>
    /// <param name="groupId">目标群号</param>
    /// <param name="content">消息内容</param>
    /// <remarks>
    /// - 内容会直接发送，无格式检查，Mod应确保内容有效
    /// - 如果WebSocket未连接，消息会被记录但不发送（仅模拟模式下显示）
    /// - 建议单条消息不超过8000字符
    /// </remarks>
    void SendGroupMessage(long groupId, string content);

    /// <summary>
    /// 发送私聊消息
    /// </summary>
    /// <param name="userId">目标用户QQ号</param>
    /// <param name="content">消息内容</param>
    void SendPrivateMessage(long userId, string content);

    /// <summary>
    /// 获取用户信息
    /// </summary>
    /// <param name="userId">QQ号</param>
    /// <returns>用户信息（昵称等），如果无缓存返回基础信息</returns>
    /// <remarks>
    /// 当前实现可能返回缓存的用户信息或基础信息
    /// 这是一个同步方法，不应进行网络I/O
    /// </remarks>
    (long UserId, string Nickname) GetUserInfo(long userId);

    /// <summary>
    /// 记录日志
    /// 日志会在程序的日志系统中显示，便于调试
    /// </summary>
    /// <param name="level">日志级别</param>
    /// <param name="message">日志内容</param>
    /// <remarks>
    /// 日志格式：[ModId] message
    /// 建议在OnLoad/OnEnable时使用Info级别记录Mod状态
    /// 在异常处理时使用Error级别记录错误信息
    /// </remarks>
    void Log(LogLevel level, string message);

    /// <summary>
    /// 当前程序是否处于模拟模式
    /// 模拟模式下消息不会实际发送到服务器，仅在UI中显示
    /// </summary>
    bool IsSimulationMode { get; }

    /// <summary>
    /// 获取导航面板注册表服务
    /// Mod 通过此服务将其 UI 面板注册到主窗口导航栏
    /// </summary>
    /// <remarks>
    /// - 应在 OnLoad() 方法中调用以注册面板
    /// - 返回 null 表示主窗口尚未初始化或不支持面板注册
    /// - 建议总是进行空值检查
    /// </remarks>
    INavigationPanelRegistry? GetNavigationPanelRegistry();

    /// <summary>
    /// 执行程序本体的指令（绕过 Mod 处理，直接调用 command handler）
    /// </summary>
    /// <param name="groupId">目标群号</param>
    /// <param name="userId">执行者QQ号</param>
    /// <param name="command">完整指令文本（如 ".ra 侦查"）</param>
    /// <remarks>
    /// - 命令必须以 . 开头
    /// - 执行结果会通过 Reply 机制发送到群聊
    /// - 模拟模式下会在 UI 中显示结果
    /// </remarks>
    void ExecuteCommand(long groupId, long userId, string command);

    /// <summary>
    /// 注册命令 reply 监听器。当由 ExecuteCommand 触发的 command handler 调用 Reply 时，
    /// 会将 reply 内容回传给 listener（groupId, userId, content）。
    /// </summary>
    /// <param name="listener">回调 Action，参数为 (groupId, userId, replyContent)</param>
    /// <remarks>
    /// - 仅捕获由本 Mod 通过 ExecuteCommand 发起的命令的 reply
    /// - 每个 Mod 可注册多个 listener；未注销则一直有效
    /// </remarks>
    void RegisterCommandReplyListener(Action<long, long, string> listener);

    /// <summary>
    /// 获取用户的权限等级（白名单等级）
    /// </summary>
    /// <param name="userId">用户QQ号</param>
    /// <returns>
    /// - null：用户未设置权限等级（使用默认权限）
    /// - 0：用户在白名单中（完全授权）
    /// - 1-9：逐级降低的权限等级
    /// </returns>
    /// <remarks>
    /// 权限等级用于控制用户能否使用特定功能
    /// 例如：AI模块中，只有等级 <= 1 的用户才能使用通用API
    /// </remarks>
    int? GetUserAuthLevel(long userId);

    /// <summary>
    /// 检查Bot是否在当前会话（群聊/私聊）中启用
    /// </summary>
    /// <param name="groupId">会话标识（群号 或 负用户号表示私聊）</param>
    /// <returns>true 表示Bot已启用，false 表示已关闭</returns>
    bool IsBotEnabled(long groupId);

    /// <summary>
    /// 检查用户是否为指定群的群主或群管理员。
    /// </summary>
    bool IsGroupAdministrator(long groupId, long userId) => false;

    /// <summary>
    /// 检查用户是否为骰子管理员（系统账号、Master 或授权等级 0/1）。
    /// </summary>
    bool IsDiceAdministrator(long userId) => false;

    /// <summary>
    /// 从持久化群数据中读取指定功能的开关。
    /// </summary>
    bool IsGroupFeatureEnabled(long groupId, string featureKey, bool defaultValue = true) => defaultValue;

    /// <summary>
    /// 将指定功能的开关写入持久化群数据。
    /// </summary>
    void SetGroupFeatureEnabled(long groupId, string featureKey, bool enabled) { }
}

/// <summary>
/// 日志级别
/// </summary>
public enum LogLevel
{
    /// <summary>调试信息，仅在开发时有用</summary>
    Debug = 0,
    
    /// <summary>普通信息，记录程序正常操作</summary>
    Info = 1,
    
    /// <summary>警告信息，表示可能的问题</summary>
    Warn = 2,
    
    /// <summary>错误信息，表示发生了异常</summary>
    Error = 3,
    
    /// <summary>严重错误，可能导致程序崩溃</summary>
    Fatal = 4
}
