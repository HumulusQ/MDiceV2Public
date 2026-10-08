# CustomizedReply Mod 面板集成指南

本文档说明如何将 CustomizedReply Mod 的管理面板集成到 MDiceV2 的主导航界面。

## 集成步骤

### 步骤 1: 修改 MainViewModel.cs

在 `MDiceV2.Core/UI/ViewModels/MainViewModel.cs` 中，找到 `InitializeViews()` 方法：

**原代码**：
```csharp
private void InitializeViews()
{
    _views[0] = CreateMainPanel();
    _views[1] = CreateLogContent();
    _views[2] = CreateChatContent();
    _views[3] = CreateSettingContent();
    _views[4] = CreateModsContent();
}
```

**修改为**：
```csharp
private void InitializeViews()
{
    _views[0] = CreateMainPanel();
    _views[1] = CreateLogContent();
    _views[2] = CreateChatContent();
    _views[3] = CreateSettingContent();
    _views[4] = CreateModsContent();
    _views[5] = CreateCustomizedReplyContent();  // ← 添加这一行
}
```

然后在 `CreateModsContent()` 方法后面添加新方法：

```csharp
/// <summary>
/// 自定义回复 Mod 管理面板
/// </summary>
private Control CreateCustomizedReplyContent()
{
    var panel = new CustomizedReplyPanel
    {
        // DataContext 在 Panel 的 Code-Behind 中设置
    };
    return panel;
}
```

### 步骤 2: 修改 MainView.axaml

在 `MDiceV2.Core/UI/Views/MainView.axaml` 中找到导航列表，在最后一个 ListBoxItem 后面添加新项：

**原代码（最后一项）**：
```xml
<!-- 模组页面菜单项 -->
<ListBoxItem Height="48" Margin="4,2" CornerRadius="6" Background="{DynamicResource SemiColorBackground0}" BorderBrush="{DynamicResource SemiColorBorder0}" BorderThickness="1">
    <Grid>
        <StackPanel Orientation="Horizontal" IsVisible="{Binding IsPaneOpen}" VerticalAlignment="Center">
           <Image Source="avares://MDiceV2.Core/Assets/Sprite/Mod.png" Width="18" Height="18" Margin="10,0,10,0"/>
           <TextBlock Text="Mods" VerticalAlignment="Center" FontWeight="Medium"/>
       </StackPanel>
        <Image Source="avares://MDiceV2.Core/Assets/Sprite/Mod.png" Width="18" Height="18"
            HorizontalAlignment="Center" VerticalAlignment="Center"
            IsVisible="{Binding !IsPaneOpen}"/>
    </Grid>
</ListBoxItem>
```

**添加新项**（在 `</ListBox>` 前）：
```xml
<!-- 自定义回复 Mod 菜单项 -->
<ListBoxItem Height="48" Margin="4,2" CornerRadius="6" Background="{DynamicResource SemiColorBackground0}" BorderBrush="{DynamicResource SemiColorBorder0}" BorderThickness="1">
    <Grid>
        <StackPanel Orientation="Horizontal" IsVisible="{Binding IsPaneOpen}" VerticalAlignment="Center">
           <Image Source="avares://MDiceV2.Core/Assets/Sprite/Mod.png" Width="18" Height="18" Margin="10,0,10,0"/>
           <TextBlock Text="Custom Reply" VerticalAlignment="Center" FontWeight="Medium"/>
       </StackPanel>
        <Image Source="avares://MDiceV2.Core/Assets/Sprite/Mod.png" Width="18" Height="18"
            HorizontalAlignment="Center" VerticalAlignment="Center"
            IsVisible="{Binding !IsPaneOpen}"/>
    </Grid>
</ListBoxItem>
```

### 步骤 3: 在 MainView.axaml 中注册命名空间

在文件顶部的 `<UserControl>` 标签中添加命名空间声明：

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             ...
             xmlns:views="using:MDiceV2.Core.UI.Views"
             ...>
