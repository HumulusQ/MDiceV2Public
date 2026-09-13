using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MDiceV2.Models;

public sealed record CocCardUpdateResult(bool Success, string Message, string? FilePath = null);

/// <summary>
/// Downloads the portable CoC investigator card from the same GitHub release and
/// mirror pipeline used by the main-program updater.  This file is data only:
/// no process replacement, reload, or restart is involved.
/// </summary>
public sealed class CocCardUpdateService
{
    private static readonly Regex VersionedAssetNamePattern = new(
        @"^TOTT_portable_CoC7e_investigator_v(?<version>\d+)\.html$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Legacy release asset name accepted for backward compatibility.
    public const string AssetName = "portable_CoC7e_charactercard.html";
    private static readonly SemaphoreSlim UpdateLock = new(1, 1);
    private readonly Action<string> _logger;

    public CocCardUpdateService(Action<string>? logger = null)
    {
        _logger = logger ?? Log.Normal;
    }

    public string LocalFilePath => GetLatestLocalCardFile(GetLocalCardDirectory())
        ?? Path.Combine(GetLocalCardDirectory(), AssetName);

    public string LocalFileName => Path.GetFileName(LocalFilePath);

    public async Task<CocCardUpdateResult> UpdateAsync(
        string owner = "HumulusQ",
        string repo = "MDiceV2Public",
        CancellationToken cancellationToken = default)
    {
        await UpdateLock.WaitAsync(cancellationToken);
        try
        {
            var updater = new CustomUpdateManager(message => _logger($"[CoC人物卡更新] {message}"));
            var releases = await updater.GetGitHubReleasesAsync(owner, repo);
            var releaseAndAsset = releases
                .OrderByDescending(release => release.PublishedAt)
                .Select(release => new
                {
                    Release = release,
                    Asset = release.Assets
                        .Where(asset => IsSupportedAssetName(asset.Name))
                        .OrderByDescending(asset => GetAssetVersion(asset.Name))
                        .FirstOrDefault()
                })
                .FirstOrDefault(item => item.Asset is not null);

            if (releaseAndAsset?.Asset is null)
                return new(false, $"未在 {owner}/{repo} 的发布包中找到 CoC 人物卡 HTML 文件。");

            var directory = GetLocalCardDirectory();
            var localFilePath = Path.Combine(directory, Path.GetFileName(releaseAndAsset.Asset.Name));
            Directory.CreateDirectory(directory);
            _logger($"[CoC人物卡更新] 下载 {releaseAndAsset.Asset.Name} 到 {directory}");
            await updater.DownloadGitHubAssetAsync(
                releaseAndAsset.Asset,
                localFilePath,
                owner,
                repo,
                releaseAndAsset.Release.TagName);

            if (!File.Exists(localFilePath) || new FileInfo(localFilePath).Length == 0)
                return new(false, "人物卡下载完成后未找到有效文件。");

            return new(true, "CoC 人物卡已下载并更新，无需重启程序。", localFilePath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(false, "人物卡下载已取消。");
        }
        catch (Exception ex)
        {
            _logger($"[CoC人物卡更新] 失败: {ex.Message}");
            return new(false, $"人物卡下载失败：{ex.Message}");
        }
        finally
        {
            UpdateLock.Release();
        }
    }

    /// <summary>Returns whether a release asset is a supported portable CoC investigator card.</summary>
    public static bool IsSupportedAssetName(string? name) =>
        string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(name) && VersionedAssetNamePattern.IsMatch(name));

    private static long GetAssetVersion(string name)
    {
        var match = VersionedAssetNamePattern.Match(name);
        return match.Success && long.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : 0;
    }

    private static string? GetLatestLocalCardFile(string directory)
    {
        if (!Directory.Exists(directory)) return null;

        return Directory.EnumerateFiles(directory)
            .Where(path => IsSupportedAssetName(Path.GetFileName(path)))
            .OrderByDescending(path => GetAssetVersion(Path.GetFileName(path)))
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string GetLocalCardDirectory() =>
        Path.Combine(GetApplicationRootDirectory(), "CharacterCards");

    private static string GetApplicationRootDirectory()
    {
        try
        {
            var modulePath = Process.GetCurrentProcess().MainModule?.FileName;
            var directory = Path.GetDirectoryName(modulePath ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(directory) &&
                Path.GetFileName(directory).Equals("Core", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(directory) ?? directory;
            }
            if (!string.IsNullOrWhiteSpace(directory)) return directory;
        }
        catch
        {
        }
        return AppContext.BaseDirectory;
    }
}
