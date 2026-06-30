using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using MDiceV2.Interfaces.Mod;
using MDiceV2.Models;

namespace MDiceV2.Core.Mod;

/// <summary>
/// Shared runtime Mod initialization for UI and headless modes.
/// </summary>
public static class RuntimeModInitializer
{
    private static readonly object SyncRoot = new();
    private static RuntimeModInitializationResult? _current;

    public static RuntimeModInitializationResult InitializeModsForRuntime(
        string runtimeMode,
        string? modsPath = null,
        MessageProcessor? messageProcessor = null,
        bool forceReload = false)
    {
        runtimeMode = string.IsNullOrWhiteSpace(runtimeMode) ? "Unknown" : runtimeMode;
        modsPath ??= Path.Combine(Directory.GetCurrentDirectory(), "mods");
        modsPath = Path.GetFullPath(modsPath);

        lock (SyncRoot)
        {
            if (!forceReload && _current != null && PathsEqual(_current.ModsPath, modsPath))
            {
                Log.Normal($"[RuntimeModInitializer] Reusing existing Mod runtime mode={runtimeMode} bridgeId={GetObjectId(_current.ModEventBridge)} mods={string.Join(",", _current.Mods.Select(m => m.Id))}");
                AttachBridgeToProcessor(_current.ModEventBridge, messageProcessor);
                return _current;
            }

            Log.Normal($"[RuntimeMode] mode={runtimeMode}");
            Log.Normal($"[ModLoad] LoadAllMods START modsPath={modsPath}");
            Console.WriteLine($"[ModLoad] LoadAllMods START modsPath={modsPath}");

            Directory.CreateDirectory(modsPath);

            var messageDistribution = MessageDistribution.GetInstance();
            var modContext = new ModContextImpl(messageDistribution, "core");
            var loader = new ModPluginLoader(modsPath, modContext);
            var loadedMods = loader.LoadAllMods();
            var bridge = new ModEventBridge(modContext);
            var records = new List<RuntimeModRecord>();

            foreach (var (plugin, metadata) in loadedMods)
            {
                var record = new RuntimeModRecord(metadata.Id, metadata.Name, plugin.GetType().FullName ?? plugin.GetType().Name);

                try
                {
                    Log.Normal($"[ModLoad] Loaded mod id={metadata.Id} type={record.TypeName}");
                    Console.WriteLine($"[ModLoad] Loaded mod id={metadata.Id} type={record.TypeName}");

                    Log.Normal($"[ModLoad] {record.TypeName}.OnLoad START id={metadata.Id}");
                    plugin.OnLoad();
                    record.OnLoadExecuted = true;
                    Log.Normal($"[ModLoad] {record.TypeName}.OnLoad END id={metadata.Id}");

                    bridge.RegisterMod(plugin, metadata, isEnabled: true);
                    record.Registered = true;
                    Log.Normal($"[ModBridge] RegisterMod id={metadata.Id} enabled=true bridgeId={GetObjectId(bridge)}");
                    Console.WriteLine($"[ModBridge] RegisterMod id={metadata.Id} enabled=true bridgeId={GetObjectId(bridge)}");

                    Log.Normal($"[ModLoad] {record.TypeName}.OnEnable START id={metadata.Id}");
                    plugin.OnEnable();
                    record.OnEnableExecuted = true;
                    Log.Normal($"[ModLoad] {record.TypeName}.OnEnable END id={metadata.Id}");

                    record.Enabled = true;
                }
                catch (Exception ex)
                {
                    record.Error = ex.Message;
                    Log.Error($"[ModLoad] Failed mod id={metadata.Id} type={record.TypeName}: {ex.Message}\n{ex.StackTrace}");
                    Console.WriteLine($"[ModLoad] Failed mod id={metadata.Id} type={record.TypeName}: {ex.Message}");
                }

                records.Add(record);
            }

            var result = new RuntimeModInitializationResult(runtimeMode, modsPath, bridge, records);
            _current = result;

            AttachBridgeToProcessor(bridge, messageProcessor);

            var enabledIds = bridge.GetAllMods()
                .Where(x => x.Value.IsEnabled)
                .Select(x => x.Key)
                .ToArray();
            Log.Normal($"[ModLoad] LoadAllMods END loaded={records.Count} enabled={string.Join(",", enabledIds)} bridgeId={GetObjectId(bridge)}");
            Console.WriteLine($"[ModLoad] LoadAllMods END loaded={records.Count} enabled={string.Join(",", enabledIds)} bridgeId={GetObjectId(bridge)}");

            return result;
        }
    }

    public static RuntimeModInitializationResult? Current
    {
        get
        {
            lock (SyncRoot)
            {
                return _current;
            }
        }
    }

    public static void UnloadCurrent()
    {
        lock (SyncRoot)
        {
            if (_current == null)
                return;

            _current.ModEventBridge.UnloadAllMods();
            _current = null;
        }
    }

    internal static void ResetForTests()
    {
        lock (SyncRoot)
        {
            _current = null;
        }
    }

    private static void AttachBridgeToProcessor(ModEventBridge bridge, MessageProcessor? messageProcessor)
    {
#pragma warning disable CS0618
        var processor = messageProcessor ?? MessageProcessor.GetInstance();
#pragma warning restore CS0618
        processor.SetModEventBridge(bridge);
        Log.Normal($"[RuntimeModInitializer] MessageProcessor.SetModEventBridge called bridgeId={GetObjectId(bridge)} processorId={GetObjectId(processor)}");
        Console.WriteLine($"[RuntimeModInitializer] MessageProcessor.SetModEventBridge called bridgeId={GetObjectId(bridge)} processorId={GetObjectId(processor)}");
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetObjectId(object? instance)
    {
        return instance is null ? "null" : RuntimeHelpers.GetHashCode(instance).ToString();
    }
}

public sealed class RuntimeModInitializationResult
{
    public RuntimeModInitializationResult(
        string runtimeMode,
        string modsPath,
        ModEventBridge modEventBridge,
        IReadOnlyList<RuntimeModRecord> mods)
    {
        RuntimeMode = runtimeMode;
        ModsPath = modsPath;
        ModEventBridge = modEventBridge;
        Mods = mods;
    }

    public string RuntimeMode { get; }
    public string ModsPath { get; }
    public ModEventBridge ModEventBridge { get; }
    public IReadOnlyList<RuntimeModRecord> Mods { get; }
}

public sealed class RuntimeModRecord
{
    public RuntimeModRecord(string id, string name, string typeName)
    {
        Id = id;
        Name = name;
        TypeName = typeName;
    }

    public string Id { get; }
    public string Name { get; }
    public string TypeName { get; }
    public bool OnLoadExecuted { get; set; }
    public bool Registered { get; set; }
    public bool OnEnableExecuted { get; set; }
    public bool Enabled { get; set; }
    public string? Error { get; set; }
}
