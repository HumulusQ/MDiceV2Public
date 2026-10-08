# Portable Mod (`.mmod`)

`.mmod` is a ZIP archive. Its root must contain `mod.json` and the managed entry DLL. The uploaded file name is ignored; `commandName` in the manifest is the stable name used by `.mod` commands.

```json
{
  "id": "com.example.weather",
  "commandName": "weather",
  "name": "Weather Mod",
  "version": "1.0.0",
  "author": "Example",
  "description": "Provides weather data and administrator controls.",
  "dllFileName": "WeatherMod.dll",
  "pluginClassName": "Example.WeatherMod",
  "priority": 100,
  "modType": "dll",
  "supportHotReload": true,
  "apiVersion": "1.0",
  "packageType": "portable"
}
```

`commandName` is case-insensitive and may contain only ASCII letters, digits, `_`, and `-`. A portable update must keep both `id` and `commandName` unchanged. Ordinary directory Mods may omit `commandName`; the host then uses their `id` as the management name.

Portable Mods are loaded through a collectible `AssemblyLoadContext`. They must stop timers, subscriptions, and background work in `OnDisable`/`OnUnload`; otherwise the host will isolate the old instance but will log that its load context could not be collected.

## Management commands

Implement `IModManagementCommandProvider` in addition to `IModPlugin`:

```csharp
public IReadOnlyCollection<ModManagementCommand> GetModManagementCommands() =>
[
    new("status", "Show current status", (args, message) => "ready"),
    new("set", "Change a setting", (args, message) => Apply(args))
];
```

The host displays the manifest `description` and registered command descriptions in `.mod menu weather`. 骰子管理员 selects the target with `.mod menu weather`, `.mod on weather`, `.mod off weather`, or `.mod reload weather`; the selection is cached per user in memory. After that, `.mod cmd status` or `.mod cmd set value` is routed to the selected Mod. All management command arguments preserve their original casing.

`.mmod` uploads are accepted only from dice administrators (system account, Master, or authorization level 0/1). Packages are limited to 100 MiB compressed, 500 MiB expanded, 4096 entries, and a 100:1 per-entry compression ratio. Packages are not required to be signed and execute with the host process's permissions.
