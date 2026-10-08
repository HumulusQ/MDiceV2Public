# 发布和历史 Release 清理

源码推送使用 `update.bat`，参见 [源码上传说明](SOURCE_UPLOAD.md)。发行包上传使用根目录的 `publish.bat`（调用 `tools/publish.ps1`）。

```powershell
.\publish.bat /nopause                 # 构建、打包、上传，保留最新10个Release
.\publish.bat /nopause /keep:5         # 上传完成后保留最新5个
.\publish.bat /nopause /nocleanup      # 本次上传后不清理历史
.\publish.bat /nopause /previewcleanup /keep:5 # 只预览清理，不构建、上传或删除
.\publish.bat /nopause /cleanup /keep:5        # 只清理历史，不构建或上传
```

直接使用 PowerShell：

```powershell
.\tools\publish.ps1 -ReleaseCleanupOnly -KeepReleases 10 -PreviewReleaseCleanup
.\tools\publish.ps1 -ReleaseCleanupOnly -KeepReleases 10
```

清理会分页读取所有已发布 Release，按发布时间决定保留项，并从最老的版本开始删除超出数量的 Release 及其附件。草稿不参与清理，Git 标签保留。普通发布始终保护本次上传的 Release，且只有全部附件上传成功后才开始清理；单独清理始终保留最新发布的 Release。清理失败时脚本返回失败，可用 `/cleanup` 重试。

需要 `token.txt` 或 `GITHUB_TOKEN`/`GH_TOKEN` 提供具有仓库 Release 写权限的令牌；预览也使用同一身份，以包含它能看到的 Release。`/keep:N` 范围为1～1000。`/cleanup`、`/previewcleanup` 与 `/nocleanup` 不可组合。
