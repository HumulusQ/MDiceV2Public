using System.Text.Json;
using System.Threading;
using MDiceV2.Interfaces.Mod;

namespace AIMod.Trpg;

public sealed partial class LlmCallTracker
{
    private static readonly AsyncLocal<LlmTurnContext?> AmbientTurnContext = new();
    private static readonly AsyncLocal<LlmRequestContext?> AmbientRequestContext = new();
    private readonly ChatDatabase _db;
    private readonly IModContext _context;
    private readonly Func<List<ChatMessage>, Task<string?>> _apiCaller;
    private readonly Func<LlmActualUsage?>? _actualUsageProvider;

    public LlmCallTracker(
        ChatDatabase db,
        IModContext context,
        Func<List<ChatMessage>, Task<string?>> apiCaller,
        Func<LlmActualUsage?>? actualUsageProvider = null)
    {
        _db = db;
        _context = context;
        _apiCaller = apiCaller;
        _actualUsageProvider = actualUsageProvider;
    }

    public IDisposable PushTurnContext(string? turnId, string? sourceMessageId, string? sourceSummary = null)
    {
        return PushAmbientTurnContext(turnId, sourceMessageId, sourceSummary);
    }

    public static IDisposable PushAmbientTurnContext(string? turnId, string? sourceMessageId, string? sourceSummary = null)
    {
        var previous = AmbientTurnContext.Value;
        AmbientTurnContext.Value = new LlmTurnContext
        {
            TurnId = turnId,
            SourceMessageId = sourceMessageId,
            SourceSummary = sourceSummary
        };
        return new PopWhenDisposed(previous);
    }

    public Task<string?> CallAsync(
        TrpgScope scope,
        string? characterId,
        List<ChatMessage> messages,
        string agentName,
        string requestKind)
    {
        return CallAsync(scope, characterId, messages, agentName, requestKind, _apiCaller);
    }

