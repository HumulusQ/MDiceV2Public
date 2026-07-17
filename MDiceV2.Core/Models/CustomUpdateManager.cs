using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using System.IO.Compression;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Abstractions;

#nullable enable
namespace MDiceV2.Models
{
    /// <summary>
    /// 定制化更新管理器：Release 标题为 UpdatePackageVn，tag 为程序集版本（如 0.2.4.0）。
    /// 1. 获取所有 GitHub releases
    /// 2. 找到标题匹配 UpdatePackageVn 且 tag 可解析为 Version 的最新发布
    /// 3. 下载该版本下的 MDiceV2.Core.Zip（优先）或 MDiceV2.Core.Dice（兼容）
    /// 4. 如果是 Zip 文件，自动解压并删除 Zip；如果是 Dice 文件，直接使用
    /// 5. 保存文件至 temp 目录并生成批处理完成覆盖
    /// </summary>
    public class CustomUpdateManager
    {
        private readonly Action<string> _logger; // 日志输出委托
        private readonly HttpClient _http; // GitHub API 请求客户端（包含 token）
        private readonly HttpClient _httpDownload; // 文件下载客户端（不包含 token）
        private readonly string _tempDir; // 临时目录路径
        private readonly string _appDir; // 应用程序根目录
        private readonly int _timeoutSeconds; // 请求超时时间（秒）
        private static readonly TimeSpan DownloadNoProgressTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan DownloadProgressLogInterval = TimeSpan.FromSeconds(5);
        private const int DownloadBufferSize = 1024 * 128;
        private PendingUpdateScript? _pendingUpdateScript;

        private sealed class PendingUpdateScript
        {
            public PendingUpdateScript(
                string scriptPath,
                string appRoot,
                string coreDirectory,
                string sourceDice,
                string targetDice,
                string targetBackup,
                string restartExecutable,
                string logDirectory,
                string logFile,
                string version)
            {
                ScriptPath = scriptPath;
                AppRoot = appRoot;
                CoreDirectory = coreDirectory;
                SourceDice = sourceDice;
                TargetDice = targetDice;
                TargetBackup = targetBackup;
                RestartExecutable = restartExecutable;
                LogDirectory = logDirectory;
                LogFile = logFile;
                Version = version;
            }

            public string ScriptPath { get; }
            public string AppRoot { get; }
            public string CoreDirectory { get; }
            public string SourceDice { get; }
            public string TargetDice { get; }
            public string TargetBackup { get; }
            public string RestartExecutable { get; }
            public string LogDirectory { get; }
            public string LogFile { get; }
            public string Version { get; }
        }

        /// <summary>
        /// 镜像站配置类：定义所有支持的镜像站
        /// </summary>
        public static class MirrorSites
        {
            /// <summary>
            /// 获取所有支持的镜像站配置，按优先级排序（从高到低）
            /// 返回元组列表：(显示名称, 源标识符, URL 前缀)
            /// </summary>
            public static List<(string DisplayName, string SourceId, string UrlPrefix)> GetAllMirrors()
            {
                return new List<(string, string, string)>
                {
                    // 国内加速镜像站（优先级从高到低）
                    ("ghproxy.net (国内加速)", "ghproxy", "https://ghproxy.net/"),
                    ("FastGit (独立加速)", "fastgit", "https://raw.fastgit.org/"),
                    ("Jihulab (极狐加速)", "jihulab", "https://jihulab.com/api/v4/projects/"),
                    ("GitHub 官方主站", "github", ""),
                };
            }

            /// <summary>
            /// 根据源标识符获取对应的 URL 前缀
            /// </summary>
            public static string GetUrlPrefix(string? sourceId)
            {
                var mirror = GetAllMirrors().FirstOrDefault(m => 
                    m.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
                
                return mirror != default ? mirror.UrlPrefix : ""; // 默认返回官方（空前缀）
            }

            /// <summary>
            /// 获取所有源标识符列表
            /// </summary>
            public static List<string> GetAllSourceIds()
            {
                return GetAllMirrors().Select(m => m.SourceId).ToList();
            }

            /// <summary>
            /// 转换 GitHub 浏览器下载 URL 为指定镜像源的 URL
            /// </summary>
            public static string TransformUrl(string githubUrl, string? sourceId)
            {
                if (string.IsNullOrWhiteSpace(sourceId) || 
                    sourceId.Equals("github", StringComparison.OrdinalIgnoreCase))
                {
                    return githubUrl; // 官方源直接返回原 URL
                }

                // 处理不同镜像站的 URL 转换
                if (sourceId.Equals("ghproxy", StringComparison.OrdinalIgnoreCase))
                {
                    // ghproxy 只需在前面添加代理前缀
                    return $"https://ghproxy.net/{githubUrl}";
                }
                else if (sourceId.Equals("fastgit", StringComparison.OrdinalIgnoreCase))
                {
                    // FastGit: 需要将 GitHub 下载 URL 转换为 FastGit API
                    // 原: https://github.com/user/repo/releases/download/tag/file
                    // 转: https://raw.fastgit.org/user/repo/tag/file
                    var match = Regex.Match(githubUrl, @"github\.com/([^/]+)/([^/]+)/releases/download/([^/]+)/(.+)");
                    if (match.Success)
                    {
                        var owner = match.Groups[1].Value;
                        var repo = match.Groups[2].Value;
                        var tag = match.Groups[3].Value;
                        var filename = match.Groups[4].Value;
                        return $"https://raw.fastgit.org/{owner}/{repo}/{tag}/{filename}";
                    }
                }
                else if (sourceId.Equals("jihulab", StringComparison.OrdinalIgnoreCase))
                {
                    // Jihulab: GitHub 代理服务（完整代理）
                    return githubUrl.Replace("github.com", "jihulab.com");
                }

                return githubUrl; // 未知源返回原 URL
            }
        }


        /// <summary>
        /// 提取版本字符串中的纯数字部分（忽略 -beta 等后缀），返回可用于 Version.Parse 的片段。
        /// </summary>
        private static string? ExtractNumericVersion(string? versionText)
        {
            if (string.IsNullOrWhiteSpace(versionText))
            {
                return null;
            }

            // 仅取开头的数字版号，最多四段，用于程序集版本比较
            var match = Regex.Match(versionText, @"^\s*([0-9]+(?:\.[0-9]+){0,3})");
            return match.Success ? match.Groups[1].Value : null;
        }

        public CustomUpdateManager(Action<string>? logger = null)
        {
            _logger = logger ?? Console.WriteLine;

            // GitHub API客户端（公开仓库，无 Token）
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromSeconds(30);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MDiceV2-CustomUpdater");
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            // 下载客户端（公开仓库，无 Token）
            _httpDownload = new HttpClient();
            _httpDownload.Timeout = Timeout.InfiniteTimeSpan; // 下载改为依赖流式读取与无进度超时
            _httpDownload.DefaultRequestHeaders.UserAgent.ParseAdd("MDiceV2-CustomUpdater");

            // 获取应用根目录
            _appDir = GetApplicationRootDirectory();
            _timeoutSeconds = 30;

            // 使用项目根目录下的 temp 文件夹作为临时目录（避免系统临时目录权限问题）
            _tempDir = Path.Combine(_appDir, "temp");

            // 确保temp目录存在
            try
            {
                if (!Directory.Exists(_tempDir))
                {
                    Directory.CreateDirectory(_tempDir);
                    _logger($"创建temp目录: {_tempDir}");
                }
                else
                {
                    _logger($"使用现有temp目录: {_tempDir}");
                }
            }
            catch (Exception ex)
            {
                _logger($"创建temp目录失败: {ex.Message}");
                // 降级到系统临时目录
                _tempDir = Path.Combine(Path.GetTempPath(), "MDiceV2Update");
                try
                {
                    if (!Directory.Exists(_tempDir))
                    {
                        Directory.CreateDirectory(_tempDir);
                    }
                    _logger($"已降级到系统临时目录: {_tempDir}");
                }
                catch
                {
                    _tempDir = Path.GetTempPath();
                }
            }
        }

        private void Log(string message)
        {
            _logger($"[{DateTime.Now:HH:mm:ss}] {message}");
        }

        /// <summary>
        /// 获取应用根目录（根据实际目录结构）
        /// 如果当前进程在 Core 子目录中，向上走一级
        /// </summary>
        private string GetApplicationRootDirectory()
        {
            try
            {
                var mainModule = Process.GetCurrentProcess().MainModule;
                if (mainModule != null && !string.IsNullOrWhiteSpace(mainModule.FileName))
                {
                    var modulePath = mainModule.FileName;
                    var moduleDir = Path.GetDirectoryName(modulePath);
                    
                    // 如果可执行文件在 Core 子目录中，向上走一级到应用根目录
                    if (!string.IsNullOrWhiteSpace(moduleDir) && 
                        Path.GetFileName(moduleDir).Equals("Core", StringComparison.OrdinalIgnoreCase))
                    {
                        var rootDir = Path.GetDirectoryName(moduleDir);
                        if (!string.IsNullOrWhiteSpace(rootDir))
                        {
                            Log($"检测到在Core子目录运行，应用根目录: {rootDir}");
                            return rootDir;
                        }
                    }
                    
                    return moduleDir ?? AppContext.BaseDirectory;
                }
            }
            catch (Exception ex)
            {
                Log($"获取启动进程路径失败: {ex.Message}，降级到AppContext.BaseDirectory");
            }

            return AppContext.BaseDirectory;
        }

