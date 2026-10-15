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

## B站公开样例

先前样例 BV1xx411c7mD / CID 62131 的验证结果：

| 步骤 | 结果 |
|---|---|
| 公开网页 | HTTP 200，正确解压后读取 BVID/CID 与标题 |
| 分 P | HTTP 200、code=0，一个分 P |
| 匿名 nav | HTTP 200、code=-101，仍有公开 WBI 参数 |
| 签名播放地址 | code=0，六路视频、两路音频 |
| 视频与音频 Range 请求 | 均 HTTP 206，收到有效 MP4 头 |
| mpv 实际远程解码 | 480×360 视频、44100 Hz 音频，播放时钟推进至约 0.6 秒 |
| 直接元数据接口 | HTTP 412、HTML 响应，原因未查清 |

因此，Biu 使用的核心公开播放请求当时可以工作；完整 Biu、目标合集/系列、分页、登录与会员内容仍未验证。单样例短时解码不代表完整播放或稳定性通过。

先前诊断参考 Biu 的探针位于仓库外，并保留其非商业许可通知；此次没有将该代码作为 MIT 应用发布。

## 当前实例与保存配置的差异

环境配置已经保存安装/启动说明及以下域名要求，保留包管理器/GitHub 预设：

- api.bilibili.com
- www.bilibili.com
- upos-hz-mirrorakam.akamaized.net
- upos-sz-mirrorcosov.bilivideo.com

媒体 CDN 会随内容和调度变化，后续按实际返回主机追加。上述保存的配置不能证明当前实例已应用它。

最新运行状态与实例策略文件显示当前实例仍只有包管理器/GitHub 预设。实测访问 API 与视频网页均在代理 CONNECT 阶段被 403 拒绝，尚未到达 B站。因此本轮远程链路未重新验证，不能误报成 B站 HTTP 412 或接口业务失效。

配置保存、用户发布、实例实际生效、新任务验证分别判断。本次文档提交不改变环境策略，也不声称新任务恢复已通过。

## Windows 尚待验证

实际 WPF 界面、视频画面与声音、真壁纸挂载、图标交互、Wallpaper Engine 共存、显示器变化、睡眠恢复、Explorer 重启和登录自启均待本地验收。Wallpaper Engine 共存需同时检查原设置、显示能力与 WallBili 自身窗口清理。

当前无须启动常驻服务；此前媒体验证进程已退出。Windows 本地开发的优先事项见 [临时交接说明](../TEMP-HANDOFF.md)。
