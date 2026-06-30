using Avalonia.Controls;
using MDiceV2.Interfaces.Mod;
using Semi.Avalonia;
using System;
using AIMod;

namespace AIMod.UI
{
    public partial class AIModPanel : UserControl
    {
        private readonly IConfigurable _configurable;
        private Func<Task>? _testListModelsCallback;

        public AIModPanel(IConfigurable configurable, Func<Task>? testListModelsCallback = null)
        {
            try
            {
                Console.WriteLine("[AIModPanel] >>> ctor START");
                Console.WriteLine("[AIModPanel] >>> Before InitializeComponent");
                InitializeComponent();
                Console.WriteLine("[AIModPanel] >>> After InitializeComponent");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AIModPanel] >>> InitializeComponent FAILED: {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[AIModPanel] >>> StackTrace: {ex.StackTrace}");
                if (ex.InnerException != null)
                    Console.WriteLine($"[AIModPanel] >>> Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                throw;
            }

            _configurable = configurable;
            _testListModelsCallback = testListModelsCallback;

            Console.WriteLine("[AIModPanel] >>> Before LoadConfig");
            LoadConfig();
            Console.WriteLine("[AIModPanel] >>> After LoadConfig");

            Console.WriteLine("[AIModPanel] >>> Before FindControl / binding");
            var saveButton = this.FindControl<Button>("SaveButton");
            saveButton.Click += SaveButton_Click;

            var testButton = this.FindControl<Button>("TestListModelsButton");
            if (testButton != null)
                testButton.Click += TestListModelsButton_Click;

            var modelComboBox = this.FindControl<ComboBox>("ModelSelectionComboBox");
            if (modelComboBox != null)
                modelComboBox.SelectionChanged += ModelSelectionComboBox_SelectionChanged;

            var modeComboBox = this.FindControl<ComboBox>("ModeSelectionComboBox");
            if (modeComboBox != null)
                modeComboBox.SelectionChanged += ModeSelectionComboBox_SelectionChanged;

            _configurable.ConfigChanged += OnConfigChanged;
            this.Unloaded += (s, e) => _configurable.ConfigChanged -= OnConfigChanged;
            Console.WriteLine("[AIModPanel] >>> ctor END");
        }

        private void ModeSelectionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateVisibility();
        }

        private void ModelSelectionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            var modeComboBox = this.FindControl<ComboBox>("ModeSelectionComboBox");
            var modelComboBox = this.FindControl<ComboBox>("ModelSelectionComboBox");

            var geminiBorder = this.FindControl<Border>("GeminiConfigBorder");
            var zhipuBorder = this.FindControl<Border>("ZhipuConfigBorder");
            var siliconflowBorder = this.FindControl<Border>("SiliconFlowConfigBorder");
            var deepseekBorder = this.FindControl<Border>("DeepSeekConfigBorder");
            var prefixModeBorder = this.FindControl<Border>("PrefixModeBorder");
            var trpgConfigBorder = this.FindControl<Border>("TrpgConfigBorder");
            var trpgGuideBorder = this.FindControl<Border>("TrpgGuideBorder");
            var systemPromptBorder = this.FindControl<Border>("SystemPromptBorder");

            int modeIndex = modeComboBox?.SelectedIndex ?? 0;
            int modelIndex = modelComboBox?.SelectedIndex ?? 0;

            // Mode-based visibility
            bool isTrpgMode = modeIndex == 2; // TRPGPlayer
            bool isPrefixMode = modeIndex == 0; // Prefix
            bool isInterceptAllMode = modeIndex == 1; // InterceptAll

            if (prefixModeBorder != null)
                prefixModeBorder.IsVisible = !isTrpgMode;
            if (trpgConfigBorder != null)
                trpgConfigBorder.IsVisible = isTrpgMode;
            if (trpgGuideBorder != null)
                trpgGuideBorder.IsVisible = isTrpgMode;

            // System prompt only relevant for Prefix/InterceptAll
            if (systemPromptBorder != null)
                systemPromptBorder.IsVisible = !isTrpgMode;

