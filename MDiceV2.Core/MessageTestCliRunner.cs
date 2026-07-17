using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using MDiceV2.Abstractions;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Core.Mod;
using MDiceV2.Models;

namespace MDiceV2.Core;

internal sealed class MessageTestCliOptions
{
    public bool Enabled { get; init; }
    public string? Message { get; init; }
    public string? OneBotJson { get; init; }
    public long GroupId { get; init; } = 10000;
    public long UserId { get; init; } = 10001;
    public bool IsPrivate { get; init; }
    public bool AtBot { get; init; }
    public int TimeoutMs { get; init; } = 5000;
    public bool Trace { get; init; }
}

internal sealed class MessageTestCliRunner
{
    public const string ResultMarker = "__MDICEV2_TEST_RESULT__";

    public static MessageTestCliOptions Parse(string[] args)
    {
        var options = new MessageTestCliOptions();
        if (!args.Any(arg => string.Equals(arg, "--message-test", StringComparison.OrdinalIgnoreCase)))
        {
            return options;
        }

        string? message = null;
        string? oneBotJson = null;
        long groupId = 10000;
        long userId = 10001;
        bool isPrivate = false;
        bool atBot = false;
        int timeoutMs = 5000;
        bool trace = false;

        foreach (var arg in args)
        {
            if (arg.StartsWith("--test-message-b64=", StringComparison.Ordinal))
            {
                message = DecodeBase64(arg["--test-message-b64=".Length..]);
            }
            else if (arg.StartsWith("--test-onebot-json-b64=", StringComparison.Ordinal))
            {
                oneBotJson = DecodeBase64(arg["--test-onebot-json-b64=".Length..]);
            }
            else if (arg.StartsWith("--test-group=", StringComparison.Ordinal) &&
                     long.TryParse(arg["--test-group=".Length..], out var parsedGroup))
            {
                groupId = parsedGroup;
            }
            else if (arg.StartsWith("--test-user=", StringComparison.Ordinal) &&
                     long.TryParse(arg["--test-user=".Length..], out var parsedUser))
            {
                userId = parsedUser;
            }
            else if (arg.StartsWith("--test-timeout-ms=", StringComparison.Ordinal) &&
                     int.TryParse(arg["--test-timeout-ms=".Length..], out var parsedTimeout))
            {
                timeoutMs = Math.Clamp(parsedTimeout, 1, 300000);
            }
            else if (string.Equals(arg, "--test-private", StringComparison.OrdinalIgnoreCase))
            {
                isPrivate = true;
            }
            else if (string.Equals(arg, "--test-at-bot", StringComparison.OrdinalIgnoreCase))
            {
                atBot = true;
            }
            else if (string.Equals(arg, "--test-trace", StringComparison.OrdinalIgnoreCase))
            {
                trace = true;
            }
        }

        return new MessageTestCliOptions
        {
            Enabled = true,
            Message = message,
            OneBotJson = oneBotJson,
            GroupId = groupId,
            UserId = userId,
            IsPrivate = isPrivate,
            AtBot = atBot,
            TimeoutMs = timeoutMs,
            Trace = trace
        };
    }

    public static int Run(MessageTestCliOptions options)
    {
        try
        {
            Trace(options, "initializing services");
            ServiceBootstrapper.EnableMessageTestMode();
            var serviceProvider = ServiceBootstrapper.BuildServices(StartupMode.Console);
            ServiceBootstrapper.ValidateServices(serviceProvider);

            MessageProcessor.EnsureInitialized();
#pragma warning disable CS0618
            RuntimeModInitializer.InitializeModsForRuntime("MessageTestCli", messageProcessor: MessageProcessor.GetInstance());
#pragma warning restore CS0618

            var distribution = MessageDistribution.GetInstance();
            distribution.SimulationSwitch = true;

            var replies = new List<string>();
            long? sourceGroupId = null;
            long? sourceUserId = null;
            bool? sourceIsPrivate = null;
            var replySignal = new AutoResetEvent(false);
            var lastReplyUtc = DateTime.MinValue;

            distribution.OnReplySent += (content, msg) =>
            {
                lock (replies)
                {
                    replies.Add(content);
                    sourceGroupId = msg.GroupId;
                    sourceUserId = msg.UserId;
                    sourceIsPrivate = msg.Source == MessageSource.privatechat;
                    lastReplyUtc = DateTime.UtcNow;
                }

                Trace(options, $"reply captured ({content.Length} chars)");
                replySignal.Set();
            };

            var oneBotMessage = BuildOneBotMessage(options);
            Trace(options, "enqueueing pseudo OneBot message");
            GlobalMessageQueue.Instance.EnqueueOneBotMessage(oneBotMessage);

            var deadline = DateTime.UtcNow.AddMilliseconds(options.TimeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                var remaining = deadline - DateTime.UtcNow;
                var waitMs = Math.Clamp((int)remaining.TotalMilliseconds, 1, 100);
                replySignal.WaitOne(waitMs);

                lock (replies)
                {
                    if (replies.Count > 0 &&
                        (DateTime.UtcNow - lastReplyUtc).TotalMilliseconds >= 200)
                    {
                        break;
                    }
                }
            }

            List<string> finalReplies;
            lock (replies)
            {
                finalReplies = replies.ToList();
            }

            var timedOut = finalReplies.Count == 0;
            WriteResult(new
            {
                success = !timedOut,
                timedOut,
                replyCount = finalReplies.Count,
                replies = finalReplies,
                group = sourceGroupId ?? (options.IsPrivate ? 0 : options.GroupId),
                user = sourceUserId ?? options.UserId,
                isPrivate = sourceIsPrivate ?? options.IsPrivate
            });

            return timedOut ? 2 : 0;
        }
        catch (Exception ex)
        {
            WriteResult(new
            {
                success = false,
                timedOut = false,
                error = ex.Message,
                exceptionType = ex.GetType().Name
            });
            return 1;
        }
    }

    private static JsonElement BuildOneBotMessage(MessageTestCliOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OneBotJson))
        {
            return JsonDocument.Parse(options.OneBotJson).RootElement.Clone();
        }

        const long botId = 1001;
        var message = options.Message ?? string.Empty;
        if (options.AtBot && !options.IsPrivate)
        {
            message = $"[CQ:at,qq={botId}] {message}";
        }

        var payload = new Dictionary<string, object?>
        {
            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["self_id"] = botId,
            ["post_type"] = "message",
            ["message_type"] = options.IsPrivate ? "private" : "group",
            ["sub_type"] = options.IsPrivate ? "friend" : "normal",
            ["message_id"] = Environment.TickCount,
            ["user_id"] = options.UserId,
            ["message"] = message,
            ["raw_message"] = message,
            ["sender"] = new Dictionary<string, object?>
            {
                ["user_id"] = options.UserId,
                ["nickname"] = $"测试用户_{options.UserId}",
                ["card"] = string.Empty,
                ["role"] = "member"
            }
        };

        if (!options.IsPrivate)
        {
            payload["group_id"] = options.GroupId;
        }

        return JsonSerializer.SerializeToElement(payload);
    }

    private static string DecodeBase64(string value)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }

    private static void WriteResult(object result)
    {
        Console.WriteLine($"{ResultMarker}{JsonSerializer.Serialize(result)}");
        Console.Out.Flush();
    }

    private static void Trace(MessageTestCliOptions options, string message)
    {
        if (!options.Trace)
        {
            return;
        }

        Console.WriteLine($"[MessageTestCli] {message}");
        Console.Out.Flush();
    }
}
