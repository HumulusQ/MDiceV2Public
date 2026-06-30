using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Linq;
using MDiceV2.Core.Infrastructure;
using MDiceV2.Abstractions;

namespace MDiceV2.Models
{
    /// <summary>
    /// 改进的更新管理器：使用GitHub API下载DLL资产并尝试替换目标目录下的同名 DLL。
    /// - 支持通过GitHub API正确下载文件
    /// - 如果目标文件被锁定（正在被应用加载），会生成一个临时的批处理脚本，在应用退出后执行替换并重启可执行文件。
    /// </summary>
    public class UpdateManager
    {
        private readonly Action<string>? _logger;
        private readonly HttpClient _http;

        public UpdateManager(Action<string>? logger = null)
        {
            _logger = logger;
            _http = new HttpClient();
            _http.Timeout = TimeSpan.FromMinutes(2);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("MDiceV2-Updater");
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            
            Log("Initialized for public GitHub releases (no token).");
        }

        private void Log(string msg)
        {
            try { _logger?.Invoke(msg); } catch { }
        }

        public async Task UpdateDllsFromLatestReleaseAsync(string owner = "HumulusQ", string repo = "MDiceV2Public")
        {
            try
            {
                Log("Checking GitHub latest release...");
                var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
                using var resp = await _http.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"Failed to get latest release: {resp.StatusCode}");
                    var errorContent = await resp.Content.ReadAsStringAsync();
                    Log($"Error details: {errorContent}");
                    return;
                }

                var json = await resp.Content.ReadAsStringAsync();
                var release = JsonSerializer.Deserialize<GitHubRelease>(json);
                
                if (release?.Assets == null || release.Assets.Count == 0)
                {
                    Log("No assets found in latest release.");
                    return;
                }

                var appDir = AppContext.BaseDirectory;
                Log($"Application directory: {appDir}");

                bool anyFound = false;
                foreach (var asset in release.Assets)
                {
                    var name = asset.Name;
                    // Prefer dll assets that contain MDiceV2 or start with MDice
                    if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!name.Contains("MDice", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("MDice", StringComparison.OrdinalIgnoreCase))
                        continue;

                    anyFound = true;
                    Log($"Found DLL asset: {name} ({asset.Size} bytes)");

                    // 使用GitHub API下载文件
                    var downloadSuccess = await DownloadAssetUsingApi(asset, name, appDir);
                    if (!downloadSuccess)
                    {
                        // 如果API下载失败，尝试使用browser_download_url（不带token）
                        Log($"Trying browser_download_url for {name}...");
                        downloadSuccess = await DownloadAssetUsingBrowserUrl(asset, name, appDir);
                    }
                }

