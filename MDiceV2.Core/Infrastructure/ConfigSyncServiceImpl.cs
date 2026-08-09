using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MDiceV2.Abstractions;
using MDiceV2.Models;

namespace MDiceV2.Core.Infrastructure;

/// <summary>
/// gRPC ConfigSyncService 实现
/// 处理远程配置的认证、拉取、推送、日志流和消息流
/// </summary>
public class ConfigSyncServiceImpl : Mdv2.Remotesync.ConfigSyncService.ConfigSyncServiceBase
{
    private readonly string _serverPassword;
    private readonly ConfigSyncServer _configServer;
    private readonly ConcurrentDictionary<string, ClientSessionContext> _activeClients;
    private readonly ConcurrentDictionary<string, string> _peerToSessionMap;  // 映射 peer 到 ClientId

    private class ClientSessionContext
    {
        public string ClientId { get; set; } = Guid.NewGuid().ToString();
        public DateTime AuthenticatedAt { get; set; }
        public DateTime LastAccessTime { get; set; }
        // ✅ 改用 ConcurrentDictionary（线程安全）
        public ConcurrentDictionary<string, string> AuthenticatedConfig { get; set; } = new();
        public bool IsAuthenticated { get; set; }
    }

    public ConfigSyncServiceImpl(string serverPassword, ConfigSyncServer configServer)
    {
        _serverPassword = serverPassword ?? "default-password";
        _configServer = configServer ?? throw new ArgumentNullException(nameof(configServer));
        _activeClients = new ConcurrentDictionary<string, ClientSessionContext>();
        _peerToSessionMap = new ConcurrentDictionary<string, string>();
    }

