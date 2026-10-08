namespace MDiceV2.Interfaces.Mod;

/// <summary>由宿主为便携 Mod 提供的、跨版本稳定的运行目录。</summary>
public sealed record PortableModContext(string DataDirectory);

/// <summary>可选接口；宿主会在 OnLoad 前注入便携 Mod 上下文。</summary>
public interface IPortableModContextReceiver
{
    void SetPortableModContext(PortableModContext context);
}
