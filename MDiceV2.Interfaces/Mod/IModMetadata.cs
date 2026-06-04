namespace MDiceV2.Interfaces.Mod;

/// <summary>
/// Mod元数据
/// 对应mod.json文件中的元数据信息
/// 用于ModPluginLoader解析和加载Mod
/// </summary>
public interface IModMetadata
{
    /// <summary>
    /// Mod唯一标识符
    /// 格式建议：com.author.modname 或 author.modname
    /// 用于日志、缓存键值等
    /// 在插件系统中必须唯一
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Mod显示名称
    /// 用于UI和日志显示
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Mod版本号
    /// 建议遵循SemVer：major.minor.patch
    /// 例如：1.2.3 或 1.0.0-alpha
    /// </summary>
    string Version { get; }

    /// <summary>
    /// Mod作者
    /// 用于识别开发者和日志记录
    /// </summary>
    string Author { get; }

    /// <summary>
    /// Mod描述
    /// 简短的功能说明，用于UI显示
    /// </summary>
    string Description { get; }

    /// <summary>
    /// DLL文件名
    /// 相对于mod文件夹的路径
    /// 例如：CustomizedReply.dll 或 bin/MyMod.dll
    /// </summary>
    string DllFileName { get; }

    /// <summary>
    /// 实现IModPlugin的类名
    /// 如果为null，将自动查找（遍历DLL中的所有类）
    /// 如果指定，将直接加载此类（性能更好）
    /// 格式：完整命名空间.类名，如 MyNamespace.CustomizedReplyMod
    /// </summary>
    string? PluginClassName { get; }

    /// <summary>
    /// Mod的执行优先级
    /// 值越大优先级越高，越先执行
    /// 默认值：100
    /// 
    /// 优先级范围指导：
    /// - 0-50: 低优先级，普通功能Mod
    /// - 50-100: 中优先级，常规Mod（默认）
    /// - 100-200: 高优先级，重要功能Mod
    /// - 200+: 极高优先级，系统级Mod
    /// 
    /// 执行顺序：
    /// 如果多个Mod都处理同一条消息，会按优先级高到低排序执行
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Mod类型
    /// 目前仅支持 "dll"
    /// 保留 "lua" 用于未来脚本Mod的支持
    /// </summary>
    string ModType { get; }

    /// <summary>
    /// 是否支持热卸载
    /// 仅适用于Lua脚本Mod
    /// DLL Mod应该设为false（DLL在禁用时不会真正卸载）
    /// 
    /// 含义：
    /// - true: 禁用Mod时会完全卸载（可能涉及Lua脚本重新解析）
    /// - false: 禁用Mod时仅标记为禁用，不卸载DLL和资源
    /// </summary>
    bool SupportHotReload { get; }

    /// <summary>
    /// API版本需求
    /// 用于检查Mod与宿主程序的兼容性
    /// 格式：major.minor
    /// 例如：1.0 表示支持MDiceV2 API 1.x系列
    /// 
    /// 宿主程序应检查Mod请求的API版本是否兼容
    /// 如果不兼容，应拒绝加载此Mod并显示警告
    /// </summary>
    string ApiVersion { get; }
}

/// <summary>
/// Mod元数据实现
/// 与mod.json文件结构对应
/// </summary>
public class ModMetadata : IModMetadata
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public required string Description { get; init; }
    public required string DllFileName { get; init; }
    public string? PluginClassName { get; init; } = null;
    public int Priority { get; init; } = 100;
    public string ModType { get; init; } = "dll";
    public bool SupportHotReload { get; init; } = false;  // DLL Mod默认不支持热卸载
    public required string ApiVersion { get; init; }
}
