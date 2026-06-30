using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using MoonSharp.Interpreter;
using MDiceV2.Core.Models;
using MDiceV2.Models;

namespace CustomizedReply;

/// <summary>
/// Lua脚本执行器
/// 负责执行自定义的Lua脚本并返回处理结果
/// </summary>
public class ScriptExecutor
{
    /// <summary>
    /// MessageProcessor实例（用于访问Mod全局存储）
    /// 由CustomizedReplyMod在初始化时设置
    /// </summary>
    public static MessageProcessor? MessageProcessor { get; set; }

    /// <summary>
    /// 当前执行脚本的Mod ID（用于Mod存储的命名空间）
    /// </summary>
    public static string ModId { get; set; } = "CustomizedReply";

    /// <summary>
    /// 脚本资源内容缓存（按文件名）
    /// Key: 脚本文件名（如 "counter.lua"）
    /// Value: 脚本文件内容
    /// </summary>
    private static Dictionary<string, string> resourceContentCache = new();

    /// <summary>
    /// 脚本实例虚拟机缓存（按实例 UID）
    /// Key: 脚本实例 UID
    /// Value: 该实例对应的 Lua 虚拟机实例
    /// 每个实例 UID 有一个独立的虚拟机，保留其长期变量和状态
    /// </summary>
    private static Dictionary<string, Script> instanceVMCache = new();

    /// <summary>
    /// 已初始化的脚本实例集合（记录已执行过 initial() 的脚本）
    /// Key: 脚本实例 UID
    /// 用于确保每个脚本的 initial() 仅执行一次
    /// </summary>
    private static HashSet<string> initializedInstances = new();

    /// <summary>
    /// 脚本实例配置字典（按实例 UID）
    /// Key: 脚本实例 UID
    /// Value: 脚本实例配置（UID -> 脚本文件名的映射）
    /// </summary>
    private static Dictionary<string, ScriptInstance> scriptInstances = new();

    /// <summary>
    /// 脚本资源目录路径
    /// </summary>
    private static string scriptsDirectory = "";

    /// <summary>
    /// 初始化脚本执行器（需要在使用前调用）
    /// </summary>
    public static void Initialize(string scriptsDir, List<ScriptInstance> instances)
    {
        scriptsDirectory = scriptsDir;
        scriptInstances.Clear();
        foreach (var instance in instances)
        {
            scriptInstances[instance.Uid] = instance;
        }
    }

    /// <summary>
    /// 更新脚本实例列表
    /// </summary>
    public static void UpdateScriptInstances(List<ScriptInstance> instances)
    {
        scriptInstances.Clear();
        foreach (var instance in instances)
        {
            scriptInstances[instance.Uid] = instance;
        }
    }

