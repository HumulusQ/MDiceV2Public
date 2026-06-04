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
                
                // GitHub API下载assets时需要设置正确的Accept头
                http.DefaultRequestHeaders.Accept.Clear();
                http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
                
                var response = await http.GetAsync(apiUrl);
                Console.WriteLine($"响应状态: {response.StatusCode}");
                Console.WriteLine($"Content-Type: {response.Content.Headers.ContentType}");
                Console.WriteLine($"Content-Length: {response.Content.Headers.ContentLength}");
                
                if (response.IsSuccessStatusCode)
                {
                    var fileName = $"api_download_fixed_{DateTime.Now:yyyyMMdd_HHmmss}.dll";
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), fileName);
                    await using var fileStream = File.Create(filePath);
                    await response.Content.CopyToAsync(fileStream);
                    Console.WriteLine($"文件保存到: {filePath}");
                    Console.WriteLine($"实际文件大小: {new FileInfo(filePath).Length} bytes");
                    
                    // 验证文件大小
                    var actualSize = new FileInfo(filePath).Length;
                    if (actualSize == asset.size)
                    {
                        Console.WriteLine("✅ 文件大小匹配！下载成功。");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"⚠️ 文件大小不匹配。预期: {asset.size}, 实际: {actualSize}");
                        return false;
                    }
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
