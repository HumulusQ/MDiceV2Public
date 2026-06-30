using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using MDiceV2.Models;

namespace MDiceV2.Models;

/// <summary>
/// 日志启动结果
/// </summary>
public enum LogStartResult
{
    Failed,
    AlreadyRecording,
    Appended,
    Created
}

/// <summary>
/// 日志备注结构
/// </summary>
public class Comment
{
    public long CommenterId { get; set; }
    public string CommenterName { get; set; } = "";
    public string CommentTime { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>
/// 日志条目结构
/// </summary>
public class LogEntry
{
    public int GlobalIndex { get; set; }      // 全局条目序号(1-based)
    public int PageLocalIndex { get; set; }   // 页内条目序号(1-50)
    public string Timestamp { get; set; } = "";
    public long UserId { get; set; }
    public string SenderName { get; set; } = "";
    public string Content { get; set; } = "";
    public List<Comment> Comments { get; set; } = new();
}

/// <summary>
/// TRPG日志管理器，负责跑团日志的记录和管理
/// </summary>
public partial class TRPGLogManager : ObservableObject
{
    public static TRPGLogManager? Instance { get; private set; }

    private static readonly object _instanceLock = new object();

    private readonly Dictionary<long, StreamWriter> _logWriters = new();
    private readonly Dictionary<long, long> _logStarters = new();
    private readonly string _logDirectory;
    private readonly Dictionary<long, string> _activeLogNames = new();
    private readonly Dictionary<long, int> _entryCounters = new(); // 每群条目计数器

    public TRPGLogManager()
    {
        _logDirectory = Path.Combine(Directory.GetCurrentDirectory(), "trpglogs");
        Directory.CreateDirectory(_logDirectory);
    }

    public static TRPGLogManager GetInstance()
    {
        if (Instance == null)
        {
            lock (_instanceLock)
            {
                if (Instance == null)
                {
                    Instance = new TRPGLogManager();
                }
            }
        }
        return Instance;
    }

    private int GetTotalEntryCountFromFile(string filePath)
    {
        try
        {
            string content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            var regex = new Regex(@"data-entry-index=""(\d+)""");
            var matches = regex.Matches(content);
            int maxIndex = 0;
            foreach (Match match in matches)
            {
                if (int.TryParse(match.Groups[1].Value, out int index))
                {
                    if (index > maxIndex)
                        maxIndex = index;
                }
            }
            return maxIndex;
        }
        catch (Exception e)
        {
            Log.Warn($"解析文件条目数失败: {e.Message}");
            return 0;
        }
    }

    private void RemoveHtmlClosingTagsFromFile(string filePath)
    {
        try
        {
            string content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
            content = Regex.Replace(content, @"\s*</div>\s*</body>\s*</html>\s*$", "", RegexOptions.Multiline);
            File.WriteAllText(filePath, content, System.Text.Encoding.UTF8);
            Log.Normal($"已清理文件 {Path.GetFileName(filePath)} 的闭合标签，准备追加新内容。");
        }
        catch (Exception e)
        {
            Log.Warn($"移除HTML闭合标签失败: {e.Message}");
        }
    }

    public LogStartResult StartLog(long groupId, long starterUserId, string logName = "")
    {
        if (_logWriters.ContainsKey(groupId))
        {
            Log.Warn($"群 {groupId} 的日志已在记录中，无需重复开启。当前日志名称: {_activeLogNames.GetValueOrDefault(groupId, "无")}");
            return LogStartResult.AlreadyRecording;
        }

        if (string.IsNullOrEmpty(logName))
        {
            Log.Error($"启动群 {groupId} 的跑团日志失败: 未指定日志名称。");
            return LogStartResult.Failed;
        }

        try
        {
            string filePath;
            bool appendMode = false;
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

            string searchPatternWithUserId = $"group_{groupId}_{starterUserId}_{logName}_*.html";
            string[] existingFilesWithUserId = Directory.GetFiles(_logDirectory, searchPatternWithUserId);

            if (existingFilesWithUserId.Length > 0)
            {
                filePath = existingFilesWithUserId[0];
                appendMode = true;
                RemoveHtmlClosingTagsFromFile(filePath);
                Log.Normal($"群 {groupId} 检测到现有日志 '{logName}'（当前用户），将在其基础上继续记录。文件：{filePath}");
            }
            else
            {
                string searchPatternWithoutUserId = $"group_{groupId}_{logName}_*.html";
                string[] existingFilesWithoutUserId = Directory.GetFiles(_logDirectory, searchPatternWithoutUserId);

                if (existingFilesWithoutUserId.Length > 0)
                {
                    filePath = existingFilesWithoutUserId[0];
                    appendMode = true;
                    RemoveHtmlClosingTagsFromFile(filePath);
                    Log.Normal($"群 {groupId} 检测到现有日志 '{logName}'（旧格式），将在其基础上继续记录。文件：{filePath}");
                }
                else
                {
                    string searchPatternAcrossGroups = $"group_*_{starterUserId}_{logName}_*.html";
                    string[] existingFilesAcrossGroups = Directory.GetFiles(_logDirectory, searchPatternAcrossGroups);

                    if (existingFilesAcrossGroups.Length > 0)
                    {
                        string oldFilePath = existingFilesAcrossGroups[0];
                        string oldFileName = Path.GetFileName(oldFilePath);
                        
                        var oldNameMatch = Regex.Match(oldFileName, @"^group_\d+_\d+_(.*)_(\d{8}_\d{6})\.html$");
                        if (oldNameMatch.Success)
                        {
                            string oldLogName = oldNameMatch.Groups[1].Value;
                            string oldTimestamp = oldNameMatch.Groups[2].Value;
                            
                            string newFileName = $"group_{groupId}_{starterUserId}_{oldLogName}_{oldTimestamp}.html";
                            string newFilePath = Path.Combine(_logDirectory, newFileName);
                            
                            if (File.Exists(oldFilePath))
                            {
                                File.Move(oldFilePath, newFilePath, true);
                                filePath = newFilePath;
                                appendMode = true;
                                RemoveHtmlClosingTagsFromFile(filePath);
                                Log.Normal($"群 {groupId} 检测到其他群的日志 '{logName}'（用户所有），已将其重命名到当前群。文件：{filePath}");
                            }
                            else
                            {
                                string fileName = $"group_{groupId}_{starterUserId}_{logName}_{timestamp}.html";
                                filePath = Path.Combine(_logDirectory, fileName);
                                Log.Normal($"群 {groupId} 未检测到现有日志 '{logName}'，将创建新日志。文件：{filePath}");
                            }
                        }
                        else
                        {
                            string fileName = $"group_{groupId}_{starterUserId}_{logName}_{timestamp}.html";
                            filePath = Path.Combine(_logDirectory, fileName);
                            Log.Normal($"群 {groupId} 未检测到现有日志 '{logName}'，将创建新日志。文件：{filePath}");
                        }
                    }
                    else
                    {
                        string fileName = $"group_{groupId}_{starterUserId}_{logName}_{timestamp}.html";
                        filePath = Path.Combine(_logDirectory, fileName);
                        Log.Normal($"群 {groupId} 未检测到现有日志 '{logName}'，将创建新日志。文件：{filePath}");
                    }
                }
            }

            StreamWriter writer = new StreamWriter(filePath, appendMode, System.Text.Encoding.UTF8);
            _logWriters[groupId] = writer;
            _activeLogNames[groupId] = logName;
            _logStarters[groupId] = starterUserId;

            if (!appendMode)
            {
                _entryCounters[groupId] = 1; // 第一条是系统消息
                writer.WriteLine("<!DOCTYPE html>");
                writer.WriteLine("<html lang=\"zh-CN\">");
                writer.WriteLine("<head>");
                writer.WriteLine("    <meta charset=\"UTF-8\">");
                writer.WriteLine("    <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">");
                writer.WriteLine($"    <title>跑团日志 - 群 {groupId} - {logName}</title>");
                writer.WriteLine("    <style>");
                writer.WriteLine("        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 20px; background-color: #1e1e1e; color: #d4d4d4; }");
                writer.WriteLine("        .log-container { max-width: 900px; margin: auto; background-color: #252526; padding: 20px; border-radius: 8px; box-shadow: 0 4px 8px rgba(0, 0, 0, 0.2); }");
                writer.WriteLine("        .message { padding: 8px 0; border-bottom: 1px solid #333; display: flex; align-items: flex-start; }");
                writer.WriteLine("        .message:last-child { border-bottom: none; }");
                writer.WriteLine("        .timestamp { color: #888; font-size: 0.8em; min-width: 120px; margin-right: 10px; flex-shrink: 0; }");
                writer.WriteLine("        .sender-name { color: #ADD8E6; font-weight: bold; margin-right: 5px; }");
                writer.WriteLine("        .content { flex-grow: 1; word-wrap: break-word; }");
                writer.WriteLine("        .gm { color: #ADD8E6; }");
                writer.WriteLine("        .ooc { color: #90EE90; font-style: italic; }");
                writer.WriteLine("        .action { color: #FFD700; font-weight: bold; }");
                writer.WriteLine("        .dialogue { color: #FFFFFF; }");
                writer.WriteLine("        .inner-thought { color: #FFB6C1; font-style: italic; }");
                writer.WriteLine("        .dice-roll { color: #FFA07A; font-weight: bold; background-color: #3a3a3a; padding: 2px 5px; border-radius: 3px; margin-left: 5px; }");
                writer.WriteLine("        .system-message { color: #FF6347; font-weight: bold; }");
                writer.WriteLine("        .comment { color: #aaa; font-size: 0.85em; margin-left: 40px; padding: 2px 0; font-style: italic; border-left: 2px solid #555; padding-left: 8px; }");
                writer.WriteLine("        .log-entry { margin-bottom: 4px; }");
                writer.WriteLine("    </style>");
                writer.WriteLine("</head>");
                writer.WriteLine("<body>");
                writer.WriteLine("    <div class=\"log-container\">");
                writer.WriteLine($"        <div class=\"message system-message\" data-userid=\"{starterUserId}\"><span class=\"timestamp\">{DateTime.Now:HH:mm:ss}</span><span class=\"sender-name\">GM: </span><span class=\"content\">跑团日志已开启。{(string.IsNullOrEmpty(logName) ? "" : $" (日志名称: {logName})")}</span></div>");
            }
            else
            {
                // 解析已有文件的条目数
                int existingEntryCount = GetTotalEntryCountFromFile(filePath);
                _entryCounters[groupId] = existingEntryCount + 1;
                writer.WriteLine($"        <div class=\"message system-message\" data-userid=\"{starterUserId}\"><span class=\"timestamp\">{DateTime.Now:HH:mm:ss}</span><span class=\"sender-name\">GM: </span><span class=\"content\">日志续写。{(string.IsNullOrEmpty(logName) ? "" : $" (日志名称: {logName})")}</span></div>");
            }

            Log.Normal($"群 {groupId} 的跑团日志已启动，文件：{filePath}");
            return appendMode ? LogStartResult.Appended : LogStartResult.Created;
        }
        catch (Exception e)
        {
            Log.Error($"启动群 {groupId} 的跑团日志失败: {e.Message}");
            return LogStartResult.Failed;
        }
    }

    public bool IsLogRecording(long groupId)
    {
        return _logWriters.ContainsKey(groupId);
    }

    public string? GetActiveLogName(long groupId)
    {
        return _activeLogNames.GetValueOrDefault(groupId);
    }

    public void StopLog(long groupId)
    {
        StreamWriter? writer;
        if (!_logWriters.TryGetValue(groupId, out writer!))
        {
            Log.Warn($"群 {groupId} 的日志未在记录中，无需关闭。");
            return;
        }

        try
        {
            long starterUserId = _logStarters.TryGetValue(groupId, out long id) ? id : 0;
            writer.WriteLine($"        <div class=\"message system-message\" data-userid=\"{starterUserId}\"><span class=\"timestamp\">{DateTime.Now:HH:mm:ss}</span><span class=\"sender-name\">GM: </span><span class=\"content\">跑团日志已关闭。</span></div>");
            writer.WriteLine("    </div>");
            writer.WriteLine("</body>");
            writer.WriteLine("</html>");
            writer.Close();
            writer.Dispose();
            _logWriters.Remove(groupId);
            _activeLogNames.Remove(groupId);
            _logStarters.Remove(groupId);
            _entryCounters.Remove(groupId);
            Log.Normal($"群 {groupId} 的跑团日志已关闭。");
        }
        catch (Exception e)
        {
            Log.Error($"关闭群 {groupId} 的跑团日志失败: {e.Message}");
        }
    }

    public class LogInfo
    {
        public string LogName { get; set; } = "";
        public string? LastRecordTime { get; set; }
    }

    public (List<LogInfo> GroupLogs, List<LogInfo> UserLogs) GetLogList(long groupId, long userId)
    {
        var groupLogs = new Dictionary<string, LogInfo>();
        var userLogs = new Dictionary<string, LogInfo>();

        try
        {
            string[] allFiles = Directory.GetFiles(_logDirectory, "group_*_*.html");
            var filePattern = new Regex(@"^group_(\d+)_(?:(\d+)_)?(.*)_(\d{8}_\d{6})\.html$");

            foreach (var filePath in allFiles)
            {
                string fileName = Path.GetFileName(filePath);
                var match = filePattern.Match(fileName);

                if (match.Success)
                {
                    long fileGroupId = long.Parse(match.Groups[1].Value);
                    string fileUserIdStr = match.Groups[2].Value;
                    string logName = match.Groups[3].Value;
                    string timestamp = match.Groups[4].Value;
                    
                    // 格式化时间戳为可读格式
                    string formattedTime = "";
                    if (DateTime.TryParseExact(timestamp, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out DateTime dt))
                    {
                        formattedTime = dt.ToString("yyyy-MM-dd HH:mm:ss");
                    }

                    var logInfo = new LogInfo { LogName = logName, LastRecordTime = formattedTime };
                    
                    if (fileGroupId == groupId)
                    {
                        if (!groupLogs.ContainsKey(logName) || string.IsNullOrEmpty(groupLogs[logName].LastRecordTime))
                        {
                            groupLogs[logName] = logInfo;
                        }
                    }

                    if (!string.IsNullOrEmpty(fileUserIdStr) && long.TryParse(fileUserIdStr, out long fileUserId) && fileUserId == userId)
                    {
                        if (!userLogs.ContainsKey(logName) || string.IsNullOrEmpty(userLogs[logName].LastRecordTime))
                        {
                            userLogs[logName] = logInfo;
                        }
                    }
                }
            }

            return (groupLogs.Values.ToList(), userLogs.Values.ToList());
        }
        catch (Exception e)
        {
            Log.Error($"获取群 {groupId} 用户 {userId} 的日志列表失败: {e.Message}");
            return (new List<LogInfo>(), new List<LogInfo>());
        }
    }

    public void WriteLog(long groupId, long userId, string senderName, string message, LogMessageType type = LogMessageType.Normal)
    {
        StreamWriter? writer;
        if (!_logWriters.TryGetValue(groupId, out writer!))
        {
            return; // 如果日志未开启，则不写入
        }

        try
        {
            int entryIndex = _entryCounters.GetValueOrDefault(groupId, 0) + 1;
            _entryCounters[groupId] = entryIndex;
            
            string formattedMessage = FormatMessageForHtml(senderName, message, type);
            writer.WriteLine($"        <div class=\"message\" data-userid=\"{userId}\" data-entry-index=\"{entryIndex}\"><span class=\"timestamp\">{DateTime.Now:HH:mm:ss}</span><span class=\"sender-name\">{System.Net.WebUtility.HtmlEncode(senderName)}: </span><span class=\"content\">{formattedMessage}</span></div>");
            writer.Flush(); // 确保内容立即写入文件
        }
        catch (Exception e)
        {
            Log.Error($"写入群 {groupId} 的跑团日志失败: {e.Message}");
        }
    }

    private string FormatMessageForHtml(string senderName, string message, LogMessageType type)
    {
        string className = "gm"; // 默认GM场景描写

        if (message.StartsWith("("))
        {
            className = "ooc"; // 画外音
        }
        else if (message.StartsWith("#"))
        {
            className = "action"; // 人物行动
        }
        else if (message.StartsWith("\"") || message.StartsWith("“"))
        {
            className = "dialogue"; // 对话
        }
        else if (message.StartsWith("【") || message.StartsWith("["))
        {
            className = "inner-thought"; // 内心活动或特别注释
        }

        // 检查是否是骰子反馈消息
        if (type == LogMessageType.DiceRoll)
        {
            className = "dice-roll";
        }
        else if (type == LogMessageType.System)
        {
            className = "system-message";
        }

        // 对消息内容进行HTML转义，防止XSS
        string escapedMessage = System.Net.WebUtility.HtmlEncode(message);

        return $"<span class=\"{className}\">{escapedMessage}</span>";
    }

    public string? GetLogPath(long groupId, string logName, long? userId = null)
    {
        try
        {
            // Step 1: 尝试在当前群中查找日志
            if (userId.HasValue)
            {
                // 先尝试带userId的格式
                string searchPatternWithUserId = $"group_{groupId}_{userId}_{logName}_*.html";
                string[] filesWithUserId = Directory.GetFiles(_logDirectory, searchPatternWithUserId);
                if (filesWithUserId.Length > 0)
                {
                    return filesWithUserId[0];
                }
            }

            // 尝试不带userId的格式（向后兼容）
            string searchPatternWithoutUserId = $"group_{groupId}_{logName}_*.html";
            string[] filesWithoutUserId = Directory.GetFiles(_logDirectory, searchPatternWithoutUserId);
            if (filesWithoutUserId.Length > 0)
            {
                return filesWithoutUserId[0];
            }

            // Step 2: 如果在当前群中未找到且userId存在，跨群搜索该用户的日志
            if (userId.HasValue)
            {
                string searchPatternAcrossGroups = $"group_*_{userId}_{logName}_*.html";
                string[] filesAcrossGroups = Directory.GetFiles(_logDirectory, searchPatternAcrossGroups);
                if (filesAcrossGroups.Length > 0)
                {
                    return filesAcrossGroups[0];
                }
            }

            return null;
        }
        catch (Exception e)
        {
            Log.Error($"获取日志路径失败 (groupId: {groupId}, logName: {logName}, userId: {userId}): {e.Message}");
            return null;
        }
    }

    public bool IsLogStarter(long groupId, long userId)
    {
        return _logStarters.TryGetValue(groupId, out long starterUserId) && starterUserId == userId;
    }

    public List<(string timestamp, long userId, string senderName, string content)> GetLastNLogEntries(long groupId, string logName, long userId, int n = 50)
    {
        var entries = new List<(string, long, string, string)>();
        var logPath = GetLogPath(groupId, logName, userId);
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
        {
            return entries;
        }

        try
        {
            string htmlContent = File.ReadAllText(logPath);
            // 使用正则表达式匹配message div
            var regex = new Regex(
                @"<div class=""message[^""]*""(?: data-userid=""(\d+)"")?(?:[^>]*)>(.*?)</div>",
                RegexOptions.Singleline
            );
            var matches = regex.Matches(htmlContent);

            var allMatches = new List<Match>();
            foreach (Match match in matches)
            {
                allMatches.Add(match);
            }
            var lastMatches = allMatches.Skip(Math.Max(0, allMatches.Count - n)).ToList();

            foreach (var match in lastMatches)
            {
                string userIdStr = match.Groups[1].Value;
                long userId_entry = 0;
                if (!string.IsNullOrEmpty(userIdStr))
                {
                    long.TryParse(userIdStr, out userId_entry);
                }

                string messageHtml = match.Groups[2].Value;

                var timestampRegex = new Regex(
                    @"<span class=""timestamp"">(.*?)</span>"
                );
                string timestamp = timestampRegex.Match(messageHtml).Groups[1].Value;

                var senderRegex = new Regex(
                    @"<span class=""sender-name"">(.*?)</span>"
                );
                string senderName = senderRegex.Match(messageHtml).Groups[1].Value;
                if (senderName.EndsWith(": "))
                {
                    senderName = senderName.Substring(0, senderName.Length - 2);
                }

                var contentRegex = new Regex(
                    @"<span class=""content"">(.*?)</span>",
                    RegexOptions.Singleline
                );
                string rawContent = contentRegex.Match(messageHtml).Groups[1].Value;

                string content = Regex.Replace(rawContent, @"<[^>]+>", "");
                content = System.Net.WebUtility.HtmlDecode(content);

                entries.Add((timestamp, userId_entry, senderName, content));
            }
        }
        catch (Exception e)
        {
            Log.Error($"读取日志条目失败: {e.Message}");
        }

        return entries;
    }

    public (List<LogEntry> Entries, int TotalCount, int TotalPages, int Page) GetPaginatedLogEntries(long groupId, string logName, long userId, int page, int pageSize = 50)
    {
        var entries = new List<LogEntry>();
        var logPath = GetLogPath(groupId, logName, userId);
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
        {
            return (entries, 0, 0, page);
        }

        try
        {
            string htmlContent = File.ReadAllText(logPath);
            
            // 缓存正则表达式
            var messageRegex = new Regex(
                @"<div class=""message[^""]*""(?: data-userid=""(\d+)"")?(?: data-entry-index=""(\d+)"")?(?:[^>]*)>(.*?)</div>",
                RegexOptions.Singleline
            );
            var timestampRegex = new Regex(@"<span class=""timestamp"">(.*?)</span>");
            var senderRegex = new Regex(@"<span class=""sender-name"">(.*?)</span>");
            var contentRegex = new Regex(@"<span class=""content"">(.*?)</span>", RegexOptions.Singleline);
            var commentRegex = new Regex(@"<div class=""comment""(?: data-commenter=""(\d+)"")?(?: data-commenter-name=""([^""]*)"")?(?: data-comment-time=""([^""]*)"")?>(.*?)</div>", RegexOptions.Singleline);

            // 先获取总数
            var allMatches = messageRegex.Matches(htmlContent);
            int totalCount = allMatches.Count;
            
            int totalPages = (int)Math.Ceiling((double)totalCount / pageSize);
            if (page < 1) page = 1;
            if (page > totalPages && totalPages > 0) page = totalPages;

            int startIndex = (page - 1) * pageSize;
            int endIndex = Math.Min(startIndex + pageSize, totalCount);

            // 只处理需要的条目
            for (int i = startIndex; i < endIndex; i++)
            {
                Match match = allMatches[i];
                
                string userIdStr = match.Groups[1].Value;
                long userId_entry = 0;
                if (!string.IsNullOrEmpty(userIdStr))
                {
                    long.TryParse(userIdStr, out userId_entry);
                }

                string entryIndexStr = match.Groups[2].Value;
                int globalIndex = 0;
                if (!string.IsNullOrEmpty(entryIndexStr))
                {
                    int.TryParse(entryIndexStr, out globalIndex);
                }
                else
                {
                    globalIndex = i + 1; // 如果没有 data-entry-index，使用位置索引
                }

                string messageHtml = match.Groups[3].Value;

                string timestamp = timestampRegex.Match(messageHtml).Groups[1].Value;

                string senderName = senderRegex.Match(messageHtml).Groups[1].Value;
                if (senderName.EndsWith(": "))
                {
                    senderName = senderName.Substring(0, senderName.Length - 2);
                }

                string rawContent = contentRegex.Match(messageHtml).Groups[1].Value;
                string content = Regex.Replace(rawContent, @"<[^>]+>", "");
                content = System.Net.WebUtility.HtmlDecode(content);

                // 直接解析该 message div 后的所有 comment div
                int searchStart = match.Index + match.Length;
                int nextMessagePos = (i < totalCount - 1) ? allMatches[i + 1].Index : -1;
                
                var commentMatches = commentRegex.Matches(htmlContent, searchStart);
                
                var comments = new List<Comment>();
                foreach (Match cmtMatch in commentMatches)
                {
                    // 如果遇到下一个 message div，停止查找 comments
                    if (nextMessagePos > 0 && cmtMatch.Index >= nextMessagePos)
                        break;
                    
                    var comment = new Comment();
                    if (long.TryParse(cmtMatch.Groups[1].Value, out long commenterId))
                        comment.CommenterId = commenterId;
                    comment.CommenterName = System.Net.WebUtility.HtmlDecode(cmtMatch.Groups[2].Value);
                    comment.CommentTime = cmtMatch.Groups[3].Value;
                    
                    string commentContent = cmtMatch.Groups[4].Value;
                    // 移除前缀 [名称 时间] 如果存在
                    var prefixMatch = Regex.Match(commentContent, @"^\[([^\]]+)\] (.+)$");
                    if (prefixMatch.Success)
                    {
                        comment.Content = System.Net.WebUtility.HtmlDecode(prefixMatch.Groups[2].Value);
                    }
                    else
                    {
                        comment.Content = System.Net.WebUtility.HtmlDecode(commentContent);
                    }
                    
                    comments.Add(comment);
                }

                var entry = new LogEntry
                {
                    GlobalIndex = globalIndex,
                    Timestamp = timestamp,
                    UserId = userId_entry,
                    SenderName = senderName,
                    Content = content,
                    Comments = comments,
                    PageLocalIndex = i - startIndex + 1
                };
                entries.Add(entry);
            }

            return (entries, totalCount, totalPages, page);
        }
        catch (Exception e)
        {
            Log.Error($"读取分页日志条目失败: {e.Message}");
            return (entries, 0, 0, page);
        }
    }

    public int GetTotalEntryCount(long groupId, string logName, long userId)
    {
        var logPath = GetLogPath(groupId, logName, userId);
        if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
        {
            return 0;
        }

        return GetTotalEntryCountFromFile(logPath);
    }

    public bool AddComment(long groupId, string logName, int globalEntryIndex, string content, long commenterId, string commenterName, long? userId = null)
    {
        try
        {
            Log.Normal($"AddComment 开始: groupId={groupId}, logName={logName}, globalEntryIndex={globalEntryIndex}, userId={userId}");
            
            // 检查日志是否正在写入，避免并发冲突
            if (_logWriters.ContainsKey(groupId))
            {
                Log.Warn($"群 {groupId} 的日志正在记录中，无法添加备注");
                return false;
            }

            var logPath = GetLogPath(groupId, logName, userId);
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
            {
                Log.Warn($"日志文件不存在: {logPath}");
                return false;
            }

            string htmlContent = File.ReadAllText(logPath);
            Log.Normal($"日志文件大小: {htmlContent.Length} 字符");
            
            // 首先尝试用 data-entry-index 查找
            var regexWithIndex = new Regex(
                $@"(<div class=""message[^""]*""(?: data-userid=""\d+"")?(?: data-entry-index=""{globalEntryIndex}"")[^>]*>.*?</div>)",
                RegexOptions.Singleline
            );
            var match = regexWithIndex.Match(htmlContent);
            Log.Normal($"data-entry-index 匹配结果: {match.Success}");
            
            if (!match.Success)
            {
                // 如果没有 data-entry-index，回退到位置查找（第 N 个 message div）
                var allMessagesRegex = new Regex(
                    @"<div class=""message[^""]*""[^>]*>.*?</div>",
                    RegexOptions.Singleline
                );
                var allMatches = allMessagesRegex.Matches(htmlContent);
                Log.Normal($"回退位置查找: 找到 {allMatches.Count} 条 message div");
                
                if (globalEntryIndex <= 0 || globalEntryIndex > allMatches.Count)
                {
                    Log.Warn($"未找到条目索引为 {globalEntryIndex} 的日志条目（日志共有 {allMatches.Count} 条）");
                    return false;
                }
                
                match = allMatches[globalEntryIndex - 1]; // 转换为 0-based
                Log.Normal($"回退匹配成功: 位置 {globalEntryIndex}, match.Length={match.Length}");
            }

            // 在 message div 后插入 comment div（包含设置人信息）
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string commentDiv = $"\n        <div class=\"comment\" data-commenter=\"{commenterId}\" data-commenter-name=\"{System.Net.WebUtility.HtmlEncode(commenterName)}\" data-comment-time=\"{timestamp}\">[{commenterName} {timestamp}] {System.Net.WebUtility.HtmlEncode(content)}</div>";
            int insertPos = match.Index + match.Length;
            htmlContent = htmlContent.Insert(insertPos, commentDiv);
            
            File.WriteAllText(logPath, htmlContent);
            Log.Normal($"已为日志 '{logName}' 条目 {globalEntryIndex} 添加备注");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"添加备注失败: {e.Message}\n堆栈: {e.StackTrace}");
            return false;
        }
    }

    public bool DeleteEntries(long groupId, string logName, List<int> globalIndices, long? userId = null)
    {
        try
        {
            Log.Normal($"DeleteEntries 开始: groupId={groupId}, logName={logName}, indices=[{string.Join(", ", globalIndices)}], userId={userId}");
            
            if (_logWriters.ContainsKey(groupId))
            {
                Log.Warn($"群 {groupId} 的日志正在记录中，无法删除条目");
                return false;
            }

            var logPath = GetLogPath(groupId, logName, userId);
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
            {
                Log.Warn($"日志文件不存在: {logPath}");
                return false;
            }

            string htmlContent = File.ReadAllText(logPath);
            Log.Normal($"日志文件大小: {htmlContent.Length} 字符");
            
            var messageRegex = new Regex(
                @"<div class=""message[^""]*""(?: data-userid=""(\d+)"")?(?: data-entry-index=""(\d+)"")?(?:[^>]*)>(.*?)</div>",
                RegexOptions.Singleline
            );
            var matches = messageRegex.Matches(htmlContent);
            
            if (matches.Count == 0)
            {
                Log.Warn("未找到任何 message div");
                return false;
            }

            // 按索引从大到小排序，避免删除时索引偏移
            var sortedIndices = globalIndices.OrderByDescending(i => i).ToList();
            
            foreach (var globalIndex in sortedIndices)
            {
                if (globalIndex < 1 || globalIndex > matches.Count)
                {
                    Log.Warn($"索引 {globalIndex} 超出范围 (1-{matches.Count})");
                    continue;
                }
                
                var match = matches[globalIndex - 1];
                htmlContent = htmlContent.Remove(match.Index, match.Length);
                Log.Normal($"已删除条目 {globalIndex}");
            }
            
            File.WriteAllText(logPath, htmlContent);
            Log.Normal($"已删除 {sortedIndices.Count} 条日志条目");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"删除条目失败: {e.Message}\n堆栈: {e.StackTrace}");
            return false;
        }
    }

    public bool InsertEntry(long groupId, string logName, int globalIndex, string content, long senderId, string senderName, long? userId = null)
    {
        try
        {
            Log.Normal($"InsertEntry 开始: groupId={groupId}, logName={logName}, globalIndex={globalIndex}, userId={userId}");
            
            if (_logWriters.ContainsKey(groupId))
            {
                Log.Warn($"群 {groupId} 的日志正在记录中，无法插入条目");
                return false;
            }

            var logPath = GetLogPath(groupId, logName, userId);
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
            {
                Log.Warn($"日志文件不存在: {logPath}");
                return false;
            }

            string htmlContent = File.ReadAllText(logPath);
            Log.Normal($"日志文件大小: {htmlContent.Length} 字符");
            
            var messageRegex = new Regex(
                @"<div class=""message[^""]*""(?: data-userid=""(\d+)"")?(?: data-entry-index=""(\d+)"")?(?:[^>]*)>(.*?)</div>",
                RegexOptions.Singleline
            );
            var matches = messageRegex.Matches(htmlContent);
            
            if (matches.Count == 0)
            {
                Log.Warn("未找到任何 message div");
                return false;
            }

            if (globalIndex < 1 || globalIndex > matches.Count + 1)
            {
                Log.Warn($"索引 {globalIndex} 超出范围 (1-{matches.Count + 1})");
                return false;
            }

            // 生成新的 message div
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string messageDiv = $"<div class=\"message\" data-userid=\"{senderId}\">\n        <span class=\"timestamp\">{timestamp}</span>\n        <span class=\"sender-name\">{senderName}: </span>\n        <span class=\"content\">{System.Net.WebUtility.HtmlEncode(content)}</span>\n    </div>";
            
            int insertPos;
            if (globalIndex <= matches.Count)
            {
                insertPos = matches[globalIndex - 1].Index;
            }
            else
            {
                // 插入到最后
                insertPos = matches[matches.Count - 1].Index + matches[matches.Count - 1].Length;
            }
            
            htmlContent = htmlContent.Insert(insertPos, messageDiv);
            
            File.WriteAllText(logPath, htmlContent);
            Log.Normal($"已在条目 {globalIndex} 前插入新内容");
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"插入条目失败: {e.Message}\n堆栈: {e.StackTrace}");
            return false;
        }
    }

