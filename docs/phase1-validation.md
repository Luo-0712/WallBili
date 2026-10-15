# MVP 阶段一验证记录

日期：2026-10-15。分支：`feat/mvp-phase1`。目标是内容接入与调试播放；队列、SQLite、桌面壁纸与 Wallpaper Engine 共存留在后续阶段。

## 接入路线

采用自行编写的 C# 原生轻量 API 适配器：游客凭据 `/x/frontend/finger/spi` → 导航公开 WBI 材料 `/x/web-interface/nav` → 签名元数据 `/x/web-interface/wbi/view` → 分 P/CID → 签名播放资源 `/x/player/wbi/playurl` → mpv 一个音视频会话。

用户提供的 Windows 10/11、Python 3.14.3、mpv 0.41.0 报告已验证上述路线，以及 `BV1gHps6yEHL`、多 P `BV17x411w7KC`、UP `946974` 的合集 `2046621`。本仓库选择该原生 API 路线，不依赖 Python、yt-dlp 或网页抓取，不继续复用 Biu 的非商业实现。

先前云端直接访问旧 `/x/web-interface/view` 得到 HTTP 412，部分样例网页曾含嵌入 JSON。这些历史结果不作为当前实现的依赖，也不足以概括所有网络或所有网页。当前实现与复测以正确的 WBI 元数据接口为准。

## 本次 C# 实测

云环境为 Linux，SDK 10.0.402、运行时 10.0.13、mpv 0.41.0，保留继承代理与 TLS 校验。接口、媒体与控制分别记录；云端 null 音视频输出不会被记作实际显示或实际出声。

| 检查 | 结果 | 证据 |
|---|---|---|
| 全解决方案构建，含 WPF | 通过，0 警告、0 错误 | `scripts/verify.sh` |
| 接入行为测试 | 56/56 通过、0 跳过 | `work/content-tests.xml`；签名/游客缓存、有限重试、分 P、分页、错误分类与格式选择 |
| 本地真实 H.264/AAC 双轨控制 | 3/3 通过、0 跳过 | `work/player-tests.xml`；暂停、继续、精确 seek、EOF、并发请求关联、进程停止/重启 |
| WBI 单 P 元数据 | 通过 | `BV1gHps6yEHL`，CID `42593223768`，标题「影视飓风的直升机？怪东西分享9.0」，时长 823 秒 |
| WBI 多 P 元数据/CID | 通过，10 个分 P | `BV17x411w7KC`，第 1/2 P 的 CID 为 `279786` / `275431` |
| 系列默认分页 | 读取两页，30 + 7 条，总计 37 | UP `37974444`，系列 `240484`，页大小 30；共 37 个不同 BVID，无跨页重复 |
| 合集分页 | 通过；另读到用户报告合集 26 条 | UP `476537405`，合集 `4947065`，页大小 1 连读第 1/2 页；UP `946974` 合集 `2046621` 返回 26 条 |
| MP4 重新解析与远程控制 | 通过 | `BV1xx411c7mD` / CID `62131`；单文件容器，实际 512×384；44100 Hz 双声道；暂停时钟稳定，seek 到 2.000 秒，继续后时钟推进，再取流成功 |
| DASH 重新解析与远程控制 | 通过 | 同样例，AVC `avc1.64001E` + AAC `mp4a.40.2`；质量 ID 32，512×384，44100 Hz 双声道；暂停时钟稳定，seek 2.000 秒，继续后时钟推进，再取流成功 |
| 切换分 P 的实际播放目标 | 通过（云端分别加载两个分 P） | `BV17x411w7KC` 第 1 P/CID `279786` 解码为 512×288；第 2 P/CID `275431` 解码为 640×360；均完成 seek 2.000 秒与重新取流 |
| Windows 实际可见画面、可听音频 | 待 Windows C# 实机验收 | 用户报告证明协议可行；本仓库调试播放器仍需按检查表运行 |
| 登录、会员内容 | 未覆盖 | 只有匿名访问；受限响应分类为需要认证，不绕过权限 |
| CDN URL 自然到期恢复 | 未覆盖 | 重解析只证明按身份获取新资源，不代表自然到期已发生 |

两种格式实际解码器均为 H.264 / AAC；Windows x64 调试应用已交叉发布为 PE32+，需要 .NET 10 Desktop Runtime 和外置 mpv，尚未在本环境运行。

MP4 返回的质量 ID 为 64，但本样例实际解码尺寸为 512×384；不把 API 质量 ID 直接解释为真实 720p。测试中的暂停与 seek 检查是播放时钟控制，不声称完成了音画误差测量。

本机生成的验证 JSON/XML 位于忽略的 `work/`，只含脱敏结果，不入库。关键编码、分辨率、控制位置与样例身份已记录在上表。

## 复测命令

先安装/激活 README 列出的工具，然后：

```bash
bash scripts/verify.sh
dotnet run --project tools/WallBili.Probe -c Release --no-build -- video BV1gHps6yEHL
dotnet run --project tools/WallBili.Probe -c Release --no-build -- video BV17x411w7KC
dotnet run --project tools/WallBili.Probe -c Release --no-build -- collection series 37974444 240484 --page 1
dotnet run --project tools/WallBili.Probe -c Release --no-build -- collection series 37974444 240484 --page 2
dotnet run --project tools/WallBili.Probe -c Release --no-build -- collection season 476537405 4947065 --page 1 --size 1
dotnet run --project tools/WallBili.Probe -c Release --no-build -- collection season 476537405 4947065 --page 2 --size 1
dotnet run --project tools/WallBili.Probe -c Release --no-build -- check-playback BV1xx411c7mD --headless
dotnet run --project tools/WallBili.Probe -c Release --no-build -- check-playback BV1xx411c7mD --format dash --headless
```

Windows 实机运行方式见 [人工验收表](windows-phase1-checklist.md)。只有调试播放器在 Windows 实际显示画面并出声后，阶段一完整验收门槛才算通过。
