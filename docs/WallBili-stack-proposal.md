**WallBili MVP 技术栈：用户已确认**

提案日期：2026-10-14。用户确认日期：2026-10-15。已按此栈执行云端工具初始化；具体当前实例结果见[初始化记录](./environment-handoff.md)。应用脚手架仍留待代码开发阶段创建。

**推荐：C# / .NET 10 LTS + WPF + SQLite + mpv，桌面视频输出使用薄 Win32 宿主模块。**

推荐依据是本项目首版明确支持 Windows 10/11，真正的视频壁纸、桌面窗口和 Wallpaper Engine 共存是主要集成难点。C# 可直接操作 Windows 窗口与系统 API；WPF 适合实现合集、队列、设置与后台入口；内容接入和播放逻辑可拆成平台无关 .NET 模块，在 Linux 云环境验证。mpv 负责媒体解码和音画同步，应用负责列表、生命周期和桌面输出。

| 层次 | 推荐技术与当前版本 | 使用范围 |
|---|---|---|
| 语言与运行时 | C# 14 / .NET 10 LTS；SDK `10.0.402` | 平台无关核心目标为 `net10.0`；Windows UI 目标为 `net10.0-windows` |
| 桌面 UI | WPF + `CommunityToolkit.Mvvm 8.4.2` | 合集列表、队列、设置、命令与状态；托盘接入 Windows 桌面 API |
| 内容请求 | .NET `HttpClient` + `System.Text.Json` | B 站元数据、合集、播放资源与失效分类；接入实现保留替换边界 |
| 本地存储 | SQLite + `Microsoft.Data.Sqlite 10.0.13` | 视频身份、来源、队列、播放进度与设置；采用简单 schema 与迁移 |
| 视频引擎 | mpv `0.41.0`，独立进程 | 音视频播放、暂停、seek、结束事件；JSON IPC 控制，Windows 使用命名管道 |
| 壁纸承载 | C# 调用 Win32；自行创建宿主窗口供 mpv 视频输出附着 | 真正桌面模式、显示器选择、图标交互和清理；具体绑定策略须共存原型验收 |
| 媒体验证 | FFmpeg / ffprobe，确认环境后锁定可信发行包 | 检查实际媒体格式与解码结果，必要时用于测试样例；播放时是否需要额外媒体处理由实测决定 |
| 核心测试 | xUnit，包 `xunit.v3 4.0.2` | 内容解析、来源分页、队列推进、SQLite 持久化与恢复、IPC 消息处理；测试运行器配套版本在初始化时锁定并验证 |
| 首版分发 | Windows x64 自包含应用 + 独立 mpv 组件 | 第一份可运行包；安装器与自动更新在 MVP 闭环之后完善 |

上述 SDK 和 NuGet 版本来自官方发布元数据，已安装/恢复并通过云端环境辅助验证；mpv 0.41.0 也已完成无界面解码与 IPC 检查。这不等于完整应用集成或 Windows 桌面运行已通过。mpv Windows 二进制发行来源、组件许可与完整性验证在准备 Windows 包时核对，应用自身 MIT 不覆盖第三方组件的许可。

**Biu 的位置。** Biu 是接口行为和列表行为的候选参考。先验证它当前的 B 站接入是否有效，再决定采用相应实现。整套 WallBili 的语言、UI 和播放引擎不会依赖它必须可用；若接口失效，替换内容适配器仍沿用上述栈。复制其代码或派生改造需遵循当前非商业许可，不能因重新使用 C# 表达实现就自动排除许可问题。

**播放与壁纸输出。** mpv 官方文档提供 `--wid` 窗口嵌入能力；Windows 参数指向 HWND。应用通过 JSON IPC 的命名管道加载内容、控制播放并订阅结束/进度事件；Windows 文档明确列出 .NET `NamedPipeClientStream` 作为双向通信包装方式。当前视频与音频由一个播放会话控制，队列收到播放结束事件再推进。

Win32 宿主只管理 WallBili 创建的窗口。常见 WorkerW/Progman 桌面结构与 mpv 的直接桌面模式均是需要验证的候选实现，不能据此承诺 Wallpaper Engine 共存。默认目标是自己的宿主 HWND；具体桌面父窗口、层叠与恢复策略由本地视频原型确定。共存失败时调整该模块，不以终止 Wallpaper Engine 为解决方案。

**建议的项目边界。** 确认技术栈后，环境准备与代码开发分别执行。后续开发可按下列组织方式建立项目：

