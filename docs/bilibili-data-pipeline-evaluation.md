# 哔哩哔哩数据链路实测与技术选线评估报告

**测试日期**：2026-10-15  
**测试环境**：Windows 10/11 x64、Python 3.14.3、mpv 0.41.0 (内置 FFmpeg 7.1+)  
**验证目标**：实测跑通能够稳定从哔哩哔哩获取视频元数据、分 P、合集明细及实际音视频流的端到端数据链路，解决前期调研中遗留的 HTTP 412 等阻塞点，为 WallBili 项目后续内容接入层架构提供决策依据。

---

## 一、 实测结论与核心发现

### 1. 成功跑通的完整闭环链路
本次在真实 Windows 网络环境中，**100% 跑通了“游客凭据初始化 → WBI 签名协商 → 视频元数据与分 P 展开 → 合集与 UP 主投稿枚举 → 音视频播放流解析 → mpv 原生双轨解码”的全链路**。无需依赖任何用户账号登录或静态 Cookie，完全以公开匿名身份稳定运转。

### 2. 前期遗留“HTTP 412”谜团已彻底破解
- **历史现象**：在云端前期调研中，直接请求 `/x/web-interface/view?bvid=...` 收到 HTTP 412 错误 HTML 页面。
- **实测根因**：B站防爬 WAF 已全面收紧对旧版无签名 Web 接口的访问，未携带合法签名与凭据的请求会被直接阻断。
- **解决验证**：升级至官方新版 WBI 签名接口 **`/x/web-interface/wbi/view`**，并附带动态计算的 `w_rid` 与 `wts`，实测 **HTTP 200 / code=0 稳定秒级响应**，彻底根除 412 问题。

### 3. 网页数据回退（Web Scraping）现状定性
- **实测事实**：对公开视频网页 `https://www.bilibili.com/video/{bvid}` 提取分析，现代 B站前端已转向客户端渲染（CSR），HTML 中**不再包含**历史上的 `window.__INITIAL_STATE__` 或 `window.__playinfo__`。
- **决策结论**：**基于网页正则/DOM 抓取的数据链路已不可用，项目应完全聚焦于基于官方轻量 API + WBI 签名的原生接口路线**。

---

## 二、 端到端链路各节点实测数据

```mermaid
flowchart TD
    A["步骤 1: 游客指纹<br/>/x/frontend/finger/spi"] -->|buvid3 / buvid4| B["步骤 2: 签名密钥<br/>/x/web-interface/nav"]
    B -->|提取 wbi_img 算 mixin_key| C["步骤 3: WBI 签名器<br/>计算 w_rid & wts"]
    C --> D1["视频元数据与分P<br/>/x/web-interface/wbi/view"]
    C --> D2["UP主投稿搜索<br/>/x/space/wbi/arc/search"]
    C --> D3["UP主合集列表<br/>/x/polymer/web-space/seasons_series_list"]
    D3 --> D4["合集明细展开<br/>/x/polymer/web-space/seasons_archives_list"]
    D1 & D4 --> E["步骤 4: 播放流解析<br/>/x/player/wbi/playurl"]
    E -->|fnval=0| F1["单文件 MP4 (音画合一)"]
    E -->|fnval=16| F2["DASH 分离流 (Video + Audio)"]
    F1 --> G["步骤 5: mpv 解码与播放<br/>(退出码 0, 稳定出画面与声音)"]
    F2 --> G
```

### 节点 1：游客身份凭据获取
- **接口**：`https://api.bilibili.com/x/frontend/finger/spi`
- **请求头**：标准浏览器 User-Agent、`Referer: https://www.bilibili.com/`
- **返回结果**：HTTP 200，`code: 0`，动态生成合法的 `b_3` (`buvid3`) 与 `b_4` (`buvid4`)。
- **作用**：放入全局 Cookie 中，避免裸 IP 访问触发初级风控。

### 节点 2：WBI 签名密钥协商
- **接口**：`https://api.bilibili.com/x/web-interface/nav`
- **返回结果**：匿名请求返回 `code: -101`（未登录），但 `data.wbi_img` 中完整提供了当日的 `img_url` 和 `sub_url`。
- **算法验证**：按官方 64 字节置换表对两段 Hash 重排取前 32 位生成 `mixin_key`，对参数字典升序排列 + MD5 计算，`w_rid` 与 `wts` 校验一次通过。

### 节点 3：视频元数据与多分 P 解析
- **接口**：`https://api.bilibili.com/x/web-interface/wbi/view?bvid={bvid}&w_rid=...&wts=...`
- **单 P 样例验证**：`BV1gHps6yEHL`（影视飓风最新投稿），耗时 180ms，完整解析出标题、UP主、时长 (823s)、封面图及 CID (42593223768)。
- **多分 P 样例验证**：`BV17x411w7KC`（合辑视频），一次性稳定提取全部 10 个分 P，各分 P 的 `cid`、标题、独立时长完整无损。

