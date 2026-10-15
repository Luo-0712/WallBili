# 云端环境初始化与验证记录

记录日期：2026-10-15。主技术栈已确认 C#/.NET 10 + WPF + SQLite + mpv。最新综合状态见 [临时交接说明](../TEMP-HANDOFF.md)。

本文件描述云端已经准备过的工具与验证证据，不是 Windows 安装脚本。本次 `init` 分支仅提交文档，不包含工具、缓存、诊断源码、媒体文件或未完成的应用代码。

## 云端工具

| 项目 | 已验证版本 |
|---|---|
| .NET SDK / 运行时 | 10.0.402 / 10.0.13 |
| CommunityToolkit.Mvvm | 8.4.2 |
| Microsoft.Data.Sqlite | 10.0.13 |
| xUnit | xunit.v3 4.0.2 |
| mpv | 0.41.0；上游提交 41f6a645068483470267271e1d09966ca3b9f413 |
| FFmpeg / ffprobe | 7.1.5-0+deb13u1 |

云端应用 checkout 为 `/workspace/WallBili`；外部工具与验证项目位于 `/workspace/wallbili-environment`。SDK 来自官方发布元数据与固定 SHA-512 校验；Debian 开发包经签名元数据和 SHA-256 核对后本地提取；mpv 从固定上游版本构建。

云端保留以下入口，仅适用于保有该工具目录的实例：

```bash
source /workspace/wallbili-environment/activate.sh
bash /workspace/wallbili-environment/verify.sh
```

安装/刷新入口为 `/workspace/wallbili-environment/install.sh`。这些辅助文件未提交 Git；本地拉取文档分支后不存在这些路径。

## 已留存的验证结果

- 三个环境检查全部通过：MVVM 代码生成与通知、SQLite 原生持久化与事务回滚、mpv H.264/AAC 实际解码和 C# JSON IPC 播放/暂停/精确 seek。零失败、零跳过、零错误。
- WPF 辅助项目以 `net10.0-windows` 和 `EnableWindowsTargeting=true` 编译成功，零警告和错误；未运行 Windows UI。
- 锁定依赖恢复与完整安装步骤复跑成功。以上均为环境辅助检查，不是 WallBili 应用测试。
- mpv 云端构建使用 null 音视频输出，验证解码和 IPC；没有验证实体音频设备或图形窗口。
- TLS 证书与软件包完整性验证保持启用；mpv 使用当前环境提供的受支持 CA 配置，不保存证书绑定或凭据值。

原始环境结果在云端 `results/environment-tests.xml` 与 `verification-final.log`，本次未提交原始文件。

## Windows 本地环境现状（2026-10-15 实测）

| 项目 | 本地状态 | 说明 |
|---|---|---|
| .NET 运行时 / SDK | 运行时 6.0.36，SDK 待安装 | 后续开发需安装 .NET 10 SDK |
| mpv | v0.41.0 (`D:\Tools\mpv\mpv.com`) | 内置 FFmpeg 7.1+、libplacebo 7.360，实测解码通过 |
| Python | 3.14.3 | 用于协议探针、辅助验证与数据比对 |
| Git | 2.x (`C:\Program Files\Git\cmd\git.exe`) | 版本管理就绪 |

## B站数据链路实测与状态定论

在 Windows 本地直连环境下，对 B站接口与推流链路进行了全面闭环验证（详见 [B站数据链路实测与选线评估报告](./bilibili-data-pipeline-evaluation.md)）：

| 步骤 | 实测结果 | 现状定论 |
|---|---|---|
| 游客指纹凭据 | `/x/frontend/finger/spi` 返回 HTTP 200，code=0 | 稳定取得合法 buvid3 与 buvid4，防风控就绪 |
| WBI 签名协商 | `/x/web-interface/nav` 匿名返回 code=-101，但包含完整 wbi_img | 动态置换生成 mixin_key，MD5 签名校验 100% 通过 |
| 视频元数据与分 P | `/x/web-interface/wbi/view` 返回 HTTP 200，code=0 | **已彻底解决**：前期 412 确认为旧接口无签名被拒，改用带签名接口后单 P / 多分 P（10 P 样例）均稳定返回 |
| 网页数据抓取 (SSR) | HTML 已不包含 `window.__INITIAL_STATE__` | **已定论废弃**：现代 B站转为 CSR，坚决废弃网页 DOM 抓取方案 |
| UP 主投稿与合集列表 | `/x/space/wbi/arc/search` 与 `/x/polymer/...` 成功 | 影视飓风 937 条投稿与合集 26 条视频明细均可完整展开 |
| 播放流与音视频 CDN | `/x/player/wbi/playurl` 返回有效流，Range 请求 HTTP 206 | 单文件 MP4 (fnval=0) 与 DASH (fnval=16) 均成功解析并校验 MP4 魔数 |
| mpv 实际拉流解码 | Windows 本地 mpv 0.41 实测通过 | 单文件原生合流与 DASH 双流（视频 + 外部音频）解码退出码均为 0 |

### 网络连通性说明

此前云端观察到的 CONNECT 403 属于云代理外网白名单限制；**当前 Windows 本地开发环境直连 B站 API 及 CDN 完全畅通**，无阻断现象。

## 下一步环境准备与验证重点

1. **安装 .NET 10 SDK**：为 C# 接入模块与 WPF 桌面工程提供构建环境。
2. **Windows 桌面宿主与 Wallpaper Engine 共存小原型**：验证 mpv 窗口嵌入 Win32 宿主后与 Wallpaper Engine 的窗口层叠、桌面图标交互与资源占用。
