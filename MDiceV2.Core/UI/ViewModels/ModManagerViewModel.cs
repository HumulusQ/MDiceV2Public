using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MDiceV2.Core.Mod;
using MDiceV2.Models;

#nullable enable

namespace MDiceV2.Core.UI.ViewModels;

/// <summary>
/// <summary>
/// Mod管理ViewModel
/// 管理Mod列表、启用/禁用状态、添加/删除Mod
/// </summary>
public partial class ModManagerViewModel : ObservableObject
{
    /// <summary>
    /// Mod信息项
    /// </summary>
    public partial class ModItem : ObservableObject
    {
        [ObservableProperty]
        private string name = string.Empty;

        [ObservableProperty]
        private string author = string.Empty;

        [ObservableProperty]
        private string version = string.Empty;

        [ObservableProperty]
        private bool isEnabled = false;

        [ObservableProperty]
        private string modPath = string.Empty;

        [ObservableProperty]
        private string modId = string.Empty;

        [ObservableProperty]
        private bool isRuntimeManaged;

        [ObservableProperty]
        private bool isSwitching;

        /// <summary>
        /// 返回包含的内容描述（脚本/资源类型）
        /// </summary>
        [ObservableProperty]
        private string containType = string.Empty;

        [ObservableProperty]
        private bool isSelected = false;

        /// <summary>
        /// 状态颜色 - 绑定到UI的状态指示器
        /// 绿色（#00AA00）=启用，红色（#AA0000）=禁用
        /// </summary>
        [ObservableProperty]
        private string stateColor = "#AA0000";

        /// <summary>
        /// 状态文本 - "Enabled" 或 "Disabled"
        /// </summary>
        public string StateText => IsRuntimeManaged ? (IsEnabled ? "Enabled" : "Disabled") : "Unavailable";

        /// <summary>
        /// 按钮文本 - 显示要执行的操作
        /// </summary>
        public string ButtonText => IsSwitching ? "WORKING" : IsRuntimeManaged ? (IsEnabled ? "Disable" : "Enable") : "UNAVAILABLE";

        public bool CanToggle => IsRuntimeManaged && !IsSwitching;

