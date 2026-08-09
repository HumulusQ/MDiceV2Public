using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MDiceV2.Abstractions;
using MDiceV2.Core.Infrastructure;

namespace MDiceV2.Models;

public sealed record AiModUpdateScheduleResult
{
    public bool Success { get; init; }
    public bool RequiresRestart { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? AssetName { get; init; }
    public string? VersionLabel { get; init; }
    public string? ScriptPath { get; init; }
    public string? PayloadDir { get; init; }
}

public partial class MessageProcessor
{
    private const string AimodPackageId = "com.humulus.aimod";
    private const string DefaultAIModDllFileName = "AIMod.dll";
    private const string DefaultAIModPluginClassName = "AIMod.AIMod";
    private const string DefaultAIModPdbFileName = "AIMod.pdb";

    private async Task<AiModUpdateScheduleResult> DownloadAndScheduleAIModUpdateAsync(Action<string> log)
    {
        const string owner = "HumulusQ";
        const string repo = "MDiceV2Public";
        const string assetPrefix = "AIModPackV";

        var appRoot = GetApplicationRootDirectory();
        var tempRoot = Path.Combine(appRoot, "temp");
        Directory.CreateDirectory(tempRoot);

        var workDir = Path.Combine(tempRoot, $"AIModUpdate_{Guid.NewGuid():N}");
        var extractDir = Path.Combine(workDir, "extract");
        var payloadDir = Path.Combine(workDir, "payload");
        Directory.CreateDirectory(extractDir);
        Directory.CreateDirectory(payloadDir);

        try
        {
            log($"AIMod 更新暂存目录: {workDir}");

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(300);
            http.DefaultRequestHeaders.UserAgent.Clear();
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MDiceV2", "1.0"));
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var releasesUrl = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=100";
            log($"拉取 Release 列表: {releasesUrl}");

            var json = await http.GetStringAsync(releasesUrl).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var asset = FindLatestAIModAsset(doc.RootElement, assetPrefix);
            if (asset == null)
            {
                return FailAIModUpdate($"未找到 AIMod 更新包：资源名称需匹配 {assetPrefix}*.zip");
            }

            var tempZip = Path.Combine(workDir, SanitizeFileName(asset.Value.AssetName));
            log($"找到资源: {asset.Value.AssetName}");
            log($"版本标签: {asset.Value.VersionLabel}");

            try
            {
                await using var stream = await http.GetStreamAsync(asset.Value.DownloadUrl).ConfigureAwait(false);
                await using var fs = new FileStream(tempZip, FileMode.Create, FileAccess.Write, FileShare.None);
                await stream.CopyToAsync(fs).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return FailAIModUpdate($"下载失败: {ex.Message}", asset.Value.AssetName, asset.Value.VersionLabel);
            }

            log($"AIMod 更新包已下载: {tempZip}");

            try
            {
                SafeExtractZip(tempZip, extractDir);
                var extractedRoot = FindAIModPayloadRoot(extractDir);
                log($"AIMod payload 根目录: {extractedRoot}");
                CopyDirectory(extractedRoot, payloadDir);
            }
            catch (Exception ex)
            {
                return FailAIModUpdate($"预解压失败: {ex.Message}", asset.Value.AssetName, asset.Value.VersionLabel);
            }

            AIModManifestInfo payloadManifest;
            try
            {
                payloadManifest = ValidateAIModPayload(payloadDir);
                log($"AIMod payload 校验通过: id={payloadManifest.Id}, dll={payloadManifest.DllFileName}");
                foreach (var warning in GetOptionalAIModPayloadWarnings(payloadDir))
                {
                    log($"AIMod payload 警告: {warning}");
                }
            }
            catch (Exception ex)
            {
                return FailAIModUpdate(
                    ex.Message,
                    asset.Value.AssetName,
                    asset.Value.VersionLabel);
            }

            string scriptPath;
            try
            {
                scriptPath = await GenerateAIModUpdateBatchFileAsync(
                    asset.Value.VersionLabel,
                    payloadDir,
                    tempZip,
                    log).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return FailAIModUpdate($"脚本生成失败: {ex.Message}", asset.Value.AssetName, asset.Value.VersionLabel);
            }

            try
            {
                LaunchAIModUpdateScript(scriptPath, log);
            }
            catch (Exception ex)
            {
                return FailAIModUpdate($"脚本启动失败: {ex.Message}", asset.Value.AssetName, asset.Value.VersionLabel, scriptPath);
            }

            return new AiModUpdateScheduleResult
            {
                Success = true,
                RequiresRestart = true,
                Message = "AIMod 更新包已下载，将退出并由脚本安装后重启",
                AssetName = asset.Value.AssetName,
                VersionLabel = asset.Value.VersionLabel,
                ScriptPath = scriptPath,
                PayloadDir = payloadDir
            };
        }
        catch (Exception ex)
        {
            return FailAIModUpdate($"更新准备失败: {ex.Message}");
        }
    }