            // Model config visibility (show only selected model's config)
            if (geminiBorder != null)
                geminiBorder.IsVisible = (modelIndex == 0);
            if (zhipuBorder != null)
                zhipuBorder.IsVisible = (modelIndex == 1);
            if (siliconflowBorder != null)
                siliconflowBorder.IsVisible = (modelIndex == 2);
            if (deepseekBorder != null)
                deepseekBorder.IsVisible = (modelIndex == 3);

        }

        private async void TestListModelsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            if (_testListModelsCallback != null)
                await _testListModelsCallback();
        }

        private void OnConfigChanged(string key, string newValue)
        {
            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                switch (key)
                {
                    case "aimod.mode":
                        var modeComboBox = this.FindControl<ComboBox>("ModeSelectionComboBox");
                        if (modeComboBox != null && Enum.TryParse<AiMode>(newValue, true, out var mode))
                            modeComboBox.SelectedIndex = (int)mode;
                        break;
                    case "aimod.selectedmodel":
                        var modelComboBox = this.FindControl<ComboBox>("ModelSelectionComboBox");
                        if (modelComboBox != null && Enum.TryParse<AiModelType>(newValue, true, out var modelType))
                            modelComboBox.SelectedIndex = (int)modelType;
                        break;
                    case "aimod.systemprompt":
                        var sp = this.FindControl<TextBox>("SystemPromptTextBox");
                        if (sp != null) sp.Text = newValue;
                        break;
                    case "aimod.gemini.apikey":
                        var gk = this.FindControl<TextBox>("GeminiApiKeyTextBox");
                        if (gk != null) gk.Text = newValue;
                        break;
                    case "aimod.gemini.modelname":
                        var gm = this.FindControl<TextBox>("GeminiModelNameTextBox");
                        if (gm != null) gm.Text = newValue;
                        break;
                    case "aimod.zhipu.apikey":
                        var zk = this.FindControl<TextBox>("ZhipuApiKeyTextBox");
                        if (zk != null) zk.Text = newValue;
                        break;
                    case "aimod.zhipu.modelname":
                        var zm = this.FindControl<TextBox>("ZhipuModelNameTextBox");
                        if (zm != null) zm.Text = newValue;
                        break;
                    case "aimod.siliconflow.apikey":
                        var sk = this.FindControl<TextBox>("SiliconFlowApiKeyTextBox");
                        if (sk != null) sk.Text = newValue;
                        break;
                    case "aimod.siliconflow.modelname":
                        var sm = this.FindControl<TextBox>("SiliconFlowModelNameTextBox");
                        if (sm != null) sm.Text = newValue;
                        break;
                    case "aimod.deepseek.apikey":
                        var dk = this.FindControl<TextBox>("DeepSeekApiKeyTextBox");
                        if (dk != null) dk.Text = newValue;
                        break;
                    case "aimod.deepseek.modelname":
                        var dm = this.FindControl<TextBox>("DeepSeekModelNameTextBox");
                        if (dm != null) dm.Text = newValue;
                        break;
                    case "aimod.prefix":
                        var pt = this.FindControl<TextBox>("PrefixTextBox");
                        if (pt != null) pt.Text = newValue;
                        break;
                    case "aimod.maxcontextturns":
                        if (int.TryParse(newValue, out int turns))
                            this.FindControl<NumericUpDown>("MaxContextTurnsUpDown").Value = turns;
                        break;
                    case "aimod.interceptall":
                        if (bool.TryParse(newValue, out bool intercept))
                            this.FindControl<CheckBox>("InterceptAllCheckBox").IsChecked = intercept;
                        break;
                    case "aimod.trpg.cooldownseconds":
                        if (int.TryParse(newValue, out int cd))
                            this.FindControl<NumericUpDown>("TrpgCooldownSecondsUpDown").Value = cd;
                        break;
                    case "aimod.trpg.tokenthreshold":
                        if (int.TryParse(newValue, out int tt))
                            this.FindControl<NumericUpDown>("TrpgTokenThresholdUpDown").Value = tt;
                        break;
                    case "aimod.trpg.recenthistorycount":
                        if (int.TryParse(newValue, out int recentHistoryCount))
                            this.FindControl<NumericUpDown>("TrpgRecentHistoryCountUpDown").Value = recentHistoryCount;
                        break;
                    case "aimod.trpg.historyfoldcount":
                        if (int.TryParse(newValue, out int historyFoldCount))
                            this.FindControl<NumericUpDown>("TrpgHistoryFoldCountUpDown").Value = historyFoldCount;
                        break;
                    case "aimod.trpg.systemprompt":
                        var tsp = this.FindControl<TextBox>("TrpgSystemPromptTextBox");
                        if (tsp != null) tsp.Text = newValue;
                        break;
                    case "aimod.trpg.stateinterceptionenabled":
                        if (bool.TryParse(newValue, out bool stateEnabled))
                            this.FindControl<CheckBox>("TrpgStateInterceptionEnabledCheckBox").IsChecked = stateEnabled;
                        break;
                    case "aimod.trpg.recalltopk":
                        if (int.TryParse(newValue, out int topk))
                            this.FindControl<NumericUpDown>("TrpgRecallTopKUpDown").Value = topk;
                        break;
                    case "aimod.trpg.recallminsimilarity":
                        if (double.TryParse(newValue, out double minSim))
                            this.FindControl<NumericUpDown>("TrpgRecallMinSimilarityUpDown").Value = (decimal)minSim;
                        break;
                }
            });
        }

        private void LoadConfig()
        {
            // Mode
            var modeComboBox = this.FindControl<ComboBox>("ModeSelectionComboBox");
            var modeStr = _configurable.GetConfigValue("aimod.mode");
            if (modeComboBox != null && Enum.TryParse<AiMode>(modeStr ?? "TRPGPlayer", true, out var mode))
                modeComboBox.SelectedIndex = (int)mode;

            // Model
            var modelComboBox = this.FindControl<ComboBox>("ModelSelectionComboBox");
            var modelStr = _configurable.GetConfigValue("aimod.selectedmodel");
            if (modelComboBox != null && Enum.TryParse<AiModelType>(modelStr ?? "Gemini", true, out var modelType))
                modelComboBox.SelectedIndex = (int)modelType;

            // System prompt
            var sp = this.FindControl<TextBox>("SystemPromptTextBox");
            if (sp != null) sp.Text = _configurable.GetConfigValue("aimod.systemprompt") ?? "You are a helpful QQ group chat bot.";

            // Gemini
            var gk = this.FindControl<TextBox>("GeminiApiKeyTextBox");
            if (gk != null) gk.Text = _configurable.GetConfigValue("aimod.gemini.apikey");
            var gm = this.FindControl<TextBox>("GeminiModelNameTextBox");
            if (gm != null) gm.Text = _configurable.GetConfigValue("aimod.gemini.modelname") ?? "gemini-2.5-flash";

            // ZhipuAI
            var zk = this.FindControl<TextBox>("ZhipuApiKeyTextBox");
            if (zk != null) zk.Text = _configurable.GetConfigValue("aimod.zhipu.apikey");
            var zm = this.FindControl<TextBox>("ZhipuModelNameTextBox");
            if (zm != null) zm.Text = _configurable.GetConfigValue("aimod.zhipu.modelname") ?? "glm-4.7-flash";

            // SiliconFlow
            var sk = this.FindControl<TextBox>("SiliconFlowApiKeyTextBox");
            if (sk != null) sk.Text = _configurable.GetConfigValue("aimod.siliconflow.apikey");
            var sm = this.FindControl<TextBox>("SiliconFlowModelNameTextBox");
            if (sm != null) sm.Text = _configurable.GetConfigValue("aimod.siliconflow.modelname") ?? "Qwen/Qwen3-8B";

            // DeepSeek
            var dk = this.FindControl<TextBox>("DeepSeekApiKeyTextBox");
            if (dk != null) dk.Text = _configurable.GetConfigValue("aimod.deepseek.apikey");
            var dm = this.FindControl<TextBox>("DeepSeekModelNameTextBox");
            if (dm != null) dm.Text = _configurable.GetConfigValue("aimod.deepseek.modelname") ?? "deepseek-chat";

            // Prefix mode settings
            var pt = this.FindControl<TextBox>("PrefixTextBox");
            if (pt != null) pt.Text = _configurable.GetConfigValue("aimod.prefix");
            var mc = this.FindControl<NumericUpDown>("MaxContextTurnsUpDown");
            if (mc != null && int.TryParse(_configurable.GetConfigValue("aimod.maxcontextturns"), out int maxTurns))
                mc.Value = maxTurns;
            var ia = this.FindControl<CheckBox>("InterceptAllCheckBox");
            if (ia != null && bool.TryParse(_configurable.GetConfigValue("aimod.interceptall"), out bool intercept))
                ia.IsChecked = intercept;

            // TRPG config
            var cd = this.FindControl<NumericUpDown>("TrpgCooldownSecondsUpDown");
            if (cd != null && int.TryParse(_configurable.GetConfigValue("aimod.trpg.cooldownseconds"), out int cooldown))
                cd.Value = cooldown;
            var tt = this.FindControl<NumericUpDown>("TrpgTokenThresholdUpDown");
            if (tt != null && int.TryParse(_configurable.GetConfigValue("aimod.trpg.tokenthreshold"), out int threshold))
                tt.Value = threshold;
            var recentHistoryCount = this.FindControl<NumericUpDown>("TrpgRecentHistoryCountUpDown");
            if (recentHistoryCount != null && int.TryParse(_configurable.GetConfigValue("aimod.trpg.recenthistorycount"), out int recentHistoryThreshold))
                recentHistoryCount.Value = recentHistoryThreshold;
            var historyFoldCount = this.FindControl<NumericUpDown>("TrpgHistoryFoldCountUpDown");
            if (historyFoldCount != null && int.TryParse(_configurable.GetConfigValue("aimod.trpg.historyfoldcount"), out int foldCount))
                historyFoldCount.Value = foldCount;
            var tsp = this.FindControl<TextBox>("TrpgSystemPromptTextBox");
            if (tsp != null) tsp.Text = _configurable.GetConfigValue("aimod.trpg.systemprompt") ?? string.Empty;
            var stateEnabled = this.FindControl<CheckBox>("TrpgStateInterceptionEnabledCheckBox");
            if (stateEnabled != null && bool.TryParse(_configurable.GetConfigValue("aimod.trpg.stateinterceptionenabled"), out bool stateOn))
                stateEnabled.IsChecked = stateOn;
            var topk = this.FindControl<NumericUpDown>("TrpgRecallTopKUpDown");
            if (topk != null && int.TryParse(_configurable.GetConfigValue("aimod.trpg.recalltopk"), out int recallTopK))
                topk.Value = recallTopK;
            var minSim = this.FindControl<NumericUpDown>("TrpgRecallMinSimilarityUpDown");
            if (minSim != null && double.TryParse(_configurable.GetConfigValue("aimod.trpg.recallminsimilarity"), out double recallMinSim))
                minSim.Value = (decimal)recallMinSim;
            var secKey = this.FindControl<TextBox>("SecondaryApiKeyTextBox");
            if (secKey != null) secKey.Text = _configurable.GetConfigValue("aimod.trpg.secondaryapikey") ?? string.Empty;
            var secModel = this.FindControl<TextBox>("SecondaryModelTextBox");
            if (secModel != null) secModel.Text = _configurable.GetConfigValue("aimod.trpg.secondarymodel") ?? string.Empty;
            var secEndpoint = this.FindControl<TextBox>("SecondaryEndpointTextBox");
            if (secEndpoint != null) secEndpoint.Text = _configurable.GetConfigValue("aimod.trpg.secondaryendpoint") ?? string.Empty;

            UpdateVisibility();
        }

        private async void SaveButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            // Mode
            var modeComboBox = this.FindControl<ComboBox>("ModeSelectionComboBox");
            string mode = modeComboBox?.SelectedIndex switch
            {
                0 => "Prefix",
                1 => "InterceptAll",
                2 => "TRPGPlayer",
                _ => "Prefix"
            };
            await _configurable.ApplyConfigAsync("aimod.mode", mode, default);

            // Model
            var modelComboBox = this.FindControl<ComboBox>("ModelSelectionComboBox");
            string selectedModel = modelComboBox?.SelectedIndex switch
            {
                0 => "Gemini",
                1 => "ZhipuAI",
                2 => "SiliconFlow",
                3 => "DeepSeek",
                _ => "Gemini"
            };
            await _configurable.ApplyConfigAsync("aimod.selectedmodel", selectedModel, default);

            // System prompt
            var sp = this.FindControl<TextBox>("SystemPromptTextBox");
            await _configurable.ApplyConfigAsync("aimod.systemprompt", sp?.Text ?? "You are a helpful QQ group chat bot.", default);

            // Gemini
            var gk = this.FindControl<TextBox>("GeminiApiKeyTextBox");
            await _configurable.ApplyConfigAsync("aimod.gemini.apikey", gk?.Text ?? string.Empty, default);
            var gm = this.FindControl<TextBox>("GeminiModelNameTextBox");
            await _configurable.ApplyConfigAsync("aimod.gemini.modelname", gm?.Text ?? "gemini-2.5-flash", default);

            // ZhipuAI
            var zk = this.FindControl<TextBox>("ZhipuApiKeyTextBox");
            await _configurable.ApplyConfigAsync("aimod.zhipu.apikey", zk?.Text ?? string.Empty, default);
            var zm = this.FindControl<TextBox>("ZhipuModelNameTextBox");
            await _configurable.ApplyConfigAsync("aimod.zhipu.modelname", zm?.Text ?? "glm-4.7-flash", default);

            // SiliconFlow
            var sk = this.FindControl<TextBox>("SiliconFlowApiKeyTextBox");
            await _configurable.ApplyConfigAsync("aimod.siliconflow.apikey", sk?.Text ?? string.Empty, default);
            var sm = this.FindControl<TextBox>("SiliconFlowModelNameTextBox");
            await _configurable.ApplyConfigAsync("aimod.siliconflow.modelname", sm?.Text ?? "Qwen/Qwen3-8B", default);

            // DeepSeek
            var dk = this.FindControl<TextBox>("DeepSeekApiKeyTextBox");
            await _configurable.ApplyConfigAsync("aimod.deepseek.apikey", dk?.Text ?? string.Empty, default);
            var dm = this.FindControl<TextBox>("DeepSeekModelNameTextBox");
            await _configurable.ApplyConfigAsync("aimod.deepseek.modelname", dm?.Text ?? "deepseek-chat", default);

            // Prefix mode settings
            var pt = this.FindControl<TextBox>("PrefixTextBox");
            await _configurable.ApplyConfigAsync("aimod.prefix", pt?.Text ?? string.Empty, default);
            var mc = this.FindControl<NumericUpDown>("MaxContextTurnsUpDown");
            await _configurable.ApplyConfigAsync("aimod.maxcontextturns", ((int)(mc?.Value ?? 10)).ToString(), default);
            var ia = this.FindControl<CheckBox>("InterceptAllCheckBox");
            await _configurable.ApplyConfigAsync("aimod.interceptall", (ia?.IsChecked ?? false).ToString(), default);

            // TRPG config
            var cd = this.FindControl<NumericUpDown>("TrpgCooldownSecondsUpDown");
            await _configurable.ApplyConfigAsync("aimod.trpg.cooldownseconds", ((int)(cd?.Value ?? 60)).ToString(), default);
            var tt = this.FindControl<NumericUpDown>("TrpgTokenThresholdUpDown");
            var recentHistoryCount = this.FindControl<NumericUpDown>("TrpgRecentHistoryCountUpDown");
            var historyFoldCount = this.FindControl<NumericUpDown>("TrpgHistoryFoldCountUpDown");
            var foldValidation = ValidateFoldSettings(
                (int)(recentHistoryCount?.Value ?? 40),
                (int)(tt?.Value ?? 6000),
                (int)(historyFoldCount?.Value ?? 20));
            var foldValidationText = this.FindControl<TextBlock>("TrpgFoldValidationTextBlock");
            if (foldValidationText != null)
                foldValidationText.Text = foldValidation.Message;
            if (!foldValidation.IsValid)
                return;

            var currentRecentHistoryCount = int.TryParse(_configurable.GetConfigValue("aimod.trpg.recenthistorycount"), out var currentRecent)
                ? currentRecent
                : 40;
            if (foldValidation.RecentHistoryCount > currentRecentHistoryCount)
            {
                await _configurable.ApplyConfigAsync("aimod.trpg.recenthistorycount", foldValidation.RecentHistoryCount.ToString(), default);
                await _configurable.ApplyConfigAsync("aimod.trpg.historyfoldcount", foldValidation.HistoryFoldCount.ToString(), default);
            }
            else
            {
                await _configurable.ApplyConfigAsync("aimod.trpg.historyfoldcount", foldValidation.HistoryFoldCount.ToString(), default);
                await _configurable.ApplyConfigAsync("aimod.trpg.recenthistorycount", foldValidation.RecentHistoryCount.ToString(), default);
            }
            await _configurable.ApplyConfigAsync("aimod.trpg.tokenthreshold", foldValidation.TokenThreshold.ToString(), default);
            var tsp = this.FindControl<TextBox>("TrpgSystemPromptTextBox");
            await _configurable.ApplyConfigAsync("aimod.trpg.systemprompt", tsp?.Text ?? string.Empty, default);
            var stateEnabled = this.FindControl<CheckBox>("TrpgStateInterceptionEnabledCheckBox");
            await _configurable.ApplyConfigAsync("aimod.trpg.stateinterceptionenabled", (stateEnabled?.IsChecked ?? true).ToString(), default);
            var topk = this.FindControl<NumericUpDown>("TrpgRecallTopKUpDown");
            await _configurable.ApplyConfigAsync("aimod.trpg.recalltopk", ((int)(topk?.Value ?? 1)).ToString(), default);
            var minSim = this.FindControl<NumericUpDown>("TrpgRecallMinSimilarityUpDown");
            await _configurable.ApplyConfigAsync("aimod.trpg.recallminsimilarity", ((double)(minSim?.Value ?? 0.85m)).ToString("0.##"), default);
            // Secondary API config
            var secKey = this.FindControl<TextBox>("SecondaryApiKeyTextBox");
            await _configurable.ApplyConfigAsync("aimod.trpg.secondaryapikey", secKey?.Text ?? string.Empty, default);
            var secModel = this.FindControl<TextBox>("SecondaryModelTextBox");
            await _configurable.ApplyConfigAsync("aimod.trpg.secondarymodel", secModel?.Text ?? string.Empty, default);
            var secEndpoint = this.FindControl<TextBox>("SecondaryEndpointTextBox");
            await _configurable.ApplyConfigAsync("aimod.trpg.secondaryendpoint", secEndpoint?.Text ?? string.Empty, default);
        }

        private static FoldSettingsValidation ValidateFoldSettings(int recentHistoryCount, int tokenThreshold, int historyFoldCount)
        {
            if (recentHistoryCount <= 0 || tokenThreshold <= 0 || historyFoldCount <= 0)
                return new FoldSettingsValidation(false, recentHistoryCount, tokenThreshold, historyFoldCount, "折叠配置不能为 0 或负数。");
            if (recentHistoryCount < 12)
                return new FoldSettingsValidation(false, recentHistoryCount, tokenThreshold, historyFoldCount, "折叠消息条数阈值不能小于 12。");
            if (tokenThreshold < 2000)
                return new FoldSettingsValidation(false, recentHistoryCount, tokenThreshold, historyFoldCount, "折叠 token 阈值不能小于 2000。");
            if (historyFoldCount < 4)
                return new FoldSettingsValidation(false, recentHistoryCount, tokenThreshold, historyFoldCount, "每次折叠条数不能小于 4。");
            if (historyFoldCount >= recentHistoryCount)
                return new FoldSettingsValidation(false, recentHistoryCount, tokenThreshold, historyFoldCount, "每次折叠条数必须小于折叠消息条数阈值。");

            if (historyFoldCount < recentHistoryCount / 4.0)
                return new FoldSettingsValidation(true, recentHistoryCount, tokenThreshold, historyFoldCount, "提示：每次折叠条数小于阈值的 1/4，可能导致频繁折叠。配置已保存。");
            if (historyFoldCount > recentHistoryCount * 3.0 / 4.0)
                return new FoldSettingsValidation(true, recentHistoryCount, tokenThreshold, historyFoldCount, "提示：每次折叠条数大于阈值的 3/4，可能导致短期上下文断裂。配置已保存。");

            return new FoldSettingsValidation(true, recentHistoryCount, tokenThreshold, historyFoldCount, "折叠配置已保存。");
        }

        private sealed record FoldSettingsValidation(
            bool IsValid,
            int RecentHistoryCount,
            int TokenThreshold,
            int HistoryFoldCount,
            string Message);
    }
}
