# WallBili 阶段二 Windows 验收包

这是 Windows x64 的框架依赖调试包。需要 .NET 10 Desktop Runtime 和独立安装的 mpv 0.41.0；包内没有附带 mpv/FFmpeg。

1. 解压整个目录，不要只复制 exe。打开 `app/WallBili.Desktop.exe`。
2. 首次运行在“mpv 路径”填写可信的 `mpv.exe` 完整路径；也可提前设置 `MPV_PATH`。路径在下一次操作时保存。
3. 添加视频链接/BVID 或导入自己选择的合集/系列；选择目录条目加入队列，双击队列项播放。
4. 使用暂停、继续、上下项、顺序/循环、音量、seek、来源刷新和列表编辑；按 `windows-phase2-checklist.md` 验证新增行为。
5. 资料保存在 `%LOCALAPPDATA%\WallBili`。播放中关闭后会恢复，暂停关闭后仍暂停；希望下次不自动播放时先点“停止”。

`desktop-probe/WallBili.DesktopProbe.exe LOCAL_VIDEO 30` 是可选的独立桌面候选原型，只在既有 Explorer 壁纸 WorkerW 下创建自己的窗口。运行前设置 `MPV_PATH`，可使用包内 `sample.mp4`。30 秒自动退出，Ctrl+C 可提前退出；找不到候选宿主时返回退出码 3。它不提供完整壁纸功能，也没有已通过的 Wallpaper Engine 共存结论。

云端已完成 80 个测试、真实 mpv 解码与两条 B 站内容的 EOF 队列推进；Windows 运行与桌面共存待实机观察。具体结果与限制见 `phase2-validation.md`。许可见 `LICENSE`、`THIRD-PARTY-NOTICES.md` 与 `licenses/`。
