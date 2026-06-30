using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using MDiceV2.Core.UI.ViewModels;
using MDiceV2.Models;

namespace MDiceV2.Core.UI.Views;

/// <summary>
/// ModManagerPanel.axaml 代码后台
/// 处理文件选择对话框和文件操作
/// </summary>
public partial class ModManagerPanel : UserControl
{
    /// <summary>
    /// DataContext的ViewModel引用
    /// </summary>
    private ModManagerViewModel? _viewModel;

    /// <summary>
    /// Mod根目录路径
    /// </summary>
    private string _modRootPath = string.Empty;

    public ModManagerPanel()
    {
        InitializeComponent();
        InitializePaths();
        AttachViewModelEvents();
        
        // 注意：LoadMods() 已在 ModManagerViewModel 的构造函数中调用
        // 不要在这里再调用 RefreshMods()，避免重复加载
    }

    /// <summary>
    /// 初始化路径
    /// </summary>
    private void InitializePaths()
    {
        string projectPath = Directory.GetCurrentDirectory();
        _modRootPath = Path.Combine(projectPath, "mods");
        Directory.CreateDirectory(_modRootPath);
    }

    /// <summary>
    /// 附加ViewModel事件
    /// </summary>
    private void AttachViewModelEvents()
    {
        DataContextChanged += async (s, e) =>
        {
            _viewModel = DataContext as ModManagerViewModel;
            // No need to attach events, command will be handled by button click
        };
    }

    /// <summary>
    /// 处理添加Mod操作
    /// 打开文件选择对话框，允许用户选择Mod压缩包或文件夹
    /// </summary>
    public async Task HandleAddModAsync()
    {
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null)
            {
                Log.Warn("Cannot access StorageProvider for file dialog");
                return;
            }

            // 打开文件选择对话框
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Mod File or Folder",
                AllowMultiple = true,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Mod Files and Archives")
                    {
                        Patterns = new[] { "*.mod", "*.zip", "*.rar", "*.7z" }
                    },
                    new FilePickerFileType("All Files")
                    {
                        Patterns = new[] { "*" }
                    }
                }
            });

            foreach (var file in files)
            {
                try
                {
                    var filePath = file.Path.LocalPath;
                    await ProcessModFileAsync(filePath);
                }
                catch (Exception ex)
                {
                    Log.Error($"Error processing mod file: {ex.Message}");
                }
            }

            // 刷新Mod列表
            Log.InfoFormat("Refreshing mod list after adding mods...");
            if (_viewModel != null)
            {
                _viewModel.RefreshModsCommand.Execute(null);
                Log.InfoFormat($"Mod list refreshed. Total mods: {_viewModel.ModItems.Count}");
            }
            else
            {
                Log.Warn("ViewModel is null, cannot refresh mod list");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error opening file dialog: {ex.Message}");
        }
    }

    /// <summary>
    /// 处理Mod文件
    /// 直接复制压缩包或文件夹到Mod文件夹
    /// </summary>
    private async Task ProcessModFileAsync(string filePath)
    {
        await Task.Run(() =>
        {
            try
            {
                var fileName = Path.GetFileName(filePath);
                var fileExtension = Path.GetExtension(filePath).ToLower();

                Log.InfoFormat($"Processing mod file: {fileName} (extension: {fileExtension})");

                // 如果是压缩文件或mod文件，直接复制（不解压）
                // 支持 .zip, .rar, .7z, .mod 格式
                if (fileExtension == ".zip" || fileExtension == ".rar" || fileExtension == ".7z" || fileExtension == ".mod")
                {
                    CopyModFile(filePath);
                }
                else if (Directory.Exists(filePath))
                {
                    // 如果是目录，则直接复制文件夹
                    CopyModFolder(filePath);
                }
                else
                {
                    Log.Error($"File is not a valid mod file or directory: {filePath}");
                    throw new InvalidOperationException($"Invalid mod file: {filePath}");
                }

                Log.InfoFormat($"Mod file processed successfully: {fileName}");
            }
            catch (Exception ex)
            {
                Log.Error($"Error processing mod file {filePath}: {ex.Message}");
                throw;
            }
        });
    }

    /// <summary>
    /// 复制Mod压缩包文件
    /// </summary>
    private void CopyModFile(string sourceFile)
    {
        try
        {
            var fileName = Path.GetFileName(sourceFile);
            var destPath = Path.Combine(_modRootPath, fileName);

            // 如果目标文件或目录已存在，则添加时间戳
            if (File.Exists(destPath) || Directory.Exists(destPath))
            {
                // 如果存在同名目录，先删除它
                if (Directory.Exists(destPath))
                {
                    Directory.Delete(destPath, true);
                    Log.InfoFormat($"Deleted existing directory: {destPath}");
                }
                
                var nameWithoutExt = Path.GetFileNameWithoutExtension(sourceFile);
                var extension = Path.GetExtension(sourceFile);
                destPath = Path.Combine(_modRootPath, $"{nameWithoutExt}_{DateTime.Now:yyyyMMdd_HHmmss}{extension}");
            }

            File.Copy(sourceFile, destPath, true);
            Log.InfoFormat($"Copied mod file to: {destPath}");
        }
        catch (Exception ex)
        {
            Log.Error($"Error copying mod file: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 复制Mod文件夹
    /// </summary>
    private void CopyModFolder(string sourcePath)
    {
        try
        {
            var folderName = Path.GetFileName(sourcePath);
            var destPath = Path.Combine(_modRootPath, folderName);

            // 如果目标目录已存在，则添加时间戳
            if (Directory.Exists(destPath))
            {
                destPath = Path.Combine(_modRootPath, $"{folderName}_{DateTime.Now:yyyyMMdd_HHmmss}");
            }

            CopyDirectoryRecursive(sourcePath, destPath);

            Log.InfoFormat($"Copied mod folder to: {destPath}");
        }
        catch (Exception ex)
        {
            Log.Error($"Error copying mod folder: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 递归复制目录
    /// </summary>
    private void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);

        // 复制文件
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile, true);
        }

        // 递归复制子目录
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectoryRecursive(dir, destSubDir);
        }
    }

    /// <summary>
    /// 切换Mod状态按钮点击事件
    /// </summary>
    private void OnToggleModStateClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel != null && sender is Button button && button.DataContext is ModManagerViewModel.ModItem modItem)
        {
            _viewModel.SelectedMod = modItem;
            _viewModel.ToggleModStateCommand.Execute(null);
        }
    }

    /// <summary>
    /// 打开Mod文件夹按钮点击事件
    /// </summary>
    private void OnOpenModFolderClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel != null && sender is Button button && button.DataContext is ModManagerViewModel.ModItem modItem)
        {
            _viewModel.SelectedMod = modItem;
            _viewModel.OpenModFolderCommand.Execute(null);
        }
    }

    /// <summary>
    /// 删除Mod按钮点击事件
    /// </summary>
    private void OnRemoveModClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel != null && sender is Button button && button.DataContext is ModManagerViewModel.ModItem modItem)
        {
            _viewModel.SelectedMod = modItem;
            _viewModel.RemoveSelectedModCommand.Execute(null);
        }
    }

    /// <summary>
    /// 添加Mod按钮点击事件
    /// </summary>
    private async void OnAddModClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await HandleAddModAsync();
    }
}
