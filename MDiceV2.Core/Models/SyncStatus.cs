using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace MDiceV2.Core.Models;

/// <summary>
/// 同步状态模型
/// 追踪同步连接的各种状态
/// </summary>
public partial class SyncStatus : ObservableObject
{
    [ObservableProperty]
    private bool isSyncEnabled = false;

    [ObservableProperty]
    private bool isConnected = false;

    [ObservableProperty]
    private string? remoteServerAddress;

    [ObservableProperty]
    private string statusMessage = "未启动同步";

    [ObservableProperty]
    private DateTime? lastSyncTime;

    [ObservableProperty]
    private int syncedItemCount = 0;

    [ObservableProperty]
    private bool isConnecting = false;

    public void SetConnecting(string message = "连接中...")
    {
        IsConnecting = true;
        StatusMessage = message;
    }

    public void SetConnected(string serverAddress, string message = "已连接")
    {
        IsConnecting = false;
        IsConnected = true;
        RemoteServerAddress = serverAddress;
        StatusMessage = message;
        LastSyncTime = DateTime.Now;
    }

    public void SetDisconnected(string message = "未连接")
    {
        IsConnecting = false;
        IsConnected = false;
        RemoteServerAddress = null;
        StatusMessage = message;
    }

    public void SetSyncEnabled(bool enabled)
    {
        IsSyncEnabled = enabled;
    }

    public void UpdateLastSyncTime()
    {
        LastSyncTime = DateTime.Now;
    }

    public void SetSyncMessage(string message)
    {
        StatusMessage = message;
        LastSyncTime = DateTime.Now;
    }
}
