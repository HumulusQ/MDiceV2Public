using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;

namespace DownloadTest
{
    public class GitHubAsset
    {
        public string name { get; set; }
        public long size { get; set; }
        public string browser_download_url { get; set; }
        public int id { get; set; }
        public string url { get; set; }
    }

    public class GitHubRelease
    {
        public string tag_name { get; set; }
        public List<GitHubAsset> assets { get; set; }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("测试GitHub文件下载（通过API）...");

            var githubToken = "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";
            var owner = "HumulusQ";
            var repo = "MDiceV2";
            var tag = "UpdatePackV3";
            var assetName = "MDiceV2.Core.dll";

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(2);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MDiceV2-CustomUpdater");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);

            try
            {
                // 找到目标release
                Console.WriteLine("=== 查找目标Release ===");
                var releasesUrl = $"https://api.github.com/repos/{owner}/{repo}/releases";
                var releasesResponse = await http.GetAsync(releasesUrl);
                
                if (releasesResponse.IsSuccessStatusCode)
                {
                    var json = await releasesResponse.Content.ReadAsStringAsync();
                    var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json);
                    
                    var targetRelease = releases?.FirstOrDefault(r => r.tag_name == tag);
                    var targetAsset = targetRelease?.assets?.FirstOrDefault(a => a.name == assetName);
                    
                    if (targetAsset != null)
                    {
                        Console.WriteLine($"找到文件: {targetAsset.name} ({targetAsset.size} bytes)");
                        Console.WriteLine($"browser_download_url: {targetAsset.browser_download_url}");
                        Console.WriteLine($"API asset url: {targetAsset.url}");

                        // 方法1: 尝试通过browser_download_url下载（不带token）
                        Console.WriteLine("\n=== 方法1: 通过browser_download_url（不带token） ===");
                        using (var httpNoToken = new HttpClient())
                        {
                            httpNoToken.Timeout = TimeSpan.FromMinutes(2);
                            httpNoToken.DefaultRequestHeaders.UserAgent.ParseAdd("MDiceV2-CustomUpdater");
                            
                            try
                            {
                                var downloadResponse = await httpNoToken.GetAsync(targetAsset.browser_download_url);
                                Console.WriteLine($"下载响应: {downloadResponse.StatusCode}");
                                
                                if (downloadResponse.IsSuccessStatusCode)
                                {
                                    var fileName = $"method1_noToken_{DateTime.Now:yyyyMMdd_HHmmss}.dll";
                                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                                    await using var fileStream = File.Create(filePath);
                                    await downloadResponse.Content.CopyToAsync(fileStream);
                                    Console.WriteLine($"方法1下载成功! 文件保存到: {filePath}");
                                }
                                else
                                {
                                    var errorContent = await downloadResponse.Content.ReadAsStringAsync();
                                    Console.WriteLine($"方法1下载失败: {downloadResponse.StatusCode}");
                                    Console.WriteLine($"错误信息: {errorContent}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"方法1异常: {ex.Message}");
                            }
                        }

                        // 方法2: 通过正确的GitHub API直接下载
                        Console.WriteLine("\n=== 方法2: 通过GitHub API直接下载（带token） ===");
                        try
                        {
                            // 使用正确的API端点下载assets
                            var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/assets/{targetAsset.id}";
                            Console.WriteLine($"API下载URL: {apiUrl}");
                            
                            var apiResponse = await http.GetAsync(apiUrl);
                            Console.WriteLine($"API下载响应: {apiResponse.StatusCode}");
                            
                            if (apiResponse.IsSuccessStatusCode)
                            {
                                var fileName = $"method2_api_{DateTime.Now:yyyyMMdd_HHmmss}.dll";
                                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                                await using var fileStream = File.Create(filePath);
                                await apiResponse.Content.CopyToAsync(fileStream);
                                Console.WriteLine($"方法2下载成功! 文件保存到: {filePath}");
                                Console.WriteLine($"文件大小: {new FileInfo(filePath).Length} bytes");
                            }
                            else
                            {
                                var error = await apiResponse.Content.ReadAsStringAsync();
                                Console.WriteLine($"方法2下载失败: {apiResponse.StatusCode} - {error}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"方法2异常: {ex.Message}");
                        }

                        // 方法3: 使用redirect URL访问（带token）
                        Console.WriteLine("\n=== 方法3: 使用带token的browser_download_url ===");
                        try
                        {
                            var browserUrlWithToken = targetAsset.browser_download_url;
                            Console.WriteLine($"带token的URL: {browserUrlWithToken}");
                            
                            var browserResponse = await http.GetAsync(browserUrlWithToken);
                            Console.WriteLine($"浏览器URL下载响应: {browserResponse.StatusCode}");
                            
                            if (browserResponse.IsSuccessStatusCode)
                            {
                                var fileName = $"method3_browserToken_{DateTime.Now:yyyyMMdd_HHmmss}.dll";
                                var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                                await using var fileStream = File.Create(filePath);
                                await browserResponse.Content.CopyToAsync(fileStream);
                                Console.WriteLine($"方法3下载成功! 文件保存到: {filePath}");
                                Console.WriteLine($"文件大小: {new FileInfo(filePath).Length} bytes");
                            }
                            else
                            {
                                var error = await browserResponse.Content.ReadAsStringAsync();
                                Console.WriteLine($"方法3下载失败: {browserResponse.StatusCode} - {error}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"方法3异常: {ex.Message}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"未找到目标文件: {assetName} 在 release {tag} 中");
                    }
                }
                else
                {
                    Console.WriteLine($"无法获取releases: {releasesResponse.StatusCode}");
                    var error = await releasesResponse.Content.ReadAsStringAsync();
                    Console.WriteLine($"错误信息: {error}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"异常: {ex.Message}");
                Console.WriteLine($"堆栈跟踪: {ex.StackTrace}");
            }

            Console.WriteLine("\n测试完成");
            Console.WriteLine("请查看生成的文件以验证下载结果。");
        }
    }
}