        /// <summary>
        /// 执行定制化更新流程（匹配 UpdatePackageVn 标题，tag=程序集版本）
        /// </summary>
        public async Task<UpdateResult> ExecuteCustomUpdateAsync(string owner = "HumulusQ", string repo = "MDiceV2Public")
        {
            var result = new UpdateResult();

            try
            {
                Log("#开始更新....");

                // 1. 获取所有releases
                Log("获取GitHub releases列表...");
                var allReleases = await GetAllReleasesAsync(owner, repo);

                // 2. 过滤标题 UpdatePackageVn，tag 可解析为 Version 的发布
                Log("筛选 UpdatePackageV* 发布...");
                var updateReleases = allReleases
                    .Select(r => new
                    {
                        Release = r,
                        NumericTag = ExtractNumericVersion(r.TagName)
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Release.Name) && x.Release.Name.StartsWith("UpdatePackageV", StringComparison.OrdinalIgnoreCase))
                    .Where(x => Version.TryParse(x.NumericTag, out _))
                    .OrderByDescending(x => Version.Parse(x.NumericTag!))
                    .ToList();

                if (!updateReleases.Any())
                {
                    result.Success = false;
                    result.Message = "未找到 UpdatePackageV* 发布";
                    Log(result.Message);
                    return result;
                }

                var latestRelease = updateReleases.First();
                var currentVersion = GetCurrentInstalledVersion();
                var latestAssemblyVersion = ParseAssemblyVersion(latestRelease.Release);
                var latestTagNumeric = latestRelease.NumericTag;

                Log($"最新发布: {latestRelease.Release.Name} tag={latestRelease.Release.TagName}");
                Log($"当前安装版本: {currentVersion ?? "未知"}");
                Log($"Release程序集版本: {latestAssemblyVersion ?? "未知"}");

                // 3. 检查是否需要更新（基于程序集版本）
                var targetVersion = latestAssemblyVersion ?? latestTagNumeric ?? latestRelease.Release.TagName;
                if (ShouldSkipByAssemblyVersion(currentVersion, targetVersion))
                {
                    result.Success = true;
                    result.Message = "当前版本已是最新";
                    Log(result.Message);
                    return result;
                }

                // 4. 下载 MDiceV2.Core.Zip 或 MDiceV2.Core.Dice
                Log("查找并下载 MDiceV2.Core 文件...");
                var (coreAsset, isZipFile) = await DownloadCoreDiceFromRelease(latestRelease.Release);

                if (coreAsset == null)
                {
                    result.Success = false;
                    result.Message = "未在最新release中找到 MDiceV2.Core.Zip 或 MDiceV2.Core.Dice";
                    Log(result.Message);
                    return result;
                }

                // 5. 下载文件到 temp 目录
                string tempCorePath;
                if (isZipFile)
                {
                    // 下载 Zip 文件
                    tempCorePath = Path.Combine(_tempDir, "MDiceV2.Core.Zip");
                    Log("下载类型：Zip 压缩包");
                }
                else
                {
                    // 下载 Dice 文件
                    tempCorePath = Path.Combine(_tempDir, "MDiceV2.Core.Dice");
                    Log("下载类型：单一可执行文件");
                }

                await DownloadAsset(coreAsset, tempCorePath, owner, repo, latestRelease.Release.TagName);

                // 6. 如果下载的是 Zip 文件，自动解压
                if (isZipFile)
                {
                    Log("开始解压 Zip 文件...");
                    try
                    {
                        var extractDir = Path.Combine(_tempDir, "MDiceV2.Core.Extract");
                        if (Directory.Exists(extractDir))
                        {
                            Directory.Delete(extractDir, true);
                        }

                        ZipFile.ExtractToDirectory(tempCorePath, extractDir);
                        Log($"✅ Zip 解压完成到: {extractDir}");

                        // 查找解压后的 MDiceV2.Core.Dice 文件
                        var diceFile = Directory.GetFiles(extractDir, "MDiceV2.Core.Dice", SearchOption.AllDirectories).FirstOrDefault();
                        if (diceFile == null)
                        {
                            throw new Exception("解压后未找到 MDiceV2.Core.Dice 文件");
                        }

                        // 将 Dice 文件移动到标准位置
                        var finalDicePath = Path.Combine(_tempDir, "MDiceV2.Core.Dice");
                        if (File.Exists(finalDicePath))
                        {
                            File.Delete(finalDicePath);
                        }

                        File.Move(diceFile, finalDicePath, true);
                        Log($"✅ Dice 文件已提取到: {finalDicePath}");

                        // 删除 Zip 文件和解压临时目录
                        File.Delete(tempCorePath);
                        Directory.Delete(extractDir, true);
                        Log("✅ Zip 文件和临时解压目录已清理");

                        tempCorePath = finalDicePath;
                    }
                    catch (Exception ex)
                    {
                        result.Success = false;
                        result.Message = $"Zip 解压失败: {ex.Message}";
                        Log(result.Message);
                        return result;
                    }
                }

                Log($"文件已保存到temp目录: {tempCorePath}");

                // 7. 生成并启动批处理文件进行延迟替换
                var versionLabel = latestAssemblyVersion ?? latestTagNumeric ?? latestRelease.Release.TagName;
                Log($"版本号更新为: {versionLabel}");

                Log("生成更新批处理文件...");
                var batPath = await GenerateUpdateBatchFile(versionLabel, tempCorePath);

                Log("启动更新进程并退出应用...");
                await LaunchUpdateProcess(batPath);

                result.Success = true;
                result.Message = $"准备更新到版本 {versionLabel}，应用即将重启";

            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"更新过程发生错误: {ex.Message}";
                Log($"更新失败: {ex}");
            }

            return result;
        }

