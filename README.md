# WallBili

面向 Windows 10/11 的 B 站视频壁纸播放器。技术栈为 C# / .NET 10、WPF、SQLite、mpv。

当前交付 MVP 阶段三：在已验证的内容、目录与队列之上接入桌面视频宿主、单屏配置、填充/适应、托盘、登录启动、断网/睡眠/Explorer 恢复。用户已确认前两阶段通过；[阶段三验证记录](docs/phase3-validation.md)区分自动检查与尚待 Windows 实机验收的门槛，阶段一、二的历史记录保留。

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

使用 `pwsh scripts/publish-windows.ps1`（Linux 开发环境可用 `bash scripts/publish-windows.sh`）生成 Windows x64 包 `work/windows-phase3.zip`，解压后启动 `app/WallBili.Desktop.exe`。默认需要 .NET 10 Desktop Runtime 和单独安装的 mpv；PowerShell 的 `-SelfContained` 可打包 .NET 运行时。旧 `publish-debug.ps1` 入口保留。

输入视频链接或 BVID 会将全部分 P 导入目录。合集支持 `https://space.bilibili.com/UP主ID/lists/合集ID?type=season`（系列为 `type=series`）及旧版 `channel/collectiondetail?sid=…` / `channel/seriesdetail?sid=…` 链接，也可直接填写 UP 主 ID、合集 ID 和类型。选择来源可以筛选目录；Ctrl/Shift 多选条目加入队列，或点击“全部加入”。目录和队列均可上移、下移、删除；双击队列项开始播放。

支持顺序播放、列表循环、上一项/下一项、暂停/继续、停止、seek、音量和重新取流。mpv 通过 WallBili 自己的 HWND 输出到桌面，只有一个音视频会话。来源刷新读取所有分页并展开权威 CID，按 BVID/CID 去重，不重建队列或重置进度。元数据无法展开的视频记入来源问题；永久播放失败跳过，临时网络失败保留当前项等待恢复。

目录、来源关系、队列顺序、当前项、播放模式、音量和进度保存到 `%LOCALAPPDATA%\WallBili\library.db`；mpv 路径保存到同目录 `preferences.json`。每五秒和关键操作时保存，正常退出前再保存一次。播放中退出/中断后，下次按内容身份重新取流并恢复；暂停退出后恢复为暂停；点击“停止”后下次不自动播放。异常中断最多可能丢失最后一次检查点之后约五秒的进度，另加正在进行的 IPC/磁盘操作时间。

删除正在播放的队列项会从其删除位置播放后继项；原来暂停则后继项保持暂停。删除最后一项停止，循环模式下也不因删除自动绕回。移除来源保留已导入目录与队列；删除目录条目同时移除其来源关系和全部对应队列项。界面同一会话只允许一个实例写库；再次启动会唤回原窗口。关闭窗口收起到托盘，“退出”清理自身 mpv。

## 桌面与后台使用

选择“桌面视频”、目标显示器和填充/适应后点击“应用桌面设置”。首版承载一块屏幕，所选屏幕离线时暂用主屏；返回后恢复原设备。隐藏画面可继续听音乐，取消相应选项则自动暂停，队列与进度保留。暂停后重新启用画面需手动继续。

托盘提供播放/暂停、下一项、画面显隐、唤回与退出。登录自启动默认关闭，勾选并应用后写入当前用户 Run 项，以托盘模式恢复队列。临时断网/限流采取 2–60 秒退避，同一内容重新取流后恢复进度；mpv 断连、约 45 秒无推进的缓冲、睡眠唤醒与被销毁的桌面输出也从身份和检查点恢复。停止会取消后台重试。

宿主仅使用已存在的 Explorer 壁纸 WorkerW，不创建/重排 Shell 窗口，不修改 Wallpaper Engine。没有候选宿主时明确等待，不能算作壁纸成功；不同 Windows/Wallpaper Engine 布局的实际层叠与图标交互必须按 [阶段三检查表](docs/windows-phase3-checklist.md)观察。两小时稳定性、音画误差与 GPU/资源结论也以实机记录为准。

`check-queue` 是可选联网验收：导入两个视频来源，各选第一 P，跳到末尾两秒，等待真实 EOF 推进，并重新读取 SQLite 检查完成记录；它使用临时数据库且会清理。自动测试使用本地媒体，运行方式见 `scripts/verify.sh` / `scripts/verify.ps1`。具体 Windows 人工步骤见 [阶段二检查表](docs/windows-phase2-checklist.md)。

## 早期桌面共存原型

独立的 `tools/WallBili.DesktopProbe` 只接收本地视频，在既有 Explorer 壁纸 WorkerW 下创建自己的子窗口，并以该 HWND 承载 mpv。运行 `dotnet run --project tools/WallBili.DesktopProbe -c Release -- C:\path\sample.mp4 30`，默认 30 秒，Ctrl+C 可提前清理。它不创建/重排 Shell 窗口，不修改 Wallpaper Engine 设置；找不到候选父窗口时明确失败。它只是阶段三显示后端的保守实验，具体适用条件与人工观察见 [检查表](docs/windows-phase2-checklist.md)。本环境仅编译，未验证 Windows 共存。

## 项目结构

| 目录 | 职责 |
|---|---|
| `src/WallBili.Core` | 稳定身份、目录导入、队列编辑/调度、恢复策略与存储/播放接口 |
| `src/WallBili.Infrastructure` | 游客身份、WBI 内容接入、合集链接解析、SQLite 事务存储 |
| `src/WallBili.Player.Mpv` | 一个音视频播放进程、JSON IPC、队列代际与原生事件关联 |
| `src/WallBili.Desktop` | WPF 操作窗口、桌面宿主、单屏配置、托盘与系统生命周期 |
| `tools/WallBili.Probe` | 可重复执行、输出脱敏 JSON 的接入和媒体验证 |
| `tests/` | 接入行为与 mpv 实际媒体控制测试 |

应用代码按 MIT 发布。没有复制 Biu 的非商业许可实现；采用自行编写的内容适配器。接口协议来源和第三方组件边界见 [来源说明](docs/protocol-and-dependencies.md)。第三方 SQLite 驱动随 NuGet 恢复并锁定；mpv 仍由用户独立安装。复杂多屏拼接、更多来源、自动更新等不属于首版范围。
