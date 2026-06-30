namespace MDiceV2.Interfaces.Mod;

/// <summary>
/// Mod子指令提供者接口
/// 允许Mod向宿主程序的已有指令注册自定义子指令
/// </summary>
/// <remarks>
/// 设计原则：
/// - 子指令的解析由父指令自行完成，Mod 只接收已解析的 (subcommand, args)
/// - 这样可以兼容不同父指令的解析范式（如 .team 用 Split、.log 用 Split 等）
/// - 父指令在自己的 default/else 分支查询 Mod 子指令，找到则调用并返回
///
/// 注册流程：
/// 1. Mod 实现此接口
/// 2. ModEventBridge.GetSubcommandProviders() 遍历所有实现了此接口的 Mod
/// 3. 父指令在未匹配到内置子命令时，调用 HandleSubcommand 查询
/// 4. 如果 Mod 返回非 null，父指令将其作为回复发送
///
/// 使用示例：
/// <code>
/// public string? HandleSubcommand(string parentCommand, string subcommand, string args, object msgObj)
/// {
///     if (parentCommand == "team" &amp;&amp; subcommand == "addai")
///     {
///         var msg = (MDiceV2.Models.Msg)msgObj;
///         // 处理 .team addai 逻辑...
///         return "✓ AI角色已加入队伍";
///     }
///     return null; // 不处理此子指令
/// }
/// </code>
/// </remarks>
public interface ISubcommandProvider
{
    /// <summary>
    /// 处理已解析的子指令
    /// </summary>
    /// <param name="parentCommand">父指令名（如 "team", "log", "deck"）</param>
    /// <param name="subcommand">已解析的子指令名（如 "addai", "removeai"）</param>
    /// <param name="args">子指令的剩余参数</param>
    /// <param name="msgObj">消息对象，应强制转换为 MDiceV2.Models.Msg</param>
    /// <returns>回复内容，null 表示不处理此子指令</returns>
    string? HandleSubcommand(string parentCommand, string subcommand, string args, object msgObj);
}