        /// <summary>
        /// 从已经暂存到本地的 QQ 更新包执行 Core 更新，不访问 GitHub release。
        /// </summary>
        public async Task<UpdateResult> ExecuteCustomUpdateFromLocalPackageAsync(
            string packagePath,
            string? expectedVersion = null,
            string? expectedSha256 = null,
            bool requireSha256Match = true)
        {
            var result = new UpdateResult();

            try
            {
                if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
                {
                    result.Success = false;
                    result.Message = $"本地更新包不存在: {packagePath}";
                    Log(result.Message);
                    return result;
                }

                expectedVersion = (expectedVersion ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(expectedVersion))
                {
                    expectedVersion = $"QQPackage-{DateTime.Now:yyyyMMdd-HHmmss}";
                }

                var normalizedExpectedSha = FileHashUtility.NormalizeSha256(expectedSha256 ?? string.Empty);
                if (requireSha256Match && !Regex.IsMatch(normalizedExpectedSha, "^[a-f0-9]{64}$", RegexOptions.IgnoreCase))
                {
                    result.Success = false;
                    result.Message = "预期 SHA256 格式无效";
                    Log(result.Message);
                    return result;
                }

                if (!IsAllowedLocalPackageName(packagePath))
                {
                    result.Success = false;
                    result.Message = "更新包文件名不被允许，仅支持 MDiceV2.Core.Zip / MDiceV2.Core.Dice / MDiceV2.Core.UpdatePackage";
                    Log(result.Message);
                    return result;
                }

                Log($"开始校验本地更新包: {packagePath}");
                var actualSha = await FileHashUtility.ComputeSha256HexAsync(packagePath);
                Log($"本地更新包 SHA256: {actualSha}");

                if (requireSha256Match &&
                    !string.Equals(actualSha, normalizedExpectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    result.Success = false;
                    result.Message = $"SHA256 校验失败。预期 {normalizedExpectedSha}，实际 {actualSha}";
                    Log(result.Message);
                    return result;
                }

                if (!requireSha256Match)
                {
                    Log("当前为受信任本地包模式：SHA256 仅记录，不作为阻止更新条件");
                }

                var tempDicePath = await PrepareLocalCoreDicePackageAsync(packagePath);
                Log($"本地更新包已准备为 Dice: {tempDicePath}");

                Log("生成更新批处理文件...");
                var batPath = await GenerateUpdateBatchFile(expectedVersion, tempDicePath);

                Log("启动更新进程并退出应用...");
                await LaunchUpdateProcess(batPath);

                result.Success = true;
                result.Message = $"准备更新到版本 {expectedVersion}，应用即将重启";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"本地更新包应用失败: {ex.Message}";
                Log($"本地更新包应用失败: {ex}");
            }

            return result;
        }

        private async Task<string> PrepareLocalCoreDicePackageAsync(string packagePath)
        {
            var finalDicePath = Path.Combine(_tempDir, "MDiceV2.Core.Dice");
            var isZip = IsZipPackage(packagePath);

            if (!isZip)
            {
                Log("本地更新包类型：单一 Dice 文件");
                if (Path.GetFullPath(packagePath).Equals(Path.GetFullPath(finalDicePath), StringComparison.OrdinalIgnoreCase))
                {
                    return finalDicePath;
                }

                if (File.Exists(finalDicePath))
                {
                    File.Delete(finalDicePath);
                }

                await using var source = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
                await using var target = new FileStream(finalDicePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
                await source.CopyToAsync(target);
                await target.FlushAsync();
                return finalDicePath;
            }

            Log("本地更新包类型：Zip 压缩包");
            var extractDir = Path.Combine(_tempDir, "MDiceV2.Core.QQUpdateExtract");
            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, true);
            }
            Directory.CreateDirectory(extractDir);

            try
            {
                using var archive = ZipFile.OpenRead(packagePath);
                var diceEntry = archive.Entries.FirstOrDefault(entry =>
                    !string.IsNullOrWhiteSpace(entry.Name) &&
                    entry.Name.Equals("MDiceV2.Core.Dice", StringComparison.OrdinalIgnoreCase));

                if (diceEntry == null)
                {
                    throw new Exception("Zip 包内未找到 MDiceV2.Core.Dice");
                }

                var extractedDicePath = Path.Combine(extractDir, "MDiceV2.Core.Dice");
                var extractRoot = Path.GetFullPath(extractDir);
                var extractedFullPath = Path.GetFullPath(extractedDicePath);
                if (!extractedFullPath.StartsWith(extractRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception("Zip 解压路径校验失败");
                }

                Log($"开始从 Zip 提取 MDiceV2.Core.Dice: {diceEntry.FullName}");
                await using (var output = new FileStream(extractedDicePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
                using (var input = diceEntry.Open())
                {
                    await input.CopyToAsync(output);
                    await output.FlushAsync();
                }

                if (File.Exists(finalDicePath))
                {
                    File.Delete(finalDicePath);
                }

                File.Move(extractedDicePath, finalDicePath, true);
                Log($"MDiceV2.Core.Dice 已提取到: {finalDicePath}");
                return finalDicePath;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(extractDir))
                    {
                        Directory.Delete(extractDir, true);
                    }
                }
                catch (Exception ex)
                {
                    Log($"清理 QQ 更新解压目录失败: {ex.Message}");
                }
            }
        }

        public Task<List<GitHubRelease>> GetGitHubReleasesAsync(string owner, string repo)
        {
            return GetAllReleasesAsync(owner, repo);
        }

        public Task DownloadGitHubAssetAsync(
            GitHubAsset asset,
            string targetPath,
            string owner = "HumulusQ",
            string repo = "MDiceV2Public",
            string? releaseTag = null)
        {
            return DownloadAsset(asset, targetPath, owner, repo, releaseTag);
        }

        public static string ResolveStandardRestartExecutablePath(string appRootDir, StartupMode startupMode)
        {
            return startupMode == StartupMode.Console
                ? Path.Combine(appRootDir, "MDiceV2.Console.exe")
                : Path.Combine(appRootDir, "MDiceV2.Launcher.exe");
        }

        public static ProcessStartInfo CreateStandardUpdateScriptStartInfo(string scriptPath)
        {
            return new ProcessStartInfo
            {
                FileName = scriptPath,
                UseShellExecute = true,
                CreateNoWindow = false
            };
        }

        public static async Task ExitCurrentProcessForExternalUpdateAsync(Action<string>? logger = null)
        {
            logger?.Invoke("应用将在2秒后自动退出...");
            await Task.Delay(2000).ConfigureAwait(false);
            logger?.Invoke("正在退出应用以完成更新...");
            Environment.Exit(0);
        }

        private static bool IsAllowedLocalPackageName(string packagePath)
        {
            var name = Path.GetFileName(packagePath);
            return name.Equals("MDiceV2.Core.Zip", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("MDiceV2.Core.Dice", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("MDiceV2.Core.UpdatePackage", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_MDiceV2.Core.Zip", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_MDiceV2.Core.Dice", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_MDiceV2.Core.UpdatePackage", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsZipPackage(string packagePath)
        {
            var name = Path.GetFileName(packagePath);
            if (name.EndsWith("MDiceV2.Core.Zip", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (name.EndsWith("MDiceV2.Core.Dice", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Span<byte> header = stackalloc byte[4];
            using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var read = stream.Read(header);
            return read >= 4 &&
                   header[0] == 0x50 &&
                   header[1] == 0x4B &&
                   header[2] == 0x03 &&
                   header[3] == 0x04;
        }

        /// <summary>
        /// 获取所有releases（带速率限制保护和重试机制）
        /// </summary>
        private async Task<List<GitHubRelease>> GetAllReleasesAsync(string owner, string repo)
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/releases";
            Log($"访问GitHub API: {url}");

            int maxRetries = 3;
            int delayBetweenRetries = 3000; // 3秒

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    Log($"[获取releases 尝试 {attempt}/{maxRetries}]");

                    // 添加请求之间的延迟，避免触发GitHub API速率限制
                    await Task.Delay(1000); // 1秒延迟

                    var response = await _http.GetAsync(url);
                    Log($"GitHub API响应状态: {response.StatusCode}");

                    if (!response.IsSuccessStatusCode)
                    {
                        // 如果是429错误，提供特定的错误信息
                        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60;
                            Log($"GitHub API速率限制: 请等待 {retryAfter} 秒后重试");
                            
                            if (attempt < maxRetries)
                            {
                                int totalWait = (int)(retryAfter * 1000);
                                Log($"将在 {totalWait}ms 后重试...");
                                await Task.Delay(totalWait);
                                continue;
                            }
                            
                            throw new Exception($"GitHub API请求过于频繁，请稍后再试。建议间隔: {retryAfter}秒");
                        }

                        // 5xx 错误可以重试
                        if ((int)response.StatusCode >= 500)
                        {
                            Log($"服务器错误 ({response.StatusCode})，将在 {delayBetweenRetries}ms 后重试...");
                            if (attempt < maxRetries)
                            {
                                await Task.Delay(delayBetweenRetries);
                                continue;
                            }
                        }

                        var errorContent = await response.Content.ReadAsStringAsync();
                        Log($"GitHub API错误内容: {errorContent}");
                        throw new Exception($"获取releases失败: {response.StatusCode} - {errorContent}");
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    Log($"获取到JSON数据长度: {json.Length} 字符");

                    // 使用更健壮的JSON反序列化
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        AllowTrailingCommas = true,
                        PropertyNamingPolicy = null // 保持原大小写
                    };

                    var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, options);
                    if (releases == null)
                    {
                        Log("JSON反序列化结果为空");
                        return new List<GitHubRelease>();
                    }

                    Log($"✅ 成功获取 {releases.Count} 个releases");

                    // 为每个release添加延迟，避免在获取assets时触发速率限制
                    foreach (var release in releases)
                    {
                        await Task.Delay(200); // 每个release 200ms延迟
                    }

                    return releases;
                }
                catch (HttpRequestException ex)
                {
                    Log($"❌ 网络请求异常 (尝试 {attempt}/{maxRetries}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        Log($"将在 {delayBetweenRetries}ms 后重试...");
                        await Task.Delay(delayBetweenRetries);
                        continue;
                    }
                    throw new Exception($"网络连接失败，已尝试 {maxRetries} 次: {ex.Message}", ex);
                }
                catch (TaskCanceledException ex)
                {
                    Log($"⏱️ 请求超时 (尝试 {attempt}/{maxRetries}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        Log($"将在 {delayBetweenRetries}ms 后重试...");
                        await Task.Delay(delayBetweenRetries);
                        continue;
                    }
                    throw new Exception($"获取GitHub releases超时，已尝试 {maxRetries} 次（设置: {_timeoutSeconds}秒）", ex);
                }
                catch (Exception ex)
                {
                    Log($"获取releases时发生错误 (尝试 {attempt}/{maxRetries}): {ex}");
                    if (attempt < maxRetries)
                    {
                        Log($"将在 {delayBetweenRetries}ms 后重试...");
                        await Task.Delay(delayBetweenRetries);
                        continue;
                    }
                    throw new Exception($"获取GitHub releases失败，已尝试 {maxRetries} 次: {ex.Message}", ex);
                }
            }

            throw new Exception($"获取releases已达到最大重试次数");
        }

        /// <summary>
        /// 获取当前安装的版本
        /// </summary>
        private string? GetCurrentInstalledVersion()
        {
            try
            {
                var dicePath = Path.Combine(_appDir, "Core", "MDiceV2.Core.Dice");
                if (File.Exists(dicePath))
                {
                    var info = FileVersionInfo.GetVersionInfo(dicePath);
                    var candidate = info.ProductVersion;
                    if (string.IsNullOrWhiteSpace(candidate))
                    {
                        candidate = info.FileVersion;
                    }

                    if (!string.IsNullOrWhiteSpace(candidate) && !candidate.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        /// <summary>
        /// 从 release 的 body 中解析程序集版本（格式: AssemblyVersion=1.2.3.4）
        /// </summary>
        private string? ParseAssemblyVersion(GitHubRelease release)
        {
            try
            {
                var body = release?.Body;
                if (string.IsNullOrWhiteSpace(body))
                {
                    return null;
                }
                var match = Regex.Match(
                    body,
                    @"AssemblyVersion *= *([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)",
                    RegexOptions.IgnoreCase
                );
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim();
                }
            }
            catch (Exception ex)
            {
                Log($"解析 release 程序集版本失败: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// 判断是否需要更新：基于程序集版本比对
        /// </summary>
        private bool ShouldSkipByAssemblyVersion(string? currentVersion, string? latestVersion)
        {
            var currentNumeric = ExtractNumericVersion(currentVersion);
            var latestNumeric = ExtractNumericVersion(latestVersion);

            // 优先用程序集版本对比（只用数字部分，忽略 -beta 等后缀）
            if (Version.TryParse(currentNumeric, out var curAsm) && Version.TryParse(latestNumeric, out var latestAsm))
            {
                if (latestAsm <= curAsm)
                {
                    Log($"程序集版本已最新: current={curAsm}, latest={latestAsm}");
                    return true;
                }
                return false;
            }
            return false;
        }

        /// <summary>
        /// 从 release 中下载 MDiceV2.Core 文件（优先 Zip，兼容 Dice）
        /// </summary>
        private async Task<(GitHubAsset? Asset, bool IsZipFile)> DownloadCoreDiceFromRelease(GitHubRelease release)
        {
            // 优先查找 MDiceV2.Core.Zip（新的压缩包格式）
            var zipAsset = release.Assets?.FirstOrDefault(a =>
                a.Name.Equals("MDiceV2.Core.Zip", StringComparison.OrdinalIgnoreCase));

            if (zipAsset != null)
            {
                Log($"✅ 优先发现 MDiceV2.Core.Zip 资源（大小: {zipAsset.Size} bytes）");
                return (zipAsset, true); // 返回 Zip 资源标记
            }

            // 兼容模式：查找 MDiceV2.Core.Dice（旧的单文件格式）
            var diceAsset = release.Assets?.FirstOrDefault(a =>
                a.Name.Equals("MDiceV2.Core.Dice", StringComparison.OrdinalIgnoreCase));

            if (diceAsset != null)
            {
                Log($"⚠️  未发现 Zip 文件，将使用兼容模式下载 MDiceV2.Core.Dice（大小: {diceAsset.Size} bytes）");
                return (diceAsset, false); // 返回 Dice 资源标记
            }

            Log("❌ 在 release 中既未找到 MDiceV2.Core.Zip 也未找到 MDiceV2.Core.Dice");
            return (null, false);
        }


        /// <summary>
        /// 下载文件到指定路径（支持多个镜像站，优先从镜像站下载）
        /// </summary>
        private async Task DownloadAsset(
            GitHubAsset asset,
            string targetPath,
            string owner,
            string repo,
            string? releaseTag = null)
        {
            try
            {
                Log($"开始下载文件: {asset.Name} ({asset.Size} bytes)");

                // 获取用户设置的首选下载源
                var preferredSource = GlobalFeedbackMessages.GetBasicSetting("UpdateSource") ?? "github";
                
                // 构建镜像站优先级列表：首选的优先，然后是其他镜像站，最后是官方源
                var allSources = MirrorSites.GetAllSourceIds();
                var downloadSourceOrder = new List<string>();

                // 1. 添加首选源到优先级列表
                if (!string.IsNullOrWhiteSpace(preferredSource))
                {
                    downloadSourceOrder.Add(preferredSource);
                }

                // 2. 添加所有其他镜像站（排除官方源）
                downloadSourceOrder.AddRange(allSources.Where(s => 
                    s != preferredSource && !s.Equals("github", StringComparison.OrdinalIgnoreCase)));

                // 3. 最后才是官方源
                downloadSourceOrder.Add("github");

                Log($"📥 下载源优先级: {string.Join(" → ", downloadSourceOrder)}");

                // 尝试按优先级从各个源下载
                Exception? lastException = null;
                foreach (var source in downloadSourceOrder)
                {
                    try
                    {
                        Log($"尝试从 {source} 下载...");
                        await DownloadAssetViaBrowserUrl(asset, targetPath, source, releaseTag);
                        Log($"✅ 从 {source} 下载成功");
                        return; // 成功，直接返回
                    }
                    catch (Exception ex)
                    {
                        Log($"⚠️  从 {source} 下载失败: {ex.Message}");
                        lastException = ex;
                        // 继续尝试下一个源
                    }
                }

                // 如果所有源都失败，尝试 API 下载作为最后手段
                if (lastException != null)
                {
                    Log("所有镜像站下载均失败，尝试 API 下载...");
                    await DownloadAssetViaApi(asset, targetPath, owner, repo);
                }
            }
            catch (Exception ex)
            {
                Log($"❌ 文件下载失败: {ex.Message}");
                throw new Exception($"无法从任何源下载文件: {ex.Message}", ex);
            }
        }


        /// <summary>
        /// 使用GitHub API下载文件（带速率限制保护和重试机制）
        /// </summary>
        private async Task DownloadAssetViaApi(GitHubAsset asset, string targetPath, string owner, string repo)
        {
            int maxRetries = 3;
            int delayBetweenRetries = 3000; // 3秒
            DownloadFailureException? lastFailure = null;

            // 使用反射获取asset的id属性
            var assetId = GetAssetId(asset);
            if (assetId <= 0)
            {
                throw new Exception("无法获取 Asset ID，跳过 API 下载");
            }

            var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/assets/{assetId}";

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    Log($"[API 尝试 {attempt}/{maxRetries}] 使用GitHub API下载: {apiUrl}");

                    // 添加下载前延迟，避免触发速率限制
                    await Task.Delay(2000); // 2秒延迟

                    await DownloadToPartFileAndVerifyAsync(
                        asset,
                        targetPath,
                        sourceId: "api",
                        sourceDisplay: "GitHub API",
                        finalUrl: apiUrl,
                        configureRequest: request =>
                        {
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                        });

                    Log($"✅ API下载成功 (尝试 {attempt}/{maxRetries})，文件大小验证通过！");
                    return;
                }
                catch (DownloadFailureException ex)
                {
                    lastFailure = ex;
                    LogDownloadFailure(ex.Info);

                    if (attempt < maxRetries && ex.Info.IsRetryable)
                    {
                        Log($"将在 {delayBetweenRetries}ms 后重试...");
                        await Task.Delay(delayBetweenRetries);
                        continue;
                    }

                    throw new Exception($"GitHub API 下载失败: {ex.Info.Message}", ex);
                }
                catch (Exception ex)
                {
                    Log($"API下载异常: {ex.Message}");
                    throw;
                }
            }

            if (lastFailure != null)
            {
                throw new Exception($"GitHub API 下载失败: {lastFailure.Info.Message}", lastFailure);
            }
        }

        /// <summary>
        /// 使用浏览器下载 URL 下载文件（支持多个镜像站加速）
        /// </summary>
        private async Task DownloadAssetViaBrowserUrl(GitHubAsset asset, string targetPath, string? sourceId, string? releaseTag = null)
        {
            int maxRetries = 3;
            int delayBetweenRetries = 2000; // 2 秒

            var rawUrl = asset.BrowserDownloadUrl;
            string finalUrl;
            string sourceDisplay;

            // 使用 MirrorSites 类进行 URL 转换
            if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Equals("github", StringComparison.OrdinalIgnoreCase))
            {
                finalUrl = rawUrl;
                sourceDisplay = "GitHub 官方主站";
            }
            else
            {
                finalUrl = MirrorSites.TransformUrl(rawUrl, sourceId);
                var mirrorInfo = MirrorSites.GetAllMirrors().FirstOrDefault(m => m.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));
                sourceDisplay = string.IsNullOrWhiteSpace(mirrorInfo.DisplayName) ? sourceId : mirrorInfo.DisplayName;
            }

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    Log($"[尝试 {attempt}/{maxRetries}] 从 {sourceDisplay} 下载: {finalUrl}");

                    await DownloadToPartFileAndVerifyAsync(
                        asset,
                        targetPath,
                        sourceId ?? "github",
                        sourceDisplay,
                        finalUrl,
                        configureRequest: request =>
                        {
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                        });

                    var fileInfo = new FileInfo(targetPath);
                    Log($"✅ 下载成功 (尝试 {attempt}/{maxRetries}): {targetPath} ({fileInfo.Length} bytes)");
                    return; // 成功，退出
                }
                catch (DownloadFailureException ex)
                {
                    LogDownloadFailure(ex.Info);
                    if (attempt < maxRetries && ex.Info.IsRetryable)
                    {
                        Log($"将在 {delayBetweenRetries}ms 后重试...");
                        await Task.Delay(delayBetweenRetries);
                        continue;
                    }

                    throw new Exception($"下载方法失败: {ex.Info.Message}", ex);
                }
                catch (Exception ex)
                {
                    Log($"下载异常 (尝试 {attempt}/{maxRetries}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        Log($"将在 {delayBetweenRetries}ms 后重试...");
                        await Task.Delay(delayBetweenRetries);
                        continue;
                    }
                    throw new Exception($"下载方法失败: {ex.Message}", ex);
                }
            }
        }

        private async Task DownloadToPartFileAndVerifyAsync(
            GitHubAsset asset,
            string targetPath,
            string sourceId,
            string sourceDisplay,
            string finalUrl,
            Action<HttpRequestMessage>? configureRequest = null,
            CancellationToken cancellationToken = default)
        {
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
                Log($"创建目录: {targetDir}");
            }

            var partPath = targetPath + ".part";

            while (true)
            {
                var partLength = GetFileLengthSafe(partPath);
                if (partLength > asset.Size)
                {
                    Log($"检测到 .part 大于预期大小，删除后重下: {partPath} ({partLength} > {asset.Size})");
                    SafeDeleteFile(partPath, "part 大于预期大小");
                    partLength = 0;
                }

                var failureInfo = CreateDownloadFailureInfo(sourceId, sourceDisplay, finalUrl, asset.Size, partLength);

                if (partLength == asset.Size && partLength > 0)
                {
                    Log($"检测到完整 .part 文件，直接校验: {partPath} ({partLength} bytes)");
                    await FinalizeDownloadedPartFileAsync(asset, targetPath, partPath, failureInfo, contentType: null, cancellationToken);
                    return;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, finalUrl);
                if (partLength > 0 && partLength < asset.Size)
                {
                    request.Headers.Range = new RangeHeaderValue(partLength, null);
                    failureInfo.RangeRequested = true;
                    Log($"检测到可续传 .part 文件，尝试断点续传: {partLength}/{asset.Size} bytes");
                }

                if (configureRequest != null)
                {
                    configureRequest(request);
                }
                else
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                }

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(DownloadNoProgressTimeout);

                HttpResponseMessage response;
                try
                {
                    response = await _httpDownload.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        linkedCts.Token);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    failureInfo.Stage = "headers";
                    failureInfo.IsRetryable = true;
                    failureInfo.CurrentPartSize = GetFileLengthSafe(partPath);
                    failureInfo.Message = $"等待响应头期间连续 {DownloadNoProgressTimeout.TotalSeconds:0} 秒无进度";
                    throw new DownloadFailureException(failureInfo, ex);
                }
                catch (HttpRequestException ex)
                {
                    failureInfo.Stage = "headers";
                    failureInfo.IsRetryable = true;
                    failureInfo.CurrentPartSize = GetFileLengthSafe(partPath);
                    failureInfo.Message = ex.Message;
                    throw new DownloadFailureException(failureInfo, ex);
                }
                catch (Exception ex)
                {
                    failureInfo.Stage = "headers";
                    failureInfo.IsRetryable = false;
                    failureInfo.CurrentPartSize = GetFileLengthSafe(partPath);
                    failureInfo.Message = ex.Message;
                    throw new DownloadFailureException(failureInfo, ex);
                }

                using (response)
                {
                    failureInfo.StatusCode = response.StatusCode;
                    failureInfo.ContentType = response.Content.Headers.ContentType?.ToString();
                    failureInfo.ContentLength = response.Content.Headers.ContentLength;
                    failureInfo.ReceivedPartialContent = response.StatusCode == HttpStatusCode.PartialContent;

                    Log($"下载响应状态: {response.StatusCode}, Content-Type: {failureInfo.ContentType ?? "未知"}, Content-Length: {failureInfo.ContentLength?.ToString() ?? "未知"}");

                    if (failureInfo.RangeRequested && response.StatusCode == HttpStatusCode.OK)
                    {
                        Log("服务器未接受 Range，删除 .part 后改为全量重下");
                        SafeDeleteFile(partPath, "服务器未接受 Range");
                        continue;
                    }

                    if (failureInfo.RangeRequested && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    {
                        Log("服务器返回 416 Range Not Satisfiable，删除 .part 后改为全量重下");
                        SafeDeleteFile(partPath, "服务器返回 416");
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        failureInfo.Stage = "headers";
                        failureInfo.IsRetryable = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                        failureInfo.CurrentPartSize = GetFileLengthSafe(partPath);
                        failureInfo.Message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                        throw new DownloadFailureException(failureInfo);
                    }

                    if (IsErrorPageContentType(response.Content.Headers.ContentType) && !IsHtmlAsset(asset))
                    {
                        failureInfo.Stage = "headers";
                        failureInfo.IsRetryable = false;
                        failureInfo.CurrentPartSize = GetFileLengthSafe(partPath);
                        failureInfo.Message = $"响应 Content-Type 可疑: {failureInfo.ContentType}";
                        throw new DownloadFailureException(failureInfo);
                    }

                    var appendToPart = failureInfo.RangeRequested && failureInfo.ReceivedPartialContent;
                    var downloadedSize = appendToPart ? partLength : 0;

                    try
                    {
                        await using var responseStream = await response.Content.ReadAsStreamAsync(linkedCts.Token);
                        await using var fileStream = new FileStream(
                            partPath,
                            appendToPart ? FileMode.Append : FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            DownloadBufferSize,
                            useAsync: true);

                        if (appendToPart)
                        {
                            fileStream.Seek(0, SeekOrigin.End);
                        }

                        var buffer = new byte[DownloadBufferSize];
                        var lastProgressLogAt = DateTime.UtcNow;

                        while (true)
                        {
                            int read;
                            try
                            {
                                read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), linkedCts.Token);
                            }
                            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                            {
                                failureInfo.Stage = "stream";
                                failureInfo.IsRetryable = true;
                                failureInfo.CurrentPartSize = GetFileLengthSafe(partPath);
                                failureInfo.Message = $"下载流连续 {DownloadNoProgressTimeout.TotalSeconds:0} 秒无进度";
                                throw new DownloadFailureException(failureInfo, ex);
                            }

                            if (read == 0)
                            {
                                break;
                            }

                            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                            downloadedSize += read;
                            linkedCts.CancelAfter(DownloadNoProgressTimeout);

                            if (DateTime.UtcNow - lastProgressLogAt >= DownloadProgressLogInterval)
                            {
                                var percent = asset.Size > 0 ? downloadedSize * 100d / asset.Size : 0d;
                                Log($"下载进度[{sourceDisplay}]: {downloadedSize}/{asset.Size} bytes ({percent:F1}%)");
                                lastProgressLogAt = DateTime.UtcNow;
                            }
                        }

                        await fileStream.FlushAsync(cancellationToken);
                        failureInfo.CurrentPartSize = downloadedSize;
                    }
                    catch (DownloadFailureException)
                    {
                        throw;
                    }
                    catch (HttpRequestException ex)
                    {
                        failureInfo.Stage = "stream";
                        failureInfo.IsRetryable = true;
                        failureInfo.CurrentPartSize = GetFileLengthSafe(partPath);
                        failureInfo.Message = ex.Message;
                        throw new DownloadFailureException(failureInfo, ex);
                    }
                    catch (IOException ex)
                    {
                        failureInfo.Stage = "stream";
                        failureInfo.IsRetryable = false;
                        failureInfo.CurrentPartSize = GetFileLengthSafe(partPath);
                        failureInfo.Message = ex.Message;
                        throw new DownloadFailureException(failureInfo, ex);
                    }
                    catch (Exception ex)
                    {
                        failureInfo.Stage = "stream";
                        failureInfo.IsRetryable = false;
                        failureInfo.CurrentPartSize = GetFileLengthSafe(partPath);
                        failureInfo.Message = ex.Message;
                        throw new DownloadFailureException(failureInfo, ex);
                    }

                    await FinalizeDownloadedPartFileAsync(
                        asset,
                        targetPath,
                        partPath,
                        failureInfo,
                        response.Content.Headers.ContentType,
                        cancellationToken);

                    return;
                }
            }
        }

