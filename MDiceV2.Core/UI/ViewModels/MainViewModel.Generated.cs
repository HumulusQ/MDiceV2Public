#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;
using System.Collections.ObjectModel;

namespace MDiceV2.Core.UI.ViewModels
{
    /// <summary>
    /// MainViewModel 的生成代码部分
    /// 包含通过 ObservableProperty 生成的属性
    /// </summary>
    public partial class MainViewModel
    {
        /// <summary>
        /// 当前选中的导航索引
        /// </summary>
        [ObservableProperty]
        private int selectedIndex;

        /// <summary>
        /// 当前显示的视图内容
        /// </summary>
        [ObservableProperty]
        private object? currentView;

        /// <summary>
        /// 导航面板是否展开
        /// </summary>
        [ObservableProperty]
        private bool isPaneOpen;

        /// <summary>
        /// 聊天消息列表
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<Message> messages = new();

        /// <summary>
        /// 当前输入的消息文本
        /// </summary>
        [ObservableProperty]
        private string currentMessageText = string.Empty;

        /// <summary>
        /// 模拟模式开关状态
        /// </summary>
        [ObservableProperty]
        private bool isSimulationMode = true;

        /// <summary>
        /// 聊天模式：true=群聊，false=私聊
        /// </summary>
        [ObservableProperty]
        private bool isGroupChatMode;

        /// <summary>
        /// 输入的群号（字符串以便允许空值）
        /// </summary>
        [ObservableProperty]
        private string groupIdInput = string.Empty;

        /// <summary>
        /// 输入的账号（字符串以便允许空值）
        /// </summary>
        [ObservableProperty]
        private string accountIdInput = string.Empty;

        /// <summary>
        /// 日志消息集合
        /// </summary>
        [ObservableProperty]
        private ObservableCollection<LogMessageItem> logMessages = new();

        /// <summary>
        /// WebSocket URL
        /// </summary>
        [ObservableProperty]
        private string wsUrl = "ws://localhost:8080";


        /// <summary>
        /// WebSocket连接状态信息
        /// </summary>
        [ObservableProperty]
        private string wsConnectionStatus = "Disconnected";

        /// <summary>
        /// WebSocket连接URL信息
        /// </summary>
        [ObservableProperty]
        private string wsConnectionUrl = "ws://localhost:8080";

        /// <summary>
        /// WebSocket连接日志信息
        /// </summary>
        [ObservableProperty]
        private string wsConnectionLogs = "Initializing WebSocket connection...\n";

        /// <summary>
        /// 属性变更回调
        /// </summary>
        partial void OnWsUrlChanged(string value)
        {
            try
            {
                LogInfo($"WsUrl 变更为: {value}");
                
                // 更新内存中的基础设置并保存到磁盘
                MDiceV2.Models.GlobalFeedbackMessages.SetBasicSetting("Url", value);
                MDiceV2.Models.GlobalFeedbackMessages.SaveBasicSettings();
                LogInfo($"WsUrl 已保存: {value}");

                // 同步到WSconnection静态字段
                if (_globalMessageProcessor?.MessageDistribution?.WSconnection != null)
                {
                    WSconnection.wsUrl = value;
                    LogInfo($"WsUrl 已同步到WSconnection");
                }
            }
            catch (Exception ex)
            {
                LogError($"更新 WsUrl 时发生错误: {ex.Message}");
            }
        }
    }
}