        public ModItem()
        {
            PropertyChanged += (s, e) =>
            {
                if (e.PropertyName is nameof(IsEnabled) or nameof(IsRuntimeManaged))
                {
                    StateColor = !IsRuntimeManaged ? "#64748B" : IsEnabled ? "#00AA00" : "#AA0000";
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(ButtonText));
                    OnPropertyChanged(nameof(CanToggle));
                }
                else if (e.PropertyName == nameof(IsSwitching))
                {
                    OnPropertyChanged(nameof(ButtonText));
                    OnPropertyChanged(nameof(CanToggle));
                }
            };
        }
    }

    /// <summary>
    /// 所有Mod项集合
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ModItem> modItems = new();

    /// <summary>
    /// 搜索/过滤文本
    /// </summary>
    [ObservableProperty]
    private string searchText = string.Empty;

    /// <summary>
    /// 过滤后的Mod项集合
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ModItem> filteredModItems = new();

    /// <summary>
    /// Mod文件夹根路径
    /// </summary>
    private string _modRootPath = string.Empty;

    /// <summary>
    /// 标题文本
    /// </summary>
    [ObservableProperty]
    private string title = "Mod Manager";

    /// <summary>
    /// 无Mod时的提示文本
    /// </summary>
    [ObservableProperty]
    private string emptyMessage = "No mods installed. Click 'Add Mod' to get started.";

    /// <summary>
    /// 是否显示空状态
    /// </summary>
    [ObservableProperty]
    private bool isEmptyState = true;

    /// <summary>
    /// 选中的Mod项
    /// </summary>
    [ObservableProperty]
    private ModItem? selectedMod = null;

    public ModManagerViewModel()
    {
        InitializeModPath();
        LoadMods();
    }

    /// <summary>
    /// 初始化Mod文件夹路径
    /// </summary>
    private void InitializeModPath()
    {
        string projectPath = Directory.GetCurrentDirectory();
        _modRootPath = Path.Combine(projectPath, "mods");
        Directory.CreateDirectory(_modRootPath);
        Log.InfoFormat($"Mod root path initialized: {_modRootPath}");
    }

    /// <summary>
    /// 从磁盘加载所有Mod
    /// </summary>
    private void LoadMods()
    {
        try
        {
            Log.InfoFormat($"LoadMods() called. Mod root path: {_modRootPath}");
            ModItems.Clear();

            if (!Directory.Exists(_modRootPath))
            {
                Log.Warn($"Mod directory does not exist: {_modRootPath}");
                IsEmptyState = true;
                return;
            }

            var modDirs = Directory.GetDirectories(_modRootPath);
            var modFiles = Directory.GetFiles(_modRootPath, "*.mod");
            
            Log.InfoFormat($"Found {modDirs.Length} mod directories and {modFiles.Length} mod files");
            
            // 加载目录形式的mod
            foreach (var modDir in modDirs)
            {
                try
                {
                    var modName = Path.GetFileName(modDir);
                    Log.InfoFormat($"Loading mod directory: {modName}");
                    var configPath = Path.Combine(modDir, "mod.json");

                    var modItem = new ModItem
                    {
                        Name = modName,
                        ModPath = modDir,
                        IsEnabled = !File.Exists(Path.Combine(modDir, ".disabled")),
                        Author = "Unknown",
                        Version = "1.0.0"
                    };

                    // 尝试从mod.json读取元数据
                    if (File.Exists(configPath))
                    {
                        try
                        {
                            var json = File.ReadAllText(configPath);
                            // 这里可以用JsonSerializer解析，简单实现则直接读取关键字段
                            if (json.Contains("\"author\""))
                            {
                                var authorMatch = System.Text.RegularExpressions.Regex.Match(json, @"""author"":\s*""([^""]+)""");
                                if (authorMatch.Success)
                                    modItem.Author = authorMatch.Groups[1].Value;
                            }
                            if (json.Contains("\"version\""))
                            {
                                var versionMatch = System.Text.RegularExpressions.Regex.Match(json, @"""version"":\s*""([^""]+)""");
                                if (versionMatch.Success)
                                    modItem.Version = versionMatch.Groups[1].Value;
                            }
                            if (json.Contains("\"id\""))
                            {
                                var idMatch = System.Text.RegularExpressions.Regex.Match(json, @"""id"":\s*""([^""]+)""");
                                if (idMatch.Success)
                                    modItem.ModId = idMatch.Groups[1].Value;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warn($"Failed to parse mod.json for {modName}: {ex.Message}");
                        }
                    }

                    // 检查包含内容
                    modItem.ContainType = DetermineMissingContent(modDir);
                    AttachRuntimeState(modItem);

                    ModItems.Add(modItem);
                    Log.InfoFormat($"Added mod to ModItems: {modName}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to load mod from {modDir}: {ex.Message}");
                }
            }
            
            // 加载 .mod 文件（压缩包形式的mod）
            foreach (var modFile in modFiles)
            {
                try
                {
                    var modName = Path.GetFileNameWithoutExtension(modFile);
                    Log.InfoFormat($"Loading mod file: {modName}");
                    
                    var modItem = new ModItem
                    {
                        Name = modName,
                        ModPath = modFile,
                        IsEnabled = !File.Exists(modFile + ".disabled"),
                        Author = "Unknown",
                        Version = "1.0.0",
                        ContainType = "Compressed Archive (.mod)"
                    };

                    ModItems.Add(modItem);
                    Log.InfoFormat($"Added mod file to ModItems: {modName}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to load mod from {modFile}: {ex.Message}");
                }
            }

            IsEmptyState = ModItems.Count == 0;
            Log.InfoFormat($"LoadMods() completed. IsEmptyState: {IsEmptyState}, ModItems count: {ModItems.Count}");
            RefreshFilteredMods();

            Log.InfoFormat($"Loaded {ModItems.Count} mods from {_modRootPath}");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to load mods: {ex.Message}");
            IsEmptyState = true;
        }
    }

    /// <summary>
    /// 刷新过滤后的Mod列表
    /// </summary>
    private void RefreshFilteredMods()
    {
        Log.InfoFormat($"RefreshFilteredMods called. ModItems count: {ModItems.Count}, SearchText: {SearchText}");
        
        FilteredModItems.Clear();

        var filtered = string.IsNullOrWhiteSpace(SearchText)
            ? ModItems.ToList()
            : ModItems.Where(m =>
                m.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                m.Author.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

        Log.InfoFormat($"Filtered mods count: {filtered.Count}");
        
        foreach (var mod in filtered)
        {
            FilteredModItems.Add(mod);
            Log.InfoFormat($"Added mod to FilteredModItems: {mod.Name}");
        }
        
        Log.InfoFormat($"RefreshFilteredMods completed. FilteredModItems count: {FilteredModItems.Count}");
    }

    /// <summary>
    /// 搜索文本变更
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        RefreshFilteredMods();
    }

    /// <summary>
    /// 添加Mod命令
    /// </summary>
    [RelayCommand]
    public async Task AddMod()
    {
        try
        {
            // 这里会由代码后台处理文件选择对话框
            // 参考ConfigContainer.axaml.cs的模式
            Log.InfoFormat("AddMod command executed");
        }
        catch (Exception ex)
        {
            Log.Error($"Error adding mod: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除选中的Mod
    /// </summary>
    [RelayCommand]
    public void RemoveSelectedMod()
    {
        if (SelectedMod != null)
        {
            try
            {
                // 删除Mod文件夹
                if (Directory.Exists(SelectedMod.ModPath))
                {
                    Directory.Delete(SelectedMod.ModPath, true);
                    Log.InfoFormat($"Deleted mod: {SelectedMod.Name}");
                }

                ModItems.Remove(SelectedMod);
                RefreshFilteredMods();
                IsEmptyState = ModItems.Count == 0;
            }
            catch (Exception ex)
            {
                Log.Error($"Error removing mod: {ex.Message}");
            }
        }
    }

    private static void AttachRuntimeState(ModItem modItem)
    {
        var bridge = RuntimeModInitializer.Current?.ModEventBridge;
        if (bridge == null)
            return;

        if (string.IsNullOrWhiteSpace(modItem.ModId))
        {
            var match = bridge.GetAllMods()
                .FirstOrDefault(x => string.Equals(x.Key, modItem.Name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.Value.Metadata.Name, modItem.Name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Key))
                modItem.ModId = match.Key;
        }

        if (string.IsNullOrWhiteSpace(modItem.ModId))
            return;

        var status = bridge.GetModStatus(modItem.ModId);
        if (status == null)
            return;

        modItem.IsRuntimeManaged = true;
        modItem.IsEnabled = status.Value.IsEnabled;
    }

    private static string GetDisabledMarkerPath(ModItem modItem)
    {
        return Directory.Exists(modItem.ModPath)
            ? Path.Combine(modItem.ModPath, ".disabled")
            : modItem.ModPath + ".disabled";
    }

    private static void SetDisabledMarker(ModItem modItem, bool isDisabled)
    {
        var markerPath = GetDisabledMarkerPath(modItem);
        if (isDisabled)
        {
            File.WriteAllText(markerPath, string.Empty);
        }
        else if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }
    }

    /// <summary>
    /// Toggle an active runtime mod and retain that choice for the next launch.
    /// </summary>
    [RelayCommand]
    public async Task ToggleModStateAsync()
    {
        var modItem = SelectedMod;
        if (modItem == null || !modItem.CanToggle)
            return;

        modItem.IsSwitching = true;
        try
        {
            // Let the button render its working state before plugin lifecycle work starts.
            await Task.Yield();

            var bridge = RuntimeModInitializer.Current?.ModEventBridge;
            var status = bridge?.GetModStatus(modItem.ModId);
            if (bridge == null || status == null)
            {
                modItem.IsRuntimeManaged = false;
                Log.Warn($"Mod '{modItem.Name}' is not available in the current runtime.");
                return;
            }

            var enable = !status.Value.IsEnabled;
            var markerPreviouslyExisted = File.Exists(GetDisabledMarkerPath(modItem));
            try
            {
                SetDisabledMarker(modItem, isDisabled: !enable);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not persist the state for mod '{modItem.Name}': {ex.Message}");
                return;
            }

            var succeeded = enable ? bridge.EnableMod(modItem.ModId) : bridge.DisableMod(modItem.ModId);
            if (!succeeded)
            {
                try
                {
                    SetDisabledMarker(modItem, isDisabled: markerPreviouslyExisted);
                }
                catch (Exception rollbackException)
                {
                    Log.Warn($"Could not restore the saved state for '{modItem.Name}': {rollbackException.Message}");
                }

                return;
            }

            modItem.IsEnabled = enable;
            Log.InfoFormat($"{(enable ? "Enabled" : "Disabled")} mod at runtime: {modItem.Name} ({modItem.ModId})");
        }
        catch (Exception ex)
        {
            Log.Error($"Error toggling mod state: {ex.Message}");
        }
        finally
        {
            modItem.IsSwitching = false;
        }
    }

    /// <summary>
    /// 确定Mod包含的内容类型
    /// </summary>
    private string DetermineMissingContent(string modPath)
    {
        var parts = new System.Collections.Generic.List<string>();

        // 检查是否有脚本文件（.lua）
        if (Directory.GetFiles(modPath, "*.lua", SearchOption.AllDirectories).Length > 0)
        {
            parts.Add("Script");
        }

        // 检查是否有资源文件（图片、音频等）
        var resourceExtensions = new[] { ".png", ".jpg", ".jpeg", ".wav", ".mp3", ".ogg" };
        if (Directory.GetFiles(modPath, "*.*", SearchOption.AllDirectories)
            .Any(f => resourceExtensions.Contains(Path.GetExtension(f).ToLower())))
        {
            parts.Add("Resource");
        }

        // 检查是否有配置文件
        if (File.Exists(Path.Combine(modPath, "mod.json")) ||
            File.Exists(Path.Combine(modPath, "config.json")))
        {
            parts.Add("Config");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "Unknown";
    }

    /// <summary>
    /// 刷新Mod列表
    /// </summary>
    [RelayCommand]
    public void RefreshMods()
    {
        LoadMods();
        Log.InfoFormat("Mod list refreshed");
    }

    /// <summary>
    /// 打开Mod文件夹
    /// </summary>
    [RelayCommand]
    public void OpenModFolder()
    {
        try
        {
            if (SelectedMod != null && Directory.Exists(SelectedMod.ModPath))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = SelectedMod.ModPath,
                    UseShellExecute = true
                });
            }
            else
            {
                // 打开Mod根目录
                if (Directory.Exists(_modRootPath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _modRootPath,
                        UseShellExecute = true
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error opening mod folder: {ex.Message}");
        }
    }
}