        private async Task FinalizeDownloadedPartFileAsync(
            GitHubAsset asset,
            string targetPath,
            string partPath,
            DownloadFailureInfo failureInfo,
            MediaTypeHeaderValue? contentType,
            CancellationToken cancellationToken)
        {
            try
            {
                await VerifyDownloadedPartFileAsync(asset, partPath, failureInfo, contentType, cancellationToken);
                File.Move(partPath, targetPath, true);
            }
            catch (DownloadFailureException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failureInfo.Stage = "verify";
                failureInfo.IsRetryable = false;
                failureInfo.CurrentPartSize = GetFileLengthSafe(partPath);
                failureInfo.Message = $"移动已验证文件失败: {ex.Message}";
                throw new DownloadFailureException(failureInfo, ex);
            }
        }

        private async Task VerifyDownloadedPartFileAsync(
            GitHubAsset asset,
            string partPath,
            DownloadFailureInfo failureInfo,
            MediaTypeHeaderValue? contentType,
            CancellationToken cancellationToken)
        {
            failureInfo.Stage = "verify";

            if (!File.Exists(partPath))
            {
                failureInfo.IsRetryable = true;
                failureInfo.CurrentPartSize = 0;
                failureInfo.Message = "下载完成后未找到 .part 文件";
                throw new DownloadFailureException(failureInfo);
            }

            var fileInfo = new FileInfo(partPath);
            failureInfo.CurrentPartSize = fileInfo.Length;

            if (fileInfo.Length != asset.Size)
            {
                failureInfo.IsRetryable = true;
                failureInfo.Message = $"文件大小不匹配，预期 {asset.Size}，实际 {fileInfo.Length}";
                if (fileInfo.Length > asset.Size)
                {
                    SafeDeleteFile(partPath, "下载文件超过预期大小");
                }
                throw new DownloadFailureException(failureInfo);
            }

            if (fileInfo.Length <= 0)
            {
                SafeDeleteFile(partPath, "下载文件为空");
                failureInfo.IsRetryable = true;
                failureInfo.Message = "下载文件为空";
                throw new DownloadFailureException(failureInfo);
            }

            var headerLength = (int)Math.Min(fileInfo.Length, 512);
            var header = new byte[headerLength];

            await using (var stream = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                var read = await stream.ReadAsync(header.AsMemory(0, header.Length), cancellationToken);
                if (read <= 0)
                {
                    SafeDeleteFile(partPath, "无法读取下载文件头");
                    failureInfo.IsRetryable = true;
                    failureInfo.Message = "无法读取下载文件头";
                    throw new DownloadFailureException(failureInfo);
                }

                if (LooksLikeErrorPayload(asset, contentType, header, read, out var errorReason))
                {
                    SafeDeleteFile(partPath, errorReason);
                    failureInfo.IsRetryable = false;
                    failureInfo.Message = errorReason;
                    throw new DownloadFailureException(failureInfo);
                }

                if (asset.Name.Equals("MDiceV2.Core.Zip", StringComparison.OrdinalIgnoreCase))
                {
                    if (read < 4 || header[0] != 0x50 || header[1] != 0x4B || header[2] != 0x03 || header[3] != 0x04)
                    {
                        SafeDeleteFile(partPath, "Zip 文件头校验失败");
                        failureInfo.IsRetryable = false;
                        failureInfo.Message = "Zip 文件头校验失败，缺少 PK\\x03\\x04 magic";
                        throw new DownloadFailureException(failureInfo);
                    }
                }
                else if (asset.Name.Equals("MDiceV2.Core.Dice", StringComparison.OrdinalIgnoreCase))
                {
                    if (fileInfo.Length < 1024)
                    {
                        SafeDeleteFile(partPath, "Dice 文件大小异常");
                        failureInfo.IsRetryable = false;
                        failureInfo.Message = $"Dice 文件大小异常: {fileInfo.Length} bytes";
                        throw new DownloadFailureException(failureInfo);
                    }
                }
            }
        }