    public List<Comment> GetComments(long groupId, string logName, int globalEntryIndex)
    {
        try
        {
            var logPath = GetLogPath(groupId, logName, 0); // userId 不影响路径
            if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
            {
                return new List<Comment>();
            }

            string htmlContent = File.ReadAllText(logPath);
            
            // 查找匹配的 message div
            var messageRegex = new Regex(
                $@"<div class=""message[^""]*""(?: data-userid=""\d+"")?(?: data-entry-index=""{globalEntryIndex}"")[^>]*>.*?</div>",
                RegexOptions.Singleline
            );
            var messageMatch = messageRegex.Match(htmlContent);
            
            if (!messageMatch.Success)
            {
                // 回退到位置查找（第 N 个 message div）
                var allMessagesRegex = new Regex(
                    @"<div class=""message[^""]*""[^>]*>.*?</div>",
                    RegexOptions.Singleline
                );
                var allMatches = allMessagesRegex.Matches(htmlContent);
                
                if (globalEntryIndex <= 0 || globalEntryIndex > allMatches.Count)
                {
                    return new List<Comment>();
                }
                
                messageMatch = allMatches[globalEntryIndex - 1]; // 转换为 0-based
            }

            // 从 message div 后提取所有 comment div
            int searchStart = messageMatch.Index + messageMatch.Length;
            var commentRegex = new Regex(@"<div class=""comment""(?: data-commenter=""(\d+)"")?(?: data-commenter-name=""([^""]*)"")?(?: data-comment-time=""([^""]*)"")?>(.*?)</div>", RegexOptions.Singleline);
            var commentMatches = commentRegex.Matches(htmlContent, searchStart);
            
            var comments = new List<Comment>();
            foreach (Match match in commentMatches)
            {
                var comment = new Comment();
                if (long.TryParse(match.Groups[1].Value, out long commenterId))
                    comment.CommenterId = commenterId;
                comment.CommenterName = System.Net.WebUtility.HtmlDecode(match.Groups[2].Value);
                comment.CommentTime = match.Groups[3].Value;
                
                string commentContent = match.Groups[4].Value;
                // 移除前缀 [名称 时间] 如果存在
                var prefixMatch = Regex.Match(commentContent, @"^\[([^\]]+)\] (.+)$");
                if (prefixMatch.Success)
                {
                    comment.Content = System.Net.WebUtility.HtmlDecode(prefixMatch.Groups[2].Value);
                }
                else
                {
                    comment.Content = System.Net.WebUtility.HtmlDecode(commentContent);
                }
                
                comments.Add(comment);
            }
            
            return comments;
        }
        catch (Exception e)
        {
            Log.Warn($"读取备注失败: {e.Message}");
            return new List<Comment>();
        }
    }

    public void Dispose()
    {
        foreach (var writer in _logWriters.Values)
        {
            try
            {
                writer.Close();
                writer.Dispose();
            }
            catch (Exception e)
            {
                Log.Error($"关闭日志写入器时发生错误: {e.Message}");
            }
        }
        _logWriters.Clear();
        _logStarters.Clear();
        _activeLogNames.Clear();
    }
}