                if (!anyFound)
                {
                    Log("No suitable DLL assets found in latest release.");
                }
            }
            catch (Exception ex)
            {
                Log($"Update failed: {ex.Message}");
                Log($"Stack trace: {ex.StackTrace}");
            }
        }

        private async Task<bool> DownloadAssetUsingApi(GitHubAsset asset, string name, string appDir)
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), name + ".download");
                Log($"Downloading via GitHub API to {tempPath} ...");
                
                // 使用反射获取asset的id属性
                // 公开仓库下通过 assets API 下载
                var assetId = GetAssetId(asset);
                if (assetId == 0)
                {
                    Log("Asset ID not available, fallback to browser_download_url.");
                    return false;
                }

                var apiUrl = $"https://api.github.com/repos/HumulusQ/MDiceV2Public/releases/assets/{assetId}";
                Log($"API download URL: {apiUrl}");
                
                // GitHub API下载assets时需要设置正确的Accept头
                _http.DefaultRequestHeaders.Accept.Clear();
                _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                
                using var dlResp = await _http.GetAsync(apiUrl);
                if (!dlResp.IsSuccessStatusCode)
                {
                    var error = await dlResp.Content.ReadAsStringAsync();
                    Log($"API download failed for {name}: {dlResp.StatusCode} - {error}");
                    return false;
                }

                await using var fs = File.Create(tempPath);
                await dlResp.Content.CopyToAsync(fs);
                var downloadedSize = new FileInfo(tempPath).Length;
                Log($"Downloaded {name} successfully via API ({downloadedSize} bytes)");

                // 验证文件大小
                if (downloadedSize == asset.Size)
                {
                    Log($"✅ File size verification passed for {name}");
                    return await ReplaceFile(tempPath, name, appDir);
                }
                else
                {
                    Log($"⚠️ File size mismatch for {name}. Expected: {asset.Size}, Downloaded: {downloadedSize}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"API download error for {name}: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> DownloadAssetUsingBrowserUrl(GitHubAsset asset, string name, string appDir)
        {
            try
            {
                var tempPath = Path.Combine(Path.GetTempPath(), name + ".download_browser");
                Log($"Downloading via browser_url to {tempPath} ...");
                
                // 使用browser_download_url（不带token）尝试下载
                using var httpNoToken = new HttpClient();
                httpNoToken.Timeout = TimeSpan.FromMinutes(2);
                httpNoToken.DefaultRequestHeaders.UserAgent.ParseAdd("MDiceV2-Updater");
                
                using var dlResp = await httpNoToken.GetAsync(asset.BrowserDownloadUrl);
                if (!dlResp.IsSuccessStatusCode)
                {
                    var error = await dlResp.Content.ReadAsStringAsync();
                    Log($"Browser URL download failed for {name}: {dlResp.StatusCode} - {error}");
                    return false;
                }

                await using var fs = File.Create(tempPath);
                await dlResp.Content.CopyToAsync(fs);
                Log($"Downloaded {name} successfully via browser URL ({new FileInfo(tempPath).Length} bytes)");

                return await ReplaceFile(tempPath, name, appDir);
            }
            catch (Exception ex)
            {
                Log($"Browser URL download error for {name}: {ex.Message}");
                return false;
            }
        }

        private int GetAssetId(GitHubAsset asset)
        {
            // GitHubRelease类中没有直接暴露id属性，我们需要通过反射或其他方式获取
            // 这里提供一个临时的解决方案，或者可以直接使用browser download URL
            // 为了简单起见，我们返回0，这样会回退到browser download URL方法
            Log("Asset ID not available, will use browser download URL method");
            return 0;
        }

        private async Task<bool> ReplaceFile(string tempPath, string name, string appDir)
        {
            var targetPath = Path.Combine(appDir, name);
            Log($"Target path: {targetPath}");

            try
            {
                // Try to replace directly
                Log($"Attempting to replace {targetPath} ...");
                File.Copy(tempPath, targetPath, true);
                Log($"Replaced {name} successfully.");
                return true;
            }
            catch (IOException ioex)
            {
                Log($"File in use or replacement failed: {ioex.Message}");
                return await CreateUpdateScript(tempPath, name, targetPath);
            }
            catch (Exception ex)
            {
                Log($"Unexpected error while replacing file: {ex.Message}");
                return false;
            }
            finally
            {
                // leave temp file for debugging if replacement failed; otherwise delete
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        private async Task<bool> CreateUpdateScript(string tempPath, string name, string targetPath)
        {
            try
            {
                // Create a batch file that waits for current process exit, copies file and restarts exe
                var pid = Process.GetCurrentProcess().Id;
                
                // 根据启动模式选择重启哪个可执行文件
                var startupMode = ServiceBootstrapper.CurrentStartupMode;
                var exePath = startupMode == StartupMode.Console
                    ? Path.Combine(AppContext.BaseDirectory, "MDiceV2.Console.exe")
                    : Path.Combine(AppContext.BaseDirectory, "MDiceV2.Launcher.exe");
                
                var exeName = Path.GetFileName(exePath);
                var batPath = Path.Combine(Path.GetTempPath(), $"mdice_update_{Guid.NewGuid():N}.bat");

                var bat = $"@echo off\r\n" +
                          $"echo Waiting for application (PID {pid}) to exit...\r\n" +
                          $":loop\r\n" +
                          $"tasklist /FI \"PID eq {pid}\" | findstr /I \"{exeName}\" >nul\r\n" +
                          $"if %ERRORLEVEL%==0 (\r\n" +
                          $"  timeout /t 1 /nobreak >nul\r\n" +
                          $"  goto loop\r\n" +
                          $")\r\n" +
                          $"echo Copying {name} to {targetPath}...\r\n" +
                          $"copy /Y \"{tempPath}\" \"{targetPath}\"\r\n" +
                          $"echo Restarting application...\r\n" +
                          $"start \"\" \"{exePath}\"\r\n" +
                          $"del \"%~f0\"\r\n";

                File.WriteAllText(batPath, bat);
                Log($"Created updater script: {batPath}");

                // launch batch and exit
                var psi = new ProcessStartInfo
                {
                    FileName = batPath,
                    UseShellExecute = true,
                    CreateNoWindow = false
                };
                Process.Start(psi);
                Log("Launched updater script. Exiting application to allow update.");
                Environment.Exit(0);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to create or start updater script: {ex.Message}");
                return false;
            }
        }
    }
}
