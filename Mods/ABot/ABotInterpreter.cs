using System;
using System.Runtime.InteropServices;
using System.Text;
using System.IO;

namespace ABot;

/// <summary>
/// ABOT解释器的C#包装类 - 使用 P/Invoke 方案 B
/// 
/// 职责：
/// =====
/// 1. 使用P/Invoke直接调用ABot.Core.dll中的C API
/// 2. 无需依赖.NET Framework 4.7.2的C++/CLI库
/// 3. 提供简洁的C# API给外部使用
/// 4. 处理资源生命周期管理
/// 5. 进行异常转换和处理
/// 
/// 架构说明（方案 B）：
/// ===================
/// C# 代码 (.NET 10)
///    ↓
/// ABotInterpreter (这个文件) - 使用 P/Invoke
///    ↓
/// C API (DllImport) - abot_create/abot_parse_character等
///    ↓
/// ABot.Core.dll (原生C++)
/// 
/// 优势：
/// =====
/// - 无需C++/CLI，不受.NET Framework限制
/// - 直接调用C++ DLL，性能略优
/// - 完全独立于.NET版本，自动兼容 .NET Core/.NET 5+
/// 
/// 使用示例：
/// =========
/// var interp = new ABotInterpreter();
/// if (interp.IsReady())
/// {
///     int result = interp.ParseCharacter(xmlString);
///     interp.ExecuteScript(script);
/// }
/// interp.Dispose();
/// </summary>
public class ABotInterpreter : IDisposable
{
    // ============ Windows API P/Invoke ============
    
    /// <summary>设置DLL搜索路径以确保ABot.Core.dll能被找到</summary>
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetDllDirectory(string lpPathName);
    
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool AddDllDirectory(string lpPathName);
    
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetWindowsLastError();
    
    // ============ P/Invoke 声明 ============
    
    private const string ABOT_DLL_NAME = "ABot.Core";
    
    /// <summary>生命周期管理函数</summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr abot_create();
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void abot_destroy(IntPtr handle);
    
    /// <summary>脚本解析和编译函数 - 使用IntPtr直接传递UTF-8字节，避免ANSI编码问题</summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_parse_character")]
    private static extern int abot_parse_character_utf8(IntPtr handle, IntPtr characterXmlBytes);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_register_skillset")]
    private static extern int abot_register_skillset_utf8(IntPtr handle, IntPtr skillsetXmlBytes);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_register_stateset")]
    private static extern int abot_register_stateset_utf8(IntPtr handle, IntPtr statesetXmlBytes);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_register_ankeset")]
    private static extern int abot_register_ankeset_utf8(IntPtr handle, IntPtr ankesetXmlBytes);
    
    /// <summary>战斗执行函数</summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_execute_battle(IntPtr handle);
    
    /// <summary>错误处理函数</summary>
    
