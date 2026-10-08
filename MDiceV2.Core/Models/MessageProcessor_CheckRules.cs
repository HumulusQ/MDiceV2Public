using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace MDiceV2.Models;

public partial class MessageProcessor
{
    private static bool TryParseCheckRule(string input, out string family, out string rule)
    {
        rule = input.Trim().ToLowerInvariant();
        var match = Regex.Match(rule, @"^([a-z]+)([1-9][0-9]*)$");
        family = match.Success ? match.Groups[1].Value : "";
        // 其他体系的选择可独立保存，实际检定须由对应体系实现支持。
        return match.Success && (family != "coc" || rule is "coc1" or "coc2" or "coc3");
    }

    private TeamInfo? GetCheckRuleTeam(Msg msg, long contextUserId)
    {
        if (msg.Source != MessageSource.group
            || !groupDataRecords.TryGetValue(msg.GroupId, out var group)
            || group.UserDefaultTeams == null || group.Teams == null
            || !group.UserDefaultTeams.TryGetValue(contextUserId, out var name)
            || !group.Teams.TryGetValue(name, out var team)
            || !team.Members.Contains(contextUserId))
            return null;
        return team;
    }

    private (string Rule, string Source) ResolveCheckRule(long userId, Msg msg, string family, long? contextUserId = null)
    {
        var team = GetCheckRuleTeam(msg, contextUserId ?? userId);
        if (team?.CheckRules != null && team.CheckRules.TryGetValue(family, out var forced))
            return (forced, $"队伍「{team.TeamName}」强制规则");
        if (userCheckRules.TryGetValue(userId, out var rules) && rules.TryGetValue(family, out var personal))
            return (personal, "个人设置");
        return ($"{family}1", "系统默认");
    }

    private string DescribeUserRules(Msg msg)
    {
        var families = new HashSet<string> { "coc" };
        if (userCheckRules.TryGetValue(msg.UserId, out var rules)) families.UnionWith(rules.Keys);
        var team = GetCheckRuleTeam(msg, msg.UserId);
        if (team?.CheckRules != null) families.UnionWith(team.CheckRules.Keys);
        return string.Join("\n", families.OrderBy(f => f).Select(f =>
        {
            var effective = ResolveCheckRule(msg.UserId, msg, f);
            string personal = rules != null && rules.TryGetValue(f, out var saved) ? saved : "未设置";
            return $"{f} 村规：个人 {personal}；当前 {effective.Rule}（{effective.Source}）";
        }));
    }

    private void HandleUserRuleCommand(string args, Msg msg)
    {
        var parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            Reply(DescribeUserRules(msg) + "\n使用 .cfg rule coc1/coc2/coc3 设置；.help cfg rule 查看规则说明。", msg);
            return;
        }
        if (parts.Length == 2 && parts[0].Equals("reset", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(parts[1], @"^[a-z]+$", RegexOptions.IgnoreCase))
        {
            string family = parts[1].ToLowerInvariant();
            if (userCheckRules.TryGetValue(msg.UserId, out var saved)) saved.TryRemove(family, out _);
            SaveUserData(msg.UserId);
            Reply($"已清除个人 {family} 村规选择。\n{DescribeUserRules(msg)}", msg);
            return;
        }
        if (parts.Length != 1 || !TryParseCheckRule(parts[0], out var system, out var rule))
        {
            Reply("格式错误：.cfg rule coc1/coc2/coc3；清除选择：.cfg rule reset coc。", msg);
            return;
        }
        userCheckRules.GetOrAdd(msg.UserId, _ => new ConcurrentDictionary<string, string>())[system] = rule;
        SaveUserData(msg.UserId);
        Reply($"已保存个人 {system} 村规：{rule}。\n{DescribeUserRules(msg)}" +
            (system == "coc" ? "" : "\n此体系的选择已保存，实际检定需要对应模式支持。"), msg);
    }

    private void HandleTeamRuleCommand(string args, Msg msg)
    {
        var team = GetCheckRuleTeam(msg, msg.UserId);
        if (team == null)
        {
            Reply("请先使用 .team join <队伍名> 或 .team set <队伍名> 选择当前队伍。", msg);
            return;
        }
        var parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            string settings = team.CheckRules == null || team.CheckRules.Count == 0 ? "未设置强制村规，采用个人设置。"
                : string.Join("\n", team.CheckRules.OrderBy(k => k.Key).Select(k => $"{k.Key}：{k.Value}（强制）"));
            Reply($"队伍「{team.TeamName}」村规：\n{settings}\n使用 .team rule coc1/coc2/coc3 设置；.team rule reset coc 取消覆盖。", msg);
            return;
        }
        if (team.CreatorId != msg.UserId && !msg.IsMasterAccount && !msg.IsSystemAccount
            && !msg.IsGroupAdmin && !IsGroupAdministrator(msg.GroupId, msg.UserId))
        {
            Reply("只有队伍创建者、群主/管理员或 Master 可以修改队伍村规。", msg);
            return;
        }
        if (parts.Length == 2 && parts[0].Equals("reset", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(parts[1], @"^[a-z]+$", RegexOptions.IgnoreCase))
        {
            string family = parts[1].ToLowerInvariant();
            team.CheckRules?.Remove(family);
            team.UpdatedAt = DateTime.UtcNow;
            SaveGroupData(msg.GroupId);
            Reply($"已取消队伍「{team.TeamName}」的 {family} 强制村规，恢复采用个人设置。", msg);
            return;
        }
        if (parts.Length != 1 || !TryParseCheckRule(parts[0], out var system, out var rule))
        {
            Reply("格式错误：.team rule coc1/coc2/coc3；取消覆盖：.team rule reset coc。", msg);
            return;
        }
        team.CheckRules ??= new Dictionary<string, string>();
        team.CheckRules[system] = rule;
        team.UpdatedAt = DateTime.UtcNow;
        SaveGroupData(msg.GroupId);
        Reply($"队伍「{team.TeamName}」已强制使用 {rule}，覆盖团内个人 {system} 村规选择。" +
            (system == "coc" ? "" : "\n此体系的选择已保存，实际检定需要对应模式支持。"), msg);
    }
}
