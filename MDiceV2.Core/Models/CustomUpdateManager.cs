using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
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
            _httpDownload.Timeout = TimeSpan.FromSeconds(300); // 下载超时更久(5倍延长:300秒)
            _httpDownload.DefaultRequestHeaders.UserAgent.ParseAdd("MDiceV2-CustomUpdater");
            _httpDownload.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

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

                await DownloadAsset(coreAsset, tempCorePath, latestRelease.Release.TagName);

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
        private async Task DownloadAsset(GitHubAsset asset, string targetPath, string? releaseTag = null)
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
                    var apiSuccess = await DownloadAssetViaApi(asset, targetPath);
                    if (!apiSuccess)
                    {
                        throw lastException;
                    }
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
        private async Task<bool> DownloadAssetViaApi(GitHubAsset asset, string targetPath)
        {
            int maxRetries = 3;
            int delayBetweenRetries = 3000; // 3秒

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    // 使用反射获取asset的id属性
                    var assetId = GetAssetId(asset);
                    if (assetId <= 0)
                    {
                        Log("无法获取Asset ID，跳过API下载");
                        return false;
                    }

                    var apiUrl = $"https://api.github.com/repos/HumulusQ/MDiceV2Public/releases/assets/{assetId}";
                    Log($"[API 尝试 {attempt}/{maxRetries}] 使用GitHub API下载: {apiUrl}");

                    // 添加下载前延迟，避免触发速率限制
                    await Task.Delay(2000); // 2秒延迟

                    // 确保使用正确的Accept头
                    _httpDownload.DefaultRequestHeaders.Accept.Clear();
                    _httpDownload.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

                    using var response = await _httpDownload.GetAsync(apiUrl);
                    Log($"API下载响应状态: {response.StatusCode}");
                    Log($"Content-Type: {response.Content.Headers.ContentType}");
                    Log($"Content-Length: {response.Content.Headers.ContentLength}");

                    if (!response.IsSuccessStatusCode)
                    {
                        // 如果是429错误，提供特定的错误信息
                        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                        {
                            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds ?? 60;
                            Log($"GitHub API下载速率限制: 请等待 {retryAfter} 秒后重试");
                            
                            if (attempt < maxRetries)
                            {
                                int totalWait = (int)(retryAfter * 1000);
                                Log($"将在 {totalWait}ms 后重试...");
                                await Task.Delay(totalWait);
                                continue;
                            }
                            
                            throw new Exception($"GitHub API请求过于频繁，下载失败。建议等待: {retryAfter}秒");
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
                        Log($"API下载失败: {response.StatusCode} - {errorContent}");
                        return false;
                    }

                    // 确保目标目录存在
                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                        Log($"创建目录: {targetDir}");
                    }

                    // 删除已存在的文件
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                        Log($"删除已存在的文件: {targetPath}");
                    }

                    await using var fileStream = File.Create(targetPath);
                    await response.Content.CopyToAsync(fileStream);

                    // 验证下载的文件
                    if (File.Exists(targetPath))
                    {
                        var fileInfo = new FileInfo(targetPath);
                        Log($"API下载完成: {targetPath} ({fileInfo.Length} bytes)");

                        // 验证文件大小
                        if (fileInfo.Length == asset.Size)
                        {
                            Log($"✅ API下载成功 (尝试 {attempt}/{maxRetries})，文件大小验证通过！");
                            return true;
                        }
                        else
                        {
                            Log($"⚠️ 文件大小不匹配。预期: {asset.Size}, 实际: {fileInfo.Length}");
                            
                            if (attempt < maxRetries)
                            {
                                Log($"将在 {delayBetweenRetries}ms 后重试...");
                                File.Delete(targetPath); // 删除损坏的文件
                                await Task.Delay(delayBetweenRetries);
                                continue;
                            }
                            return false;
                        }
                    }
                    else
                    {
                        throw new Exception("文件下载后未找到目标文件");
                    }
                }
                catch (TaskCanceledException ex)
                {
                    Log($"⏱️ API下载超时 (尝试 {attempt}/{maxRetries}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        Log($"将在 {delayBetweenRetries}ms 后重试...");
                        await Task.Delay(delayBetweenRetries);
                        continue;
                    }
                    Log("API下载已达到最大重试次数，放弃");
                    return false;
                }
                catch (HttpRequestException ex)
                {
                    Log($"❌ API网络错误 (尝试 {attempt}/{maxRetries}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        Log($"将在 {delayBetweenRetries}ms 后重试...");
                        await Task.Delay(delayBetweenRetries);
                        continue;
                    }
                    Log("API下载已达到最大重试次数，放弃");
                    return false;
                }
                catch (Exception ex)
                {
                    Log($"API下载异常: {ex.Message}");
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// 使用浏览器下载 URL 下载文件（支持多个镜像站加速）
        /// </summary>
        private async Task DownloadAssetViaBrowserUrl(GitHubAsset asset, string targetPath, string? sourceId, string? releaseTag = null)
        {
            int maxRetries = 3;
            int delayBetweenRetries = 2000; // 2 秒

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
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
                        sourceDisplay = mirrorInfo.DisplayName;
                    }

                    Log($"[尝试 {attempt}/{maxRetries}] 从 {sourceDisplay} 下载: {finalUrl}");

                    // 使用预配置的下载客户端（超时时间为 300 秒）
                    using var response = await _httpDownload.GetAsync(finalUrl);
                    Log($"下载响应状态: {response.StatusCode}");

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        Log($"下载失败: {response.StatusCode} - {errorContent}");
                        
                        // 如果是 429（速率限制）或 5xx 错误，进行重试
                        if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                        {
                            Log($"服务器错误 ({response.StatusCode})，将在 {delayBetweenRetries}ms 后重试...");
                            if (attempt < maxRetries)
                            {
                                await Task.Delay(delayBetweenRetries);
                                continue;
                            }
                        }

                        throw new Exception($"下载失败: {response.StatusCode}");
                    }

                    // 确保目标目录存在
                    var targetDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    {
                        Directory.CreateDirectory(targetDir);
                        Log($"创建目录: {targetDir}");
                    }

                    // 删除旧文件（如果存在）
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }

                    // 下载文件
                    await using var fileStream = File.Create(targetPath);
                    await response.Content.CopyToAsync(fileStream);

                    var fileInfo = new FileInfo(targetPath);
                    Log($"✅ 下载成功 (尝试 {attempt}/{maxRetries}): {targetPath} ({fileInfo.Length} bytes)");
                    
                    return; // 成功，退出
                }
                catch (TaskCanceledException ex)
                {
                    Log($"⏱️ 下载超时 (尝试 {attempt}/{maxRetries}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        Log($"将在 {delayBetweenRetries}ms 后重试...");
                        await Task.Delay(delayBetweenRetries);
                        continue;
                    }
                    throw new Exception($"下载超时，已尝试 {maxRetries} 次。请检查网络连接。", ex);
                }
                catch (HttpRequestException ex)
                {
                    Log($"❌ 网络错误 (尝试 {attempt}/{maxRetries}): {ex.Message}");
                    if (attempt < maxRetries)
                    {
                        Log($"将在 {delayBetweenRetries}ms 后重试...");
                        await Task.Delay(delayBetweenRetries);
                        continue;
                    }
                    throw new Exception($"网络连接失败，已尝试 {maxRetries} 次。请检查网络连接。", ex);
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
            var exePath = startupMode == StartupMode.Console
                ? Path.Combine(appRootDir, "MDiceV2.Console.exe")
                : Path.Combine(appRootDir, "MDiceV2.Launcher.exe");
            
            // 核心应用 .Dice 文件的路径（在 Core 子目录中）
            var coreSubDir = Path.Combine(appRootDir, "Core");
            var targetDicePath = Path.Combine(coreSubDir, "MDiceV2.Core.Dice");
            var targetDiceBackup = Path.Combine(coreSubDir, "MDiceV2.Core.Dice.bak");

                Log($"生成批处理文件: {batPath}");
                Log($"可执行文件路径: {exePath}");
                Log($"启动模式: {startupMode}");
                Log($"应用根目录: {appRootDir}");
                Log($"目标Dice路径: {targetDicePath}");
                Log($"临时Dice路径: {tempDicePath}");

                var batBuilder = new System.Text.StringBuilder();
                batBuilder.AppendLine("@echo off");
                batBuilder.AppendLine("setlocal enabledelayedexpansion");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("REM MDiceV2 Update Script (Safe Version)");
                batBuilder.AppendLine($"REM Version: {version}");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("set LOGFILE=%~dp0error.txt");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("echo ==== Update started at %date% %time% ==== >> \"%LOGFILE%\"");
                batBuilder.AppendLine("");
                batBuilder.AppendLine($"set PID={currentPid}");
                batBuilder.AppendLine("set MAX_WAIT_SECONDS=60");
                batBuilder.AppendLine("set WAIT_COUNTER=0");
                batBuilder.AppendLine("");
                batBuilder.AppendLine($"echo MDiceV2 正在更新到版本 {version}...");
                batBuilder.AppendLine($"echo 等待进程 PID !PID! 退出...");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("REM 等待旧进程退出");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("if \"!PID!\"==\"\" (");
                batBuilder.AppendLine("    echo [Error] PID 为空，无法等待进程退出 >> \"%LOGFILE%\"");
                batBuilder.AppendLine("    goto :ERROR");
                batBuilder.AppendLine(")");
                batBuilder.AppendLine("");
                batBuilder.AppendLine(":WAIT_LOOP");
                batBuilder.AppendLine("tasklist /FI \"PID eq !PID!\" | findstr /I \"!PID!\" >nul 2>&1");
                batBuilder.AppendLine("if %ERRORLEVEL% EQU 0 (");
                batBuilder.AppendLine("    set /a WAIT_COUNTER+=1");
                batBuilder.AppendLine("    if !WAIT_COUNTER! LEQ !MAX_WAIT_SECONDS! (");
                batBuilder.AppendLine("        timeout /t 1 /nobreak >nul");
                batBuilder.AppendLine("        goto WAIT_LOOP");
                batBuilder.AppendLine("    ) else (");
                batBuilder.AppendLine("        echo [Timeout] 进程 !PID! 未在规定时间退出 >> \"%LOGFILE%\"");
                batBuilder.AppendLine("    )");
                batBuilder.AppendLine(")");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("echo 进程已退出，开始更新...");
                batBuilder.AppendLine("timeout /t 1 >nul");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("REM 备份旧文件");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine($"if exist \"{targetDicePath}\" (");
                batBuilder.AppendLine("    echo 正在备份旧文件...");
                batBuilder.AppendLine($"    if exist \"{targetDiceBackup}\" del /f /q \"{targetDiceBackup}\" >nul");
                batBuilder.AppendLine($"    ren \"{targetDicePath}\" \"MDiceV2.Core.Dice.bak\" >nul");
                batBuilder.AppendLine("    if !ERRORLEVEL! NEQ 0 (");
                batBuilder.AppendLine("        echo [Error] 备份失败 >> \"%LOGFILE%\"");
                batBuilder.AppendLine("        goto :ERROR");
                batBuilder.AppendLine("    )");
                batBuilder.AppendLine(") else (");
                batBuilder.AppendLine("    echo 目标文件不存在，跳过备份");
                batBuilder.AppendLine(")");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("REM 复制新文件");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine($"if exist \"{tempDicePath}\" (");
                batBuilder.AppendLine("    echo 正在复制新文件...");
                
                batBuilder.AppendLine($"    copy /Y \"{tempDicePath}\" \"{targetDicePath}\" >nul");
                batBuilder.AppendLine("    if !ERRORLEVEL! NEQ 0 (");
                batBuilder.AppendLine("        echo [Error] 复制新文件失败 >> \"%LOGFILE%\"");
                batBuilder.AppendLine("        goto :RESTORE");
                batBuilder.AppendLine("    )");
                batBuilder.AppendLine(") else (");
                batBuilder.AppendLine($"    echo [Error] 临时文件不存在: \"{tempDicePath}\" >> \"%LOGFILE%\"");
                batBuilder.AppendLine("    goto :ERROR");
                batBuilder.AppendLine(")");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("REM 文件系统稳定等待（避免 0xc0000142）");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("timeout /t 1 >nul");
                batBuilder.AppendLine($"dir \"{targetDicePath}\" >nul 2>&1");
                batBuilder.AppendLine("timeout /t 1 >nul");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("REM 验证 Core 子目录");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine($"if not exist \"{coreSubDir}\" mkdir \"{coreSubDir}\" >nul");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("REM 重启应用（安全启动）");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("echo 正在重启应用...");
                batBuilder.AppendLine($"start \"\" \"{exePath}\"");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("goto :CLEANUP");
                batBuilder.AppendLine("");
                batBuilder.AppendLine(":RESTORE");
                batBuilder.AppendLine("echo 正在恢复备份文件...");
                batBuilder.AppendLine($"if exist \"{targetDiceBackup}\" (");
                batBuilder.AppendLine($"    del /f /q \"{targetDicePath}\" >nul");
                batBuilder.AppendLine($"    ren \"{targetDiceBackup}\" \"MDiceV2.Core.Dice\" >nul");
                batBuilder.AppendLine(") else (");
                batBuilder.AppendLine("    echo [Error] 无法恢复备份 >> \"%LOGFILE%\"");
                batBuilder.AppendLine(")");
                batBuilder.AppendLine("goto :CLEANUP");
                batBuilder.AppendLine("");
                batBuilder.AppendLine(":ERROR");
                batBuilder.AppendLine("echo 更新过程发生错误，详情见 error.txt");
                batBuilder.AppendLine("");
                batBuilder.AppendLine(":CLEANUP");
                batBuilder.AppendLine("echo 清理临时文件...");
                batBuilder.AppendLine($"if exist \"{tempDicePath}\" del /f /q \"{tempDicePath}\" >nul");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("echo ==== Update finished at %date% %time% ==== >> \"%LOGFILE%\"");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("REM 自删除（安全方式，不影响日志写入）");
                batBuilder.AppendLine("REM ========================================");
                batBuilder.AppendLine("start \"\" cmd /c \"timeout /t 1 >nul & del /f /q \"%~f0\"\"");
                batBuilder.AppendLine("");
                batBuilder.AppendLine("endlocal");
                batBuilder.AppendLine("exit /b 0");

                var batContent = batBuilder.ToString();

                // 写入文件
                await File.WriteAllTextAsync(batPath, batContent, System.Text.Encoding.Default);

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
            try
            {
                // 验证批处理文件是否存在
                if (!File.Exists(batPath))
                {
                    throw new Exception($"批处理文件不存在: {batPath}");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = batPath,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };

                Log($"启动批处理文件: {batPath}");
                var process = Process.Start(startInfo);

                if (process == null)
                {
                    throw new Exception("无法启动批处理文件");
                }

                Log("更新进程已启动");
                Log($"批处理进程ID: {process.Id}");
                Log("应用将在2秒后自动退出...");

                // 增加延迟时间并添加更多日志，确保用户了解情况
                await Task.Delay(2000);

                Log("正在退出应用以完成更新...");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Log($"启动更新进程失败: {ex.Message}");
                throw new Exception($"启动更新进程失败: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// 测试更新脚本（使用临时目录中已有的文件，不下载）
        /// </summary>
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
