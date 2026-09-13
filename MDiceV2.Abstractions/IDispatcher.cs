namespace MDiceV2.Abstractions;

/// <summary>
/// 抽象UI调度器，隔离平台特定的UI线程实现
/// 支持UI模式（Avalonia）和Console模式的统一接口
/// </summary>
public interface IDispatcher
{
    /// <summary>
    /// 在UI线程（或同步上下文）上同步执行action
    /// </summary>
    void Post(Action action);

    /// <summary>
    /// 在UI线程（或同步上下文）上异步执行异步操作
    /// </summary>
    Task PostAsync(Func<Task> action);

    /// <summary>
    /// 在UI线程（或同步上下文）上异步执行异步操作并获取返回值
    /// </summary>
    Task<T> PostAsync<T>(Func<Task<T>> action);
}
