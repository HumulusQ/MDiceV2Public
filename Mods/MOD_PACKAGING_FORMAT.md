# .mod 文件格式规范

## 概述

`.mod` 文件是 MDiceV2 Mod 的分发和部署格式。本质上是一个 ZIP 压缩包，包含 Mod 的所有必要文件。

---

## 文件结构

### 基本结构

```
MyMod.mod (实际是 ZIP 压缩包)
├── mod.json                  # Mod 元数据（必需）
├── MyMod.dll                 # 编译后的 DLL 文件（必需）
├── data.json                 # 数据配置（可选，取决于 Mod）
├── config.json               # 用户配置模板（可选）
├── README.md                 # Mod 说明文档（推荐）
└── resources/                # 资源文件夹（可选）
    ├── icons/                # 图标文件
    └── templates/            # 模板文件
```

### 文件详解

| 文件 | 必需 | 说明 |
|------|------|------|
| `mod.json` | ✅ | Mod 元数据配置，定义 Mod 信息和加载参数 |
| `*.dll` | ✅ | 编译后的 DLL 文件，必须与 mod.json 中的 dllFileName 匹配 |
| `data.json` | ❌ | Mod 运行时数据（如规则库），OnLoad() 时加载 |
| `config.json` | ❌ | 用户配置模板，用户可在 data/mods/ModName/ 中修改 |
| `README.md` | ❌ | Mod 说明文档（强烈推荐） |
| `resources/` | ❌ | 其他资源文件夹（图标、图片等） |
| `LICENSE` | ❌ | 许可证文件 |

---

## mod.json 详细格式

```json
{
  "id": "com.example.mymod",
  "name": "My Mod",
  "version": "1.0.0",
  "author": "Author Name",
  "description": "A brief description of what this mod does.",
  "dllFileName": "MyMod.dll",
  "pluginClassName": "MyNamespace.MyModPlugin",
  "priority": 100,
  "modType": "dll",
  "supportHotReload": false,
  "apiVersion": "1.0",
  "minApiVersion": "1.0",
  "maxApiVersion": "1.x"
}
```

### 字段详解

```json
{
  // 唯一标识符（反向域名格式）
  "id": "com.author.modname",
  
  // 显示名称（用户可读）
  "name": "My Awesome Mod",
  
  // 版本号（SemVer 格式）
  "version": "1.2.3",
  
  // 作者名称
  "author": "Your Name",
  
  // 简短描述
  "description": "...",
  
  // DLL 文件名（相对于 mod 文件夹）
  // 例如：MyMod.dll 或 bin/MyMod.dll
  "dllFileName": "MyMod.dll",
  
  // 实现 IModPlugin 的完整类名（可选，不指定时自动查找）
  "pluginClassName": "MyNamespace.MyModPlugin",
  
  // 执行优先级（高优先级先执行）
  // 建议值：50(低) 100(中) 150(高) 200(系统)
  "priority": 100,
  
  // Mod 类型（"dll" 或 "lua"）
  "modType": "dll",
  
  // 是否支持热卸载（DLL 应为 false）
  "supportHotReload": false,
  
  // 需要的 API 版本
  "apiVersion": "1.0",
  
  // 最低 API 版本要求
  "minApiVersion": "1.0",
  
  // 最高 API 版本支持（用于兼容性检查）
  "maxApiVersion": "1.x"
}
```

---

## 打包步骤（开发者指南）

### 方法 1: 使用 Windows 资源管理器（推荐用于初学者）

1. 创建文件夹结构：
   ```
   MyMod/
   ├── mod.json
   ├── MyMod.dll
   ├── data.json
   └── README.md
   ```

2. 在 MyMod 文件夹中右键，选择"发送到" → "压缩文件夹"

3. 将生成的 `MyMod.zip` 重命名为 `MyMod.mod`

### 方法 2: 使用 PowerShell（推荐用于自动化）

```powershell
# 编译 Mod
dotnet build MyMod/MyMod.csproj -c Release

# 创建打包文件夹
$ModName = "MyMod"
$Version = "1.0.0"
$OutputDir = ".\dist\$ModName-$Version"
mkdir $OutputDir -Force

# 复制文件
Copy-Item "MyMod\bin\Release\net10.0-windows\MyMod.dll" $OutputDir
Copy-Item "MyMod\mod.json" $OutputDir
Copy-Item "MyMod\data.json" $OutputDir -ErrorAction SilentlyContinue
Copy-Item "MyMod\README.md" $OutputDir -ErrorAction SilentlyContinue

# 压缩为 .mod 文件
$ModFile = ".\dist\$ModName-$Version.mod"
Compress-Archive -Path "$OutputDir\*" -DestinationPath $ModFile -Force

Write-Host "Mod 打包完成: $ModFile"
```

### 方法 3: 使用 7-Zip 命令行（推荐用于 CI/CD）

```bash
# 假设已编译 DLL 到 bin/Release

set MOD_NAME=MyMod
set VERSION=1.0.0
set OUTPUT=%MOD_NAME%-%VERSION%.mod

REM 创建 mod 文件（实际是 ZIP）
"C:\Program Files\7-Zip\7z.exe" a -tzip "%OUTPUT%" ^
  "mod.json" ^
  "bin\Release\net10.0-windows\MyMod.dll" ^
  "data.json" ^
  "README.md"

echo Mod 已打包: %OUTPUT%
```

---

## 部署步骤（用户指南）

1. **获取 .mod 文件**
   - 从开发者获取 `MyMod.mod` 文件

