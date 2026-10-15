# WallBili

面向 Windows 10/11 的 B 站视频壁纸播放器。技术栈为 C# / .NET 10、WPF、SQLite、mpv。

当前分支交付 MVP 阶段二：在阶段一的内容与播放链路上，增加合集/系列导入刷新、分 P 目录、持久化可编辑队列、EOF 连续播放、失败跳过与进度恢复，以及 WPF 操作窗口。[阶段二验证记录](docs/phase2-validation.md)区分自动检查与 Windows 人工验收；阶段一记录保留在 [此处](docs/phase1-validation.md)。真正的桌面视频壁纸属于阶段三。

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
dotnet run --project tools/WallBili.Probe -- check-queue BV1xx411c7mD BV17x411w7KC --headless
```

`media` 从相同 BVID/CID 重新解析两次，并只输出格式、清晰度、主机和解析时间。默认优先单文件 MP4，`--format dash` 使用独立音视频轨。`check-playback` 实际解码视频与音频，检查时钟推进、暂停、继续和精确跳转，再按内容身份重新取流。`--page N` 选择分 P；链接中的 `?p=N` 同样有效。省略 `--headless` 会使用正常音画输出，但实际显示与出声仍需人工观察。探针输出不保存临时媒体 URL、游客 Cookie 或会话请求头值。

元数据使用 `/x/web-interface/wbi/view`，不依赖旧无签名 `/x/web-interface/view` 或网页嵌入 JSON。游客身份通过 `/x/frontend/finger/spi` 初始化，导航接口提供公开 WBI 签名材料；凭据和签名材料仅在进程内缓存。

公开视频和匿名访问是本阶段的支持范围。登录、会员权限、短链接、已失效或受限内容有明确失败状态；未实现账户登录。遇到新 CDN 的网络策略阻断，应按实际主机更新环境策略，保留代理与 TLS 验证。

## Windows 合集与队列

在 Windows 安装 .NET 10 Desktop Runtime/SDK 和 mpv，将 `MPV_PATH` 设置为可信的 `mpv.exe` 路径，然后执行：

```powershell
dotnet run --project src/WallBili.Desktop -c Release
```

也可使用 `pwsh scripts/publish-debug.ps1` 生成 Windows x64 调试包，输出在 `work/windows-debug/`；它需要 .NET 10 Desktop Runtime 和单独安装的 mpv。

输入视频链接或 BVID 会将全部分 P 导入目录。合集支持 `https://space.bilibili.com/UP主ID/lists/合集ID?type=season`（系列为 `type=series`）及旧版 `channel/collectiondetail?sid=…` / `channel/seriesdetail?sid=…` 链接，也可直接填写 UP 主 ID、合集 ID 和类型。选择来源可以筛选目录；Ctrl/Shift 多选条目加入队列，或点击“全部加入”。目录和队列均可上移、下移、删除；双击队列项开始播放。

支持顺序播放、列表循环、上一项/下一项、暂停/继续、停止、seek、音量和重新取流。mpv 继续使用独立窗口。来源刷新会读取所有分页并展开权威 CID，按 BVID/CID 去重，不重建现有队列或重置进度。元数据无法展开的视频记入来源问题列表，不会捏造 CID；播放失败会显示状态并跳过。手动选择失败项或刷新来源可重试。

目录、来源关系、队列顺序、当前项、播放模式、音量和进度保存到 `%LOCALAPPDATA%\WallBili\library.db`；mpv 路径保存到同目录 `preferences.json`。每五秒和关键操作时保存，正常退出前再保存一次。播放中退出/中断后，下次按内容身份重新取流并恢复；暂停退出后恢复为暂停；点击“停止”后下次不自动播放。异常中断最多可能丢失最后一次检查点之后约五秒的进度，另加正在进行的 IPC/磁盘操作时间。

删除正在播放的队列项会从其删除位置播放后继项；原来暂停则后继项保持暂停。删除最后一项停止，循环模式下也不因删除自动绕回。移除来源保留已导入目录与队列；删除目录条目同时移除其来源关系和全部对应队列项。界面同一会话只允许一个实例写库。关闭应用会清理自身 mpv 进程。

`check-queue` 是可选联网验收：导入两个视频来源，各选第一 P，跳到末尾两秒，等待真实 EOF 推进，并重新读取 SQLite 检查完成记录；它使用临时数据库且会清理。自动测试使用本地媒体，运行方式见 `scripts/verify.sh` / `scripts/verify.ps1`。具体 Windows 人工步骤见 [阶段二检查表](docs/windows-phase2-checklist.md)。

## 早期桌面共存原型

独立的 `tools/WallBili.DesktopProbe` 只接收本地视频，在既有 Explorer 壁纸 WorkerW 下创建自己的子窗口，并以该 HWND 承载 mpv。运行 `dotnet run --project tools/WallBili.DesktopProbe -c Release -- C:\path\sample.mp4 30`，默认 30 秒，Ctrl+C 可提前清理。它不创建/重排 Shell 窗口，不修改 Wallpaper Engine 设置；找不到候选父窗口时明确失败。它只是阶段三显示后端的保守实验，具体适用条件与人工观察见 [检查表](docs/windows-phase2-checklist.md)。本环境仅编译，未验证 Windows 共存。

## 项目结构

| 目录 | 职责 |
|---|---|
| `src/WallBili.Core` | 稳定身份、目录导入、队列编辑/调度、恢复策略与存储/播放接口 |
| `src/WallBili.Infrastructure` | 游客身份、WBI 内容接入、合集链接解析、SQLite 事务存储 |
| `src/WallBili.Player.Mpv` | 一个音视频播放进程、JSON IPC、队列代际与原生事件关联 |
| `src/WallBili.Desktop` | WPF 来源、目录、队列与播放控制窗口 |
| `tools/WallBili.Probe` | 可重复执行、输出脱敏 JSON 的接入和媒体验证 |
| `tests/` | 接入行为与 mpv 实际媒体控制测试 |

应用代码按 MIT 发布。没有复制 Biu 的非商业许可实现；采用自行编写的内容适配器。接口协议来源和第三方组件边界见 [来源说明](docs/protocol-and-dependencies.md)。第三方 SQLite 驱动随 NuGet 恢复并锁定；mpv 仍由用户独立安装。后续阶段包括完整桌面壁纸宿主、托盘/自启动、显示与系统生命周期恢复及 Wallpaper Engine 共存验收。
