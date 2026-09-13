namespace MDiceV2.Interfaces.Mod;

/// <summary>
/// Mod指令提供者接口
/// 允许Mod向宿主程序注册自定义的群聊指令处理器
/// </summary>
/// <remarks>
/// 指令注册流程：
/// 1. Mod 实现此接口，在 GetCommandHandlers() 中返回自己的指令处理器字典
/// 2. 宿主程序在MessageProcessor初始化指令处理器时，调用ModEventBridge.GetAllCommandHandlers()
/// 3. ModEventBridge遍历所有已加载的Mod，收集实现了ICommandProvider的指令
/// 4. 返回的指令处理器被合并到主程序的commandHandlers字典中
/// 5. 用户在群聊中输入指令时，主程序自动调用对应的处理器
/// 
/// 指令触发格式：.{指令名} {参数}
/// 例如：.abot script [脚本代码]
/// 
/// 注意事项：
/// - 指令名不应包含"."前缀，前缀由主程序自动添加
/// - 如果多个Mod注册相同的指令名，先注册的优先，后注册的会被忽略（会有日志警告）
/// - 处理器委托的第一参数是去除指令名后的参数部分，第二参数是消息对象
/// </remarks>
public interface ICommandProvider
{
    /// <summary>
    /// 获取此Mod提供的所有指令处理器
    /// </summary>
    /// <returns>
    /// 字典，键为指令名（不含.前缀），值为对应的处理器委托
    /// 
    /// 处理器委托签名说明：
    ///   - 第一参数(string): 用户输入的指令参数部分（去除指令名后的内容）
    ///     示例：用户输入 ".abot script [脚本代码]"
    ///           指令名为 "abot"，参数为 "script [脚本代码]"
    ///   - 第二参数(object): 消息对象，包含GroupId、UserId、Content等信息
    ///     实现者应将其强制转换为 MDiceV2.Models.Msg 类型
    ///   - 返回值(string?): 要发送给用户的回复内容，如果返回null则不发送回复
    /// 
    /// 使用示例：
    /// <code>
    /// public Dictionary&lt;string, Func&lt;string, object, string?&gt;&gt; GetCommandHandlers()
    /// {
    ///     return new Dictionary&lt;string, Func&lt;string, object, string?&gt;&gt;
    ///     {
    ///         { "abot", HandleAbotCommand },
    ///         { "custom", HandleCustomCommand }
    ///     };
    /// }
    /// 
    /// private string? HandleAbotCommand(string args, object msgObj)
    /// {
    ///     var msg = (MDiceV2.Models.Msg)msgObj;  // 类型转换
    ///     // args 中包含 "script [脚本代码]" 或 "nr" 等参数
    ///     // msg 包含群组ID、用户ID等信息
    ///     // 处理逻辑...
    ///     return "处理结果"; // 返回要发送的回复内容
    /// }
    /// </code>
    /// </returns>
    Dictionary<string, Func<string, object, string?>> GetCommandHandlers();
}
