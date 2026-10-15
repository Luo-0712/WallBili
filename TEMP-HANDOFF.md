# WallBili 临时交接说明

更新日期：2026-10-15（北京时间）。这份文件独立汇总当前调研进度、技术选型、实测证据与下一步，便于拉回 Windows 本地继续开发。详细计划见文末链接。

## 目标与已经确定的约束

- 主要平台为 Windows 10/11；初版优先 Windows x64。
- 从用户选定的 B站 UP 主内容、合集或视频中建立播放队列，最终支持登录 Windows 后自动运行和恢复播放。
- 正在播放的视频最终成为真正的桌面壁纸，桌面图标仍可操作；普通全屏播放不算壁纸验收通过。
- WallBili 播放时允许 Wallpaper Engine 继续正常运行：不终止其进程、不替换其壁纸选择、不改写其设置或破坏其窗口结构。共存不要求开发 Wallpaper Engine 插件或让其成为依赖。
- 复用、参考、自研可以按模块灵活组合；最终视觉细节仍可迭代。视频做壁纸与主技术栈已经确定。
- 不需要在实施中反复征求普通技术细节；影响目标的分支决策集中列出后再决定。

## 已确认的技术选型

| 部分 | 选型 | 当前说明 |
|---|---|---|
| 语言与运行时 | C# 14 / .NET 10 LTS | 云端 SDK 10.0.402，运行时 10.0.13；核心模块面向 net10.0 |
| 桌面界面 | WPF + CommunityToolkit.Mvvm 8.4.2 | Windows UI 面向 net10.0-windows；实际界面尚未实现 |
| 内容接入 | HttpClient + System.Text.Json | 独立封装元数据、分 P、来源列表和播放资源解析，保留替换实现的边界 |
| 本地数据 | SQLite + Microsoft.Data.Sqlite 10.0.13 | 保存 BVID/CID、目录、队列和进度；临时 CDN 地址不进持久化播放列表 |
| 播放 | mpv 0.41.0 独立进程 + JSON IPC | Windows 使用命名管道；同一播放会话控制音视频 |
| 视频壁纸 | C# 调用 Win32，自建宿主 HWND 承载 mpv | 具体桌面挂载与层叠方案等待 Windows 共存原型验证 |
| 验证工具 | FFmpeg / ffprobe、xUnit | 云端 FFmpeg 7.1.5；测试包 xunit.v3 4.0.2 |

WPF、Win32 宿主和 Wallpaper Engine 的实际效果需要 Windows 实机。Linux 编译或无界面解码不能证明这些能力已通过。

## 已经完成的调研

1. 已比较 Biu、MusicFreeDesktop、Lively、mpv/yt-dlp 组合、自建播放器及桌面宿主等路线。
2. 已核对 Biu 核心请求实现：它直接请求 B站；`bilibili-API-collect` 是接口资料参考，不是运行时必须访问的服务。资料库下线本身不等于现有请求全部失效。
3. Biu 是有条件参考对象，不能视为全部功能已验证的 SDK。其 PolyForm Noncommercial 许可也不能默认并入本项目 MIT。
4. 可复用播放器引擎、列表行为或接口协议参考；内容适配器独立封装，以便接口变化后替换。
5. 桌面宿主方案需要独立验证与 Wallpaper Engine 共存，不能仅凭对方进程仍存在就认定兼容。

## 实测结果与可信范围

以下为 Windows 本地实机结合历史验证的最新状态，数据链路实测详见 [B站数据链路实测与技术选线评估报告](./docs/bilibili-data-pipeline-evaluation.md)。

