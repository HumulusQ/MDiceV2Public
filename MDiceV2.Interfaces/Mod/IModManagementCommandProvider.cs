namespace MDiceV2.Interfaces.Mod;

/// <summary>由 Mod 注册到宿主 .mod cmd 路径的管理子命令。</summary>
public sealed class ModManagementCommand
{
    public ModManagementCommand(
        string name,
        string description,
        Func<string, object, string?> handler)
    {
        Name = name;
        Description = description;
        Handler = handler;
    }

    public string Name { get; }
    public string Description { get; }
    public Func<string, object, string?> Handler { get; }
}

/// <summary>
/// 可选的 Mod 管理命令提供者。命令只会经由 .mod cmd 分发，
/// 不会污染普通的点号命令命名空间。
/// </summary>
public interface IModManagementCommandProvider
{
    IReadOnlyCollection<ModManagementCommand> GetModManagementCommands();
}