    public async Task<string?> CallAsync(
        TrpgScope scope,
        string? characterId,
        List<ChatMessage> messages,
        string agentName,
        string requestKind,
        Func<List<ChatMessage>, Task<string?>> apiCaller)
    {
        var estimatedInputTokens = EstimateTokens(messages.Sum(m => m.Content?.Length ?? 0));
        var started = DateTime.UtcNow;
        string? errorType = null;
        string? response = null;
        var success = false;
        var previousRequestContext = AmbientRequestContext.Value;
        AmbientRequestContext.Value = new LlmRequestContext
        {
            Scope = scope,
            CharacterId = characterId,
            AgentName = agentName,
            RequestKind = requestKind,
            InputCharCount = messages.Sum(m => m.Content?.Length ?? 0)
        };
        _actualUsageProvider?.Invoke();
        try
        {
            response = await apiCaller(messages);
            success = !string.IsNullOrWhiteSpace(response);
            return response;
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            var actualUsage = _actualUsageProvider?.Invoke();
            var hasActualTokens = actualUsage != null
                                  && (actualUsage.InputTokens > 0
                                      || actualUsage.OutputTokens > 0
                                      || actualUsage.TotalTokens > 0);
            var inputTokens = hasActualTokens
                ? Math.Max(0, actualUsage!.InputTokens)
                : estimatedInputTokens;
            var outputTokens = hasActualTokens
                ? Math.Max(0, actualUsage!.OutputTokens)
                : EstimateTokens(response?.Length ?? 0);
            var entry = new LlmUsageLogEntry
            {
                CreatedAt = started,
                Provider = string.IsNullOrWhiteSpace(actualUsage?.Provider) ? "trpg-fallback" : actualUsage!.Provider,
                Model = string.IsNullOrWhiteSpace(actualUsage?.Model) ? "selected-or-fallback" : actualUsage!.Model,
                AgentName = agentName,
                RequestKind = requestKind,
                WorldId = scope.WorldId,
                GroupId = scope.GroupId,
                CharacterId = string.IsNullOrWhiteSpace(characterId) ? null : characterId,
                TurnId = AmbientTurnContext.Value?.TurnId,
                SourceMessageId = AmbientTurnContext.Value?.SourceMessageId,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                CachedInputTokens = actualUsage?.CachedInputTokens,
                CacheHitTokens = actualUsage?.CacheHitTokens,
                CacheMissTokens = actualUsage?.CacheMissTokens,
                EstimatedCost = EstimateCost(inputTokens, outputTokens),
                Success = success,
                ErrorType = errorType,
                Metadata = JsonSerializer.Serialize(new
                {
                    estimated = !hasActualTokens,
                    message_count = messages.Count,
                    chars_in = messages.Sum(m => m.Content?.Length ?? 0),
                    chars_out = response?.Length ?? 0,
                    actual_total_tokens = actualUsage?.TotalTokens,
                    turn_id = AmbientTurnContext.Value?.TurnId,
                    source_message_id = AmbientTurnContext.Value?.SourceMessageId,
                    source_summary = AmbientTurnContext.Value?.SourceSummary
                })
            };
            try
            {
                await _db.InsertLlmUsageLogAsync(entry);
                _context.Log(LogLevel.Info,
                    $"[AIMod:TRPG] LlmCallTracker agent={agentName} requestKind={requestKind} provider={entry.Provider} model={entry.Model} input={inputTokens} output={outputTokens} cost≈{entry.EstimatedCost:F6} cached={entry.CachedInputTokens ?? 0} success={success} estimated={!hasActualTokens}");

                if (actualUsage?.IsCommonDefaultApi == true)
                {
                    var commonMetadata = new
                    {
                        estimated = !hasActualTokens,
                        message_count = messages.Count,
                        chars_in = messages.Sum(m => m.Content?.Length ?? 0),
                        chars_out = response?.Length ?? 0,
                        actual_total_tokens = actualUsage.TotalTokens,
                        turn_id = AmbientTurnContext.Value?.TurnId,
                        source_message_id = AmbientTurnContext.Value?.SourceMessageId,
                        source_summary = AmbientTurnContext.Value?.SourceSummary,
                        owner_resolution = string.IsNullOrWhiteSpace(actualUsage.OwnerResolution) ? "unknown" : actualUsage.OwnerResolution,
                        api_source = string.IsNullOrWhiteSpace(actualUsage.ApiSourceKind) ? "common-default" : actualUsage.ApiSourceKind
                    };

                    await _db.InsertCommonApiUsageLogAsync(new CommonApiUsageLogEntry
                    {
                        CreatedAt = started,
                        UserId = actualUsage.OwnerUserId,
                        GroupId = scope.GroupId,
                        WorldId = scope.WorldId,
                        TeamName = scope.TeamName,
                        CharacterId = string.IsNullOrWhiteSpace(characterId) ? null : characterId,
                        Provider = entry.Provider,
                        Model = entry.Model,
                        AgentName = agentName,
                        RequestKind = requestKind,
                        InputTokens = inputTokens,
                        OutputTokens = outputTokens,
                        TotalTokens = actualUsage.TotalTokens > 0 ? actualUsage.TotalTokens : inputTokens + outputTokens,
                        CachedInputTokens = actualUsage.CachedInputTokens,
                        CacheHitTokens = actualUsage.CacheHitTokens,
                        CacheMissTokens = actualUsage.CacheMissTokens,
                        EstimatedCost = entry.EstimatedCost,
                        Success = success,
                        SourceMessageId = AmbientTurnContext.Value?.SourceMessageId,
                        TurnId = AmbientTurnContext.Value?.TurnId,
                        Metadata = JsonSerializer.Serialize(commonMetadata)
                    });
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] LlmCallTracker log skipped: {ex.Message}");
            }

            // 如果启用了 debug，记录完整的 debug 日志
            try
            {
                if (await _db.IsGlobalDebugEnabledAsync(scope))
                {
                    var debugEntry = new LlmDebugLogEntry
                    {
                        CreatedAt = started,
                        WorldId = scope.WorldId,
                        GroupId = scope.GroupId,
                        CharacterId = string.IsNullOrWhiteSpace(characterId) ? null : characterId,
                        AgentName = agentName,
                        RequestKind = requestKind,
                        MessagesJson = JsonSerializer.Serialize(messages),
                        ResponseText = response,
                        Success = success,
                        Error = errorType,
                        InputCharCount = messages.Sum(m => m.Content?.Length ?? 0),
                        OutputCharCount = response?.Length ?? 0,
                        Metadata = JsonSerializer.Serialize(new
                        {
                            message_count = messages.Count,
                            chars_in = messages.Sum(m => m.Content?.Length ?? 0),
                            chars_out = response?.Length ?? 0,
                            provider = actualUsage?.Provider ?? "unknown",
                            model = actualUsage?.Model ?? "unknown",
                            has_actual_usage = actualUsage != null
                        })
                    };
                    await _db.InsertLlmDebugLogAsync(debugEntry);
                }
            }
            catch (Exception ex)
            {
                _context.Log(LogLevel.Warn, $"[AIMod:TRPG] LlmCallTracker debug log skipped: {ex.Message}");
            }

            AmbientRequestContext.Value = previousRequestContext;
        }
    }