| 能力 | 已观察到的结果 | 当前定论 / 限制 |
|---|---|---|
| .NET / NuGet 工具链 | 运行时 6.0 存在，SDK 待安装 | Windows 本地待安装 .NET 10 SDK |
| 本地数据 / SQLite | 原生驱动、持久化、事务回滚通过 | 正式目录与队列业务模块待编写 |
| mpv Windows 本地解码 | mpv 0.41.0 (内置 FFmpeg 7.1+) 实测通过 | 单文件与 DASH 分离流解码退出码均为 0 |
| 游客指纹凭据 | `/x/frontend/finger/spi` 稳定取得 buvid3/buvid4 | 匿名请求成功，防止裸 IP 触发初级拦截 |
| WBI 签名机制 | `/x/web-interface/nav` 取得密钥，mixin_key 计算正确 | 匿名即可取得盐值，签名算法自闭环 |
| 视频元数据与分 P | `/x/web-interface/wbi/view` HTTP 200、code=0 | **已解决**：前期 412 确认为旧接口无签名被拒，带签名接口完全稳定；单 P 与多分 P（10 P 样例）均通过 |
| 视频网页抓取 (SSR) | HTML 不含 `window.__INITIAL_STATE__` | **已定论**：现代前端已转向 CSR，废弃网页 DOM 抓取路线 |
| UP 主投稿与合集列表 | `/x/space/wbi/arc/search` 与 `/x/polymer/...` 成功 | **已解决**：影视飓风 937 条投稿与合集 26 条视频展开均成功 |
| 播放流解析与音视频 CDN | `/x/player/wbi/playurl` 返回有效流，Range 请求 HTTP 206 | 单文件 MP4 (fnval=0) 与 DASH (fnval=16) 均可解析 |
| mpv 音画同步流播放 | mpv 挂载视频与音频流成功拉取解码 | 退出码 0，两路流同步拉取无报错 |
| 桌面壁纸与 Wallpaper Engine 共存 | 尚未实机验证 | 阶段三核心任务，待做独立共存小原型 |

### 网络与接入现状

此前云环境观察到的代理 CONNECT 403 属于云端外网策略受限，**本地 Windows 环境直连 B 站 API 完全通畅**。

通过本次实测，此前遗留的核心疑问已全部查清：
1. **直接接口 HTTP 412**：已证实是 B 站对未签名旧接口实施了 WAF 拦截。全面改用带 WBI 签名的 `/x/web-interface/wbi/view` 之后，返回 HTTP 200，code=0，元数据获取 100% 稳定。
2. **网页数据回退**：已证实网页端不再内嵌预渲染 JSON，坚决放弃该方案，收敛至纯 API 链路。
3. **多分 P 与合集支持**：多分 P（如 `BV17x411w7KC` 10 P）、UP 主合集（如合集 2046621 全部 26 条视频）均经实测验证，数据结构规整完备。
4. **推流与解码**：mpv 0.41.0 对单文件 MP4 及 DASH 双流解码均通过。

## 代码现状与本次工作

- 本分支目前交付架构规划、实测评估与交接文档，保持 MIT LICENSE。
- 阶段一的内容接入链路与推流解码已完成全流程验证，技术选型正式裁决为：**基于 C# 原生 HttpClient + System.Text.Json 的轻量 WBI API 适配器**（不引入外部重型爬虫或 CLI 工具）。
- 验证阶段编写的探测与排查脚本已清理，详细测试数据已留档于 `docs/bilibili-data-pipeline-evaluation.md`。

## 下一步任务

阶段一的“证明能够稳定拿到 B 站内容并解码”已达成，接下来按 MVP 计划实施：

1. **准备本地 .NET 10 SDK 环境**：在 Windows 本机配置 .NET 10 SDK，建立 C# 解决方案。
2. **工程化实现 C# 接入模块**：封装 `BilibiliApiClient` 与 `WbiSigner`，实现元数据查询、分 P 展开、合集导入与临时播放地址解析。
3. **验证连续播放与队列推进**：接入 SQLite 持久化队列，通过 mpv JSON IPC 控制视频切歌与状态同步。
4. **制作 Windows 桌面壁纸共存小原型**：使用短本地视频先行验证 Win32 宿主在 Wallpaper Engine 运行时的挂载、图标交互与自身窗口清理。

## 文档索引

- [B站数据链路实测与技术选线评估报告](./docs/bilibili-data-pipeline-evaluation.md)：2026-10-15 Windows 本地端到端实测留档与路线裁决。
- [三阶段 MVP 计划](./docs/WallBili-mvp-plan.md)：开发任务与各阶段验收门槛。
- [确认后的技术栈](./docs/WallBili-stack-proposal.md)：技术依据与模块边界。
- [环境初始化记录](./docs/environment-handoff.md)：工具版本、验证结果与平台边界。
- [历史路线调研](./docs/WallBili-route-report.md)：原始候选比较及资料来源。
