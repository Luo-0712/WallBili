# WallBili

面向 Windows 10/11 的 B 站视频壁纸播放器。技术栈为 C# / .NET 10、WPF、mpv；SQLite 随阶段二的目录与队列引入。

当前分支交付 MVP 阶段一：游客凭据初始化、WBI 签名 API、公开视频/分 P 元数据、MP4/DASH 音视频资源解析、合集/系列分页探针、mpv JSON IPC 控制和 WPF 调试入口。阶段一验收状态见 [验证记录](docs/phase1-validation.md)。

## 开发与验证

需要 .NET SDK `10.0.402`（允许同功能带补丁）和 mpv `0.41.0`。mpv 独立安装，通过 `PATH` 或 `MPV_PATH` 指定；当前仓库不分发第三方播放器二进制。运行播放器集成测试还需要 FFmpeg。

已有云环境先执行：

```bash
source /workspace/wallbili-environment/activate.sh
bash scripts/verify.sh
```

普通开发环境先安装以上工具，再运行相同验证脚本。脚本构建全部项目（含 WPF 跨平台编译）并执行离线协议测试和本地媒体实际播放控制测试，不访问 B 站。WPF 编译通过不代表已在 Windows 运行。

## 内容与播放探针

```bash
dotnet run --project tools/WallBili.Probe -- video BV1xx411c7mD
dotnet run --project tools/WallBili.Probe -- video 'https://www.bilibili.com/video/BV1q54y1q79w/?p=2'
dotnet run --project tools/WallBili.Probe -- media BV1xx411c7mD
dotnet run --project tools/WallBili.Probe -- media BV1xx411c7mD --format dash
dotnet run --project tools/WallBili.Probe -- collection season 476537405 4947065 --page 1 --size 1
dotnet run --project tools/WallBili.Probe -- collection season 476537405 4947065 --page 2 --size 1
dotnet run --project tools/WallBili.Probe -- check-playback BV1xx411c7mD --headless
```

`media` 从相同 BVID/CID 重新解析两次，并只输出格式、清晰度、主机和解析时间。默认优先单文件 MP4，`--format dash` 使用独立音视频轨。`check-playback` 实际解码视频与音频，检查时钟推进、暂停、继续和精确跳转，再按内容身份重新取流。`--page N` 选择分 P；链接中的 `?p=N` 同样有效。省略 `--headless` 会使用正常音画输出，但实际显示与出声仍需人工观察。探针输出不保存临时媒体 URL、游客 Cookie 或会话请求头值。

元数据使用 `/x/web-interface/wbi/view`，不依赖旧无签名 `/x/web-interface/view` 或网页嵌入 JSON。游客身份通过 `/x/frontend/finger/spi` 初始化，导航接口提供公开 WBI 签名材料；凭据和签名材料仅在进程内缓存。

公开视频和匿名访问是本阶段的支持范围。登录、会员权限、短链接、已失效或受限内容有明确失败状态；未实现账户登录。遇到新 CDN 的网络策略阻断，应按实际主机更新环境策略，保留代理与 TLS 验证。

## Windows 调试播放器

在 Windows 安装 .NET 10 Desktop Runtime/SDK 和 mpv，将 `MPV_PATH` 设置为可信的 `mpv.exe` 路径，然后执行：

```powershell
dotnet run --project src/WallBili.Desktop -c Release
```

也可使用 `pwsh scripts/publish-debug.ps1` 生成 Windows x64 调试包，输出在 `work/windows-debug/`；它需要 .NET 10 Desktop Runtime 和单独安装的 mpv。

输入视频链接或 BVID、读取元数据、选择分 P 后播放。mpv 使用独立调试窗口；控制窗口提供暂停、继续、seek 和重新取流。关闭调试应用会清理自身 mpv 进程。具体人工验收步骤见 [Windows 检查表](docs/windows-phase1-checklist.md)。

## 项目结构

| 目录 | 职责 |
|---|---|
| `src/WallBili.Core` | 稳定 BVID/CID 身份、内容和媒体模型、失败分类、接入接口 |
| `src/WallBili.Infrastructure` | 游客身份、WBI 元数据/播放资源、分 P、合集/系列分页 |
| `src/WallBili.Player.Mpv` | 一个音视频播放进程、JSON IPC、事件与生命周期 |
| `src/WallBili.Desktop` | WPF 阶段一调试控制窗口 |
| `tools/WallBili.Probe` | 可重复执行、输出脱敏 JSON 的接入和媒体验证 |
| `tests/` | 接入行为与 mpv 实际媒体控制测试 |

应用代码按 MIT 发布。没有复制 Biu 的非商业许可实现；本阶段选择自行编写边界清晰的内容适配器。接口协议来源和第三方组件边界见 [来源说明](docs/protocol-and-dependencies.md)。后续阶段包括 SQLite 队列、连续播放、真正的桌面壁纸宿主及 Wallpaper Engine 共存验收。
