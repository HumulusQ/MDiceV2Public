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
            Console.WriteLine("GitHub下载修复测试...");

            var githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN")
                               ?? Environment.GetEnvironmentVariable("GH_TOKEN");

            if (string.IsNullOrWhiteSpace(githubToken))
            {
                var tokenPath = Path.Combine(AppContext.BaseDirectory, "token.txt");
                if (File.Exists(tokenPath))
                {
                    githubToken = File.ReadLines(tokenPath).FirstOrDefault()?.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(githubToken))
            {
                Console.WriteLine("未找到 GitHub token，请在环境变量 GITHUB_TOKEN/GH_TOKEN 或根目录 token.txt 提供。");
                return;
            }
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
                // 查找目标release
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
                        Console.WriteLine($"Asset ID: {targetAsset.id}");
                        Console.WriteLine($"browser_download_url: {targetAsset.browser_download_url}");

                        // 方法1: 通过GitHub API下载（推荐方法）
                        Console.WriteLine("\n=== 方法1: 通过GitHub API下载（推荐） ===");
                        var success1 = await DownloadViaApi(http, targetAsset, assetName);
                        if (success1)
                        {
                            Console.WriteLine("✅ API下载成功！这是推荐的方法。");
                        }
                        else
                        {
                            Console.WriteLine("❌ API下载失败，尝试备用方法...");
                        }

                        // 方法2: 通过browser_download_url（带token）
                        Console.WriteLine("\n=== 方法2: 通过browser_download_url（带token） ===");
                        var success2 = await DownloadViaBrowserUrl(http, targetAsset, assetName);
                        if (success2)
                        {
                            Console.WriteLine("✅ 浏览器URL下载成功！");
                        }
                        else
                        {
                            Console.WriteLine("❌ 浏览器URL下载失败。");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"未找到目标文件: {assetName} 在 release {tag} 中");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"异常: {ex.Message}");
            }

            Console.WriteLine("\n测试完成");
        }

        static async Task<bool> DownloadViaApi(HttpClient http, GitHubAsset asset, string assetName)
        {
            try
            {
                var apiUrl = $"https://api.github.com/repos/HumulusQ/MDiceV2/releases/assets/{asset.id}";
                Console.WriteLine($"API URL: {apiUrl}");
                
                var response = await http.GetAsync(apiUrl);
                Console.WriteLine($"响应状态: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    var fileName = $"api_download_{DateTime.Now:yyyyMMdd_HHmmss}.dll";
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    await using var fileStream = File.Create(filePath);
                    await response.Content.CopyToAsync(fileStream);
                    Console.WriteLine($"文件保存到: {filePath}");
                    Console.WriteLine($"实际文件大小: {new FileInfo(filePath).Length} bytes");
                    return true;
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"下载失败: {response.StatusCode} - {error}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"API下载异常: {ex.Message}");
                return false;
            }
        }

        static async Task<bool> DownloadViaBrowserUrl(HttpClient http, GitHubAsset asset, string assetName)
        {
            try
            {
                Console.WriteLine($"浏览器URL: {asset.browser_download_url}");
                
                var response = await http.GetAsync(asset.browser_download_url);
                Console.WriteLine($"响应状态: {response.StatusCode}");
                
                if (response.IsSuccessStatusCode)
                {
                    var fileName = $"browser_download_{DateTime.Now:yyyyMMdd_HHmmss}.dll";
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    await using var fileStream = File.Create(filePath);
                    await response.Content.CopyToAsync(fileStream);
                    Console.WriteLine($"文件保存到: {filePath}");
                    Console.WriteLine($"实际文件大小: {new FileInfo(filePath).Length} bytes");
                    return true;
                }
                else
                {
                    var error = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"下载失败: {response.StatusCode} - {error}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"浏览器URL下载异常: {ex.Message}");
                return false;
            }
        }
    }
}