2. **打开 MDiceV2 程序**
   - 启动 MDiceV2

3. **进入 Mod 管理面板**
   - 点击导航栏的"Mods"选项

4. **添加 Mod**
   - 点击"Add Mod"按钮
   - 选择 `MyMod.mod` 文件

5. **自动解压和部署**
   - 程序会自动将 .mod 解压到 `data/mods/MyMod/`
   - 如果同名 Mod 存在，会添加时间戳避免覆盖

6. **启用 Mod**
   - 重启程序或手动启用 Mod

---

## 文件验证和兼容性检查

加载 Mod 时，宿主程序应进行以下检查：

### 1. mod.json 验证

```
✓ 文件存在且有效 JSON
✓ id 字段唯一（与已加载 Mod 不重复）
✓ 名称和版本符合规范
✓ dllFileName 指定的文件存在
```

### 2. DLL 文件验证

```
✓ 文件存在
✓ 是有效的 .NET DLL
✓ 包含实现 IModPlugin 的类（通过反射检查）
✓ 与 MDiceV2.Interfaces 版本兼容
```

### 3. API 版本检查

```csharp
// 伪代码
var modApiVersion = Version.Parse(metadata.ApiVersion);
var hostApiVersion = new Version("1.0");

if (modApiVersion > hostApiVersion)
{
    LogWarning($"Mod API 版本过新：{modApiVersion}，宿主版本：{hostApiVersion}");
    // 可以选择不加载或警告用户
}
```

---

## 最佳实践

### 文件大小

- **DLL 文件**：通常 50KB-5MB
- **data.json**：通常 <1MB（使用压缩 JSON）
- **.mod 文件**：通常 <10MB（包含所有文件）

### 命名规范

| 项目 | 规范 | 示例 |
|------|------|------|
| Mod ID | `com.author.modname` | `com.example.customreply` |
| 文件名 | `ModName.mod` | `CustomizedReply.mod` |
| 版本号 | SemVer (major.minor.patch) | `1.2.3` |
| 发布文件 | `ModName-version.mod` | `CustomizedReply-1.0.0.mod` |

### 文档要求

每个 .mod 都应包含 README.md，说明：
- Mod 功能描述
- 使用方法
- 配置参数
- 常见问题
- 作者信息和许可证

### 资源文件管理

如果 Mod 需要外部资源（图标、模板等）：

```
MyMod/
├── mod.json
├── MyMod.dll
├── resources/
│   ├── icons/
│   │   └── icon.png
│   └── templates/
│       └── template.json
└── README.md
```

在代码中访问资源：

```csharp
// 资源路径相对于 mod 文件夹
var resourcePath = Path.Combine(modPath, "resources", "icons", "icon.png");
```

---

## 版本兼容性管理

### 向后兼容更新

```json
{
  "version": "1.1.0",  // minor 版本号增加
  "apiVersion": "1.0"  // 保持 API 版本不变
}
```

### 破坏性更新

```json
{
  "version": "2.0.0",  // major 版本号增加
  "apiVersion": "2.0"  // API 版本更新
}
```

### 版本检查代码（伪代码）

```csharp
public bool IsModCompatible(ModMetadata metadata)
{
    var hostVersion = new Version("1.0");
    var minVersion = Version.TryParse(metadata.MinApiVersion, out var min) 
        ? min 
        : new Version("1.0");
    var maxVersion = Version.TryParse(metadata.MaxApiVersion?.Replace("x", "0"), out var max)
        ? max
        : new Version("2.0");
    
    return hostVersion >= minVersion && hostVersion <= maxVersion;
}
```

---

## 示例：CustomizedReply.mod 的完整结构

```
CustomizedReply.mod (ZIP 格式)
├── mod.json
│   └── 内容：
│       {
│         "id": "com.example.customreply",
│         "name": "Custom Reply System",
│         "version": "1.0.0",
│         "author": "Example Author",
│         "dllFileName": "CustomizedReply.dll",
│         "priority": 100,
│         "modType": "dll",
│         "supportHotReload": false,
│         "apiVersion": "1.0"
│       }
│
├── CustomizedReply.dll
│   └── 编译后的 DLL 文件（约 50-100KB）
│
├── data.json
│   └── 规则库数据（初始示例规则）
│
├── README.md
│   └── 用户文档
│
└── LICENSE
    └── 许可证信息
```

---

## 常见问题

**Q: .mod 文件可以用什么软件打开？**  
A: 任何 ZIP 解压软件（7-Zip、WinRAR、Windows 内置解压等）

**Q: 如何更新已安装的 Mod？**  
A: 用户应删除旧 Mod 文件夹，然后添加新的 .mod 文件重新安装

**Q: 能否混合包含多个 DLL 文件？**  
A: 不建议。一个 .mod 应只对应一个 IModPlugin 实现

**Q: 如何在 .mod 中包含依赖的 DLL？**  
A: 不建议直接打包依赖。建议依赖也作为独立 Mod 或在 mod.json 中声明依赖关系

**Q: 如何压缩 data.json 减小文件大小？**  
A: 使用 `JsonSerializerOptions { WriteIndented = false }` 生成紧凑 JSON

---

## 总结

.mod 文件本质上是一个结构化的 ZIP 包：

✅ 始终包含 `mod.json` 和对应的 DLL  
✅ 可选包含 `data.json`、`README.md` 等  
✅ 支持任意文件夹结构和资源文件  
✅ 部署时自动解压到 `data/mods/ModName/`  
✅ 版本和兼容性通过 mod.json 管理