    /// <summary>获取最后错误 - 返回UTF-8字节的指针</summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_get_last_error")]
    private static extern IntPtr abot_get_last_error_utf8(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void abot_clear_error(IntPtr handle);
    
    /// <summary>角色调试信息函数</summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_get_character_debug_info")]
    private static extern IntPtr abot_get_character_debug_info_utf8(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_get_character_basic_info")]
    private static extern IntPtr abot_get_character_basic_info_utf8(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_get_character_skills_info")]
    private static extern IntPtr abot_get_character_skills_info_utf8(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_get_character_states_info")]
    private static extern IntPtr abot_get_character_states_info_utf8(IntPtr handle);
    
    /// <summary>状态查询函数</summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_is_ready(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr abot_get_version();
    
    /// <summary>回合管理器函数</summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_round_manager_init(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_round_manager_advance(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_round_manager_advance_multiple(IntPtr handle, int count);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_round_manager_skip(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_round_manager_add_character(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_round_manager_clear_all_characters(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void abot_round_manager_pause(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern void abot_round_manager_resume(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_round_manager_is_running(IntPtr handle);
    
    /// <summary>脚本编译诊断函数</summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "DiagnoseScriptCompilation")]
    private static extern int DiagnoseScript_Native(string script_source, StringBuilder out_error, int out_error_len);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_round_manager_is_finished(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_round_manager_get_current_round(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_round_manager_get_status")]
    private static extern IntPtr abot_round_manager_get_status_utf8(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_round_manager_get_log")]
    private static extern IntPtr abot_round_manager_get_log_utf8(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_round_manager_get_skill_trigger_log")]
    private static extern IntPtr abot_round_manager_get_skill_trigger_log_utf8(IntPtr handle);
    
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_round_manager_execute_command")]
    private static extern int abot_round_manager_execute_command_utf8(IntPtr handle, IntPtr commandPtr, IntPtr paramsPtr);
    
    /// <summary>状态导出/导入函数（多用户隔离支持）</summary>
    
    /// <summary>
    /// 将当前解释器状态导出为 JSON 字符串
    /// 返回值：UTF-8 编码的 JSON 字符串（包含角色信息、回合状态等）
    /// 调用者不需要释放返回的指针（由 C++ 管理）
    /// </summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_export_state_json")]
    private static extern IntPtr abot_export_state_json_utf8(IntPtr handle);

    /// <summary>
    /// 从 JSON 字符串导入状态到解释器
    /// 参数：jsonStateBytes - UTF-8 编码的 JSON 状态数据指针
    /// 返回值：0 = 成功，其他 = 错误码
    /// 调用者需要释放 jsonStateBytes 指针（本函数不负责）
    /// </summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_import_state_json")]
    private static extern int abot_import_state_json_utf8(IntPtr handle, IntPtr jsonStateBytes);

    /// <summary>
    /// 将当前解释器状态导出为二进制格式
    /// 参数：outSize - 输出参数，返回二进制数据的大小（字节）
    /// 返回值：指向二进制状态数据的指针（调用者不需要释放）
    /// 注意：返回的指针在下一次调用 abot_* 函数时可能失效
    /// </summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_export_state_binary")]
    private static extern IntPtr abot_export_state_binary_utf8(IntPtr handle, out int outSize);

    /// <summary>
    /// 从二进制格式导入状态到解释器
    /// 参数：binaryData - 二进制状态数据指针
    ///      binarySize - 二进制数据大小（字节）
    /// 返回值：0 = 成功，其他 = 错误码
    /// </summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_import_state_binary")]
    private static extern int abot_import_state_binary_utf8(IntPtr handle, IntPtr binaryData, int binarySize);

    /// <summary>
    /// 将当前已解析的角色序列化为 JSON 格式
    /// 用于保存角色状态到数据库
    /// 返回值：JSON 字符串指针，需转换为托管字符串
    /// </summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr abot_serialize_character_json(IntPtr handle);

    /// <summary>
    /// 【新增】序列化 RoundManager 中所有角色为 JSON 数组
    /// 用于多角色战斗状态的完整保存
    /// 返回值：JSON 数组字符串指针，格式：[{角色1}, {角色2}, ...]
    /// </summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr abot_serialize_all_characters_json(IntPtr handle);

    /// <summary>
    /// 从 JSON 反序列化并创建一个新的角色
    /// 解析给定的 JSON，创建 Character 对象并添加到 RoundManager
    /// 返回值：0 = 成功，其他 = 错误码
    /// </summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int abot_deserialize_character_json(IntPtr handle, string characterJson);

    /// <summary>
    /// 检查 RoundManager 是否已初始化并准备好执行
    /// ✅ 关键健康检查函数，用于验证 LoadState 后的状态完整性
    /// 
    /// 返回值：
    ///   1 = RoundManager 已初始化、Battle 已创建、准备好执行
    ///   0 = RoundManager 未初始化或状态不完整
    /// 
    /// 用途：LoadState() 导入后立即调用此函数进行验证
    ///      防止返回假成功（原始 abot_import_state_json 的问题）
    /// </summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
    private static extern int abot_round_manager_is_ready(IntPtr handle);

    // ============ 错误码常量 ============
    
    private const int ABOT_OK = 0;
    private const int ABOT_ERROR_NULL_PTR = 1;
    private const int ABOT_ERROR_INVALID_XML = 2;
    private const int ABOT_ERROR_PARSE_ERROR = 3;
    private const int ABOT_ERROR_COMPILE_ERROR = 4;
    private const int ABOT_ERROR_RUNTIME_ERROR = 5;
    private const int ABOT_ERROR_OUT_OF_MEMORY = 6;
    private const int ABOT_ERROR_UNKNOWN = -1;

    // ============ 私有字段 ============
    
    private bool _disposed = false;
    private IntPtr _handle = IntPtr.Zero;
    private string? _loadError;  // 记录初始化失败的具体原因
    private bool _isReady = false;

    // ============ 构造函数 ============

    /// <summary>
    /// 创建一个新的解释器实例
    /// 此时会通过P/Invoke调用abot_create()创建一个C++对象
    /// </summary>
    public ABotInterpreter()
    {
        try
        {
            Console.WriteLine("[ABot.C# Interpreter] Constructor START (P/Invoke方案)");
            Console.WriteLine($"[ABot.C# Interpreter] Host process framework: {GetRuntimeInfo()}");
            Console.WriteLine("[ABot.C# Interpreter] Attempting to load ABot.Core.dll via P/Invoke...");
            
            // ============ Phase 1: Setup DLL Search Paths ============
            // 尝试设置DLL搜索路径以找到ABot.Core.dll
            try
            {
                string appDirectory = AppDomain.CurrentDomain.BaseDirectory;
                string debugDirectory = Path.Combine(Path.GetDirectoryName(typeof(ABotInterpreter).Assembly.Location) ?? appDirectory, "MDiceV2_Debug");
                string currentDirectory = Directory.GetCurrentDirectory();
                
                Console.WriteLine("[ABot.C# Interpreter] Setting up DLL search paths...");
                Console.WriteLine($"  AppDomain.BaseDirectory: {appDirectory}");
                Console.WriteLine($"  Assembly location: {typeof(ABotInterpreter).Assembly.Location}");
                Console.WriteLine($"  Current directory: {currentDirectory}");
                Console.WriteLine($"  Debug directory candidate: {debugDirectory}");
                
                // Try setting DLL directory with current app directory first
                if (!string.IsNullOrEmpty(appDirectory) && Directory.Exists(appDirectory))
                {
                    SetDllDirectory(appDirectory);
                    Console.WriteLine($"[ABot.C# Interpreter]   [+] SetDllDirectory({appDirectory})");
                }
                
                // Try adding debug directory if it exists
                if (Directory.Exists(debugDirectory))
                {
                    try
                    {
                        AddDllDirectory(debugDirectory);
                        Console.WriteLine($"[ABot.C# Interpreter]   [+] AddDllDirectory({debugDirectory})");
                    }
                    catch (Exception adEx)
                    {
                        Console.WriteLine($"[ABot.C# Interpreter]   [!] AddDllDirectory failed (Windows 7?): {adEx.Message}");
                    }
                }
                
                // Also try current directory
                if (!string.IsNullOrEmpty(currentDirectory) && Directory.Exists(currentDirectory))
                {
                    Console.WriteLine($"[ABot.C# Interpreter]   [+] Current working directory is searchable");
                }
                
                // Check if DLL exists in any of these locations
                string[] dllSearchPaths = new[]
                {
                    Path.Combine(appDirectory, "ABot.Core.dll"),
                    Path.Combine(debugDirectory, "ABot.Core.dll"),
                    Path.Combine(currentDirectory, "ABot.Core.dll"),
                    "ABot.Core.dll"  // System PATH
                };
                
                Console.WriteLine("[ABot.C# Interpreter] Checking for ABot.Core.dll in search paths:");
                foreach (var path in dllSearchPaths)
                {
                    bool exists = File.Exists(path);
                    Console.WriteLine($"  {(exists ? "✓" : "✗")} {path}");
                }
            }
            catch (Exception setupEx)
            {
                Console.WriteLine($"[ABot.C# Interpreter] WARNING: DLL path setup failed: {setupEx.Message}");
                Console.WriteLine("[ABot.C# Interpreter] Continuing anyway - system PATH search may still work");
            }
            
            // ============ Phase 2: Load ABot.Core ============
            try
            {
                // 尝试调用abot_create来初始化C++对象
                Console.WriteLine("[ABot.C# Interpreter] Calling abot_create()...");
                _handle = abot_create();
                
                if (_handle == IntPtr.Zero)
                {
                    _loadError = "abot_create() returned NULL handle";
                    Console.WriteLine($"[ABot.C# Interpreter] ERROR: {_loadError}");
                }
                else
                {
                    Console.WriteLine($"[ABot.C# Interpreter] Successfully created interpreter handle: 0x{_handle.ToInt64():X}");
                    
                    // 验证是否准备就绪
                    int ready = abot_is_ready(_handle);
                    _isReady = (ready != 0);
                    Console.WriteLine($"[ABot.C# Interpreter] Initial IsReady status: {_isReady}");
                    
                    // 尝试获取版本信息
                    try
                    {
                        IntPtr versionPtr = abot_get_version();
                        string version = Marshal.PtrToStringAnsi(versionPtr) ?? "unknown";
                        Console.WriteLine($"[ABot.C# Interpreter] ABot version: {version}");
                    }
                    catch (Exception verifyEx)
                    {
                        Console.WriteLine($"[ABot.C# Interpreter] WARNING: Failed to get version: {verifyEx.Message}");
                    }
                }
            }
            catch (DllNotFoundException dllEx)
            {
                _loadError = $"ABot.Core.dll not found: {dllEx.Message}. P/Invoke requires native C++ DLL";
                Console.WriteLine($"[ABot.C# Interpreter] DllNotFoundException: {_loadError}");
                System.Diagnostics.Debug.WriteLine($"[ABot.C#] ABot.Core.dll not found: {dllEx.Message}");
            }
            catch (EntryPointNotFoundException epEx)
            {
                _loadError = $"C API function not found in ABot.Core.dll: {epEx.Message}";
                Console.WriteLine($"[ABot.C# Interpreter] EntryPointNotFoundException: {_loadError}");
                System.Diagnostics.Debug.WriteLine($"[ABot.C#] C API entry point not found: {epEx.Message}");
            }
            catch (BadImageFormatException bife)
            {
                // Architecture or format mismatch
                _loadError = $"ABot.Core.dll architecture mismatch or invalid format: {bife.Message}";
                Console.WriteLine($"[ABot.C# Interpreter] BadImageFormatException: {_loadError}");
                Console.WriteLine("[ABot.C# Interpreter] Possible causes:");
                Console.WriteLine("  - DLL is x86 but process is x64 (or vice versa)");
                Console.WriteLine("  - DLL is corrupted or invalid");
                Console.WriteLine("  - Runtime incompatibility");
            }
            catch (Exception ex)
            {
                // 捕获任何其他P/Invoke异常
                _loadError = $"P/Invoke error: {ex.GetType().Name}: {ex.Message}";
                Console.WriteLine($"[ABot.C# Interpreter] P/Invoke exception: {_loadError}");
                System.Diagnostics.Debug.WriteLine($"[ABot.C#] P/Invoke error: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _loadError = $"Unexpected error during ABot.Core initialization: {ex.GetType().Name}: {ex.Message}";
            Console.WriteLine($"[ABot.C# Interpreter] Unexpected error: {_loadError}");
            Console.WriteLine($"[ABot.C# Interpreter] StackTrace: {ex.StackTrace}");
            System.Diagnostics.Debug.WriteLine($"[ABot.C#] Unexpected error during initialization: {ex.Message}");
        }
        
        Console.WriteLine($"[ABot.C# Interpreter] Constructor END - handle is {(_handle != IntPtr.Zero ? "VALID" : "NULL")}");
    }

    // ============ 公开API方法 ============

    /// <summary>
    /// 解析人物卡参数单元定义
    /// 格式为 ABOT 自定义的参数单元语法（非标准 XML）
    /// 格式示例：<character name=hero, attributes={hp=100, mp=50}, def=expr(...)>
    /// </summary>
    public int ParseCharacter(string characterXml)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(characterXml))
            throw new ArgumentNullException(nameof(characterXml));

        if (_handle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[ABot.C#] ParseCharacter: Interpreter not initialized");
            return -1;
        }

        try
        {
            // 转换为UTF-8并调用C++
            IntPtr utf8Ptr = StringToUtf8Ptr(characterXml);
            try
            {
                int result = abot_parse_character_utf8(_handle, utf8Ptr);
                return result == ABOT_OK ? 0 : result;
            }
            finally
            {
                Marshal.FreeHGlobal(utf8Ptr);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to parse character card: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 注册技能集参数单元
    /// 格式为 ABOT 自定义的参数单元语法（非标准 XML）
    /// 格式示例：<skillset id=sword_skills, skills={slash={power=10, cost=5}}, def=expr(...)>
    /// </summary>
    public int RegisterSkillset(string skillsetXml)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(skillsetXml))
            throw new ArgumentNullException(nameof(skillsetXml));

        if (_handle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[ABot.C#] RegisterSkillset: Interpreter not initialized");
            return -1;
        }

        try
        {
            IntPtr utf8Ptr = StringToUtf8Ptr(skillsetXml);
            try
            {
                int result = abot_register_skillset_utf8(_handle, utf8Ptr);
                return result == ABOT_OK ? 0 : result;
            }
            finally
            {
                Marshal.FreeHGlobal(utf8Ptr);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to register skillset: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 注册状态集参数单元
    /// 格式为 ABOT 自定义的参数单元语法（非标准 XML）
    /// 格式示例：<stateset id=conditions, states={poison={effect=-1hp/turn, duration=3}}, def=expr(...)>
    /// </summary>
    public int RegisterStateset(string statesetXml)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(statesetXml))
            throw new ArgumentNullException(nameof(statesetXml));

        if (_handle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[ABot.C#] RegisterStateset: Interpreter not initialized");
            return -1;
        }

        try
        {
            IntPtr utf8Ptr = StringToUtf8Ptr(statesetXml);
            try
            {
                int result = abot_register_stateset_utf8(_handle, utf8Ptr);
                return result == ABOT_OK ? 0 : result;
            }
            finally
            {
                Marshal.FreeHGlobal(utf8Ptr);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to register stateset: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 注册安科集参数单元
    /// 格式为 ABOT 自定义的参数单元语法（非标准 XML）
    /// 格式示例：<ankeset id=damage_calc, formulas={physical_dmg=expr(ATK-DEF), magic_dmg=expr(INT*2-RES)}, def=expr(...)>
    /// </summary>
    public int RegisterANKESet(string ankesetXml)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(ankesetXml))
            throw new ArgumentNullException(nameof(ankesetXml));

        if (_handle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[ABot.C#] RegisterANKESet: Interpreter not initialized");
            return -1;
        }

        try
        {
            IntPtr utf8Ptr = StringToUtf8Ptr(ankesetXml);
            try
            {
                int result = abot_register_ankeset_utf8(_handle, utf8Ptr);
                return result == ABOT_OK ? 0 : result;
            }
            finally
            {
                Marshal.FreeHGlobal(utf8Ptr);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to register ankeset: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 执行战斗循环
    /// 执行完整的战斗模拟
    /// </summary>
    public int ExecuteBattle()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[ABot.C#] ExecuteBattle: Interpreter not initialized");
            return -1;
        }

        try
        {
            int result = abot_execute_battle(_handle);
            return result == ABOT_OK ? 0 : result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to execute battle: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 执行 ABOT 脚本代码
    /// 支持词法分析、语法分析、编译和执行
    /// 支持UTF-8编码中文字符
    /// </summary>
    public int ExecuteScript(string script)
    {
        ThrowIfDisposed();

        if (string.IsNullOrEmpty(script))
            throw new ArgumentNullException(nameof(script));

        if (_handle == IntPtr.Zero)
        {
            System.Diagnostics.Debug.WriteLine("[ABot.C#] ExecuteScript: Interpreter not initialized");
            return -1;
        }

        try
        {
            // 将C# UTF-16 string转换为UTF-8字节数组
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(script);
            
            // 记录 UTF-8 字节进行调试
            string firstBytes = string.Join(" ", utf8Bytes.Take(50).Select(b => b.ToString("X2")));
            Console.WriteLine($"[ABot.C# ExecuteScript] UTF-8 bytes (first 50): {firstBytes}");
            
            // 创建非托管内存缓冲区
            IntPtr scriptPtr = Marshal.AllocHGlobal(utf8Bytes.Length + 1);
            try
            {
                // 复制字节数据到非托管内存
                Marshal.Copy(utf8Bytes, 0, scriptPtr, utf8Bytes.Length);
                // 添加空终止符
                Marshal.WriteByte(scriptPtr, utf8Bytes.Length, 0);
                
                Console.WriteLine($"[ABot.C# ExecuteScript] Calling abot_execute_script_direct with scriptPtr={scriptPtr.ToInt64():X}");
                
                // 调用C++函数，传递handle和UTF-8编码的脚本指针
                int result = abot_execute_script_direct(_handle, scriptPtr);
                return result == ABOT_OK ? 0 : result;
            }
            finally
            {
                // 释放非托管内存
                Marshal.FreeHGlobal(scriptPtr);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to execute script: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// 直接调用abot_execute_script，接收handle和原始指针
    /// </summary>
    [DllImport(ABOT_DLL_NAME, CallingConvention = CallingConvention.Cdecl, EntryPoint = "abot_execute_script")]
    private static extern int abot_execute_script_direct(IntPtr handle, IntPtr scriptPtr);

    /// <summary>
    /// 检查解释器是否已就绪
    /// 返回 true 如果程序已加载且可以执行
    /// </summary>
    public bool IsReady()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            Console.WriteLine("[ABot.C# IsReady] Interpreter handle is NULL");
            return false;
        }

        try
        {
            int result = abot_is_ready(_handle);
            bool ready = (result != 0);
            Console.WriteLine($"[ABot.C# IsReady] abot_is_ready returned: {result} (= {ready})");
            return ready;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C# IsReady] Exception: {ex.GetType().Name}: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[ABot.C#] IsReady error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取初始化失败的详细原因
    /// 如果为 null，表示初始化成功
    /// </summary>
    public string? GetLoadError() => _loadError;

    /// <summary>
    /// 获取最后发生的错误信息
    /// 返回上一次操作中的错误描述（UTF-8编码）
    /// </summary>
    public string GetLastError()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return "Interpreter not initialized";
        }

        try
        {
            IntPtr errorPtr = abot_get_last_error_utf8(_handle);
            // 使用UTF-8解码而不是ANSI
            string error = Utf8PtrToString(errorPtr);
            return string.IsNullOrEmpty(error) ? "Unknown error" : error;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// 清空错误状态
    /// 清除最后一次错误消息
    /// </summary>
    public void ClearError()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            abot_clear_error(_handle);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ABot.C#] ClearError error: {ex.Message}");
        }
    }

    // ============ 角色调试信息方法 ============

    /// <summary>
    /// 获取已解析角色的完整调试信息
    /// 包括基本信息、技能集、状态集、标签等
    /// </summary>
    public string? GetCharacterDebugInfo()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return "Interpreter not initialized";
        }

        try
        {
            IntPtr infoPtr = abot_get_character_debug_info_utf8(_handle);
            return Utf8PtrToString(infoPtr);
        }
        catch (Exception ex)
        {
            return $"Error getting character debug info: {ex.Message}";
        }
    }

    /// <summary>
    /// 获取已解析角色的基本信息
    /// 包括名字、阵营、HP、ATK、仇恨值等
    /// </summary>
    public string? GetCharacterBasicInfo()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return "Interpreter not initialized";
        }

        try
        {
            IntPtr infoPtr = abot_get_character_basic_info_utf8(_handle);
            return Utf8PtrToString(infoPtr);
        }
        catch (Exception ex)
        {
            return $"Error getting character basic info: {ex.Message}";
        }
    }

    /// <summary>
    /// 获取已解析角色的技能信息
    /// 列出所有技能及其属性
    /// </summary>
    public string? GetCharacterSkillsInfo()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return "Interpreter not initialized";
        }

        try
        {
            IntPtr infoPtr = abot_get_character_skills_info_utf8(_handle);
            return Utf8PtrToString(infoPtr);
        }
        catch (Exception ex)
        {
            return $"Error getting character skills info: {ex.Message}";
        }
    }

    /// <summary>
    /// 获取已解析角色的状态信息
    /// 列出所有状态效果及其属性
    /// </summary>
    public string? GetCharacterStatesInfo()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return "Interpreter not initialized";
        }

        try
        {
            IntPtr infoPtr = abot_get_character_states_info_utf8(_handle);
            return Utf8PtrToString(infoPtr);
        }
        catch (Exception ex)
        {
            return $"Error getting character states info: {ex.Message}";
        }
    }

    /// <summary>
    /// 诊断脚本编译状态
    /// 返回编译结果：0=成功, 1=Lexer失败, 2=Parser失败, 3=Compiler失败, -1=参数错误
    /// </summary>
    public int DiagnoseScriptCompilation(string script, out string diagnosis)
    {
        ThrowIfDisposed();
        diagnosis = "";

        if (string.IsNullOrEmpty(script))
        {
            diagnosis = "Script is empty";
            return -1;
        }

        try
        {
            StringBuilder errorBuffer = new StringBuilder(1024);
            int result = DiagnoseScript_Native(script, errorBuffer, errorBuffer.Capacity);
            diagnosis = errorBuffer.ToString();
            return result;
        }
        catch (Exception ex)
        {
            diagnosis = $"Error during compilation diagnosis: {ex.Message}";
            return -1;
        }
    }

    // ============ 回合管理器方法 ============

    /// <summary>
    /// 将已解析的角色添加到回合管理器
    /// 必须在调用 InitializeRoundManager 之前多次调用以添加所有角色
    /// </summary>
    public int AddCharacterToRoundManager()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            int result = abot_round_manager_add_character(_handle);
            return result == ABOT_OK ? 0 : result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] AddCharacterToRoundManager error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 清除所有参战角色并重置战斗状态
    /// 用于 .abot script 命令开始新战斗时
    /// </summary>
    public int ClearAllCharacters()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            int result = abot_round_manager_clear_all_characters(_handle);
            return result == ABOT_OK ? 0 : result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] ClearAllCharacters error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 初始化回合管理器
    /// 必须在战斗开始前调用（在添加所有角色之后）
    /// </summary>
    public int InitializeRoundManager()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            int result = abot_round_manager_init(_handle);
            return result == ABOT_OK ? 0 : result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] InitializeRoundManager error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 推进一个回合
    /// 执行当前行动者的技能，应用结果，推进到下一回合
    /// </summary>
    public int AdvanceRound()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            ABotLogger.Debug($"[ADVANCE] About to call C++ abot_round_manager_advance() with handle {_handle}");
            int result = abot_round_manager_advance(_handle);
            ABotLogger.Debug($"[ADVANCE] C++ returned with code: {result}");
            
            if (result != ABOT_OK)
            {
                string lastErr = GetLastError();
                ABotLogger.Error($"[ADVANCE] C++ error (code {result}): {lastErr}");
            }
            
            return result == ABOT_OK ? 0 : result;
        }
        catch (Exception ex)
        {
            ABotLogger.Error($"[ABot.C#] AdvanceRound exception: {ex.Message}\n{ex.StackTrace}");
            return -1;
        }
    }

    /// <summary>
    /// 推进指定数量的回合
    /// </summary>
    public int AdvanceRounds(int count)
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            int result = abot_round_manager_advance_multiple(_handle, count);
            return result == ABOT_OK ? 0 : result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] AdvanceRounds error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 跳过当前回合
    /// </summary>
    public int SkipRound()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            int result = abot_round_manager_skip(_handle);
            return result == ABOT_OK ? 0 : result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] SkipRound error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 暂停战斗
    /// </summary>
    public void PauseRound()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            abot_round_manager_pause(_handle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] PauseRound error: {ex.Message}");
        }
    }

    /// <summary>
    /// 恢复战斗
    /// </summary>
    public void ResumeRound()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            abot_round_manager_resume(_handle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] ResumeRound error: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查战斗是否在运行中
    /// </summary>
    public bool IsRoundRunning()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            int result = abot_round_manager_is_running(_handle);
            return result != 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] IsRoundRunning error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检查战斗是否已结束
    /// </summary>
    public bool IsRoundFinished()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            int result = abot_round_manager_is_finished(_handle);
            return result != 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] IsRoundFinished error: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// 获取当前回合数
    /// </summary>
    public int GetCurrentRound()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return -1;
        }

