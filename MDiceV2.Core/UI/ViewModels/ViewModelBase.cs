using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;

namespace MDiceV2.Core.UI.ViewModels
{
    /// <summary>
    /// 视图模型基类
    /// </summary>
    public class ViewModelBase : ObservableObject
    {
        /// <summary>
        /// 最大日志行数
        /// </summary>
        protected const int MaxLogLines = 200;

        /// <summary>
        /// WS连接日志最大行数
        /// </summary>
        protected const int WsLogMaxLines = 50;

        /// <summary>
        /// 记录日志消息
        /// </summary>
        protected void LogToQueue(string message, LogMessageType type = LogMessageType.Normal)
        {
            if (GlobalMessageQueue.Instance != null)
            {
                var logItem = new LogMessageItem
                {
                    Text = $"[{GetType().Name}] {message}",
                    Type = type,
                    Timestamp = DateTime.Now
                };

                GlobalMessageQueue.Instance.LogMessageQueued += (text, type) => { };
            }
        }

        /// <summary>
        /// 记录一般日志
        /// </summary>
        protected void LogInfo(string message) => LogToQueue(message, LogMessageType.Normal);

        /// <summary>
        /// 记录警告日志
        /// </summary>
        protected void LogWarning(string message) => LogToQueue(message, LogMessageType.Warning);

        /// <summary>
        /// 记录错误日志
        /// </summary>
        protected void LogError(string message) => LogToQueue(message, LogMessageType.Error);

        /// <summary>
        /// 记录重要日志
        /// </summary>
        protected void LogImportant(string message) => LogToQueue(message, LogMessageType.Important);
    }
}