    private static long EstimateTokens(int chars)
        => Math.Max(0, (long)Math.Ceiling(chars / 3.5));

    private static decimal EstimateCost(long inputTokens, long outputTokens)
    {
        const decimal inputPerMillion = 0.27m;
        const decimal outputPerMillion = 1.10m;
        return (inputTokens / 1_000_000m * inputPerMillion)
               + (outputTokens / 1_000_000m * outputPerMillion);
    }
    internal static void ResetAmbientTurnContext(LlmTurnContext? value)
    {
        AmbientTurnContext.Value = value;
    }

    internal static LlmRequestContext? GetAmbientRequestContext()
    {
        return AmbientRequestContext.Value;
    }
}

file sealed class PopWhenDisposed : IDisposable
{
    private readonly LlmTurnContext? _previous;

    public PopWhenDisposed(LlmTurnContext? previous)
    {
        _previous = previous;
    }

    public void Dispose()
    {
        LlmCallTracker.ResetAmbientTurnContext(_previous);
    }
}

public sealed class LlmTurnContext
{
    public string? TurnId { get; set; }
    public string? SourceMessageId { get; set; }
    public string? SourceSummary { get; set; }
}

public sealed class LlmActualUsage
{
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long TotalTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? CacheHitTokens { get; set; }
    public long? CacheMissTokens { get; set; }
    public bool IsCommonDefaultApi { get; set; }
    public long OwnerUserId { get; set; }
    public string OwnerResolution { get; set; } = "";
    public string ApiSourceKind { get; set; } = "";
}

public sealed class LlmRequestContext
{
    public TrpgScope Scope { get; set; } = TrpgScope.Create(0, 0, "default");
    public string? CharacterId { get; set; }
    public string AgentName { get; set; } = "";
    public string RequestKind { get; set; } = "";
    public int InputCharCount { get; set; }
}

public sealed class LlmUsageLogEntry
{
    public long Id { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string RequestKind { get; set; } = "";
    public string WorldId { get; set; } = "";
    public long GroupId { get; set; }
    public string? CharacterId { get; set; }
    public string? TurnId { get; set; }
    public string? SourceMessageId { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? CacheHitTokens { get; set; }
    public long? CacheMissTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public bool Success { get; set; }
    public string? ErrorType { get; set; }
    public string Metadata { get; set; } = "{}";
}

public sealed class LlmCostReport
{
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public int RequestCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheHitTokens { get; set; }
    public long CacheMissTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public List<LlmCostBreakdown> ProviderModels { get; set; } = new();
    public List<LlmCostBreakdown> TopAgents { get; set; } = new();
    public List<LlmCostBreakdown> TopRequestKinds { get; set; } = new();
}

public sealed class LlmCostBreakdown
{
    public string Name { get; set; } = "";
    public int RequestCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheHitTokens { get; set; }
    public long CacheMissTokens { get; set; }
    public decimal EstimatedCost { get; set; }
}

public sealed class LlmTurnCostRow
{
    public string TurnId { get; set; } = "";
    public string SourceMessageId { get; set; } = "";
    public string SourceSummary { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public int RequestCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedInputTokens { get; set; }
    public long CacheHitTokens { get; set; }
    public long CacheMissTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public string MostExpensiveAgent { get; set; } = "";
}