    private async Task<string> GenerateAIModUpdateBatchFileAsync(
        string versionLabel,
        string payloadDir,
        string downloadedZipPath,
        Action<string> log)
    {
        var appRoot = GetApplicationRootDirectory();
        var tempRoot = Path.Combine(appRoot, "temp");
        Directory.CreateDirectory(tempRoot);

        var currentProcess = Process.GetCurrentProcess();
        var pid = currentProcess.Id;
        var startupMode = ServiceBootstrapper.CurrentStartupMode;
        var restartTarget = ResolveRestartTargetForUpdate(appRoot, startupMode, log);
        var exePath = restartTarget.ExePath;
        var restartWorkingDirectory = restartTarget.WorkingDirectory;
        log($"AIMod 更新后重启目标使用主程序同款设置: mode={startupMode}, exePath={exePath}, exists={File.Exists(exePath)}");

        var modsRoot = Path.Combine(appRoot, "mods");
        var targetDir = Path.Combine(modsRoot, "AIMod");
        var backupDir = Path.Combine(modsRoot, $"AIMod_backup_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}");
        var cacheFile = Path.Combine(modsRoot, "AIMod.mod");
        var logPath = Path.Combine(tempRoot, "aimod_update.log");
        var errorLogPath = Path.Combine(tempRoot, "aimod_update_error.txt");
        var safeVersionLabel = SanitizeFileName(versionLabel);
        var scriptPath = Path.Combine(tempRoot, $"AIMod_Update_{safeVersionLabel}_{Guid.NewGuid():N}.bat");
        var payloadManifest = ValidateAIModPayload(payloadDir);
        var batchDllFileName = payloadManifest.DllFileName.Replace('/', '\\');

        var bat = new StringBuilder();
        bat.AppendLine("@echo off");
        bat.AppendLine("setlocal enabledelayedexpansion");
        bat.AppendLine($"set \"PID={pid}\"");
        bat.AppendLine($"set \"APP_ROOT={appRoot}\"");
        bat.AppendLine($"set \"MODS_DIR={modsRoot}\"");
        bat.AppendLine($"set \"TARGET={targetDir}\"");
        bat.AppendLine($"set \"BACKUP={backupDir}\"");
        bat.AppendLine($"set \"PAYLOAD={payloadDir}\"");
        bat.AppendLine($"set \"ZIP_PATH={downloadedZipPath}\"");
        bat.AppendLine($"set \"CACHE_FILE={cacheFile}\"");
        bat.AppendLine($"set \"EXE_PATH={exePath}\"");
        bat.AppendLine($"set \"RESTART_WORKDIR={restartWorkingDirectory}\"");
        bat.AppendLine($"set \"LOGFILE={logPath}\"");
        bat.AppendLine($"set \"ERROR_LOGFILE={errorLogPath}\"");
        bat.AppendLine($"set \"DLL_FILE={batchDllFileName}\"");
        bat.AppendLine("break > \"%LOGFILE%\"");
        bat.AppendLine("echo AIMod update script started. >> \"%LOGFILE%\"");
        bat.AppendLine("echo Version: " + EscapeBatchEcho(versionLabel) + " >> \"%LOGFILE%\"");
        bat.AppendLine("echo Waiting for MDiceV2 PID %PID% to exit... >> \"%LOGFILE%\"");
        bat.AppendLine(":wait_loop");
        bat.AppendLine("tasklist /FI \"PID eq %PID%\" 2>nul | findstr /R /C:\"^ *%PID% \" >nul");
        bat.AppendLine("if not errorlevel 1 (");
        bat.AppendLine("  timeout /t 1 /nobreak >nul");
        bat.AppendLine("  goto wait_loop");
        bat.AppendLine(")");
        bat.AppendLine("if not exist \"%PAYLOAD%\\mod.json\" goto missing_payload");
        bat.AppendLine("if not exist \"%PAYLOAD%\\%DLL_FILE%\" goto missing_payload");
        bat.AppendLine("if not exist \"%MODS_DIR%\" mkdir \"%MODS_DIR%\"");
        bat.AppendLine("if exist \"%BACKUP%\" goto backup_failed");
        bat.AppendLine("if exist \"%TARGET%\" (");
        bat.AppendLine("  echo Backing up old AIMod directory... >> \"%LOGFILE%\"");
        bat.AppendLine("  move \"%TARGET%\" \"%BACKUP%\" >> \"%LOGFILE%\" 2>&1");
        bat.AppendLine("  if errorlevel 1 goto backup_failed");
        bat.AppendLine(")");
        bat.AppendLine("echo Copying new AIMod payload... >> \"%LOGFILE%\"");
        bat.AppendLine("robocopy \"%PAYLOAD%\" \"%TARGET%\" /E /COPY:DAT /R:3 /W:2 >> \"%LOGFILE%\" 2>&1");
        bat.AppendLine("set \"ROBOCOPY_EXIT=%ERRORLEVEL%\"");
        bat.AppendLine("if %ROBOCOPY_EXIT% GEQ 8 goto copy_failed");
        bat.AppendLine("if not exist \"%TARGET%\\mod.json\" goto verify_failed");
        bat.AppendLine("if not exist \"%TARGET%\\%DLL_FILE%\" goto verify_failed");
        bat.AppendLine("if exist \"%ZIP_PATH%\" copy /Y \"%ZIP_PATH%\" \"%CACHE_FILE%\" >> \"%LOGFILE%\" 2>&1");
        bat.AppendLine("echo AIMod update installed successfully. >> \"%LOGFILE%\"");
        bat.AppendLine("REM ==== File-system stability wait (avoid 0xc0000142) ====");
        bat.AppendLine("timeout /t 1 /nobreak >nul");
        bat.AppendLine("dir \"%TARGET%\" >nul 2>&1");
        bat.AppendLine("timeout /t 1 /nobreak >nul");
        bat.AppendLine("goto restart_success");
        bat.AppendLine(":missing_payload");
        bat.AppendLine("echo ERROR: payload missing mod.json or required DLL %DLL_FILE%. >> \"%LOGFILE%\"");
        bat.AppendLine("goto restore_and_restart");
        bat.AppendLine(":backup_failed");
        bat.AppendLine("echo ERROR: failed to backup old AIMod directory. >> \"%LOGFILE%\"");
        bat.AppendLine("copy /Y \"%LOGFILE%\" \"%ERROR_LOGFILE%\" >nul 2>&1");
        bat.AppendLine("goto restart_failure");
        bat.AppendLine(":copy_failed");
        bat.AppendLine("echo ERROR: robocopy failed with code %ROBOCOPY_EXIT%. >> \"%LOGFILE%\"");
        bat.AppendLine("goto restore_and_restart");
        bat.AppendLine(":verify_failed");
        bat.AppendLine("echo ERROR: installed AIMod is missing mod.json or required DLL %DLL_FILE%. >> \"%LOGFILE%\"");
        bat.AppendLine("goto restore_and_restart");
        bat.AppendLine(":restore_and_restart");
        bat.AppendLine("if exist \"%TARGET%\" rmdir /s /q \"%TARGET%\" >> \"%LOGFILE%\" 2>&1");
        bat.AppendLine("if exist \"%BACKUP%\" (");
        bat.AppendLine("  echo Restoring old AIMod directory... >> \"%LOGFILE%\"");
        bat.AppendLine("  move \"%BACKUP%\" \"%TARGET%\" >> \"%LOGFILE%\" 2>&1");
        bat.AppendLine(")");
        bat.AppendLine("copy /Y \"%LOGFILE%\" \"%ERROR_LOGFILE%\" >nul 2>&1");
        bat.AppendLine("goto restart_failure");
        bat.AppendLine(":restart_success");
        bat.AppendLine("echo Restarting MDiceV2 after AIMod update. >> \"%LOGFILE%\"");
        bat.AppendLine("echo APP_ROOT=%APP_ROOT% >> \"%LOGFILE%\"");
        bat.AppendLine("echo EXE_PATH=%EXE_PATH% >> \"%LOGFILE%\"");
        bat.AppendLine("echo Working directory: %RESTART_WORKDIR% >> \"%LOGFILE%\"");
        bat.AppendLine("echo Current script directory: %CD% >> \"%LOGFILE%\"");
        bat.AppendLine("REM ==== Delay to let file locks fully release ====");
        bat.AppendLine("timeout /t 2 /nobreak >nul");
        bat.AppendLine("if exist \"%EXE_PATH%\" (");
        bat.AppendLine("  echo Restart target exists. Starting... >> \"%LOGFILE%\"");
        bat.AppendLine("  start \"\" /D \"%RESTART_WORKDIR%\" \"%EXE_PATH%\"");
        bat.AppendLine("  echo Start command issued. >> \"%LOGFILE%\"");
        bat.AppendLine(") else (");
        bat.AppendLine("  echo ERROR: restart target not found: %EXE_PATH% >> \"%LOGFILE%\"");
        bat.AppendLine("  echo Listing APP_ROOT: >> \"%LOGFILE%\"");
        bat.AppendLine("  dir \"%APP_ROOT%\" >> \"%LOGFILE%\" 2>&1");
        bat.AppendLine("  copy /Y \"%LOGFILE%\" \"%ERROR_LOGFILE%\" >nul 2>&1");
        bat.AppendLine(")");
        bat.AppendLine("endlocal");
        bat.AppendLine("del /f /q \"%~f0\" 2>nul");
        bat.AppendLine("exit /b 0");
        bat.AppendLine(":restart_failure");
        bat.AppendLine("echo Restarting MDiceV2 after AIMod update failure/recovery. >> \"%LOGFILE%\"");
        bat.AppendLine("echo APP_ROOT=%APP_ROOT% >> \"%LOGFILE%\"");
        bat.AppendLine("echo EXE_PATH=%EXE_PATH% >> \"%LOGFILE%\"");
        bat.AppendLine("echo Working directory: %RESTART_WORKDIR% >> \"%LOGFILE%\"");
        bat.AppendLine("echo Current script directory: %CD% >> \"%LOGFILE%\"");
        bat.AppendLine("REM ==== Delay to let file locks fully release ====");
        bat.AppendLine("timeout /t 2 /nobreak >nul");
        bat.AppendLine("if exist \"%EXE_PATH%\" (");
        bat.AppendLine("  echo Restart target exists. Starting... >> \"%LOGFILE%\"");
        bat.AppendLine("  start \"\" /D \"%RESTART_WORKDIR%\" \"%EXE_PATH%\"");
        bat.AppendLine("  echo Start command issued. >> \"%LOGFILE%\"");
        bat.AppendLine(") else (");
        bat.AppendLine("  echo ERROR: restart target not found: %EXE_PATH% >> \"%LOGFILE%\"");
        bat.AppendLine("  echo Listing APP_ROOT: >> \"%LOGFILE%\"");
        bat.AppendLine("  dir \"%APP_ROOT%\" >> \"%LOGFILE%\" 2>&1");
        bat.AppendLine("  copy /Y \"%LOGFILE%\" \"%ERROR_LOGFILE%\" >nul 2>&1");
        bat.AppendLine(")");
        bat.AppendLine("endlocal");
        bat.AppendLine("del /f /q \"%~f0\" 2>nul");
        bat.AppendLine("exit /b 1");

        await File.WriteAllTextAsync(scriptPath, bat.ToString(), Encoding.Default).ConfigureAwait(false);
        if (!File.Exists(scriptPath))
        {
            throw new IOException($"脚本未创建: {scriptPath}");
        }

        log($"AIMod 更新脚本已生成: {scriptPath}");
        log($"AIMod 更新脚本日志: {logPath}");
        return scriptPath;
    }