    /// <summary>
    /// RPC: Authenticate - 验证客户端密码，建立会话
    /// </summary>
    public override async Task<Mdv2.Remotesync.AuthResponse> Authenticate(
        Mdv2.Remotesync.AuthRequest request,
        ServerCallContext context)
    {
        try
        {
            var peer = context.Peer;
            LogInfo($"[ConfigSync] 【认证】客户端认证请求，Peer: {peer}");
            LogInfo($"[ConfigSync] 【认证】当前已有会话映射: {_peerToSessionMap.Count}");

            if (string.IsNullOrEmpty(request.PasswordHash))
            {
                LogWarn("[ConfigSync] 【认证】失败：密码为空");
                return new Mdv2.Remotesync.AuthResponse
                {
                    Success = false,
                    Message = "Password hash is empty"
                };
            }

            // 验证密码哈希
            if (!VerifyPasswordHash(request.PasswordHash))
            {
                LogWarn("[ConfigSync] 【认证】失败：密码哈希不匹配");
                return new Mdv2.Remotesync.AuthResponse
                {
                    Success = false,
                    Message = "Invalid password"
                };
            }

            // 创建新的客户端会话 - 使用独立的 GUID 作为 ClientId
            var clientId = Guid.NewGuid().ToString();
            var session = new ClientSessionContext
            {
                ClientId = clientId,
                AuthenticatedAt = DateTime.UtcNow,
                LastAccessTime = DateTime.UtcNow,
                IsAuthenticated = true
            };

            _activeClients.TryAdd(clientId, session);
            // ✅ 建立 peer → clientId 的映射
            _peerToSessionMap[peer] = clientId;

            LogInfo($"[ConfigSync] 【认证】✓ 客户端 {peer} 认证成功");
            LogInfo($"[ConfigSync] 【认证】   ClientId: {clientId}");
            LogInfo($"[ConfigSync] 【认证】   会话映射已建立");
            LogInfo($"[ConfigSync] 【认证】   当前活跃会话数: {_activeClients.Count}");
            LogInfo($"[ConfigSync] 【认证】   当前Peer映射数: {_peerToSessionMap.Count}");

            return new Mdv2.Remotesync.AuthResponse
            {
                Success = true,
                Message = "Authentication successful",
                ClientId = clientId
            };
        }
        catch (Exception ex)
        {
            LogError($"[ConfigSync] 【认证】异常: {ex.Message}");
            return new Mdv2.Remotesync.AuthResponse
            {
                Success = false,
                Message = $"Authentication error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// RPC: PullConfig - 拉取远程配置
    /// </summary>
    public override async Task<Mdv2.Remotesync.SyncConfigResponse> PullConfig(
        Mdv2.Remotesync.PullConfigRequest request,
        ServerCallContext context)
    {
        try
        {
            var peer = context.Peer;
            var clientId = request?.ClientId ?? string.Empty;
            LogInfo($"[ConfigSync] 【拉取】接收到请求，Peer: {peer}, ClientId: {clientId}");
            
            // ✅ 使用请求中的ClientId，而不是peer映射
            if (string.IsNullOrEmpty(clientId))
            {
                LogWarn($"[ConfigSync] 【拉取】客户端 {peer} 未提供ClientId");
                return new Mdv2.Remotesync.SyncConfigResponse
                {
                    Success = false,
                    Message = "ClientId is required",
                    ConflictCount = 0
                };
            }

            if (!_activeClients.TryGetValue(clientId, out var session) || !session.IsAuthenticated)
            {
                LogWarn($"[ConfigSync] 【拉取】客户端 {peer} (ID: {clientId}) 未认证或会话已过期");
                return new Mdv2.Remotesync.SyncConfigResponse
                {
                    Success = false,
                    Message = "Client not authenticated",
                    ConflictCount = 0
                };
            }

            // ✅ 更新访问时间 + 更新Peer映射
            session.LastAccessTime = DateTime.UtcNow;
            _peerToSessionMap[peer] = clientId;  // 更新peer映射，以应对IPv6动态端口变化
            LogInfo($"[ConfigSync] ✓ 【拉取】客户端 {peer} (ID: {clientId}) 拉取配置");

            // ✅ 【修复】使用 BuildConfigForPull() 方法而不是 GetCurrentConfig()
            // 这样可以从 ConfigApplierRegistry 中实时导出最新配置，与推送使用相同的 Configurers
            var currentConfig = _configServer.BuildConfigForPull();
            LogInfo($"[ConfigSync] 【拉取】构建配置完成，共 {currentConfig.Count} 个配置项");

            var response = new Mdv2.Remotesync.SyncConfigResponse
            {
                Success = true,
                Message = "Config pulled successfully",
                ConflictCount = 0
            };

            // 将配置转换为 proto 格式
            foreach (var kvp in currentConfig)
            {
                response.ConfigItems.Add(new Mdv2.Remotesync.ConfigItem
                {
                    Key = kvp.Key,
                    Value = kvp.Value,
                    UpdatedAtTicks = DateTime.UtcNow.Ticks,
                    LastModifiedBy = "server"
                });
            }

            // ✅ 更新会话的配置快照（使用 ConcurrentDictionary）
            session.AuthenticatedConfig.Clear();
            foreach (var kvp in currentConfig)
            {
                session.AuthenticatedConfig[kvp.Key] = kvp.Value;
            }

            LogInfo($"[ConfigSync] ✓ 【拉取】客户端 {peer} (ID: {clientId}) 拉取了 {response.ConfigItems.Count} 个配置项");
            return response;
        }
        catch (Exception ex)
        {
            LogError($"[ConfigSync] 【拉取】异常: {ex.Message}");
            return new Mdv2.Remotesync.SyncConfigResponse
            {
                Success = false,
                Message = $"Pull config error: {ex.Message}",
                ConflictCount = 0
            };
        }
    }

    /// <summary>
    /// RPC: PushConfig - 推送配置到服务器
    /// </summary>
    public override async Task<Mdv2.Remotesync.SyncConfigResponse> PushConfig(
        Mdv2.Remotesync.SyncConfigRequest request,
        ServerCallContext context)
    {
        try
        {
            var peer = context.Peer;
            
            // ✅ 改进的会话获取逻辑 - 先用peer查找，再用请求中的clientId
            string? clientId = null;
            if (_peerToSessionMap.TryGetValue(peer, out var mappedClientId))
            {
                clientId = mappedClientId;
            }
            
            if (clientId == null)
            {
                return new Mdv2.Remotesync.SyncConfigResponse
                {
                    Success = false,
                    Message = "Client not authenticated",
                    ConflictCount = 0
                };
            }

            if (!_activeClients.TryGetValue(clientId, out var session) || !session.IsAuthenticated)
            {
                return new Mdv2.Remotesync.SyncConfigResponse
                {
                    Success = false,
                    Message = "Client not authenticated",
                    ConflictCount = 0
                };
            }

            // ✅ 更新访问时间 + 更新Peer映射（以防Peer变化）
            session.LastAccessTime = DateTime.UtcNow;
            _peerToSessionMap[peer] = clientId;
            
            // 检测mod配置并进行可选验证（不强制失败）
            var failedConfigKeys = new Dictionary<string, string>();
            foreach (var item in request.ConfigItems)
            {
                if (item.Key.StartsWith("mod.customreply.", StringComparison.OrdinalIgnoreCase))
                {
                    // Mod配置验证为可选（日志记录但不阻止推送）
                }
            }
            
            // 将 proto 配置转换为字典
            var pushedConfig = new Dictionary<string, string>();
            foreach (var item in request.ConfigItems)
            {
                // 跳过验证失败的mod配置
                if (!failedConfigKeys.ContainsKey(item.Key))
                {
                    pushedConfig[item.Key] = item.Value;
                }
            }
            
            // 更新服务器配置
            var conflictCount = _configServer.UpdateConfig(pushedConfig, clientId);

            // 在响应中包含失败的配置信息
            var response = new Mdv2.Remotesync.SyncConfigResponse
            {
                Success = failedConfigKeys.Count == 0,
                Message = failedConfigKeys.Count > 0 
                    ? $"部分配置验证失败: {string.Join(", ", failedConfigKeys.Keys)}" 
                    : "Config pushed successfully",
                ConflictCount = conflictCount
            };

            return response;
        }
        catch (Exception ex)
        {
            LogError($"[ConfigSync] 推送配置异常: {ex.Message}");
            return new Mdv2.Remotesync.SyncConfigResponse
            {
                Success = false,
                Message = $"Push config error: {ex.Message}",
                ConflictCount = 0
            };
        }
    }

    /// <summary>
    /// RPC: SubscribeLogs - 服务器端流：推送日志到客户端
    /// </summary>
    public override async System.Threading.Tasks.Task SubscribeLogs(
        Mdv2.Remotesync.SubscribeLogsRequest request,
        Grpc.Core.IServerStreamWriter<Mdv2.Remotesync.LogBatch> responseStream,
        Grpc.Core.ServerCallContext context)
    {
        try
        {
            var peer = context.Peer;
            var clientId = request?.ClientId ?? string.Empty;
            // ✅ 使用请求中的ClientId
            if (string.IsNullOrEmpty(clientId))
            {
                LogWarn($"[ConfigSync] 客户端 {peer} 未提供ClientId");
                return;
            }

            if (!_activeClients.TryGetValue(clientId, out var session) || !session.IsAuthenticated)
            {
                LogWarn($"[ConfigSync] 客户端 {peer} (ID: {clientId}) 未认证");
                return;
            }

            // ✅ 更新访问时间和peer映射
            session.LastAccessTime = DateTime.UtcNow;
            _peerToSessionMap[peer] = clientId;  // 更新peer映射，以应对IPv6动态端口变化
            LogInfo($"[ConfigSync] 客户端 {peer} (ID: {clientId}) 订阅日志流");

            if (!_activeClients.TryGetValue(clientId, out var _) || !session.IsAuthenticated)
            {
                LogWarn($"[ConfigSync] 客户端 {peer} (ID: {clientId}) 未认证");
                return;
            }

            // 设置流式日志回调
            var batchCallback = new Func<List<Infrastructure.LogEntry>, Task>(async batch =>
            {
                try
                {
                    if (batch.Count == 0 || context.CancellationToken.IsCancellationRequested)
                        return;

                    var logBatch = new Mdv2.Remotesync.LogBatch
                    {
                        BatchId = Guid.NewGuid().ToString(),
                        CreatedAtTicks = DateTime.UtcNow.Ticks
                    };

                    foreach (var entry in batch)
                    {
                        logBatch.Entries.Add(new Mdv2.Remotesync.LogEntry
                        {
                            GroupId = entry.GroupId,
                            Content = entry.Content,
                            TimestampTicks = entry.Timestamp.Ticks,
                            Level = entry.Level ?? "INFO"
                        });
                    }

                    await responseStream.WriteAsync(logBatch);
                    LogDebug($"[ConfigSync] 向客户端 {clientId} 发送日志批次，大小: {batch.Count}");
                }
                catch (Exception ex)
                {
                    LogError($"[ConfigSync] 发送日志批次失败: {ex.Message}");
                }
            });

            // 注册日志订阅（等待日志直到客户端断开或请求取消）
            _configServer.SubscribeToLogs(clientId, batchCallback);

            // 保持连接，直到取消或断开
            await Task.Delay(Timeout.Infinite, context.CancellationToken);
        }
        catch (OperationCanceledException)
        {
            LogInfo($"[ConfigSync] 日志流已断开");
        }
        catch (Exception ex)
        {
            LogError($"[ConfigSync] 日志流异常: {ex.Message}");
        }
    }

    /// <summary>
    /// RPC: StreamSimulationMessages - 双向流：接收和发送模拟消息
    /// </summary>
    public override async System.Threading.Tasks.Task StreamSimulationMessages(
        Grpc.Core.IAsyncStreamReader<Mdv2.Remotesync.SimulationMessage> requestStream,
        Grpc.Core.IServerStreamWriter<Mdv2.Remotesync.SimulationAck> responseStream,
        Grpc.Core.ServerCallContext context)
    {
        try
        {
            var clientId = context.Peer;
            LogInfo($"[ConfigSync] 客户端 {clientId} 开始消息流");

            if (!_activeClients.TryGetValue(clientId, out var session) || !session.IsAuthenticated)
            {
                LogWarn($"[ConfigSync] 客户端 {clientId} 未认证");
                return;
            }

            // 处理来自客户端的消息流
            await foreach (var message in requestStream.ReadAllAsync(context.CancellationToken))
            {
                try
                {
                    LogDebug($"[ConfigSync] 收到来自 {message.UserId} 的消息");

                    // 处理模拟消息
                    await _configServer.ProcessSimulationMessageAsync(
                        message.UserId,
                        message.GroupId,
                        message.Content);

                    // 发送确认
                    await responseStream.WriteAsync(new Mdv2.Remotesync.SimulationAck
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Success = true,
                        Message = "Message received",
                        ProcessedAtTicks = DateTime.UtcNow.Ticks
                    });
                }
                catch (Exception ex)
                {
                    LogError($"[ConfigSync] 处理消息失败: {ex.Message}");
                    await responseStream.WriteAsync(new Mdv2.Remotesync.SimulationAck
                    {
                        MessageId = Guid.NewGuid().ToString(),
                        Success = false,
                        Message = $"Process error: {ex.Message}",
                        ProcessedAtTicks = DateTime.UtcNow.Ticks
                    });
                }
            }

            LogInfo($"[ConfigSync] 客户端 {clientId} 消息流已关闭");
        }
        catch (OperationCanceledException)
        {
            LogDebug("[ConfigSync] 消息流已取消");
        }
        catch (Exception ex)
        {
            LogError($"[ConfigSync] 消息流异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 验证密码哈希（HMAC-SHA256）
    /// </summary>
    private bool VerifyPasswordHash(string receivedHash)
    {
        try
        {
            using (var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_serverPassword)))
            {
                var computed = hmac.ComputeHash(Encoding.UTF8.GetBytes(_serverPassword));
                var computedHash = Convert.ToBase64String(computed);
                return computedHash == receivedHash;
            }
        }
        catch (Exception ex)
        {
            LogError($"[ConfigSync] 密码验证异常: {ex.Message}");
            return false;
        }
    }

    private void LogInfo(string message) => LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [ConfigSyncServiceImpl] {message}");
    private void LogWarn(string message) => LogSender.Warn($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [ConfigSyncServiceImpl] {message}");
    private void LogError(string message) => LogSender.Error($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [ConfigSyncServiceImpl] ERROR: {message}");
    private void LogDebug(string message) => LogSender.Normal($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [ConfigSyncServiceImpl] DEBUG: {message}");
}
