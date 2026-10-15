# MVP 阶段三实现与验证

日期：2026-10-16（UTC+8）。基于已合并阶段二的 `main`（`924f818`），功能分支 `feat/mvp-phase3`。用户已确认阶段一、二验证通过；旧阶段文档保留当时的历史记录。

## 实现

- WPF 内的 `DesktopVideoHost` 在既有 Explorer 壁纸 WorkerW 下承载 WallBili 自己的 HWND，直接把它交给同一个 mpv 的 `--wid`。填充/适应通过 IPC `panscan` 切换，不启动独立音频进程。
- 选择一块显示器，以设备 ID 保存；坐标映射包含负坐标，应用采用 PerMonitorV2。所选屏幕离线时暂用主屏，返回后重新选择原设备。每五秒检查宿主，显示变化事件触发即时检查。
- 画面状态、播放意图和临时恢复状态分开：关闭画面可继续音乐，或自动暂停；停止不丢队列且取消重试。闲置/等待恢复时隐藏宿主，不以黑色空窗覆盖原壁纸。
- Explorer 窗口变化时只重新附着自己的子窗口；HWND 被销毁后重建宿主，并串行释放旧播放器再按当前身份/检查点恢复，避免新旧音频进程重叠。
- `LibrarySession` 对网络/限流保留当前项、进度与 Playing/Paused 意图，2、4、8、16、32、60 秒退避，之后最多间隔 60 秒。网络恢复事件可提前重试。媒体运行错误先重新解析同项，连续两次恢复仍错误才记录失败并推进；自然 EOF 才累计完成数。
- mpv 断连与加载超时触发同项恢复；播放意图为 Playing 且检查点位置约 45 秒不推进时重取流。永久来源失效沿用阶段二失败跳过；手动停止、删除和旧代际事件不触发意外复播。
- 睡眠保存检查点并停止会话，唤醒时按稳定身份重新解析，暂停状态保留。未自动开始的 Stopped 不因唤醒或桌面重建自动播放。
- 托盘支持唤回、暂停/播放、下一项、画面显隐与退出；窗口关闭默认收起，第二实例唤回现有窗口。登录启动开关仅操作当前用户的 WallBili Run 项，默认关闭，使用 `--background`。
- Windows Job 绑定所创建的 mpv，正常退出和应用进程被结束时只清理自己的播放器。不会关闭 Wallpaper Engine，不修改其设置，不重排 Shell 或其他应用窗口。
- seek 不只等待命令 ACK，还等待 `seek` 后的 `playback-restart`（或自然 EOF），避免暂停恢复后立刻保存时把尚未更新的位置误写成 0；新增真实恢复测试覆盖此问题。
- SQLite schema 保持 v1；新增桌面偏好保存至既有 `preferences.json`，兼容原先仅含 `MpvPath` 的文件。没有持久化 Cookie、播放 URL 或请求头。

## 验证证据

执行环境：Linux、SDK 10.0.402、mpv 0.41.0、FFmpeg 7.1.5。入口：`bash scripts/verify.sh` / `pwsh scripts/verify.ps1`。真实本机 IPC 需要执行环境允许 Unix socket，测试不访问 B 站。

| 检查 | 结果 |
|---|---|
| 锁定依赖恢复与全解决方案构建 | 已通过，含 WPF/WinForms 与 Windows Job 的跨平台编译；最终构建零警告、零错误 |
| 内容协议回归 | 56 项通过 |
| mpv 音视频实际解码/暂停/seek/填充回归 | 3 项通过；切换 panscan 时 PID 保持 |
| 目录、SQLite、队列与阶段三恢复测试 | 29 项通过，含断网退避、延迟联网、暂停重连、停止取消、睡眠与输出重绑 |
| 真实媒体进程中断/20次切换/输出重建 | 已通过；本地 H.264/AAC，20 次替换保持同一 PID，结束该进程后同项进度恢复；输出重建后旧 PID 消失，退出后自身进程消失 |
| Windows x64 包 | 由 `scripts/publish-windows.sh` 或 `.ps1` 生成；默认依赖 .NET 10 Desktop Runtime，mpv 单独安装 |
| Windows 图标交互、双启动顺序共存、睡眠/Explorer 恢复 | 本环境不能运行，按 Windows 检查表实机验收 |
| 两小时稳定性、GPU、音画误差、登录到播放实测秒数 | 尚未测量，未宣称达到目标 |

共 88 项测试通过，零失败、零跳过。构建初期 NuGet 审计源曾不可达而产生 NU1900；恢复网络访问后使用 `--no-http-cache --locked-mode` 刷新恢复，最终构建已无告警。实测日志：`work/phase3-verification.log`、`work/content-tests.xml`、`work/player-tests.xml`、`work/library-tests.xml`。临时日志与测试媒体不提交 Git。

## 后端边界与实机验收

延续阶段二已提供的保守 WorkerW 原型，不发送 `0x052C` 来创建或重排桌面结构。仅支持存在候选 Explorer WorkerW 的布局；没有宿主时给出等待状态，不降级成全屏窗口。Windows 或 Wallpaper Engine 版本可能改变布局与遮挡，必须目视验证双方启动顺序、图标交互与退出后的壁纸恢复。PID 存在、拥有 HWND 和构建成功均不能代替这些结果。

完整步骤、采样命令与结果填写位置见 [Windows 阶段三检查表](windows-phase3-checklist.md)。代码交付和自动验证通过不等于阶段三所有实机门槛通过；MVP 的最终闭环仍以这份 Windows 结果为准。