    /// <summary>
    /// 清除指定脚本资源的缓存（当脚本文件被修改时调用）
    /// </summary>
    public static void InvalidateScriptResource(string scriptFileName)
    {
        resourceContentCache.Remove(scriptFileName);

        // 清除所有引用此资源的实例的虚拟机缓存
        var affectedUids = scriptInstances
            .Where(kv => kv.Value.ScriptFileName == scriptFileName)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var uid in affectedUids)
        {
            instanceVMCache.Remove(uid);
            // 同时清除初始化标记，便于后续重新初始化
            initializedInstances.Remove(uid);
        }
    }

    /// <summary>
    /// 脚本执行上下文
    /// 包含脚本运行时需要的所有数据
    /// </summary>
    public class ScriptContext
    {
        /// <summary>
        /// 收到消息的用户ID
        /// </summary>
        public long UserId { get; set; }

        /// <summary>
        /// 用户昵称
        /// </summary>
        public string UserName { get; set; } = "";

        /// <summary>
        /// 消息所在的群ID（私聊时为0）
        /// </summary>
        public long GroupId { get; set; }

        /// <summary>
        /// 群名称（私聊时为空）
        /// </summary>
        public string GroupName { get; set; } = "";

        /// <summary>
        /// 收到的原始消息内容
        /// </summary>
        public string Message { get; set; } = "";

        /// <summary>
        /// 默认回复内容（来自规则的回复列表）
        /// </summary>
        public string DefaultReply { get; set; } = "";

        /// <summary>
        /// 消息接收时间戳
        /// </summary>
        public long Timestamp { get; set; }

        /// <summary>
        /// 是否是@消息
        /// </summary>
        public bool IsAted { get; set; }

        /// <summary>
        /// 自定义数据字典（脚本可以存储临时数据）
        /// </summary>
        public Dictionary<string, object> CustomData { get; set; } = new();

        /// <summary>
        /// 脚本的持久状态（跨脚本执行保留，用于维护长期变量）
        /// </summary>
        public Dictionary<string, object> PersistentState { get; set; } = new();

        /// <summary>
        /// 本次执行的本地变量（单次执行的临时变量）
        /// </summary>
        public Dictionary<string, object> LocalVariables { get; set; } = new();

        /// <summary>
        /// 执行后是否需要保存持久状态
        /// </summary>
        public bool SaveStateAfter { get; set; } = false;
    }

    /// <summary>
    /// 执行Lua脚本并返回结果行列表（新的半开放脚本方式）
    /// 脚本返回的内容会被分行存储，可通过 <output:N> 标签在回复中引用
    /// </summary>
    /// <param name="scriptContent">Lua脚本代码</param>
    /// <param name="context">脚本执行上下文</param>
    /// <returns>脚本输出的行列表，如果执行失败返回空列表</returns>
    public List<string> ExecuteScriptAndGetOutputLines(string scriptContent, ScriptContext context)
    {
        try
        {
            // 检查脚本是否包含MainProcess函数
            if (!scriptContent.Contains("function MainProcess") && !scriptContent.Contains("function main_process"))
            {
                throw new InvalidOperationException("脚本必须包含 MainProcess 函数");
            }

            // 注意：这里需要集成真实的Lua执行引擎（如NLua或MoonSharp）
            // 当前为演示代码，实际实现需要Lua运行时支持
            
            string result = ExecuteScriptInternal(scriptContent, context);
            
            // 将结果分行存储
            var outputLines = result.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
            
            // 过滤并移除空行（保留非空行）
            outputLines = outputLines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
            
            return outputLines;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScriptExecutor] 脚本执行失败: {ex.Message}");
            return new List<string> { $"脚本执行错误: {ex.Message}" };
        }
    }

    /// <summary>
    /// 执行Lua脚本并返回结果（旧方法，保留向后兼容性）
    /// </summary>
    /// <param name="scriptContent">Lua脚本代码</param>
    /// <param name="context">脚本执行上下文</param>
    /// <param name="replyTemplate">带有<output>标签的回复模板</param>
    /// <returns>处理后的回复文本，如果执行失败返回原始模板</returns>
    public string ExecuteScript(string scriptContent, ScriptContext context, string replyTemplate)
    {
        try
        {
            // 检查脚本是否包含MainProcess函数
            if (!scriptContent.Contains("function MainProcess") && !scriptContent.Contains("function main_process"))
            {
                throw new InvalidOperationException("脚本必须包含 MainProcess 函数");
            }

            // 注意：这里需要集成真实的Lua执行引擎（如NLua或MoonSharp）
            // 当前为演示代码，实际实现需要Lua运行时支持
            
            string result = ExecuteScriptInternal(scriptContent, context);
            
            // 替换<output>标签
            return ReplaceOutputTag(replyTemplate, result);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScriptExecutor] 脚本执行失败: {ex.Message}");
            return replyTemplate; // 失败时返回原始模板
        }
    }

    /// <summary>
    /// 内部脚本执行逻辑（使用MoonSharp Lua引擎）
    /// </summary>
    private string ExecuteScriptInternal(string scriptContent, ScriptContext context)
    {
        try
        {
            // 创建Lua脚本环境
            var script = new Script();

            // 注册上下文数据到Lua全局环境
            script.Globals["user_id"] = context.UserId;
            script.Globals["user_name"] = context.UserName;
            script.Globals["group_id"] = context.GroupId;
            script.Globals["group_name"] = context.GroupName;
            script.Globals["message"] = context.Message;
            script.Globals["default_reply"] = context.DefaultReply;
            script.Globals["timestamp"] = context.Timestamp;
            script.Globals["is_ated"] = context.IsAted;

            // 如果有自定义数据，也注册到Lua环境
            if (context.CustomData != null && context.CustomData.Count > 0)
            {
                script.Globals["custom_data"] = context.CustomData;
            }

            // === 注册Mod全局存储的读写函数 ===
            // 读取Mod存储中的值: mod_storage_read(key) -> value
            script.Globals["mod_storage_read"] = (Func<string, string>)((string key) =>
            {
                try
                {
                    if (MessageProcessor != null && !string.IsNullOrEmpty(key))
                    {
                        return MessageProcessor.GetModStorageValue(ModId, key);
                    }
                    return "";
                }
                catch
                {
                    return "";
                }
            });

            // 写入Mod存储中的值: mod_storage_write(key, value)
            script.Globals["mod_storage_write"] = (Action<string, string>)((string key, string value) =>
            {
                try
                {
                    if (MessageProcessor != null && !string.IsNullOrEmpty(key))
                    {
                        MessageProcessor.SetModStorageValue(ModId, key, value ?? "");
                    }
                }
                catch
                {
                    // 写入失败时静默处理
                }
            });

            // 执行脚本内容（注册函数定义）
            script.DoString(scriptContent);

            // 调用MainProcess函数并获取返回值
            var mainProcessFunc = script.Globals.Get("MainProcess");
            if (mainProcessFunc == null || mainProcessFunc.Type != DataType.Function)
            {
                throw new InvalidOperationException("脚本中找不到MainProcess函数或其不是函数类型");
            }

            DynValue result = script.Call(mainProcessFunc);

            // 提取返回值
            if (result == null)
            {
                return context.DefaultReply;
            }

            // 如果返回多个值，取第一个
            if (result.Type == DataType.Table)
            {
                var table = result.Table;
                if (table.Length > 0)
                {
                    return table.Get(1)?.ToString() ?? context.DefaultReply;
                }
                return context.DefaultReply;
            }

            // 返回字符串类型的结果
            return result.ToString();
        }
        catch (Exception ex)
        {
            // 脚本执行异常，返回错误信息
            System.Diagnostics.Debug.WriteLine($"[ScriptExecutor] Lua脚本执行异常: {ex.Message}");
            return $"[脚本错误] {ex.Message}";
        }
    }

    /// <summary>
    /// 替换回复模板中的<output>标签
    /// </summary>
    private string ReplaceOutputTag(string template, string scriptOutput)
    {
        // 查找并替换<output>标签
        int startIndex = template.IndexOf("<output>");
        int endIndex = template.IndexOf("</output>");

        if (startIndex >= 0 && endIndex > startIndex)
        {
            return template.Remove(startIndex, endIndex - startIndex + "</output>".Length)
                          .Insert(startIndex, scriptOutput);
        }

        // 如果没有<output>标签，直接返回脚本输出
        return scriptOutput;
    }

    /// <summary>
    /// 替换回复模板中的索引输出标签（如<output:0>、<output:1>等）
    /// </summary>
    /// <param name="template">包含标签的回复模板</param>
    /// <param name="outputLines">脚本输出的行列表</param>
    /// <returns>替换后的文本</returns>
    public string ReplaceIndexedOutputTags(string template, List<string> outputLines)
    {
        if (outputLines == null || outputLines.Count == 0)
        {
            return template;
        }

        // 使用正则表达式匹配 <output:N> 格式的标签
        var pattern = @"<output:(\d+)>";
        
        return System.Text.RegularExpressions.Regex.Replace(template, pattern, match =>
        {
            // 提取数字索引
            if (int.TryParse(match.Groups[1].Value, out int index))
            {
                // 如果索引有效，返回对应的输出行；否则返回空字符串
                return index < outputLines.Count ? outputLines[index] : "";
            }
            return match.Value; // 如果解析失败，返回原始标签
        });
    }

    /// <summary>
    /// 向Lua虚拟机注册全局变量和函数
    /// </summary>
    private static void RegisterGlobals(Script vm, ScriptContext context)
    {
        if (vm == null || context == null)
            return;

        // 注册上下文数据到Lua全局环境
        vm.Globals["user_id"] = context.UserId;
        vm.Globals["user_name"] = context.UserName;
        vm.Globals["group_id"] = context.GroupId;
        vm.Globals["group_name"] = context.GroupName;
        vm.Globals["message"] = context.Message;
        vm.Globals["default_reply"] = context.DefaultReply;
        vm.Globals["timestamp"] = context.Timestamp;
        vm.Globals["is_ated"] = context.IsAted;

        // 如果有自定义数据，也注册到Lua环境
        if (context.CustomData != null && context.CustomData.Count > 0)
        {
            vm.Globals["custom_data"] = context.CustomData;
        }

        // === 初始化全局脚本注册表（用于脚本间通信） ===
        // 如果不存在则创建，便于多个脚本共享接口
        var registryValue = vm.Globals.Get("_script_registry");
        if (registryValue == null || registryValue.IsNil())
        {
            vm.Globals["_script_registry"] = new Table(vm);
        }

        // === 注册脚本间查询接口 ===
        // 用法: local feedpet_api = get_script("feedpet")
        vm.Globals["get_script"] = (Func<string, DynValue>)((string scriptName) =>
        {
            try
            {
                var registry = vm.Globals.Get("_script_registry");
                if (registry != null && registry.Type == DataType.Table)
                {
                    var scriptApi = registry.Table.Get(scriptName);
                    return scriptApi ?? DynValue.Nil;
                }
                return DynValue.Nil;
            }
            catch
            {
                return DynValue.Nil;
            }
        });

        // === 注册Mod全局存储的读写函数 ===
        // 读取Mod存储中的值: mod_storage_read(key) -> value
        vm.Globals["mod_storage_read"] = (Func<string, string>)((string key) =>
        {
            try
            {
                if (MessageProcessor != null && !string.IsNullOrEmpty(key))
                {
                    return MessageProcessor.GetModStorageValue(ModId, key);
                }
                return "";
            }
            catch
            {
                return "";
            }
        });

        // 写入Mod存储中的值: mod_storage_write(key, value)
        vm.Globals["mod_storage_write"] = (Action<string, string>)((string key, string value) =>
        {
            try
            {
                if (MessageProcessor != null && !string.IsNullOrEmpty(key))
                {
                    MessageProcessor.SetModStorageValue(ModId, key, value ?? "");
                }
            }
            catch
            {
                // 写入失败时静默处理
            }
        });
    }

    /// <summary>
    /// 执行脚本中的指定函数（新的调用方式）
    /// </summary>
    /// <param name="scriptInstanceUid">脚本实例 UID</param>
    /// <param name="functionName">要调用的函数名</param>
    /// <param name="context">脚本执行上下文</param>
    /// <returns>函数返回值字符串，如果执行失败返回错误消息</returns>
    public static string? ExecuteFunction(string scriptInstanceUid, string functionName, ScriptContext context)
    {
        try
        {
            // 1. 获取脚本实例配置
            if (!scriptInstances.TryGetValue(scriptInstanceUid, out var instance))
            {
                return $"[错误] 脚本实例不存在: {scriptInstanceUid}";
            }

            string scriptFileName = instance.ScriptFileName;

            // 2. 加载脚本资源内容
            string scriptContent = LoadScriptResourceContent(scriptFileName);
            if (scriptContent == null)
            {
                return $"[错误] 脚本文件不存在: {scriptFileName}";
            }

            // 3. 获取或创建该实例的虚拟机
            Script vm;
            bool isNewVM = false;

            if (instanceVMCache.TryGetValue(scriptInstanceUid, out var cachedVM))
            {
                // 虚拟机已存在，复用并更新上下文
                vm = cachedVM;
            }
            else
            {
                // 虚拟机不存在，创建新的
                vm = new Script();
                RegisterGlobals(vm, context);

                try
                {
                    vm.DoString(scriptContent);
                }
                catch (Exception ex)
                {
                    return $"[错误] 脚本加载失败: {ex.Message}";
                }

                // 缓存虚拟机
                instanceVMCache[scriptInstanceUid] = vm;
                isNewVM = true;
            }

            // 4. 如果是新虚拟机，执行 initial() 初始化
            if (isNewVM && !initializedInstances.Contains(scriptInstanceUid))
            {
                try
                {
                    var initialFunc = vm.Globals.Get("initial");
                    if (initialFunc != null && initialFunc.Type == DataType.Function)
                    {
                        vm.Call(initialFunc);
                        System.Diagnostics.Debug.WriteLine($"[ScriptLifecycle] ✓ Initial function executed for script: {scriptInstanceUid}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScriptLifecycle] ⚠ Initial function error for script {scriptInstanceUid}: {ex.Message}");
                    // 初始化失败时继续执行，不中断主逻辑
                }
                finally
                {
                    // 标记为已初始化
                    initializedInstances.Add(scriptInstanceUid);
                }
            }

            // 5. 更新虚拟机的上下文变量
            vm.Globals["user_id"] = context.UserId;
            vm.Globals["user_name"] = context.UserName;
            vm.Globals["message"] = context.Message;
            vm.Globals["group_id"] = context.GroupId;
            vm.Globals["group_name"] = context.GroupName;
            vm.Globals["default_reply"] = context.DefaultReply;
            vm.Globals["timestamp"] = context.Timestamp;
            vm.Globals["is_ated"] = context.IsAted;

            // 6. 调用指定的函数
            var func = vm.Globals.Get(functionName);
            if (func == null || func.Type != DataType.Function)
            {
                return $"[错误] 函数不存在: {functionName}";
            }

            var result = vm.Call(func);
            
            // 7. 返回结果
            if (result == null || result.IsNil())
            {
                return null;
            }

            if (result.Type == DataType.String)
            {
                return result.String;
            }

            return result.ToString();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScriptExecutor] 函数执行异常: {ex.Message}");
            return $"[脚本错误] {ex.Message}";
        }
    }

    /// <summary>
    /// 加载脚本资源内容（支持缓存）
    /// </summary>
    private static string? LoadScriptResourceContent(string scriptFileName)
    {
        if (string.IsNullOrEmpty(scriptFileName) || string.IsNullOrEmpty(scriptsDirectory))
        {
            return null;
        }

        // 检查缓存
        if (resourceContentCache.TryGetValue(scriptFileName, out var cached))
        {
            return cached;
        }

        // 从文件读取
        string filePath = Path.Combine(scriptsDirectory, scriptFileName);
        
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            string content = File.ReadAllText(filePath, Encoding.UTF8);
            resourceContentCache[scriptFileName] = content;
            return content;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 从目录加载所有脚本资源
    /// </summary>
    public static List<ScriptResource> LoadScriptResourcesFromDirectory(string scriptsDirectory)
    {
        var resources = new List<ScriptResource>();

        if (!Directory.Exists(scriptsDirectory))
        {
            return resources;
        }

        try
        {
            var luaFiles = Directory.GetFiles(scriptsDirectory, "*.lua", SearchOption.TopDirectoryOnly);

            foreach (var filePath in luaFiles)
            {
                try
                {
                    var fileName = Path.GetFileName(filePath);
                    var fileInfo = new FileInfo(filePath);
                    var content = File.ReadAllText(filePath, Encoding.UTF8);

                    // 尝试加载元数据
                    var description = "";
                    var metaFileName = fileName + ".meta.json";
                    var metaPath = Path.Combine(scriptsDirectory, metaFileName);
                    if (File.Exists(metaPath))
                    {
                        try
                        {
                            var metaJson = File.ReadAllText(metaPath, Encoding.UTF8);
                            var metadata = JsonDocument.Parse(metaJson).RootElement;
                            if (metadata.TryGetProperty("Description", out var descProp))
                            {
                                description = descProp.GetString() ?? "";
                            }
                        }
                        catch
                        {
                            // 元数据读取失败，使用默认值
                        }
                    }

                    resources.Add(new ScriptResource
                    {
                        FileName = fileName,
                        Content = content,
                        LastModified = fileInfo.LastWriteTimeUtc,
                        Description = description
                    });

                    // 同时缓存内容
                    resourceContentCache[fileName] = content;
                }
                catch
                {
                    // 个别文件加载失败，继续加载其他文件
                }
            }
        }
        catch
        {
            // 读取目录失败，返回空列表
        }

        return resources;
    }

    /// <summary>
    /// 保存脚本资源到文件
    /// </summary>
    public static bool SaveScriptResourceToFile(ScriptResource resource, string scriptsDirectory)
    {
        if (resource == null || string.IsNullOrEmpty(resource.FileName))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(scriptsDirectory))
            {
                Directory.CreateDirectory(scriptsDirectory);
            }

            var filePath = Path.Combine(scriptsDirectory, resource.FileName);

            // 防止路径遍历攻击
            var fullPath = Path.GetFullPath(filePath);
            var fullScriptsDir = Path.GetFullPath(scriptsDirectory);
            if (!fullPath.StartsWith(fullScriptsDir))
            {
                return false;
            }

            // 保存脚本内容
            File.WriteAllText(filePath, resource.Content ?? "", Encoding.UTF8);

            // 保存元数据（Description）
            var metaFileName = resource.FileName + ".meta.json";
            var metaPath = Path.Combine(scriptsDirectory, metaFileName);
            var metadata = new
            {
                Description = resource.Description ?? "",
                LastModified = resource.LastModified.ToString("O")
            };
            var metaJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(metaPath, metaJson, Encoding.UTF8);

            // 更新缓存
            resourceContentCache[resource.FileName] = resource.Content ?? "";

            // 清除相关的VM缓存（脚本文件变更）
            InvalidateScriptResource(resource.FileName);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 删除脚本资源文件
    /// </summary>
    public static bool DeleteScriptResourceFile(string scriptFileName, string scriptsDirectory)
    {
        if (string.IsNullOrEmpty(scriptFileName))
        {
            return false;
        }

        try
        {
            var filePath = Path.Combine(scriptsDirectory, scriptFileName);

            // 防止路径遍历攻击
            var fullPath = Path.GetFullPath(filePath);
            var fullScriptsDir = Path.GetFullPath(scriptsDirectory);
            if (!fullPath.StartsWith(fullScriptsDir))
            {
                return false;
            }

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                
                // 同时删除元数据文件
                var metaFileName = scriptFileName + ".meta.json";
                var metaPath = Path.Combine(scriptsDirectory, metaFileName);
                try
                {
                    if (File.Exists(metaPath))
                    {
                        File.Delete(metaPath);
                    }
                }
                catch
                {
                    // 元数据文件删除失败，不影响脚本文件删除
                }
                
                resourceContentCache.Remove(scriptFileName);
                InvalidateScriptResource(scriptFileName);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 卸载所有已初始化的脚本（调用dispose函数）
    /// 在Mod卸载时由CustomizedReplyMod.OnUnload()调用
    /// </summary>
    public static void UnloadAllScripts()
    {
        try
        {
            // 获取所有已初始化的脚本实例UID副本（因为会在循环中修改集合）
            var initializedUids = new List<string>(initializedInstances);

            foreach (var scriptInstanceUid in initializedUids)
            {
                try
                {
                    // 获取该实例的虚拟机
                    if (instanceVMCache.TryGetValue(scriptInstanceUid, out var vm))
                    {
                        // 尝试调用dispose函数
                        var disposeFunc = vm.Globals.Get("dispose");
                        if (disposeFunc != null && disposeFunc.Type == DataType.Function)
                        {
                            try
                            {
                                vm.Call(disposeFunc);
                                System.Diagnostics.Debug.WriteLine($"[ScriptLifecycle] ✓ Dispose function executed for script: {scriptInstanceUid}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"[ScriptLifecycle] ⚠ Dispose function error for script {scriptInstanceUid}: {ex.Message}");
                                // 继续执行，不中断卸载流程
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScriptLifecycle] ⚠ Error unloading script {scriptInstanceUid}: {ex.Message}");
                    // 继续处理其他脚本
                }
                finally
                {
                    // 移除初始化标记
                    initializedInstances.Remove(scriptInstanceUid);
                }
            }

            // 清理所有虚拟机缓存
            instanceVMCache.Clear();
            initializedInstances.Clear();
            
            System.Diagnostics.Debug.WriteLine($"[ScriptLifecycle] ✓ All scripts unloaded successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScriptLifecycle] ✗ Error during UnloadAllScripts: {ex.Message}");
        }
    }

    /// <summary>
    /// 生成Lua脚本模板
    /// </summary>
    public static string GenerateScriptTemplate()
    {
        return @"-- CustomizedReply Lua 脚本模板
-- MainProcess 函数会在规则被触发时自动调用

function MainProcess()
    -- 可以访问的全局变量：
    -- user_id: 发送消息的用户ID
    -- user_name: 用户昵称
    -- group_id: 群ID（私聊时为0）
    -- group_name: 群名称
    -- message: 收到的原始消息
    -- default_reply: 规则中的默认回复
    -- timestamp: 消息时间戳
    -- is_ated: 是否被@

    -- 示例：根据消息内容返回不同的回复
    if string.find(message, ""你好"") then
        return ""你好！有什么我可以帮助你的吗？""
    elseif string.find(message, ""谢谢"") then
        return ""不客气！""
    else
        return default_reply
    end
end
";
    }
}
