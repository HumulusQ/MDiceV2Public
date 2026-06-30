namespace MDiceV2.Models;

/// <summary>
/// 消息类
/// 表示一条聊天消息及其属性
/// </summary>
public class Msg
{
    /// <summary>
    /// 是否为模拟模式
    /// </summary>
    public bool IsSimulationMode { get; set; }

    /// <summary>
    /// 群ID（私聊时为0）
    /// </summary>
    public long GroupId { get; set; }

    /// <summary>
    /// 用户ID
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// 消息内容
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 消息内容的小写版本
    /// </summary>
    public string ContentLower { get; set; } = string.Empty;

    /// <summary>
    /// 消息来源
    /// </summary>
    public MessageSource Source { get; set; }

    /// <summary>
    /// 是否被@了
    /// </summary>
    public bool IsAted { get; set; }

    /// <summary>
    /// 是否应该忽略
    /// </summary>
    public bool ShouldIgnore { get; set; }

    /// <summary>
    /// 来源Mod ID（用于RefineMsg的modId参数）
    /// null表示主程序调用
    /// </summary>
    public string? ModId { get; set; }

    /// <summary>
    /// 回复前置文本（仅在Reply时拼接使用）
    /// </summary>
    public string? ReplyPrefix { get; set; }

    /// <summary>
    /// 是否已预载权限信息
    /// </summary>
    public bool IsAuthInfoLoaded { get; set; }

    /// <summary>
    /// 用户授权等级（null=未设置）
    /// </summary>
    public int? UserAuthLevel { get; set; }

    /// <summary>
    /// 是否为系统账号
    /// </summary>
    public bool IsSystemAccount { get; set; }

    /// <summary>
    /// 是否为Master账号
    /// </summary>
    public bool IsMasterAccount { get; set; }

    /// <summary>
    /// 是否具有系统指令权限
    /// </summary>
    public bool HasAuthPermission { get; set; }

    /// <summary>
    /// 是否为白名单用户
    /// </summary>
    public bool IsWhitelisted { get; set; }

    /// <summary>
    /// 是否为群管理员或群主
    /// </summary>
    public bool IsGroupAdmin { get; set; }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="groupId">群ID</param>
    /// <param name="userId">用户ID</param>
    /// <param name="content">消息内容</param>
    /// <param name="source">消息来源</param>
    /// <param name="isSimulationMode">是否为模拟模式</param>
    /// <param name="isAted">是否被@了</param>
    /// <param name="shouldIgnore">是否应该忽略</param>
    public Msg(long groupId, long userId, string content, MessageSource source,
               bool isSimulationMode = false, bool isAted = false, bool shouldIgnore = false)
    {
        GroupId = groupId;
        UserId = userId;
        Content = content;
        if (!string.IsNullOrEmpty(content))
        {
            var trimmed = content.Trim();
            if (trimmed.StartsWith("。"))
                ContentLower = ("." + trimmed.Substring(1)).ToLower();
            else
                ContentLower = trimmed.ToLower();
        }
        else
        {
            ContentLower = string.Empty;
        }
        Source = source;
        IsSimulationMode = isSimulationMode;
        IsAted = isAted;
        ShouldIgnore = shouldIgnore;
    }
}