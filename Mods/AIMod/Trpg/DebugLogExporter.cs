using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

/// <summary>
/// LLM Debug 日志导出器
/// </summary>
public sealed class DebugLogExporter
{
    private readonly ChatDatabase _db;
    private readonly IModContext _context;
    private readonly string _aiModDataRoot;

    public DebugLogExporter(ChatDatabase db, IModContext context, string aiModDataRoot)
    {
        _db = db;
        _context = context;
        _aiModDataRoot = aiModDataRoot;
    }

    public async Task<string> ExportDebugLogsAsync(
        TrpgScope scope,
        int limit = 50,
        string? filterAgentName = null)
    {
        // 获取日志
        List<LlmDebugLogEntry> logs = filterAgentName != null
            ? await _db.GetRecentLlmDebugLogsByAgentAsync(scope, filterAgentName, limit)
            : await _db.GetRecentLlmDebugLogsAsync(scope, limit);

        if (!logs.Any())
            return "没有 debug 日志可导出";

        // 创建导出目录
        var debugDir = Path.Combine(_aiModDataRoot, "debug", scope.GroupId.ToString());
        Directory.CreateDirectory(debugDir);

        // 生成文件名
        var fileName = $"debug_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
        var filePath = Path.Combine(debugDir, fileName);

        // 写入文件
        using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
        {
            await writer.WriteLineAsync("AIMod LLM Debug Export");
            await writer.WriteLineAsync($"WorldId: {scope.WorldId}");
            await writer.WriteLineAsync($"GroupId: {scope.GroupId}");
            await writer.WriteLineAsync($"ExportedAt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            await writer.WriteLineAsync($"Filter: {(filterAgentName != null ? $"Agent={filterAgentName}" : "All agents")}");
            await writer.WriteLineAsync($"Count: {logs.Count}");
            await writer.WriteLineAsync();
            await writer.WriteLineAsync(new string('=', 80));
            await writer.WriteLineAsync();

            foreach (var log in logs)
            {
                await writer.WriteLineAsync($"DebugLogId: {log.Id}");
                await writer.WriteLineAsync($"CreatedAt: {log.CreatedAt:yyyy-MM-dd HH:mm:ss}");
                await writer.WriteLineAsync($"AgentName: {log.AgentName}");
                await writer.WriteLineAsync($"RequestKind: {log.RequestKind}");
                await writer.WriteLineAsync($"CharacterId: {log.CharacterId ?? "(null)"}");
                await writer.WriteLineAsync($"Success: {log.Success}");
                if (!string.IsNullOrEmpty(log.Error))
                    await writer.WriteLineAsync($"Error: {log.Error}");
                await writer.WriteLineAsync($"InputCharCount: {log.InputCharCount}");
                await writer.WriteLineAsync($"OutputCharCount: {log.OutputCharCount}");

                if (!string.IsNullOrEmpty(log.MessagesJson) && log.MessagesJson != "[]")
                {
                    await writer.WriteLineAsync("[MESSAGES]");
                    try
                    {
                        var messagesDoc = JsonDocument.Parse(log.MessagesJson);
                        var formatted = JsonSerializer.Serialize(messagesDoc.RootElement,
                            new JsonSerializerOptions { WriteIndented = true });
                        await writer.WriteLineAsync(formatted);
                    }
                    catch
                    {
                        await writer.WriteLineAsync(log.MessagesJson);
                    }
                }

                if (!string.IsNullOrEmpty(log.ResponseText))
                {
                    await writer.WriteLineAsync("[RESPONSE]");
                    await writer.WriteLineAsync(log.ResponseText);
                }

                if (!string.IsNullOrEmpty(log.Metadata) && log.Metadata != "{}")
                {
                    await writer.WriteLineAsync("[METADATA]");
                    try
                    {
                        var metadataDoc = JsonDocument.Parse(log.Metadata);
                        var formatted = JsonSerializer.Serialize(metadataDoc.RootElement,
                            new JsonSerializerOptions { WriteIndented = true });
                        await writer.WriteLineAsync(formatted);
                    }
                    catch
                    {
                        await writer.WriteLineAsync(log.Metadata);
                    }
                }

                await writer.WriteLineAsync();
                await writer.WriteLineAsync(new string('-', 80));
                await writer.WriteLineAsync();
            }
        }

        return $"✓ 已导出 {logs.Count} 条 debug 日志\n路径: {filePath}";
    }
}
