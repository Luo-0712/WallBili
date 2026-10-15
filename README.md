# WallBili

面向 Windows 10/11 的后台 B站音乐与视频壁纸项目。目标是从指定内容来源维护播放队列，并将正在播放的视频显示在真正的桌面壁纸层，保留桌面图标操作，与 Wallpaper Engine 共存。

`init` 分支目前交付规划、技术选型和交接文档，尚无可运行应用。

- [临时交接说明：当前调研、选型、实测与下一步](./TEMP-HANDOFF.md)
- [B站数据链路实测与选线评估报告](./docs/bilibili-data-pipeline-evaluation.md)
- [三阶段 MVP 开发计划](./docs/WallBili-mvp-plan.md)
- [已确认的技术栈](./docs/WallBili-stack-proposal.md)
- [环境初始化与验证记录](./docs/environment-handoff.md)
- [历史技术调研与路线比较](./docs/WallBili-route-report.md)

已确认：C# / .NET 10 LTS、WPF、SQLite、mpv，以及 C#/Win32 桌面宿主。开发顺序为取得 B站内容、维护合集与队列、接入视频壁纸。公开内容元数据、分 P、合集及音视频播放流的端到端获取与解码链路已在 Windows 本地实测跑通并留档（见 [评估报告](./docs/bilibili-data-pipeline-evaluation.md)），技术选型收敛至原生 WBI API 适配器，当前推进 C# 接入模块与桌面壁纸宿主原型验证。