        private DownloadFailureInfo CreateDownloadFailureInfo(
            string sourceId,
            string sourceDisplay,
            string finalUrl,
            long expectedSize,
            long currentPartSize)
        {
            return new DownloadFailureInfo
            {
                SourceId = sourceId,
                SourceDisplay = sourceDisplay,
                FinalUrl = finalUrl,
                Host = GetUrlHost(finalUrl),
                ExpectedSize = expectedSize,
                CurrentPartSize = currentPartSize,
                Stage = "headers",
            };
        }

        private void LogDownloadFailure(DownloadFailureInfo info)
        {
            Log(
                $"下载源失败 | sourceId={info.SourceId} | sourceDisplay={info.SourceDisplay} | host={info.Host} | " +
                $"status={(info.StatusCode.HasValue ? $"{(int)info.StatusCode.Value} {info.StatusCode.Value}" : "未知")} | " +
                $"contentType={info.ContentType ?? "未知"} | contentLength={info.ContentLength?.ToString() ?? "未知"} | " +
                $"rangeRequested={info.RangeRequested} | received206={info.ReceivedPartialContent} | " +
                $"partSize={info.CurrentPartSize} | expectedSize={info.ExpectedSize} | stage={info.Stage} | " +
                $"url={info.FinalUrl} | message={info.Message}");
        }

