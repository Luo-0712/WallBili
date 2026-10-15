# WallBili 阶段三 Windows 验收包

这是 Windows x64 的框架依赖调试包。需要 .NET 10 Desktop Runtime 和独立安装的 mpv 0.41.0；包内没有附带 mpv/FFmpeg。

1. 解压整个目录，不要只复制 exe。打开 `app/WallBili.Desktop.exe`。
2. 首次运行在“mpv 路径”填写可信的 `mpv.exe` 完整路径；也可提前设置 `MPV_PATH`。路径在下一次操作时保存。
3. 添加视频链接/BVID 或导入自己选择的合集/系列；选择目录条目加入队列，双击队列项播放。
4. 勾选“桌面视频”，选择一块屏幕与填充/适应，点击“应用桌面设置”。没有可用 Explorer 壁纸宿主时会显示等待状态，按 `windows-phase3-checklist.md` 记录。
5. 关闭窗口会收起到托盘。双击托盘或再次启动 exe 唤回；用“退出”彻底结束。取消“隐藏画面时继续音乐”会在隐藏时暂停，重新启用后可手动继续。
6. 需要登录启动时勾选并应用；默认关闭。资料保存在 `%LOCALAPPDATA%\WallBili`。退出后保留播放/暂停意图与进度；希望下次不自动播放时先点“停止”。移走安装目录前应取消登录启动，再从新位置设置。

`desktop-probe/WallBili.DesktopProbe.exe LOCAL_VIDEO 30` 是可选的独立桌面候选原型，只在既有 Explorer 壁纸 WorkerW 下创建自己的窗口。运行前设置 `MPV_PATH`，并自行提供短本地视频。30 秒自动退出，Ctrl+C 可提前退出；找不到候选宿主时返回退出码 3。它不提供完整壁纸功能，也没有已通过的 Wallpaper Engine 共存结论。

云端完成构建、协议与真实 mpv 解码/恢复回归；Windows 桌面、Wallpaper Engine 共存、两小时稳定性与音画误差仍须实机观察。结果与边界见 `phase3-validation.md`。可用 `measure-resources.ps1` 采样本机资源。许可见 `LICENSE`、`THIRD-PARTY-NOTICES.md` 与 `licenses/`。
