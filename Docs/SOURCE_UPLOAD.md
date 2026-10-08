# 公开源码上传清单

`update.bat` 同步 MDiceV2Public 的源码，不是发行包。发行包仍由 `publish.bat` 处理。

## 使用

```powershell
.\update.bat --preview  # 默认行为：仅检查和生成清单，不联网
.\update.bat --upload   # 展示与远端的差异，输入 UPLOAD 后才提交和推送
```

完整清单在 `outputs/update-upload-files.csv` 和 `outputs/update-upload-files.txt`。

## 保留

- 主程序、ABot、Mods 与测试源码，项目/解决方案文件、中央版本配置。
- 构建、测试、发布脚本，包括 `Directory.Build.targets` 调用的版本脚本。
- Assets 图片/字体、Resources 规则、Mod 源脚本和配置模板。
- README、Docs、Mod 文档、用户手册和可移植角色卡 HTML。
- `BotFrameworks/README.md` 与 `sources.json`，不包含第三方运行时。

## 排除

- bin/obj、编译产物、发行目录、ZIP/RAR 发行包、日志。
- 数据库、TRPG 聊天记录、机器人账户数据、登录状态和下载缓存。
- token、私钥、.env、可写 AI/API 配置、IDE/代理本地状态。
- 临时/归档目录、生成语音预览、一次性补丁和审计输出。
- `DownloadTest/` 下含硬编码认证令牌的独立下载诊断；正式单元测试仍保留。

排除只影响源码上传，不删除本地文件。规则由根目录 `.gitignore` 统一管理。
AI 配置使用 `Mods/AIMod/ai-config.example.json` 作为模板；本地复制为 `ai-config.json` 后再填写密钥。

## 安全边界

- 在 work 中使用独立 Git 元数据，不修改本地 `.git` 或暂存区，不公开本地私有提交。
- 以远端 master 为父提交，仅普通推送；不删除历史，不强制推送。
- 远端文件如果本地缺失且未被 `.gitignore` 排除，上传会被阻止，并输出 `outputs/update-remote-source-removal-review.txt`。需单独复核后恢复文件，或显式排除已确认废弃的文件，不会自动删除远端源码。
- 远端已跟踪的排除文件从新快照中移除，不从旧历史中清除。如果密钥已泄露，需先撤销/轮换，再单独处理历史。
- 连接路径、超过 50 MiB 的单文件、疑似 GitHub token/私钥会阻止上传。这不是全面密钥扫描，仍需人工审阅。