        try
        {
            return abot_round_manager_get_current_round(_handle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] GetCurrentRound error: {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    /// 获取回合状态摘要（格式化文本版本，用于显示）
    /// </summary>
    public string GetRoundStatus()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return "Interpreter not initialized";
        }

        try
        {
            IntPtr statusPtr = abot_round_manager_get_status_utf8(_handle);
            return Utf8PtrToString(statusPtr);
        }
        catch (Exception ex)
        {
            return $"Error getting round status: {ex.Message}";
        }
    }

    /// <summary>
    /// ✅ 新增函数：获取 RoundManager 的 JSON 格式（用于状态恢复）
    /// 这个函数返回结构化的 JSON 对象，而不是格式化的文本
    /// C++ 端需要从这个 JSON 中恢复 RoundManager 状态
    /// </summary>
    public string GetRoundManagerJson()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return "{\"current_round\":0,\"is_running\":false,\"characters\":[]}";
        }

        try
        {
            // 获取文本格式的状态（包含"Round: X", "Participants: Y"等信息）
            string textStatus = GetRoundStatus();
            
            // 从文本中解析信息（简单的正则匹配）
            int currentRound = 0;
            bool isRunning = false;
            int participantCount = 0;
            
            // 尝试从文本中提取 Round 数字 (e.g., "Current Round: 2")
            var roundMatch = System.Text.RegularExpressions.Regex.Match(textStatus, @"Current Round:\s*(\d+)");
            if (roundMatch.Success && int.TryParse(roundMatch.Groups[1].Value, out int round))
            {
                currentRound = round;
            }
            
            // 尝试从文本中提取运行状态 (e.g., "Is Running: Yes")
            var runningMatch = System.Text.RegularExpressions.Regex.Match(textStatus, @"Is Running:\s*(Yes|No)");
            if (runningMatch.Success)
            {
                isRunning = runningMatch.Groups[1].Value == "Yes";
            }
            
            // 尝试从文本中提取参与者数量 (e.g., "Total Characters: 2")
            var participantMatch = System.Text.RegularExpressions.Regex.Match(textStatus, @"Total Characters:\s*(\d+)");
            if (participantMatch.Success && int.TryParse(participantMatch.Groups[1].Value, out int count))
            {
                participantCount = count;
            }
            
            // 构建结构化的 JSON 对象
            var json = new System.Text.StringBuilder();
            json.Append("{");
            json.Append($"\"current_round\":{currentRound},");
            json.Append($"\"is_running\":{(isRunning ? "true" : "false")},");
            json.Append($"\"participant_count\":{participantCount},");
            json.Append($"\"status_text\":{EscapeJsonString(textStatus)}");
            json.Append("}");
            
            return json.ToString();
        }
        catch (Exception ex)
        {
            ABotLogger.Warn($"[GetRoundManagerJson] Error: {ex.Message}, returning empty battle state");
            // 返回有效的最小化 JSON
            return "{\"current_round\":0,\"is_running\":false,\"characters\":[]}";
        }
    }

    /// <summary>
    /// 获取回合日志
    /// </summary>
    public string GetRoundLog()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return "Interpreter not initialized";
        }

        try
        {
            IntPtr logPtr = abot_round_manager_get_log_utf8(_handle);
            return Utf8PtrToString(logPtr);
        }
        catch (Exception ex)
        {
            return $"Error getting round log: {ex.Message}";
        }
    }

    /// <summary>
    /// 获取技能触发日志
    /// </summary>
    public string GetSkillTriggerLog()
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return "Interpreter not initialized";
        }

        try
        {
            IntPtr skillLogPtr = abot_round_manager_get_skill_trigger_log_utf8(_handle);
            return Utf8PtrToString(skillLogPtr);
        }
        catch (Exception ex)
        {
            return $"Error getting skill trigger log: {ex.Message}";
        }
    }

    /// <summary>
    /// 执行外部指令（"advance", "skip", "pause", "resume", "restart"等）
    /// </summary>
    public int ExecuteRoundCommand(string command, string parameters = "")
    {
        ThrowIfDisposed();

        if (_handle == IntPtr.Zero)
        {
            return -1;
        }

        if (string.IsNullOrEmpty(command))
        {
            throw new ArgumentNullException(nameof(command));
        }

        try
        {
            IntPtr cmdPtr = StringToUtf8Ptr(command);
            IntPtr paramPtr = StringToUtf8Ptr(parameters);
            try
            {
                int result = abot_round_manager_execute_command_utf8(_handle, cmdPtr, paramPtr);
                return result == ABOT_OK ? 0 : result;
            }
            finally
            {
                Marshal.FreeHGlobal(cmdPtr);
                Marshal.FreeHGlobal(paramPtr);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ABot.C#] ExecuteRoundCommand error: {ex.Message}");
            return -1;
        }
    }

    // ============ 状态管理（多用户隔离支持）============

    /// <summary>
    /// 保存当前解释器状态为快照
    /// 用于 LRU 驱逐时的持久化保存
    /// 
    /// 快照包含：
    /// - 所有参战角色的完整 JSON 数组（从 RoundManager 获取）
    /// - 回合管理器状态（当前回合、战斗日志）
    /// - 版本和时间戳
    /// 
    /// 调用场景：
    /// 1. 当 LRU 缓存满（5个活跃用户），要驱逐最久未使用的用户时
    /// 2. 在阶段 5 中持久化到 SQLite 数据库
    /// 3. 用户离线时备份游戏进度
    /// </summary>
    public ABotStateSnapshot SaveState(long userId)
    {
        ThrowIfDisposed();

        // 【指令1】重构：获取 RoundManager 中的所有角色
        // 使用新的 abot_serialize_all_characters_json() 函数获取角色数组
        string allCharactersJson = Utf8PtrToString(abot_serialize_all_characters_json(_handle));
        
        ABotLogger.Info($"[SAVE STATE] User {userId}: Getting all characters from RoundManager");
        ABotLogger.Debug($"[SAVE STATE] Raw C++ JSON length: {allCharactersJson.Length}");
        ABotLogger.Debug($"[SAVE STATE] Raw C++ JSON (first 300 chars): {allCharactersJson.Substring(0, Math.Min(300, allCharactersJson.Length))}");
        
        // 验证返回的 JSON 是否为有效的数组
        if (!allCharactersJson.StartsWith("["))
        {
            ABotLogger.Warn($"[SAVE STATE] ⚠ Warning: Characters JSON does not start with '[', expected array format");
        }
        
        // 【关键修复】验证 JSON 中的中文字符是否完整
        // 如果检测到非法字符（\uFFFD 替换符），说明 C++ 解析出错，需要标记问题
        if (allCharactersJson.Contains("\uFFFD"))
        {
            ABotLogger.Error($"[SAVE STATE] ❌ CRITICAL: Detected corrupted character (U+FFFD) in C++ returned JSON!");
            ABotLogger.Error($"[SAVE STATE] This indicates the C++ name extraction failed. Full JSON for diagnosis:");
            ABotLogger.Error($"[SAVE STATE] {allCharactersJson}");
        }
        
        // 【编码防护】使用 Base64 编码 Characters 字段，防止数据库往返时中文被破坏
        // 编码流程：中文 JSON → UTF-8 字节 → Base64 字符串 → 数据库（安全）
        string charactersBase64 = "";
        try
        {
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(allCharactersJson);
            charactersBase64 = Convert.ToBase64String(utf8Bytes);
            
            // 【验证】验证 Base64 只包含有效字符
            if (!IsValidBase64(charactersBase64))
            {
                ABotLogger.Error($"[SAVE STATE] ❌ Generated Base64 contains invalid characters!");
            }
            
            ABotLogger.Info($"[SAVE STATE] ✅ Characters encoded with Base64 ({charactersBase64.Length} chars from {utf8Bytes.Length} UTF-8 bytes)");
            
            // 【测试解码】立即验证 Base64 是否真的可逆
            try
            {
                byte[] testDecoded = Convert.FromBase64String(charactersBase64);
                string testJson = Encoding.UTF8.GetString(testDecoded);
                if (testJson != allCharactersJson)
                {
                    ABotLogger.Error($"[SAVE STATE] ❌ Base64 roundtrip verification FAILED!");
                    ABotLogger.Error($"[SAVE STATE]   Original length: {allCharactersJson.Length}");
                    ABotLogger.Error($"[SAVE STATE]   Decoded length: {testJson.Length}");
                }
                else
                {
                    ABotLogger.Info($"[SAVE STATE] ✅ Base64 roundtrip verification passed");
                }
            }
            catch (Exception testEx)
            {
                ABotLogger.Error($"[SAVE STATE] ❌ Base64 decoding test failed: {testEx.Message}");
            }
        }
        catch (Exception encEx)
        {
            ABotLogger.Error($"[SAVE STATE] Base64 encoding failed: {encEx.Message}, using raw JSON");
            charactersBase64 = allCharactersJson;  // 降级：直接使用原始 JSON
        }
        
        var snapshot = new ABotStateSnapshot
        {
            UserId = userId,
            CreatedAt = DateTime.Now,
            // 【新格式】使用 Base64 编码后的 Characters 存储所有角色
            // 这样即使数据库编码出问题，也能正确恢复中文
            Characters = charactersBase64,
            // 【保留字段】保留旧字段以兼容旧代码，设为 null
            CharacterBasicInfo = null,
            CharacterSkillsInfo = null,
            CharacterStatesInfo = null,
            RoundManagerStatus = GetRoundManagerJson(),
            RoundManagerLog = GetRoundLog(),
            SkillTriggerLog = GetSkillTriggerLog(),
            LastError = GetLastError(),
            ABotVersion = GetVersion()
        };

        ABotLogger.Info($"[SAVE STATE] User {userId}: State saved successfully ({snapshot.EstimatedSizeBytes} bytes)");
        ABotLogger.Info($"[SAVE STATE] Characters count in array: {CountCharactersInJson(allCharactersJson)}");
        
        return snapshot;
    }
    
    /// <summary>
    /// 辅助方法：验证字符串是否为有效的 Base64 (仅包含 A-Za-z0-9+/= 字符)
    /// </summary>
    private bool IsValidBase64(string input)
    {
        if (string.IsNullOrEmpty(input))
            return true;
        
        // 移除可能的换行符和空格
        input = input.Replace("\r", "").Replace("\n", "").Replace(" ", "");
        
        // Base64 必须由 A-Za-z0-9+/= 组成，长度必须是 4 的倍数
        return System.Text.RegularExpressions.Regex.IsMatch(input, @"^[A-Za-z0-9+/]*={0,2}$") && 
               input.Length % 4 == 0;
    }
    
    /// <summary>
    /// 辅助方法：计算 JSON 数组中角色的数量
    /// </summary>
    private int CountCharactersInJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return 0;
        
        // 简单计数：数一下 "\"name\":" 出现的次数
        return System.Text.RegularExpressions.Regex.Matches(json, "\"name\":").Count;
    }

    /// <summary>
    /// 从快照恢复解释器状态
    /// 用于 LRU 驱逐后用户重新上线时的状态恢复
    /// 
    /// 恢复流程：
    /// 1. 接收一个之前保存的 ABotStateSnapshot
    /// 2. 验证快照为新格式（包含 Characters 数组）
    /// 3. 将快照数据转换为 JSON 格式
    /// 4. 调用 C++ 的 abot_import_state_json() 恢复状态
    /// 5. 严格验证返回值
    /// 
    /// 注意：
    /// - 【指令5】仅支持新格式快照（包含 Characters 数组字段）
    /// - 旧格式快照（没有 Characters 字段）将被拒绝
    /// - 此方法仅在新创建的解释器上调用
    /// - 如果恢复失败，返回 false，错误信息可通过 GetLastError() 获取
    /// </summary>
    public bool LoadState(ABotStateSnapshot snapshot)
    {
        ThrowIfDisposed();

        if (snapshot == null)
            throw new ArgumentNullException(nameof(snapshot));

        if (!snapshot.IsValid)
        {
            ABotLogger.Error($"[LOAD STATE] ❌ Invalid snapshot for user {snapshot.UserId}");
            return false;
        }

        if (_handle == IntPtr.Zero)
        {
            ABotLogger.Error($"[LOAD STATE] ❌ Interpreter handle is null for user {snapshot.UserId}");
            return false;
        }

        // 【指令5】严格检查快照格式：必须包含 Characters 数组
        if (string.IsNullOrEmpty(snapshot.Characters))
        {
            ABotLogger.Error($"[LOAD STATE] ❌ Snapshot missing 'Characters' array field");
            ABotLogger.Error($"[LOAD STATE] This snapshot is in old format and cannot be restored");
            ABotLogger.Error($"[LOAD STATE] Required: New format snapshot with 'characters' JSON array");
            ABotLogger.Error($"[LOAD STATE] Suggestion: Save a new snapshot from current game state");
            return false;
        }

        ABotLogger.Info($"[LOAD STATE] ✅ Snapshot is in new format (contains Characters array)");

        try
        {
            // 构造 JSON 格式的状态数据
            // 格式：{ "characters": [...], "round_manager": {...}, "logs": {...} }
            ABotLogger.Info($"[LOAD STATE] Constructing JSON for user {snapshot.UserId}...");
            string jsonState = ConstructStateJson(snapshot);
            ABotLogger.Info($"[LOAD STATE] JSON constructed: {jsonState.Length} bytes");
            
            // 🔍 详细诊断：分析 JSON 内容
            ABotLogger.Debug($"[LOAD STATE] JSON content dump:");
            ABotLogger.Debug($"[LOAD STATE] {jsonState}");
            
            // 验证 characters 在最终 JSON 中的位置
            int charArrayPos = jsonState.IndexOf("\"characters\":");
            if (charArrayPos >= 0)
            {
                int startPos = charArrayPos + "\"characters\":".Length;
                int endPos = jsonState.IndexOf("],\"", startPos) + 1;  // 找到数组结束的 ]
                if (endPos <= 0) endPos = jsonState.IndexOf("},\"", startPos);
                if (endPos <= 0) endPos = Math.Min(startPos + 300, jsonState.Length);
                
                string charArrayValue = jsonState.Substring(startPos, Math.Min(300, endPos - startPos));
                ABotLogger.Info($"[LOAD STATE] characters array value extracted (first {charArrayValue.Length} chars):");
                ABotLogger.Debug($"[LOAD STATE]   {charArrayValue}...");
            }
            else
            {
                ABotLogger.Error($"[LOAD STATE] ❌ JSON missing 'characters' field!");
                return false;
            }
            
            // 验证 round_manager 在最终 JSON 中的位置
            int roundMgrPos = jsonState.IndexOf("\"round_manager\":");
            if (roundMgrPos >= 0)
            {
                ABotLogger.Info($"[LOAD STATE] ✓ JSON contains round_manager field");
            }
            else
            {
                ABotLogger.Error($"[LOAD STATE] ❌ JSON missing round_manager field!");
                return false;
            }
            
            // 转换为 UTF-8 并传递给 C++
            ABotLogger.Info($"[LOAD STATE] Converting to UTF-8 and calling C++ import...");
            IntPtr utf8Ptr = StringToUtf8Ptr(jsonState);
            try
            {
                ABotLogger.Info($"[LOAD STATE] Calling abot_import_state_json_utf8()...");
                int result = abot_import_state_json_utf8(_handle, utf8Ptr);
                
                ABotLogger.Info($"[LOAD STATE] C++ function returned: {result}");
                
                // 【指令4】严格检查返回值
                if (result != ABOT_OK)
                {
                    // ❌ 导入失败
                    string error = GetLastError();
                    ABotLogger.Error($"[LOAD STATE] ❌ C++ import FAILED with return code: {result}");
                    ABotLogger.Error($"[LOAD STATE] Error message: {error}");
                    return false;  // 立即返回，禁止继续
                }
                
                // ✅ C++ 返回成功，但仍需检查 RoundManager 是否真的准备好了
                ABotLogger.Info($"[LOAD STATE] ✅ Import returned OK, performing health check...");
                
                // 调用新增的 abot_round_manager_is_ready() 检查状态完整性
                int is_ready = abot_round_manager_is_ready(_handle);
                
                if (is_ready != 1)
                {
                    // ❌ 导入返回成功，但 RoundManager 状态不完整
                    ABotLogger.Error($"[LOAD STATE] ❌ Health check FAILED: RoundManager not ready despite abot_import_state_json returning OK");
                    
                    string detailError = GetLastError();
                    if (!string.IsNullOrEmpty(detailError))
                    {
                        ABotLogger.Error($"[LOAD STATE] ❌ C++ error details: {detailError}");
                    }
                    return false;  // 立即返回
                }
                
                ABotLogger.Info($"[LOAD STATE] ✅ Health check PASSED: RoundManager is ready for execution");
                ABotLogger.Info($"[LOAD STATE] ✅ Successfully restored state for user {snapshot.UserId}");
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(utf8Ptr);
            }
        }
        catch (Exception ex)
        {
            ABotLogger.Error($"[LOAD STATE] ❌ Exception in LoadState: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// 构造状态快照的 JSON 表示
    /// 用于序列化为 JSON 格式传递给 C++
    /// 包含完整的状态信息，足以进行完全恢复
    /// 
    /// 【新格式】：包含 "characters" 数组，存储所有参战角色
    /// </summary>
    private string ConstructStateJson(ABotStateSnapshot snapshot)
    {
        // 【编码恢复】检测和解码 Characters 字段
        // 快照中的 Characters 可能是 Base64 编码的（用于防止数据库编码问题）
        // 也可能是原始 JSON
        string charactersJson = snapshot.Characters ?? "";
        
        if (!string.IsNullOrEmpty(charactersJson) && !charactersJson.StartsWith("["))
        {
            // 尝试从 Base64 解码
            try
            {
                byte[] decodedBytes = Convert.FromBase64String(charactersJson);
                string decodedJson = Encoding.UTF8.GetString(decodedBytes);
                
                // 验证是否为有效的 JSON 数组
                if (decodedJson.StartsWith("["))
                {
                    charactersJson = decodedJson;
                    ABotLogger.Info($"[JSON BUILD] ✅ Characters decoded from Base64");
                    
                    // 【防御性修复】Base64解码后，对所有名称字段进行Trim处理
                    // 防止UTF-16 BOM或其他编码问题导致的前导空格
                    charactersJson = TrimCharacterNamesInJson(charactersJson);
                }
            }
            catch
            {
                // Base64 解码失败，可能就是原始 JSON，继续使用
                ABotLogger.Debug($"[JSON BUILD] Characters is not Base64 encoded, using as raw JSON");
            }
        }
        
        // 【指令3】检查必要字段
        ABotLogger.Debug($"[JSON BUILD] Characters array: {(string.IsNullOrEmpty(charactersJson) ? "EMPTY" : charactersJson.Length + " bytes")}");
        ABotLogger.Debug($"[JSON BUILD] RoundManagerStatus: {(string.IsNullOrEmpty(snapshot.RoundManagerStatus) ? "EMPTY" : snapshot.RoundManagerStatus!.Length + " bytes")}");
        ABotLogger.Debug($"[JSON BUILD] RoundManagerLog: {(string.IsNullOrEmpty(snapshot.RoundManagerLog) ? "EMPTY" : snapshot.RoundManagerLog!.Length + " bytes")}");
        
        // 验证新格式字段
        if (string.IsNullOrEmpty(charactersJson))
        {
            ABotLogger.Warn($"[JSON BUILD] ⚠ Characters array is EMPTY! Cannot restore multi-character battle state.");
            // 这不是致命错误，可以继续，但会导致恢复失败
        }
        else
        {
            ABotLogger.Debug($"[JSON BUILD] Characters content (first 200 chars): {charactersJson.Substring(0, Math.Min(200, charactersJson.Length))}");
            
            // 检查是否为有效的 JSON 数组
            if (!charactersJson.StartsWith("["))
            {
                ABotLogger.Warn($"[JSON BUILD] ⚠ Characters field does not start with '[', may not be valid array JSON");
            }
        }
        
        if (string.IsNullOrEmpty(snapshot.RoundManagerStatus))
        {
            ABotLogger.Warn($"[JSON BUILD] ⚠ RoundManagerStatus is EMPTY! RoundManager may not be restorable.");
        }
        else
        {
            ABotLogger.Debug($"[JSON BUILD] RoundManagerStatus content: {snapshot.RoundManagerStatus}");
        }
        
        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append($"\"userId\":{snapshot.UserId},");
        sb.Append($"\"createdAt\":\"{snapshot.CreatedAt:O}\",");
        
        // 【新增】characters 数组 - 核心字段
        // 格式：[{角色1}, {角色2}, ...]
        if (string.IsNullOrEmpty(charactersJson))
        {
            sb.Append($"\"characters\":[],");  // 空数组
        }
        else if (IsValidJson(charactersJson))
        {
            // 直接使用，不转义
            sb.Append($"\"characters\":{charactersJson},");
        }
        else
        {
            // 一般不会发生，但以防万一转义
            ABotLogger.Warn($"[JSON BUILD] ⚠ Characters JSON validation failed, will escape as string");
            sb.Append($"\"characters\":{EscapeJsonString(charactersJson)},");
        }
        
        // 【向后兼容】保留旧的 characterBasicInfo 字段为 null
        sb.Append($"\"characterBasicInfo\":null,");
        sb.Append($"\"characterSkillsInfo\":null,");
        sb.Append($"\"characterStatesInfo\":null,");
        
        // 【必需】round_manager 字段
        if (string.IsNullOrEmpty(snapshot.RoundManagerStatus))
        {
            sb.Append($"\"round_manager\":null,");
        }
        else if (IsValidJson(snapshot.RoundManagerStatus))
        {
            sb.Append($"\"round_manager\":{snapshot.RoundManagerStatus},");
        }
        else
        {
            sb.Append($"\"round_manager\":{EscapeJsonString(snapshot.RoundManagerStatus)},");
        }
        
        sb.Append($"\"roundManagerLog\":{EscapeJsonString(snapshot.RoundManagerLog)},");
        sb.Append($"\"skillTriggerLog\":{EscapeJsonString(snapshot.SkillTriggerLog)},");
        sb.Append($"\"lastError\":{EscapeJsonString(snapshot.LastError)},");
        sb.Append($"\"aBotVersion\":\"{snapshot.ABotVersion}\"");
        sb.Append("}");
        
        string json = sb.ToString();
        ABotLogger.Info($"[JSON BUILD] Constructed JSON: {json.Length} bytes");
        ABotLogger.Info($"[JSON BUILD] JSON contains 'characters' field: {json.Contains("\"characters\":")}");
        ABotLogger.Info($"[JSON BUILD] JSON contains 'round_manager' field: {json.Contains("\"round_manager\":")}");
        ABotLogger.Debug($"[JSON BUILD] Final JSON: {json}");
        
        return json;
    }

    /// <summary>
    /// 检查字符串是否为有效的 JSON（对象或原始值）
    /// 用于判断是否需要在 ConstructStateJson 中进行转义
    /// </summary>
    private bool IsValidJson(string? str)
    {
        if (string.IsNullOrEmpty(str))
            return true;  // null/empty 是有效的 JSON
        
        str = str.Trim();
        
        // 检查是否为 JSON 对象或数组
        if ((str.StartsWith("{") && str.EndsWith("}")) ||
            (str.StartsWith("[") && str.EndsWith("]")))
        {
            try
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(str))
                {
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
        
        // 检查是否为 JSON 原始值（数字、null、true、false）
        if (str == "null" || str == "true" || str == "false" ||
            double.TryParse(str, out _))
        {
            return true;
        }
        
        // 其他情况（如字符串）不是有效的独立 JSON
        return false;
    }

    /// <summary>
    /// 转义字符串为 JSON 格式
    /// 处理 null、引号、特殊字符等
    /// </summary>
    private string EscapeJsonString(string? str)
    {
        if (string.IsNullOrEmpty(str))
            return "null";
        
        // 简单的 JSON 转义
        string escaped = str
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
        
        return $"\"{escaped}\"";
    }

    /// <summary>
    /// 获取 ABOT 解释器的版本号
    /// 这是一个静态方法，可以在不创建实例的情况下调用
    /// </summary>
    public static string GetVersion()
    {
        try
        {
            IntPtr versionPtr = abot_get_version();
            string? version = Marshal.PtrToStringAnsi(versionPtr);
            return version ?? "0.1.0-unknown";
        }
        catch (DllNotFoundException)
        {
            return "0.1.0 (ABot.Core not available)";
        }
        catch (Exception ex)
        {
            return $"0.1.0 (error: {ex.Message})";
        }
    }

    /// <summary>
    /// 获取当前运行时的对应信息
    /// </summary>
    private static string GetRuntimeInfo()
    {
        try
        {
            return System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        }
        catch
        {
            return ".NET runtime version unknown";
        }
    }

    // ============ 资源管理 ============


    /// <summary>
    /// 私有辅助方法：将C# string转换为UTF-8字节并返回非托管指针
    /// </summary>
    private IntPtr StringToUtf8Ptr(string? str)
    {
        if (string.IsNullOrEmpty(str))
        {
            // 返回空字符串指针
            IntPtr ptr = Marshal.AllocHGlobal(1);
            Marshal.WriteByte(ptr, 0);
            return ptr;
        }
        
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(str);
        IntPtr nativeUtf8 = Marshal.AllocHGlobal(utf8Bytes.Length + 1);
        Marshal.Copy(utf8Bytes, 0, nativeUtf8, utf8Bytes.Length);
        Marshal.WriteByte(nativeUtf8, utf8Bytes.Length, 0);  // 空终止符
        return nativeUtf8;
    }
    
    /// <summary>
    /// 私有辅助方法：从UTF-8字节指针读取字符串
    /// </summary>
    private string Utf8PtrToString(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero)
            return "";
            
        // 找到空终止符
        int length = 0;
        while (Marshal.ReadByte(ptr, length) != 0)
            length++;
            
        // 读取字节
        byte[] buffer = new byte[length];
        Marshal.Copy(ptr, buffer, 0, length);
        
        // 转换为UTF-8字符串
        return Encoding.UTF8.GetString(buffer);
    }

    /// <summary>
    /// 【防御性修复】在解码后的JSON中，对所有character对象的"name"字段进行Trim处理
    /// 防止UTF-16 BOM、编码问题导致的前导/末尾空格
    /// 例如：将 "name":"  海王" 修正为 "name":"海王"
    /// </summary>
    private string TrimCharacterNamesInJson(string json)
    {
        // 使用正则表达式找到所有 "name":"..." 字段并去除空格
        var result = new StringBuilder();
        int i = 0;
        
        while (i < json.Length)
        {
            // 查找 "name": 或 "name":
            if (i + 8 < json.Length && json.Substring(i, 7) == "\"name\"")
            {
                // 找到冒号
                int colonPos = i + 7;
                while (colonPos < json.Length && json[colonPos] != ':')
                    colonPos++;
                
                if (colonPos < json.Length && json[colonPos] == ':')
                {
                    colonPos++;  // Skip the colon
                    
                    // Skip whitespace after colon
                    while (colonPos < json.Length && char.IsWhiteSpace(json[colonPos]))
                        colonPos++;
                    
                    if (colonPos < json.Length && json[colonPos] == '"')
                    {
                        // Found the opening quote of the name value
                        int valueStart = colonPos + 1;
                        int valueEnd = valueStart;
                        
                        // Find the closing quote, handling escaped quotes
                        while (valueEnd < json.Length)
                        {
                            if (json[valueEnd] == '"')
                            {
                                // Check if it's escaped
                                int backslashCount = 0;
                                int checkPos = valueEnd - 1;
                                while (checkPos >= valueStart && json[checkPos] == '\\')
                                {
                                    backslashCount++;
                                    checkPos--;
                                }
                                
                                if (backslashCount % 2 == 0)
                                {
                                    // This quote is not escaped
                                    break;
                                }
                            }
                            valueEnd++;
                        }
                        
                        if (valueEnd < json.Length)
                        {
                            // Add the "name": part
                            result.Append(json.Substring(i, colonPos - i));
                            result.Append('"');
                            
                            // Extract and trim the name value
                            string nameValue = json.Substring(valueStart, valueEnd - valueStart);
                            string trimmedName = nameValue.Trim();
                            
                            // 【特殊处理】去除可能的UTF-8 BOM
                            if (trimmedName.Length >= 1 && trimmedName[0] == '\ufeff')
                            {
                                trimmedName = trimmedName.Substring(1);
                            }
                            
                            result.Append(trimmedName);
                            result.Append('"');
                            
                            i = valueEnd + 1;
                            continue;
                        }
                    }
                }
            }
            
            result.Append(json[i]);
            i++;
        }
        
        return result.ToString();
    }

    /// <summary>
    /// 【调试用】验证中文编码完整性
    /// 用于测试系统中文字符的处理是否正确
    /// 测试流程：String → UTF-8 bytes → Base64 → 数据库 → Base64 → UTF-8 bytes → String
    /// </summary>
    public string ValidateChineseEncoding()
    {
        const string TEST_CHINESE_NAMES = "烈海王,范马勇次郎,刃牙,ジャック・ハンマー";
        var result = new System.Text.StringBuilder();
        result.AppendLine("[中文编码验证报告]");
        result.AppendLine($"测试字符串: {TEST_CHINESE_NAMES}");
        result.AppendLine();
        
        try
        {
            // 步骤1：编码为 UTF-8 字节
            byte[] utf8Bytes = Encoding.UTF8.GetBytes(TEST_CHINESE_NAMES);
            result.AppendLine($"✅ UTF-8 编码成功: {utf8Bytes.Length} 字节");
            result.AppendLine($"   字节序列 (hex): {string.Join(" ", utf8Bytes.Take(20).Select(b => b.ToString("X2")))}...");
            
            // 步骤2：编码为 Base64
            string base64 = Convert.ToBase64String(utf8Bytes);
            result.AppendLine($"✅ Base64 编码成功: {base64.Length} 字符");
            result.AppendLine($"   Base64: {base64}");
            
            // 步骤3：模拟数据库往返（此处只演示，实际需要真实数据库操作）
            // 从 Base64 解码回 UTF-8
            byte[] decodedBytes = Convert.FromBase64String(base64);
            result.AppendLine($"✅ Base64 解码成功: {decodedBytes.Length} 字节");
            
            // 步骤4：字符串恢复
            string restored = Encoding.UTF8.GetString(decodedBytes);
            result.AppendLine($"✅ UTF-8 解码成功: {restored.Length} 字符");
            
            // 步骤5：验证完整性
            if (restored == TEST_CHINESE_NAMES)
            {
                result.AppendLine("✅ 验证通过: 原字符串与恢复字符串完全匹配");
            }
            else
            {
                result.AppendLine($"❌ 验证失败: 字符串不匹配");
                result.AppendLine($"   原始: {TEST_CHINESE_NAMES}");
                result.AppendLine($"   恢复: {restored}");
            }
            
            // 字符级别检查
            result.AppendLine();
            result.AppendLine("[逐字符验证]");
            for (int i = 0; i < Math.Min(TEST_CHINESE_NAMES.Length, restored.Length); i++)
            {
                char orig = TEST_CHINESE_NAMES[i];
                char rest = restored[i];
                string match = orig == rest ? "✅" : "❌";
                result.AppendLine($"{match} [{i}] 原: '{orig}' ({(int)orig:X4}) | 恢复: '{rest}' ({(int)rest:X4})");
            }
            
            result.AppendLine();
            result.AppendLine("✅ 中文编码验证完成");
        }
        catch (Exception ex)
        {
            result.AppendLine($"❌ 验证失败: {ex.Message}");
            result.AppendLine($"堆栈: {ex.StackTrace}");
        }
        
        return result.ToString();
    }

    /// <summary>
    /// 释放所有资源
    /// 调用此方法后，此对象不应再被使用
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 析构函数 - 确保资源被释放
    /// </summary>
    ~ABotInterpreter()
    {
        Dispose(false);
    }

    /// <summary>
    /// 受保护的 Dispose 方法
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            // 释放托管资源
            Console.WriteLine("[ABot.C# Dispose] Called (managed phase)");
        }

        // 释放非托管资源
        if (_handle != IntPtr.Zero)
        {
            try
            {
                Console.WriteLine($"[ABot.C# Dispose] Destroying interpreter handle: 0x{_handle.ToInt64():X}");
                abot_destroy(_handle);
                Console.WriteLine("[ABot.C# Dispose] Successfully destroyed C++ interpreter");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[ABot.C#] Error destroying C++ interpreter: {ex.Message}");
            }
        }

        _handle = IntPtr.Zero;
        _disposed = true;
    }

    /// <summary>
    /// 检查对象是否已被释放
    /// 如果已释放，抛出 ObjectDisposedException
    /// </summary>
    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(
                GetType().Name,
                "ABotInterpreter has been disposed");
        }
    }
}
