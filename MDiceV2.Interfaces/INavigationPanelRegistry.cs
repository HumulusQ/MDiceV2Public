namespace MDiceV2.Interfaces;

/// <summary>
/// 导航面板注册表接口
/// Mod 通过此接口将其导航面板注册到主窗口
/// 
/// 设计原则：
/// - Mod 通过 IModContext 获取此服务，而不是直接依赖实现
/// - 保持 Mod 对宿主程序的依赖最小化
/// - 注册是一次性操作，应在 OnLoad() 中完成
/// </summary>
public interface INavigationPanelRegistry
{
    /// <summary>
    /// 注册导航面板提供者
    /// </summary>
    /// <param name="provider">实现 INavigationPanelProvider 接口的对象</param>
    /// <remarks>
    /// - 应在 Mod 的 OnLoad() 方法中调用
    /// - 每个 Mod 可以注册多个面板，但建议每个 Mod 注册一个
    /// - 面板 ID 必须全局唯一
    /// - 如果注册失败，应捕获异常并记录错误
    /// </remarks>
    void Register(INavigationPanelProvider provider);

    /// <summary>
    /// 注销导航面板
    /// </summary>
    /// <param name="panelId">面板ID</param>
    /// <returns>是否成功注销</returns>
    /// <remarks>
    /// - 通常在 Mod 卸载时调用
    /// - 如果面板不存在，返回 false
    /// </remarks>
    bool Unregister(string panelId);

    /// <summary>
    /// 获取已注册的所有面板提供者（按优先级排序）
    /// </summary>
    /// <returns>按优先级降序排列的面板提供者列表</returns>
    IList<INavigationPanelProvider> GetRegisteredPanels();

    /// <summary>
    /// 获取指定ID的面板提供者
    /// </summary>
    /// <param name="panelId">面板ID</param>
    /// <returns>面板提供者，如果不存在返回 null</returns>
    INavigationPanelProvider? GetPanel(string panelId);
}