        private static string GetUrlHost(string finalUrl)
        {
            return Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri) ? uri.Host : "未知";
        }

        private static long GetFileLengthSafe(string path)
        {
            try
            {
                return File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        private void SafeDeleteFile(string path, string reason)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    Log($"已删除无效文件: {path} ({reason})");
                }
            }
            catch (Exception ex)
            {
                Log($"删除无效文件失败: {path}, reason={reason}, error={ex.Message}");
            }
        }

        private static bool IsErrorPageContentType(MediaTypeHeaderValue? contentType)
        {
            var mediaType = contentType?.MediaType;
            return mediaType != null &&
                   (mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsHtmlAsset(GitHubAsset asset) =>
            asset.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            asset.Name.EndsWith(".htm", StringComparison.OrdinalIgnoreCase);

        private static bool LooksLikeErrorPayload(
            GitHubAsset asset,
            MediaTypeHeaderValue? contentType,
            byte[] header,
            int read,
            out string reason)
        {
            reason = string.Empty;

            if (read <= 0)
            {
                reason = "下载文件头为空";
                return true;
            }

            var isHtmlAsset = IsHtmlAsset(asset);

            // HTML is a valid release asset for the portable CoC card. Its
            // expected size has already been verified before this check, so do
            // not confuse a valid <!doctype html> document with an error page.
            if (IsErrorPageContentType(contentType) && !isHtmlAsset)
            {
                reason = $"响应 Content-Type 可疑: {contentType}";
                return true;
            }

            var prefix = Encoding.UTF8.GetString(header, 0, read)
                .TrimStart('\uFEFF', '\0', ' ', '\t', '\r', '\n')
                .ToLowerInvariant();

            if (prefix.StartsWith("{\"message\":", StringComparison.Ordinal) ||
                prefix.StartsWith("{\"message\"", StringComparison.Ordinal) ||
                prefix.StartsWith("{\"error\":", StringComparison.Ordinal) ||
                prefix.StartsWith("{\"documentation_url\"", StringComparison.Ordinal) ||
                prefix.StartsWith("404:", StringComparison.Ordinal) ||
                prefix.StartsWith("403:", StringComparison.Ordinal))
            {
                reason = "下载内容看起来像 HTML/JSON/文本错误页";
                return true;
            }

            if (!isHtmlAsset &&
                (prefix.StartsWith("<html", StringComparison.Ordinal) ||
                 prefix.StartsWith("<!doctype", StringComparison.Ordinal)))
            {
                reason = "下载内容看起来像 HTML 错误页";
                return true;
            }

            if (!isHtmlAsset &&
                contentType?.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true &&
                (prefix.Contains("not found", StringComparison.Ordinal) ||
                 prefix.Contains("error", StringComparison.Ordinal) ||
                 prefix.Contains("forbidden", StringComparison.Ordinal) ||
                 prefix.Contains("bad gateway", StringComparison.Ordinal) ||
                 prefix.Contains("access denied", StringComparison.Ordinal)))
            {
                reason = "下载内容看起来像文本错误页";
                return true;
            }

            return false;
        }

        private sealed class DownloadFailureException : Exception
        {
            public DownloadFailureException(DownloadFailureInfo info, Exception? innerException = null)
                : base(info.Message, innerException)
            {
                Info = info;
            }

            public DownloadFailureInfo Info { get; }
        }

        private sealed class DownloadFailureInfo
        {
            public string SourceId { get; set; } = string.Empty;
            public string SourceDisplay { get; set; } = string.Empty;
            public string FinalUrl { get; set; } = string.Empty;
            public string Host { get; set; } = string.Empty;
            public string Stage { get; set; } = "headers";
            public HttpStatusCode? StatusCode { get; set; }
            public string? ContentType { get; set; }
            public long? ContentLength { get; set; }
            public bool RangeRequested { get; set; }
            public bool ReceivedPartialContent { get; set; }
            public long CurrentPartSize { get; set; }
            public long ExpectedSize { get; set; }
            public string Message { get; set; } = string.Empty;
            public bool IsRetryable { get; set; }
        }


        /// <summary>
        /// 使用反射获取GitHubAsset的id属性
        /// </summary>
        private int GetAssetId(GitHubAsset asset)
        {
            try
            {
                var idProperty = typeof(GitHubAsset).GetProperty("id", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (idProperty != null && idProperty.CanRead)
                {
                    var idValue = idProperty.GetValue(asset);
                    if (idValue is int id && id > 0)
                    {
                        return id;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"获取Asset ID时出错: {ex.Message}");
            }

            return 0;
        }

        /// <summary>
        /// 生成更新批处理文件
        /// </summary>
    private async Task<string> GenerateUpdateBatchFile(string version, string tempDicePath)
    {
        var batFileName = $"MDiceV2_Update_{version}_{Guid.NewGuid():N}.bat";
        var batPath = Path.Combine(_tempDir, batFileName);

        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var currentPid = currentProcess.Id;
            
            // 根据启动模式选择重启哪个可执行文件
            var startupMode = ServiceBootstrapper.CurrentStartupMode;
            var appRootDir = GetApplicationRootDirectory();
            var exePath = ResolveStandardRestartExecutablePath(appRootDir, startupMode);
            
            // 核心应用 .Dice 文件的路径（在 Core 子目录中）
            var coreSubDir = Path.Combine(appRootDir, "Core");
            var targetDicePath = Path.Combine(coreSubDir, "MDiceV2.Core.Dice");
            var targetDiceBackup = Path.Combine(coreSubDir, "MDiceV2.Core.Dice.bak");
            var logDirectory = Path.Combine(appRootDir, "logs");
            var logFileName = $"update-{DateTime.Now:yyyyMMdd-HHmmss}-{currentPid}.log";
            var updateLogPath = Path.Combine(logDirectory, logFileName);

            // Create this before shutting down. The batch file will also retry so that
            // a read-only/broken installation is recorded as the first failure.
            Directory.CreateDirectory(logDirectory);
            await File.AppendAllTextAsync(
                updateLogPath,
                $"[{DateTime.Now:O}] [INFO] Preparing external update. Root={appRootDir}; Core={coreSubDir}; Source={tempDicePath}; Restart={exePath}{Environment.NewLine}",
                Encoding.UTF8);

            try
            {
                if (!File.Exists(tempDicePath))
                {
                    throw new FileNotFoundException("The downloaded update file is missing before the application is closed.", tempDicePath);
                }

                if (!File.Exists(exePath))
                {
                    throw new FileNotFoundException("The restart executable is missing before the application is closed.", exePath);
                }

                Directory.CreateDirectory(coreSubDir);
                var writeProbePath = Path.Combine(coreSubDir, $".mdice-update-write-probe-{Guid.NewGuid():N}.tmp");
                await File.WriteAllTextAsync(writeProbePath, string.Empty, Encoding.UTF8);
                File.Delete(writeProbePath);
                await File.AppendAllTextAsync(
                    updateLogPath,
                    $"[{DateTime.Now:O}] [INFO] Update preflight passed: the Core directory is writable.{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch (Exception ex)
            {
                await File.AppendAllTextAsync(
                    updateLogPath,
                    $"[{DateTime.Now:O}] [ERROR] Update preflight failed; application will remain open.{Environment.NewLine}{ex}{Environment.NewLine}",
                    Encoding.UTF8);
                throw;
            }

                Log($"生成批处理文件: {batPath}");
                Log($"可执行文件路径: {exePath}");
                Log($"启动模式: {startupMode}");
                Log($"应用根目录: {appRootDir}");
                Log($"目标Dice路径: {targetDicePath}");
                Log($"临时Dice路径: {tempDicePath}");
                Log($"更新执行日志: {updateLogPath}");

                var batBuilder = new System.Text.StringBuilder();
                batBuilder.AppendLine("@echo off");
                batBuilder.AppendLine("setlocal enabledelayedexpansion");
                batBuilder.AppendLine("set \"APP_ROOT=%MDICE_UPDATE_APP_ROOT%\"");
                batBuilder.AppendLine("set \"CORE_DIR=%MDICE_UPDATE_CORE_DIR%\"");
                batBuilder.AppendLine("set \"TEMP_DICE=%MDICE_UPDATE_SOURCE_DICE%\"");
                batBuilder.AppendLine("set \"TARGET_DICE=%MDICE_UPDATE_TARGET_DICE%\"");
                batBuilder.AppendLine("set \"TARGET_BACKUP=%MDICE_UPDATE_TARGET_BACKUP%\"");
                batBuilder.AppendLine("set \"RESTART_EXE=%MDICE_UPDATE_RESTART_EXE%\"");
                batBuilder.AppendLine("set \"LOG_DIR=%MDICE_UPDATE_LOG_DIR%\"");
                batBuilder.AppendLine("set \"LOGFILE=%MDICE_UPDATE_LOG_FILE%\"");
                batBuilder.AppendLine("set \"UPDATE_RESULT=SUCCESS\"");
                batBuilder.AppendLine("if not exist \"!LOG_DIR!\" mkdir \"!LOG_DIR!\" 2>nul");
                batBuilder.AppendLine("if not exist \"!LOG_DIR!\" (");
                batBuilder.AppendLine("    echo [FATAL] Cannot create update log directory: !LOG_DIR!");
                batBuilder.AppendLine("    exit /b 1");
                batBuilder.AppendLine(")");
                batBuilder.AppendLine("call :LOG \"==== Update script started ====\"");
                batBuilder.AppendLine("call :LOG \"Version: %MDICE_UPDATE_VERSION%\"");
                batBuilder.AppendLine("call :LOG \"Script: %~f0\"");
                batBuilder.AppendLine("call :LOG \"App root: !APP_ROOT!\"");
                batBuilder.AppendLine("call :LOG \"Current directory before cd: !CD!\"");
                batBuilder.AppendLine("call :LOG \"Core directory: !CORE_DIR!\"");
                batBuilder.AppendLine("call :LOG \"Source file: !TEMP_DICE!\"");
                batBuilder.AppendLine("call :LOG \"Target file: !TARGET_DICE!\"");
                batBuilder.AppendLine("call :LOG \"Restart executable: !RESTART_EXE!\"");
                batBuilder.AppendLine("ver >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("whoami >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("cd /d \"!APP_ROOT!\" >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("if errorlevel 1 goto :FAIL_CHANGE_DIRECTORY");
                batBuilder.AppendLine("call :LOG \"Working directory after cd: !CD!\"");
                batBuilder.AppendLine($"set PID={currentPid}");
                batBuilder.AppendLine("set MAX_WAIT_SECONDS=60");
                batBuilder.AppendLine("set WAIT_COUNTER=0");
                batBuilder.AppendLine("if \"!PID!\"==\"\" (");
                batBuilder.AppendLine("    call :FAIL \"PID is empty; cannot wait for the current process.\"");
                batBuilder.AppendLine("    goto :CLEANUP");
                batBuilder.AppendLine(")");
                batBuilder.AppendLine("call :LOG \"Waiting for application PID !PID! to exit.\"");
                batBuilder.AppendLine(":WAIT_LOOP");
                batBuilder.AppendLine("tasklist /FI \"PID eq !PID!\" | findstr /I \"!PID!\" >nul 2>&1");
                batBuilder.AppendLine("if not errorlevel 1 (");
                batBuilder.AppendLine("    set /a WAIT_COUNTER+=1");
                batBuilder.AppendLine("    if !WAIT_COUNTER! EQU 1 call :LOG \"Application is still running; wait limit is !MAX_WAIT_SECONDS! seconds.\"");
                batBuilder.AppendLine("    set /a WAIT_REMAINDER=!WAIT_COUNTER! %% 10");
                batBuilder.AppendLine("    if !WAIT_REMAINDER! EQU 0 call :LOG \"Still waiting for PID !PID! (!WAIT_COUNTER! seconds).\"");
                batBuilder.AppendLine("    if !WAIT_COUNTER! LEQ !MAX_WAIT_SECONDS! (");
                batBuilder.AppendLine("        timeout /t 1 /nobreak >nul");
                batBuilder.AppendLine("        goto WAIT_LOOP");
                batBuilder.AppendLine("    ) else (");
                batBuilder.AppendLine("        call :FAIL \"Timed out waiting for application PID !PID! to exit.\"");
                batBuilder.AppendLine("        goto :CLEANUP");
                batBuilder.AppendLine("    )");
                batBuilder.AppendLine(")");
                batBuilder.AppendLine("call :LOG \"Application process has exited; beginning replacement.\"");
                batBuilder.AppendLine("timeout /t 1 >nul");
                batBuilder.AppendLine("if not exist \"!CORE_DIR!\" mkdir \"!CORE_DIR!\" >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("if not exist \"!CORE_DIR!\" goto :FAIL_CREATE_CORE_DIRECTORY");
                batBuilder.AppendLine("if not exist \"!TEMP_DICE!\" goto :FAIL_SOURCE_MISSING");
                batBuilder.AppendLine("for %%I in (\"!TEMP_DICE!\") do call :LOG \"Source file size: %%~zI bytes\"");
                batBuilder.AppendLine("if not exist \"!RESTART_EXE!\" goto :FAIL_RESTART_EXE_MISSING");
                batBuilder.AppendLine("if exist \"!TARGET_DICE!\" (");
                batBuilder.AppendLine("    call :LOG \"Existing target found; creating backup.\"");
                batBuilder.AppendLine("    if exist \"!TARGET_BACKUP!\" del /f /q \"!TARGET_BACKUP!\" >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("    if exist \"!TARGET_BACKUP!\" goto :FAIL_DELETE_OLD_BACKUP");
                batBuilder.AppendLine("    move /Y \"!TARGET_DICE!\" \"!TARGET_BACKUP!\" >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("    if errorlevel 1 goto :RESTORE_OR_FAIL_BACKUP");
                batBuilder.AppendLine("    if not exist \"!TARGET_BACKUP!\" goto :RESTORE_OR_FAIL_BACKUP");
                batBuilder.AppendLine("    for %%I in (\"!TARGET_BACKUP!\") do call :LOG \"Backup file size: %%~zI bytes\"");
                batBuilder.AppendLine(") else (");
                batBuilder.AppendLine("    call :LOG \"No existing target file; replacement will be a fresh install.\"");
                batBuilder.AppendLine(")");
                batBuilder.AppendLine("call :LOG \"Copying update file to Core directory.\"");
                batBuilder.AppendLine("copy /Y \"!TEMP_DICE!\" \"!TARGET_DICE!\" >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("if errorlevel 1 goto :RESTORE");
                batBuilder.AppendLine("if not exist \"!TARGET_DICE!\" goto :RESTORE");
                batBuilder.AppendLine("for %%I in (\"!TARGET_DICE!\") do set TARGET_SIZE=%%~zI");
                batBuilder.AppendLine("for %%I in (\"!TEMP_DICE!\") do set SOURCE_SIZE=%%~zI");
                batBuilder.AppendLine("call :LOG \"Copied file size: !TARGET_SIZE! bytes (source !SOURCE_SIZE! bytes).\"");
                batBuilder.AppendLine("if not \"!TARGET_SIZE!\"==\"!SOURCE_SIZE!\" goto :RESTORE");
                batBuilder.AppendLine("call :LOG \"Replacement succeeded; starting application.\"");
                batBuilder.AppendLine("start \"MDiceV2 restart\" /D \"!APP_ROOT!\" \"!RESTART_EXE!\" >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("if errorlevel 1 goto :RESTORE");
                batBuilder.AppendLine("call :LOG \"Restart command was accepted by Windows. Check launcher.log for application startup details.\"");
                batBuilder.AppendLine("goto :CLEANUP");
                batBuilder.AppendLine("");
                batBuilder.AppendLine(":FAIL_CHANGE_DIRECTORY");
                batBuilder.AppendLine("call :FAIL \"Could not switch to application root.\"");
                batBuilder.AppendLine("goto :CLEANUP");
                batBuilder.AppendLine(":FAIL_CREATE_CORE_DIRECTORY");
                batBuilder.AppendLine("call :FAIL \"Core directory is missing and could not be created.\"");
                batBuilder.AppendLine("goto :CLEANUP");
                batBuilder.AppendLine(":FAIL_SOURCE_MISSING");
                batBuilder.AppendLine("call :FAIL \"Downloaded update file is missing before replacement.\"");
                batBuilder.AppendLine("goto :CLEANUP");
                batBuilder.AppendLine(":FAIL_RESTART_EXE_MISSING");
                batBuilder.AppendLine("call :FAIL \"Restart executable is missing.\"");
                batBuilder.AppendLine("goto :CLEANUP");
                batBuilder.AppendLine(":FAIL_DELETE_OLD_BACKUP");
                batBuilder.AppendLine("call :FAIL \"Could not delete the previous backup; target was not replaced.\"");
                batBuilder.AppendLine("goto :CLEANUP");
                batBuilder.AppendLine(":RESTORE_OR_FAIL_BACKUP");
                batBuilder.AppendLine("call :FAIL \"Could not move the existing target to its backup location.\"");
                batBuilder.AppendLine("goto :CLEANUP");
                batBuilder.AppendLine(":RESTORE");
                batBuilder.AppendLine("call :FAIL \"Copy, verification, or restart failed; attempting to restore the previous version.\"");
                batBuilder.AppendLine("if exist \"!TARGET_BACKUP!\" (");
                batBuilder.AppendLine("    if exist \"!TARGET_DICE!\" del /f /q \"!TARGET_DICE!\" >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("    move /Y \"!TARGET_BACKUP!\" \"!TARGET_DICE!\" >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("    if exist \"!TARGET_DICE!\" (call :LOG \"Previous version restored.\") else (call :FAIL \"Previous version could not be restored.\")");
                batBuilder.AppendLine(") else (");
                batBuilder.AppendLine("    call :LOG \"No previous version was backed up; leaving the copied file in place for manual diagnosis.\"");
                batBuilder.AppendLine(")");
                batBuilder.AppendLine("goto :CLEANUP");
                batBuilder.AppendLine("");
                batBuilder.AppendLine(":CLEANUP");
                batBuilder.AppendLine("if exist \"!TEMP_DICE!\" del /f /q \"!TEMP_DICE!\" >> \"!LOGFILE!\" 2>&1");
                batBuilder.AppendLine("if exist \"!TEMP_DICE!\" (call :FAIL \"Could not delete temporary update file.\") else (call :LOG \"Temporary update file removed.\")");
                batBuilder.AppendLine("call :LOG \"==== Update script finished with result !UPDATE_RESULT! ====");
                batBuilder.AppendLine("endlocal");
                batBuilder.AppendLine("exit /b 0");
                batBuilder.AppendLine("");
                batBuilder.AppendLine(":LOG");
                batBuilder.AppendLine("echo [%date% %time%] [INFO] %~1>> \"!LOGFILE!\"");
                batBuilder.AppendLine("exit /b 0");
                batBuilder.AppendLine(":FAIL");
                batBuilder.AppendLine("set \"UPDATE_RESULT=FAILED\"");
                batBuilder.AppendLine("echo [%date% %time%] [ERROR] %~1>> \"!LOGFILE!\"");
                batBuilder.AppendLine("exit /b 0");

                var batContent = batBuilder.ToString();

                // 写入文件
                // Keep the batch source ASCII-only. All potentially non-ASCII paths are
                // supplied through the Unicode process environment when cmd.exe starts.
                await File.WriteAllTextAsync(batPath, batContent, Encoding.ASCII);

                // 验证文件是否创建成功
                if (File.Exists(batPath))
                {
                    var fileInfo = new FileInfo(batPath);
                    Log($"批处理文件已生成: {batPath} ({fileInfo.Length} bytes)");
                }
                else
                {
                    throw new Exception("批处理文件创建失败");
                }

                _pendingUpdateScript = new PendingUpdateScript(
                    batPath,
                    appRootDir,
                    coreSubDir,
                    tempDicePath,
                    targetDicePath,
                    targetDiceBackup,
                    exePath,
                    logDirectory,
                    updateLogPath,
                    version);

                return batPath;
            }
            catch (Exception ex)
            {
                Log($"生成批处理文件失败: {ex.Message}");
                throw new Exception($"生成更新批处理文件失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 启动更新进程
        /// </summary>
        private async Task LaunchUpdateProcess(string batPath)
        {
            var pendingScript = _pendingUpdateScript;
            try
            {
                // 验证批处理文件是否存在
                if (!File.Exists(batPath))
                {
                    throw new Exception($"批处理文件不存在: {batPath}");
                }

                if (pendingScript == null ||
                    !string.Equals(pendingScript.ScriptPath, batPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The update-script launch context is missing.");
                }

                var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                var startInfo = new ProcessStartInfo
                {
                    FileName = commandProcessor,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(batPath) ?? _tempDir
                };
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(batPath);
                startInfo.Environment["MDICE_UPDATE_APP_ROOT"] = pendingScript.AppRoot;
                startInfo.Environment["MDICE_UPDATE_CORE_DIR"] = pendingScript.CoreDirectory;
                startInfo.Environment["MDICE_UPDATE_SOURCE_DICE"] = pendingScript.SourceDice;
                startInfo.Environment["MDICE_UPDATE_TARGET_DICE"] = pendingScript.TargetDice;
                startInfo.Environment["MDICE_UPDATE_TARGET_BACKUP"] = pendingScript.TargetBackup;
                startInfo.Environment["MDICE_UPDATE_RESTART_EXE"] = pendingScript.RestartExecutable;
                startInfo.Environment["MDICE_UPDATE_LOG_DIR"] = pendingScript.LogDirectory;
                startInfo.Environment["MDICE_UPDATE_LOG_FILE"] = pendingScript.LogFile;
                startInfo.Environment["MDICE_UPDATE_VERSION"] = pendingScript.Version;

                await TryAppendUpdateScriptLogAsync(
                    pendingScript.LogFile,
                    "INFO",
                    $"Launching cmd.exe explicitly. CommandProcessor={commandProcessor}; Script={batPath}");

                Log($"启动批处理文件: {batPath}");
                var process = Process.Start(startInfo);

                if (process == null)
                {
                    throw new Exception("无法启动批处理文件");
                }

                Log("更新进程已启动");
                Log($"批处理进程ID: {process.Id}");
                await TryAppendUpdateScriptLogAsync(
                    pendingScript.LogFile,
                    "INFO",
                    $"cmd.exe started successfully. ProcessId={process.Id}");
                await ExitCurrentProcessForExternalUpdateAsync(Log);
            }
            catch (Exception ex)
            {
                if (pendingScript != null)
                {
                    await TryAppendUpdateScriptLogAsync(pendingScript.LogFile, "ERROR", $"Unable to start the external update script.{Environment.NewLine}{ex}");
                }
                Log($"启动更新进程失败: {ex.Message}");
                throw new Exception($"启动更新进程失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 测试更新脚本（使用临时目录中已有的文件，不下载）
        /// </summary>
        private static async Task TryAppendUpdateScriptLogAsync(string logPath, string level, string message)
        {
            try
            {
                await File.AppendAllTextAsync(
                    logPath,
                    $"[{DateTime.Now:O}] [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // Diagnostic logging must never prevent the updater from attempting recovery.
            }
        }

        public async Task<UpdateResult> TestUpdateScriptAsync()
        {
            var result = new UpdateResult();
            
            try
            {
                Log("开始测试更新脚本...");
                
                // 检查临时目录中是否存在 MDiceV2.Core.Dice
                var tempDicePath = Path.Combine(_tempDir, "MDiceV2.Core.Dice");
                if (!File.Exists(tempDicePath))
                {
                    result.Success = false;
                    result.Message = $"临时目录中未找到测试文件: {tempDicePath}";
                    Log(result.Message);
                    return result;
                }
                
                Log($"找到测试文件: {tempDicePath}");
                
                // 使用测试版本号
                var testVersion = $"Test_{DateTime.Now:yyyyMMdd_HHmmss}";
                
                // 生成批处理脚本
                var batPath = await GenerateUpdateBatchFile(testVersion, tempDicePath);
                Log($"批处理脚本已生成: {batPath}");
                
                // 启动更新进程
                await LaunchUpdateProcess(batPath);
                
                result.Success = true;
                result.Message = "测试更新脚本已启动，应用将退出并执行更新";
                Log(result.Message);
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = $"测试更新脚本失败: {ex.Message}";
                Log(result.Message);
                return result;
            }
        }

        /// <summary>
        /// 测试更新管理器的基本功能（不执行实际更新）
        /// </summary>
        public async Task<string> TestConnectionAsync(string owner = "HumulusQ", string repo = "MDiceV2Public")
        {
            try
            {
                Log("开始测试GitHub连接...");

                var allReleases = await GetAllReleasesAsync(owner, repo);
                Log($"成功获取到 {allReleases.Count} 个releases");

                var updateReleases = allReleases
                    .Select(r => new
                    {
                        Release = r,
                        NumericTag = ExtractNumericVersion(r.TagName)
                    })
                    .Where(x => !string.IsNullOrWhiteSpace(x.Release.Name) && x.Release.Name.StartsWith("UpdatePackageV", StringComparison.OrdinalIgnoreCase))
                    .Where(x => Version.TryParse(x.NumericTag, out _))
                    .OrderByDescending(x => Version.Parse(x.NumericTag!))
                    .ToList();

                Log($"找到 {updateReleases.Count} 个 UpdatePackageV* 发布");

                if (updateReleases.Any())
                {
                    var latest = updateReleases.First();
                    var asm = ParseAssemblyVersion(latest.Release) ?? latest.NumericTag ?? latest.Release.TagName;
                    Log($"最新发布: {latest.Release.Name}, tag={latest.Release.TagName}, asm={asm} (发布于: {latest.Release.PublishedAt:yyyy-MM-dd HH:mm:ss})");

                    var coreDice = latest.Release.Assets.FirstOrDefault(a => a.Name.Equals("MDiceV2.Core.Dice", StringComparison.OrdinalIgnoreCase));
                    if (coreDice != null)
                    {
                        Log($"找到MDiceV2.Core.Dice: {coreDice.Name} ({coreDice.Size} bytes)");
                        return $"测试成功！最新版本: {asm}，文件大小: {coreDice.Size} bytes";
                    }
                    else
                    {
                        return $"警告：{latest.Release.Name} 中未找到MDiceV2.Core.Dice";
                    }
                }

                return "警告：未找到 UpdatePackageV* 发布";
            }
            catch (Exception ex)
            {
                var errorMsg = $"测试失败: {ex.Message}";
                Log(errorMsg);
                return errorMsg;
            }
        }
    }

    /// <summary>
    /// GitHub Release数据模型
    /// </summary>
    public class GitHubRelease
    {
        public string? name { get; set; }  // Release title
        public string? tag_name { get; set; }  // GitHub API使用的字段名
        public DateTime published_at { get; set; }  // GitHub API使用的字段名
        public string? body { get; set; } // Release body（包含程序集版本信息）
        [System.Text.Json.Serialization.JsonPropertyName("assets")]
        public List<GitHubAsset>? AssetData { get; set; }  // 明确映射到JSON的assets字段

        // 属性访问器，提供兼容的接口
        [System.Text.Json.Serialization.JsonIgnore]
        public string Name => name ?? "";
        [System.Text.Json.Serialization.JsonIgnore]
        public string TagName => tag_name ?? "";
        [System.Text.Json.Serialization.JsonIgnore]
        public DateTime PublishedAt => published_at;
        [System.Text.Json.Serialization.JsonIgnore]
        public string Body => body ?? "";
        [System.Text.Json.Serialization.JsonIgnore]
        public List<GitHubAsset> Assets => AssetData ?? new List<GitHubAsset>();
    }

    /// <summary>
    /// GitHub Asset数据模型
    /// </summary>
    public class GitHubAsset
    {
        public string? name { get; set; }  // GitHub API使用的字段名
        public long size { get; set; }  // GitHub API使用的字段名
        public string? browser_download_url { get; set; }  // GitHub API使用的字段名
        public int id { get; set; }  // GitHub API使用的字段名 - 新增这个关键字段

        // 属性访问器，提供兼容的接口
        [System.Text.Json.Serialization.JsonIgnore]
        public string Name => name ?? "";
        [System.Text.Json.Serialization.JsonIgnore]
        public long Size => size;
        [System.Text.Json.Serialization.JsonIgnore]
        public string BrowserDownloadUrl => browser_download_url ?? "";
        [System.Text.Json.Serialization.JsonIgnore]
        public int Id => id;  // 新增ID属性访问器
    }

    /// <summary>
    /// 更新结果
    /// </summary>
    public class UpdateResult
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
    }
}