```text
src/
  WallBili.Core/             内容模型、来源接口、队列与播放状态
  WallBili.Infrastructure/   HttpClient、SQLite、配置与内容接入实现
  WallBili.Player.Mpv/       播放进程、IPC 与事件适配
  WallBili.Desktop/          WPF、托盘、自启动与 Win32 桌面宿主
tools/
  WallBili.Probe/            接口与媒体链路的调试入口
tests/
  WallBili.Core.Tests/       队列与恢复的行为测试
  WallBili.Integration.Tests/ 存储、IPC 与选择性真实接入验证
```

Core/Infrastructure 与协议处理部分按平台无关方式编写；Windows 窗口操作限制在 Desktop 模块。配置目录、SQLite 文件与媒体缓存位于应用数据目录，播放列表不持久化临时 CDN 地址。

**三阶段与技术栈的对应关系。**

| 阶段 | 使用方式 | 必须取得的结果 |
|---|---|---|
| 一：取得 B 站内容 | C# 探针 + HttpClient；mpv/ffprobe 验媒体，Windows 调试播放器验真实音画 | 有效元数据、分 P、合集探针、实际音视频播放与重新解析资源 |
| 二：维护合集与队列 | .NET 核心 + SQLite + 最小 WPF 操作界面 | 合集导入/刷新、队列编辑、连续播放与进度恢复 |
| 三：视频进入壁纸层 | WPF/Win32 宿主 + mpv 的窗口输出 + IPC | 真桌面视频、图标交互、音画同步、后台恢复与 Wallpaper Engine 共存 |

在完整 UI 投入前，用本地测试视频验证桌面共存；该原型可与列表开发相互独立。mpv 的窗口嵌入能力已由文档核实，具体 Windows 显示与共存行为尚未实测。

**已执行的云端初始化范围。**

1. 保留现有 checkout 与许可证；检查工具现状，安装并验证 `.NET SDK 10.0.402`，为工具激活保存可复用路径与指令。
2. 准备 NuGet 缓存、Linux mpv/FFmpeg 调试工具和必要的本地验证辅助文件。没有应用项目时先验证工具与必要依赖，不生成假的应用就绪结论。
3. 按阶段一需要保存 B 站网络域名要求：先允许 `api.bilibili.com` 和 `www.bilibili.com`；取得媒体地址后确认并添加实际 CDN 主机。保留环境已有预设与自定义网络规则。
4. 在网络更新可用后重试公开内容链路，并分别记录元数据、媒体资源、实际解码与合集能力；匿名验证不先索取账户 Cookie。
5. 执行并验证需要的安装步骤后，将完整安装指令保存到环境配置草稿。只有出现实际需要重启的服务或启动流程时，再保存对应启动说明；草稿保存与环境发布分别报告。

当前云工作区是 Linux。可完成平台无关核心、存储、内容探针、媒体解码/IPC 的验证；WPF 运行、Windows 桌面壁纸、音频设备、图标与 Wallpaper Engine 共存需要 Windows 本机。WPF 可评估在 Linux 上启用 Windows targeting 编译，编译成功也不代替 Windows 运行验收。首版 Windows 包随后在 Windows 本机或 Windows runner 构建验证。

环境初始化先验证已准备的工具与内容链路。后续建立真实应用项目时，锁定依赖文件并验证项目的实际构建、测试与启动命令；分别记录环境就绪与应用实现的结果。

**官方依据。**

- [.NET 10 发布与 LTS 信息](https://github.com/dotnet/core/blob/main/release-notes/10.0/README.md)，以及[官方版本元数据](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)。本次元数据显示 SDK `10.0.402`、运行时 `10.0.13`，LTS 至 2028-11-14。
- [.NET Community Toolkit](https://github.com/CommunityToolkit/dotnet)，[MVVM 包版本](https://api.nuget.org/v3-flatcontainer/communitytoolkit.mvvm/index.json)，[SQLite 包版本](https://api.nuget.org/v3-flatcontainer/microsoft.data.sqlite/index.json)，[xUnit 包版本](https://api.nuget.org/v3-flatcontainer/xunit.v3/index.json)。
- [mpv 0.41.0 发布](https://github.com/mpv-player/mpv/releases/tag/v0.41.0)、[窗口与 IPC 参数文档](https://github.com/mpv-player/mpv/blob/master/DOCS/man/options.rst)、[JSON IPC 文档](https://github.com/mpv-player/mpv/blob/master/DOCS/man/ipc.rst)。

用户已确认本技术栈。云端已执行工具初始化、环境检查与短时远程音视频解码；元数据接口和 Windows 桌面行为的未覆盖项见初始化记录。环境草稿保存与发布状态分别记录，不将当前实例结果扩大为新任务恢复或完整应用验收。