```

（如果已经有 `xmlns:views` 声明，跳过此步骤）

### 步骤 4: 编译并测试

1. 编译项目：
   ```bash
   dotnet build MDiceV2.sln
   ```

2. 运行程序并测试：
   - 点击导航栏中的"Custom Reply"
   - 应该显示自定义回复管理面板
   - 验证列表、添加、编辑、删除功能是否正常

---

## 文件清单

集成后的文件结构：

```
MDiceV2.Core/
├── UI/
│   ├── Views/
│   │   ├── MainView.axaml              ✏️ 修改：添加导航项
│   │   ├── CustomizedReplyPanel.axaml  ✨ 新增
│   │   └── CustomizedReplyPanel.axaml.cs ✨ 新增
│   └── ViewModels/
│       ├── MainViewModel.cs            ✏️ 修改：添加初始化方法
│       └── CustomizedReplyViewModel.cs ✨ 新增
└── Mod/
    ├── ModPluginLoader.cs              ✨ 新增
    ├── ModEventBridge.cs               ✨ 新增
    └── ModContextImpl.cs                ✨ 新增
```

---

## 关键概念

### 导航索引映射

| 索引 | 页面 | 备注 |
|------|------|------|
| 0 | Main | 主面板 |
| 1 | Log | 日志面板 |
| 2 | Chat | 聊天面板 |
| 3 | Setting | 设置面板 |
| 4 | Mods | Mod 管理面板 |
| **5** | **Custom Reply** | **自定义回复 Mod** |

### 自动更新导航项数量

注意：MainView.axaml 中的导航项数量必须与 `InitializeViews()` 中的索引数量一致。

如果缺少任何一项或索引对不上，会导致：
- 点击导航项时出现空白页面
- 或点击无效

---

## 常见问题

**Q: 添加新的 ListBoxItem 后仍然看不到？**  
A: 确保：
1. ✅ 修改了 `InitializeViews()` 并添加了 `_views[5]` 行
2. ✅ 新的 ListBoxItem 在 `<ListBox>` 的正确位置
3. ✅ 重新编译了项目
4. ✅ 清理了 bin/obj 目录重新编译

**Q: 点击导航项出现错误？**  
A: 检查：
1. ✅ `CreateCustomizedReplyContent()` 方法是否存在
2. ✅ `CustomizedReplyPanel.axaml` 是否正确编译
3. ✅ ViewModel 的 DataContext 是否正确设置

**Q: 导航项图标不显示？**  
A: 
1. ✅ 确保图片文件存在：`MDiceV2.Core/Assets/Sprite/Mod.png`
2. ✅ 或更改 Source 为其他存在的图片资源
3. ✅ 或使用 Unicode 符号代替图片

**Q: 面板内容显示但按钮不工作？**  
A: 
1. ✅ 检查 CustomizedReplyViewModel 的命令是否正确实现
2. ✅ 确保 XAML 中的 Binding 路径正确
3. ✅ 查看日志是否有 binding 错误

---

## 下一步

完成集成后，您可以：

1. **测试核心功能**
   - 加载规则并显示在列表中
   - 添加新规则
   - 编辑和删除规则

2. **集成 ModPluginLoader**
   - 在应用启动时加载所有 Mod
   - 初始化 CustomizedReply Mod

3. **集成 ModEventBridge**
   - 在消息处理时调用 Mod 的消息处理方法
   - 处理 Mod 的返回结果

4. **UI 优化**
   - 美化面板样式
   - 添加搜索/过滤功能
   - 添加规则导入导出

---

## 参考资源

- [MainView.axaml](MainView.axaml) - 导航视图
- [CustomizedReplyPanel.axaml](../Views/CustomizedReplyPanel.axaml) - 管理面板
- [CustomizedReplyViewModel.cs](../Models/CustomizedReplyViewModel.cs) - 视图模型
- [MOD_PACKAGING_FORMAT.md](../../Mods/MOD_PACKAGING_FORMAT.md) - Mod 打包格式