    private static void LaunchAIModUpdateScript(string scriptPath, Action<string> log)
    {
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("AIMod 更新脚本不存在", scriptPath);
        }

        var process = Process.Start(CustomUpdateManager.CreateStandardUpdateScriptStartInfo(scriptPath));

        if (process == null)
        {
            throw new InvalidOperationException("无法启动 AIMod 更新脚本");
        }

        log($"AIMod 更新脚本已启动: {scriptPath}, pid={process.Id}");
    }

    private static (string AssetName, string DownloadUrl, string VersionLabel)? FindLatestAIModAsset(JsonElement releases, string assetPrefix)
    {
        string? downloadUrl = null;
        string? assetName = null;
        DateTime bestPublishedAt = DateTime.MinValue;
        long bestNumericVersion = -1;

        foreach (var rel in releases.EnumerateArray())
        {
            var publishedAt = DateTime.MinValue;
            if (rel.TryGetProperty("published_at", out var publishedEl))
            {
                var publishedStr = publishedEl.GetString();
                if (!string.IsNullOrWhiteSpace(publishedStr) && DateTime.TryParse(publishedStr, out var parsedPublishedAt))
                {
                    publishedAt = parsedPublishedAt;
                }
            }

            if (!rel.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var nameEl))
                {
                    continue;
                }

                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name) ||
                    !name.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!asset.TryGetProperty("browser_download_url", out var urlEl))
                {
                    continue;
                }

                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var numericVersion = ParseAIModAssetNumericVersion(name, assetPrefix);
                var isBetter = publishedAt > bestPublishedAt ||
                               (publishedAt == bestPublishedAt && numericVersion > bestNumericVersion);

                if (isBetter)
                {
                    bestPublishedAt = publishedAt;
                    bestNumericVersion = numericVersion;
                    downloadUrl = url;
                    assetName = name;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(assetName))
        {
            return null;
        }

        return (assetName, downloadUrl, Path.GetFileNameWithoutExtension(assetName));
    }

    private static long ParseAIModAssetNumericVersion(string assetName, string assetPrefix)
    {
        if (assetName.Length <= assetPrefix.Length + ".zip".Length)
        {
            return -1;
        }

        var digits = assetName.Substring(assetPrefix.Length, assetName.Length - assetPrefix.Length - ".zip".Length);
        return long.TryParse(digits, out var parsedVersion) ? parsedVersion : -1;
    }

    private static void SafeExtractZip(string zipPath, string destinationDir)
    {
        var destinationFullPath = Path.GetFullPath(destinationDir);
        Directory.CreateDirectory(destinationFullPath);

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var entryPath = Path.GetFullPath(Path.Combine(destinationFullPath, entry.FullName));
            if (!entryPath.StartsWith(destinationFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !entryPath.Equals(destinationFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Zip 包包含非法路径: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(entryPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
            entry.ExtractToFile(entryPath, overwrite: true);
        }
    }

    private static string FindAIModPayloadRoot(string extractDir)
    {
        var dirsToCheck = new List<string> { extractDir };
        dirsToCheck.AddRange(Directory.GetDirectories(extractDir));

        var dirsWithModJson = new List<string>();
        var dirsWithMatchingManifest = new List<string>();
        var candidates = new List<string>();

        foreach (var dir in dirsToCheck)
        {
            var modJsonPath = Path.Combine(dir, "mod.json");
            if (!File.Exists(modJsonPath))
            {
                continue;
            }

            dirsWithModJson.Add(dir);
            if (TryResolveAIModPayloadCandidate(dir, out _))
            {
                dirsWithMatchingManifest.Add(dir);
                candidates.Add(dir);
            }
            else if (TryReadAIModManifest(modJsonPath, out var manifest) && IsExpectedAIModManifest(manifest))
            {
                dirsWithMatchingManifest.Add(dir);
            }
        }

        if (candidates.Count == 0)
        {
            if (dirsWithModJson.Count == 0)
            {
                throw new InvalidDataException("包结构错误，缺少 mod.json");
            }

            if (dirsWithMatchingManifest.Count == 0)
            {
                throw new InvalidDataException("包结构错误，mod.json 不是 AIMod 清单");
            }

            throw new InvalidDataException("包结构错误，缺少 AIMod.dll");
        }

        return candidates
            .OrderBy(dir => Path.GetFileName(dir).Equals("AIMod", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(dir => dir.Equals(extractDir, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(dir => dir, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    private static AIModManifestInfo ValidateAIModPayload(string payloadDir)
    {
        var modJsonPath = Path.Combine(payloadDir, "mod.json");
        if (!File.Exists(modJsonPath))
        {
            throw new InvalidDataException("包结构错误，缺少 mod.json");
        }

        if (!TryReadAIModManifest(modJsonPath, out var manifest))
        {
            throw new InvalidDataException("包结构错误，mod.json 无法解析");
        }

        if (!manifest.Id.Equals(AimodPackageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"包结构错误，mod.json id 必须为 {AimodPackageId}");
        }

        if (!manifest.DllFileName.Equals(DefaultAIModDllFileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"包结构错误，mod.json dllFileName 必须为 {DefaultAIModDllFileName}");
        }

        if (!manifest.PluginClassName.Equals(DefaultAIModPluginClassName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"包结构错误，mod.json pluginClassName 必须为 {DefaultAIModPluginClassName}");
        }

        if (!TryGetSafeRelativeFullPath(payloadDir, manifest.DllFileName, out var dllPath) || !File.Exists(dllPath))
        {
            throw new InvalidDataException($"包结构错误，缺少 {DefaultAIModDllFileName}");
        }

        var unexpectedArtifacts = GetUnexpectedAIModPayloadArtifacts(payloadDir, manifest.DllFileName)
            .Take(12)
            .ToList();
        if (unexpectedArtifacts.Count > 0)
        {
            throw new InvalidDataException(
                $"包结构错误，AIMod 更新包应为瘦插件包，不应包含: {string.Join(", ", unexpectedArtifacts)}");
        }

        return manifest;
    }

    private static bool TryResolveAIModPayloadCandidate(string payloadRoot, out AIModManifestInfo manifest)
    {
        manifest = default;

        var modJsonPath = Path.Combine(payloadRoot, "mod.json");
        if (!File.Exists(modJsonPath))
        {
            return false;
        }

        try
        {
            if (!TryReadAIModManifest(modJsonPath, out manifest) || !IsExpectedAIModManifest(manifest))
            {
                return false;
            }

            return TryGetSafeRelativeFullPath(payloadRoot, manifest.DllFileName, out var dllPath) && File.Exists(dllPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadAIModManifest(string modJsonPath, out AIModManifestInfo manifest)
    {
        manifest = default;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(modJsonPath));
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                ? (idElement.GetString() ?? string.Empty).Trim()
                : string.Empty;
            var dllFileName = root.TryGetProperty("dllFileName", out var dllElement) && dllElement.ValueKind == JsonValueKind.String
                ? (dllElement.GetString() ?? DefaultAIModDllFileName).Trim()
                : DefaultAIModDllFileName;
            var pluginClassName = root.TryGetProperty("pluginClassName", out var pluginElement) && pluginElement.ValueKind == JsonValueKind.String
                ? (pluginElement.GetString() ?? string.Empty).Trim()
                : string.Empty;

            dllFileName = dllFileName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            if (!IsSafeRelativePath(dllFileName))
            {
                return false;
            }

            manifest = new AIModManifestInfo(id, dllFileName, pluginClassName);
            return true;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"包结构错误，mod.json 无法解析: {ex.Message}", ex);
        }
    }

    private static bool IsExpectedAIModManifest(AIModManifestInfo manifest)
    {
        return manifest.Id.Equals(AimodPackageId, StringComparison.OrdinalIgnoreCase) &&
               manifest.DllFileName.Equals(DefaultAIModDllFileName, StringComparison.OrdinalIgnoreCase) &&
               manifest.PluginClassName.Equals(DefaultAIModPluginClassName, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetOptionalAIModPayloadWarnings(string payloadDir)
    {
        if (!File.Exists(Path.Combine(payloadDir, "ai-config.json")))
        {
            yield return "未发现 ai-config.json，将依赖宿主现有配置或后续手动补充";
        }

        if (!Directory.Exists(Path.Combine(payloadDir, "Assets")))
        {
            yield return "未发现 Assets 目录，若面板依赖图片资源可能导致 UI 显示不完整";
        }
    }

    private static IEnumerable<string> GetUnexpectedAIModPayloadArtifacts(string payloadDir, string allowedDllRelativePath)
    {
        var allowedDllFullPath = Path.GetFullPath(Path.Combine(payloadDir, allowedDllRelativePath));
        var runtimesDir = Path.Combine(payloadDir, "runtimes");
        if (Directory.Exists(runtimesDir))
        {
            yield return "runtimes/";
        }

        foreach (var filePath in Directory.EnumerateFiles(payloadDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(payloadDir, filePath).Replace('\\', '/');
            var fileName = Path.GetFileName(filePath);
            if (fileName.Equals(DefaultAIModPdbFileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (fileName.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                yield return relativePath;
                continue;
            }

            if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !Path.GetFullPath(filePath).Equals(allowedDllFullPath, StringComparison.OrdinalIgnoreCase))
            {
                yield return relativePath;
            }
        }
    }

    private static bool TryGetSafeRelativeFullPath(string rootDir, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (!IsSafeRelativePath(relativePath))
        {
            return false;
        }

        var rootFullPath = Path.GetFullPath(rootDir);
        fullPath = Path.GetFullPath(Path.Combine(rootFullPath, relativePath));
        return fullPath.Equals(rootFullPath, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(rootFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSafeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return false;
        }

        var invalidPathChars = Path.GetInvalidPathChars();
        var invalidBatchChars = new[] { '"', '<', '>', '|', '?', '*', ':', '%', '!', '&' };
        if (relativePath.Any(ch => invalidPathChars.Contains(ch) || invalidBatchChars.Contains(ch)))
        {
            return false;
        }

        var segments = relativePath
            .Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);

        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        return segments.Length > 0 &&
               segments.All(segment =>
                   segment != "." &&
                   segment != ".." &&
                   segment.Any() &&
                   !segment.Any(ch => invalidFileNameChars.Contains(ch))) &&
               relativePath.EndsWith(Path.DirectorySeparatorChar) == false &&
               relativePath.EndsWith(Path.AltDirectorySeparatorChar) == false;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(fileName.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? $"AIModPack_{Guid.NewGuid():N}.zip" : sanitized;
    }

    private static string EscapeBatchEcho(string value)
    {
        return Regex.Replace(value ?? string.Empty, @"[&|<>^]", string.Empty);
    }

    private static AiModUpdateScheduleResult FailAIModUpdate(
        string message,
        string? assetName = null,
        string? versionLabel = null,
        string? scriptPath = null,
        string? payloadDir = null)
    {
        return new AiModUpdateScheduleResult
        {
            Success = false,
            RequiresRestart = false,
            Message = message,
            AssetName = assetName,
            VersionLabel = versionLabel,
            ScriptPath = scriptPath,
            PayloadDir = payloadDir
        };
    }

    private readonly record struct RestartTargetInfo(
        string ExePath,
        string WorkingDirectory,
        IReadOnlyList<string> CandidatePaths,
        string Reason);

    private static RestartTargetInfo ResolveRestartTargetForUpdate(
        string appRoot, StartupMode startupMode, Action<string> log)
    {
        var currentProcessPath = Process.GetCurrentProcess().MainModule?.FileName;

        var candidates = new List<string>();

        if (startupMode == StartupMode.Console)
        {
            candidates.Add(Path.Combine(appRoot, "MDiceV2.Console.exe"));
            if (!string.IsNullOrWhiteSpace(currentProcessPath))
                candidates.Add(currentProcessPath);
            candidates.Add(Path.Combine(appRoot, "Core", "MDiceV2.Core.Dice"));
        }
        else
        {
            // UI mode: prioritize MDiceV2.Launcher.exe (user-confirmed entry point)
            candidates.Add(Path.Combine(appRoot, "MDiceV2.Launcher.exe"));
            if (!string.IsNullOrWhiteSpace(currentProcessPath))
                candidates.Add(currentProcessPath);
            candidates.Add(Path.Combine(appRoot, "Core", "MDiceV2.Core.Dice"));
        }

        // Collect all candidate paths for logging
        var allCandidates = new List<string>(candidates);
        if (startupMode == StartupMode.Console)
        {
            var launcherPath = Path.Combine(appRoot, "MDiceV2.Launcher.exe");
            if (!allCandidates.Contains(launcherPath, StringComparer.OrdinalIgnoreCase))
                allCandidates.Add(launcherPath);
        }
        else
        {
            var consolePath = Path.Combine(appRoot, "MDiceV2.Console.exe");
            if (!allCandidates.Contains(consolePath, StringComparer.OrdinalIgnoreCase))
                allCandidates.Add(consolePath);
        }

        if (!allCandidates.Contains(Path.Combine(appRoot, "Core", "MDiceV2.Core.Dice"), StringComparer.OrdinalIgnoreCase))
            allCandidates.Add(Path.Combine(appRoot, "Core", "MDiceV2.Core.Dice"));

        // Select the first existing candidate
        string? selectedPath = null;
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                selectedPath = candidate;
                break;
            }
        }

        string reason;
        if (selectedPath != null)
        {
            reason = $"Selected first existing candidate: {selectedPath}";
        }
        else
        {
            // Fallback to primary candidate even if it doesn't exist locally;
            // the bat script will log and handle the missing file.
            selectedPath = candidates[0];
            reason = $"No candidate exists locally, using primary candidate (bat will verify): {selectedPath}";
        }

        var workingDirectory = appRoot;

        log($"AIMod 更新后重启目标: mode={startupMode}, appRoot={appRoot}, exePath={selectedPath}, exists={File.Exists(selectedPath)}");
        log($"当前进程路径: {currentProcessPath ?? "N/A"}");
        log($"候选路径: {string.Join("; ", allCandidates)}");
        log($"选择理由: {reason}");

        return new RestartTargetInfo(selectedPath, workingDirectory, allCandidates.AsReadOnly(), reason);
    }

    private readonly record struct AIModManifestInfo(string Id, string DllFileName, string PluginClassName);
}
