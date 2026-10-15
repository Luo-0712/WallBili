# WallBili

面向 Windows 10/11 的后台 B站音乐与视频壁纸项目。目标是从指定内容来源维护播放队列，并将正在播放的视频显示在真正的桌面壁纸层，保留桌面图标操作，与 Wallpaper Engine 共存。

`init` 分支目前交付规划、技术选型和交接文档，尚无可运行应用。

- [临时交接说明：当前调研、选型、实测与下一步](./TEMP-HANDOFF.md)
- [三阶段 MVP 开发计划](./docs/WallBili-mvp-plan.md)
- [已确认的技术栈](./docs/WallBili-stack-proposal.md)
- [环境初始化与验证记录](./docs/environment-handoff.md)
- [历史技术调研与路线比较](./docs/WallBili-route-report.md)

已确认：C# / .NET 10 LTS、WPF、SQLite、mpv，以及 C#/Win32 桌面宿主。开发顺序为取得 B站内容、维护合集与队列、接入视频壁纸。当前优先打通公开内容的播放链路。
