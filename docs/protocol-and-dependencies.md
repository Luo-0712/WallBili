# 接入协议与依赖来源

本阶段内容适配器由 WallBili 自行实现，使用 .NET `HttpClient` 与 `System.Text.Json`。没有复制或改写环境目录里 Biu 的非商业许可探针，也没有将 Biu 源代码纳入 MIT 仓库。此前环境探针的成功仅作为可行性背景。

元数据采用带 WBI 签名的 `/x/web-interface/wbi/view`，游客身份采用 `/x/frontend/finger/spi`，播放采用 `/x/player/wbi/playurl`。不调用旧无签名元数据接口，不抓取 HTML。这个选择依据用户提供的 2026-10-15 Windows 实测及本项目的 C# 复测。

协议行为通过公开返回数据和公共接口资料核对；B 站接口可能变化，成功结论限定于验证记录里的样例与时间。

- [Bilibili API Collect：视频基本信息](https://socialsisteryi.github.io/bilibili-API-collect/docs/video/info.html)：BVID、CID、分 P。
- [WBI 签名说明](https://socialsisteryi.github.io/bilibili-API-collect/docs/misc/sign/wbi.html)：导航公开 WBI 参数和规范化签名行为。
- [视频流获取](https://socialsisteryi.github.io/bilibili-API-collect/docs/video/videostream_url.html)：DASH 分离视频/音频与请求条件。
- [合集/系列接口资料](https://github.com/SocialSisterYi/bilibili-API-collect/tree/master/docs/user)：UP 主合集/系列分页。
- [mpv 0.41.0 JSON IPC](https://github.com/mpv-player/mpv/blob/v0.41.0/DOCS/man/ipc.rst)：命令关联、事件、Windows 命名管道、Unix socket。
- [mpv 选项](https://github.com/mpv-player/mpv/blob/v0.41.0/DOCS/man/options.rst)：独立音轨、请求头、暂停和输出。

这些链接是协议参考资料；不把链接存在解释为所有接口已经实测。具体接口、错误分类、分页和媒体控制由项目测试及真实探针记录支持。

本阶段使用 xUnit v3 `4.0.2` 测试依赖；项目锁文件固定解析结果。WPF 为 Windows 平台框架。mpv、FFmpeg 由开发者单独提供，保留各自许可与发布要求；仓库 MIT 许可证只覆盖 WallBili 自有代码。SQLite 与 MVVM Toolkit 的引入按后续实际功能需要执行。

Windows 播放进程显式使用 `--ytdl=no`，资源由原生 API 适配器提供。当前云端验证 mpv 构建没有 Lua，也没有 ytdl hook，因此不传入该构建不支持的选项；两端均不依赖 yt-dlp。两端使用隔离配置目录、单一播放进程和必要的请求头。
