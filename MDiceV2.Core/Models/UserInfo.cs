using System;

namespace MDiceV2.Models;

/// <summary>
/// 用户信息类
/// 表示QQ用户的基本信息
/// </summary>
public class UserInfo
{
    /// <summary>
    /// 用户ID
    /// </summary>
    public long UserId { get; set; }

    /// <summary>
    /// 用户昵称
    /// </summary>
    public string Nickname { get; set; }

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="userId">用户ID</param>
    /// <param name="nickname">用户昵称</param>
    public UserInfo(long userId, string nickname)
    {
        UserId = userId;
        Nickname = nickname ?? throw new ArgumentNullException(nameof(nickname));
    }

    /// <summary>
    /// 默认构造函数
    /// </summary>
    public UserInfo()
    {
        UserId = 0;
        Nickname = string.Empty;
    }

    /// <summary>
    /// 返回字符串表示
    /// </summary>
    public override string ToString()
    {
        return $"{Nickname}({UserId})";
    }
}