### 节点 4：UP 主合集与空间投稿枚举
- **UP 主空间投稿**：`/x/space/wbi/arc/search`（带 WBI 签名），实测影视飓风（mid: 946974），成功获取 937 条投稿总量，分页数据字段规范。
- **合集与系列列表**：`/x/polymer/web-space/seasons_series_list?mid={mid}`，实测返回 10 个合集。
- **合集明细提取**：`/x/polymer/web-space/seasons_archives_list?mid={mid}&season_id=2046621`，一次性获取合集《样片日记》全部 26 条视频明细，含 BVID、标题、时长等，为阶段二播放列表维护提供了完整数据源。

### 节点 5：播放流解析与媒体解码（mpv 验证）
- **接口**：`https://api.bilibili.com/x/player/wbi/playurl`
- **实测比对两种推流方案**：

| 指标 | 方案 A：单文件 MP4 (`fnval=0`) | 方案 B：DASH 分离流 (`fnval=16`) |
|---|---|---|
| **音画组织** | 单一 MP4 文件（原生包含 H.264 视频 + AAC 音频） | 独立 Video 轨道（HEVC/AVC） + 独立 Audio 轨道（AAC） |
| **最高画质** | 未登录通常限 360P/480P/720P | 未登录支持 480P/720P，登录后可支持 1080P/4K |
| **播放器开销** | 极低（单个网络连接，解封装简便） | 稍高（双连接，由 mpv 内部统一时钟对齐） |
| **mpv 接入方式** | `mpv --referrer=... URL` | `mpv --http-header-fields-append=... --audio-file=A_URL V_URL` |
| **实机实测结果** | **退出码 0，音视频成功解码** | **退出码 0，两路流同步拉取并完成首批帧解码** |

---

## 三、 未来技术选线对比与裁决建议

| 路线候选 | 优势 | 风险与短板 | 裁决建议 |
|---|---|---|---|
| **路线 1：原生轻量 WBI API 适配器**<br/>(C# HttpClient + System.Text.Json) | • **100% 经实测跑通**<br/>• 无外部重型依赖，资源极小<br/>• 结构化 JSON 解析极快<br/>• 分P、合集、UP主空间原生支持 | • 需自维护 WBI 签名算法（代码量仅约 30 行，算法稳定已公开数年） | **【强力推荐，项目首选】**<br/>完全符合已确定的 C#/.NET 10 技术栈，维护边界清晰。 |
| **路线 2：网页 SSR / DOM 抓取**<br/>(HtmlAgilityPack 等) | • 表面上无需了解签名算法 | • **实测已失效**：现代 B站页面已无嵌入 JSON<br/>• 页面体积达数十 KB，带宽浪费严重<br/>• 无法获取合集和空间分页 | **【坚决废弃】**<br/>不具备长期可用性与技术先进性。 |
| **路线 3：第三方 CLI 胶水（yt-dlp）**<br/>(进程调用解析) | • 社区维护反爬规则 | • 需外挂 Python/yt-dlp 二进制（增加数十 MB 体积）<br/>• 启动子进程开销大，延迟从几十毫秒增加到 1~2 秒<br/>• 破坏统一 C# 架构边界 | **【作为终极 Fallback / 备用方案】**<br/>仅在核心 API 出现无法破解的重大改动时才启动。 |

---

## 四、 下一步落地规划（对阶段一开发的指导）

1. **凭据与签名缓存设计**：
   - `buvid3` / `buvid4` 获取一次后可在进程生命周期或本地 SQLite 中缓存数周，无需每次请求都重复调用 `/x/frontend/finger/spi`。
   - `mixin_key` 每天刷新一次即可（B站每日通常固定盐值）。
2. **播放流过期与动态恢复策略**：
   - 播放流 URL 具有短期时效性（实测 `deadline` 为数小时），**绝对不要存入持久化数据库**。
   - 本地数据库仅保存 `BVID`、`CID` 与条目元数据。在切歌或从队列播放时，动态按需解析临时播放 URL。
3. **播放引擎参数配置规范**：
   - mpv 启动时务必指定：
     ```powershell
     mpv --no-ytdl --http-header-fields-append="Referer: https://www.bilibili.com/" --http-header-fields-append="User-Agent: Mozilla/5.0..."
     ```
   - 壁纸场景优先选用 `fnval=0` 单文件流（稳定性最高、CPU/网络占用最低），画质要求较高时选用 `fnval=16` DASH 分离流。
