---
name: wallbili-mcp
description: 通过 wallbili MCP 工具操作 WallBili 桌面应用：搜索 B 站视频/UP 主、导入来源、整理本地片单、维护播放队列、控制桌面视频播放（播放/暂停/切集/跳转/音量/显示隐藏）。当用户提到 B 站、bilibili、BV 号、视频、播放、暂停、下一集、片单、队列、壁纸视频、WallBili，或提出"找个视频放一下""把这套教程排着播""现在在放什么""切下一集"之类的需求时使用，即使没点名 MCP 或工具名。应用未启动时会话拿不到 wallbili 工具，此时用随包启动器自动拉起应用再继续，不要要求用户手动启动。也可用于排查 wallbili 工具不可用的问题。
---

# WallBili MCP 使用

WallBili 是本地视频壁纸应用。这组工具让它搜索、整理、播放 B 站内容；**选什么内容的语义理解由你负责，WallBili 只负责获取数据和实际播放**。工具名以 `mcp__wallbili__` 开头，共 20 个。

## 0. 先确认工具可用（应用没开就自己拉起）

本会话可能有 `mcp__wallbili__` 前缀的原生工具，也可能没有——**应用没启动时会话就注册不到这些工具，这是常态，不是你放弃或打断用户的理由**。按下面处理：

1. **有原生工具**：正常调用。若调用返回连接失败或未授权，继续第 2 步；启动器读取的是应用当前的端口和令牌，能自愈“端口改过”“令牌重置过”这两类失配。
2. **没有原生工具，或调用连不上**：找到启动器。读取数据目录下的 `agent.json`（默认 `%LOCALAPPDATA%\WallBili\agent.json`；Git Bash 用 `$LOCALAPPDATA/WallBili/agent.json`，执行时把 `C:\...` 路径写成 `/c/...`），取其中的 `cliPath`。没有该文件时使用环境变量 `WALLBILI_CLI`。
3. 运行 `"<cliPath>" ensure`。它会在需要时用 `--background` 静默拉起应用（只出现托盘图标，不弹窗），等到 MCP 就绪才返回。按返回的 JSON 处理：
   - `{"ok":true,...}`：可以继续。`launched` 为 true 说明是刚拉起的。**本会话后续所有操作改用** `"<cliPath>" call <tool> '<json>'`，工具名和参数与 `references/tools.md` 完全一致，返回同样是 `summary` + `data`。对用户只说一句“已自动启动 WallBili（托盘，不会弹窗）”，然后继续干活，不要要求用户操作。
   - `{"ok":false,"code":"mcp_disabled",...}`：设置里 MCP 是关闭的。把 `nextStep` 转成一句话，请用户打开“助手连接”启用 MCP，然后再试一次。
   - `code` 为 `token_invalid`：请用户重新复制助手配置或在“助手连接”里重置令牌；为 `timeout` / `app_not_found` / `launch_failed` / `port_mismatch`：如实说明并给出 `nextStep`。
4. **绝不要**因为缺工具就默认回答“请先启动 WallBili / 开个新会话”。只有 `ensure` 明确失败时，才把它的原因告诉用户。原生工具只在会话启动时注册，本会话拿不到属于正常情况，用 `call` 走完即可；下次会话应用仍在托盘时会自动恢复原生工具。
5. 应用没开时不要用 web 搜索或口头描述假装完成“播放”类请求；拉起失败就如实报告。

`ensure` 的退出码：0 就绪，3 未就绪（看 `code`）。`call` 的退出码：0 成功，1 工具业务错误，3 传输/鉴权问题。

## 1. 铁律

1. **先搜后选，引用必须来自返回值。** 不要凭记忆或猜测构造 bvid / cid / uploaderId / collectionId。cid 只能来自 `get_content` 的真实分 P，合集 ID 只能来自查询结果。
2. **播放要核实。** 任务被接受、装载成功都只是"请求已收到"。请求播放后必须调 `get_status`，读到 `outputActive: true` 且有实际输出，才能对用户说"已开始播放"。
3. **入队后如果没在播，直接起播，不要等用户。** 任何一次成功的队列写入（`edit_queue` 的 `append`/`insert_next`/`replace`、`load_playlist`）之后，先 `get_status`：`outputActive` 为 false、或 `intent` 不是 `playing`（`stopped`/`paused` 都算），就立刻 `control_playback {action:"play"}` 起播；`load_playlist` 也可以直接带 `autoplay: true`。**例外**：当前已经在播、而用户只是说"加到队列/排上"时，不要打断正在放的这首，追加即可。只 `import_source` 或只 `create_playlist`（没有入队）仍然不播放。
4. **改前读 revision。** 片单修改（`edit_playlist`/`delete_playlist`/`refresh_playlist`）用 `get_playlist` 返回的 `revision`；队列修改（`edit_queue` 的 remove/move/reorder/clear/replace、`load_playlist` 的 `mode: "replace"`）用 `get_queue` 的 `revision`。收到 `Conflict` 就重新读取再改，不要盲目重试。
5. **requestId 保证幂等。** 所有写操作建议带唯一 `requestId`。重试同一个操作：相同 requestId + 相同参数（重放已存结果）；新操作：新 ID。同一 ID 换参数会报 `Conflict`。返回 `Accepted` 表示结果未知——先查 `get_status`/`get_task` 再决定，不要直接再发一次。
6. **三步分开：导入 ≠ 入队 ≠ 播放（入队后会自动起播）。** 用户说"收藏/保存"通常只要 import 或建片单，不碰播放；说"排上/加进队列/推进队列"就是入队，入队后按第 3 条直接起播，不用再问一句"要现在放吗"；说"放/播"是 load + autoplay。
7. **尊重用户的顺序与移除。** `edit_playlist` 的 append 追加不重排（`reorder` 才动顺序）；refresh 会保留手动移除记录。不要擅自"帮你整理"掉用户的手动改动。

## 2. 典型工作流

### A. "找个视频放一下"（最常见）

```
1. mcp__wallbili__search_content  {query, kind: "video", order: "relevance"}
2. （候选多时）挑 3–5 个向用户展示：标题 / UP 主 / 时长
3. 确认后二选一：
   - 单个视频快速播 → edit_queue {action: "append", references: [...]} 再 control_playback {action: "play"}
   - 或建片单 → create_playlist → load_playlist {autoplay: true}
4. mcp__wallbili__get_status → 用 outputActive 核实后再汇报
```

搜索结果的 `reference` 可直接传给后续工具。播放意图明确的连续请求（如"搜罗翔的视频放一个"）可以不必每步都追问，但候选选择要基于真实搜索结果并简短告知选了什么。入队后如果当时没在播，直接起播（见第 1 节第 3 条），不必再问"要现在放吗"。

### B. 整理一套 UP 主/合集

```
1. mcp__wallbili__search_content {query, kind: "creator"}   # ⚠ 见下方已知问题
2. mcp__wallbili__list_creator_content {uploaderId, kind: "collections"}
3. mcp__wallbili__get_content {reference: {kind:"collection", uploaderId, collectionId, collectionKind}}
4. create_playlist {name, references}  → 返回 playlistId + taskId（后台增量准备）
5. load_playlist {playlistId, autoplay: true}（若当前没在播，直接带入队起播；已在播则按需 append）
```

长合集不必等准备完：`create_playlist` 立即返回，首项就绪即可播，后续内容逐批入队；播到准备边界时 `get_status` 会显示等待后续内容（`waitingForContent`）。用户暂停/停止后，后台准备不会擅自恢复播放。

**已知问题（2026-10-18 实测）**：`search_content` 的 `kind: "creator"` 连续报 `InvalidResponse`（视频搜索正常）。此时的降级路径：先搜视频，从结果里取 `uploader`/`uploaderId` 字段，直接调 `list_creator_content`，不要反复重试 creator 搜索。

### C. 管理已有片单/队列

```
list_playlists / list_sources → get_playlist {playlistId} → 拿 revision
edit_playlist {playlistId, action, expectedRevision, ...}
get_queue → edit_queue {action, expectedRevision, ...}
```

删除类操作（`delete_playlist`/`remove_source`）先说清影响再执行："删除片单不会删除已入队内容"。

改完队列（`edit_queue`/`load_playlist`）顺手 `get_status`：没在播就立刻起播，不要停下来等用户。

### D. 播放控制（不动队列）

`control_playback` 一次一个动作：

| action | 说明 |
|---|---|
| `play` | 播放当前项；可带 `itemId` 指定队列项 |
| `resume` / `pause` / `stop` | 恢复当前源 / 暂停 / 停止 |
| `next` / `previous` | 切集，建议带 requestId |
| `seek` | `seconds` 秒（从 0 计） |
| `set_volume` | `volume` 0–100 |
| `set_mode` | `sequential` / `repeatAll` |
| `show_video` / `hide_video` | 桌面显示/隐藏视频，隐藏后仍在播 |

### E. 后台任务

`get_task` 查进度与部分失败；`cancel_task` 停掉剩余整理（已入队内容保留）；`retry_task` 重试 failed / partial / cancelled / interrupted 任务（不会产生重复分 P）。应用重启会把未完成任务标为 `interrupted`，需要显式重试，不要自动重试。同时最多 2 个准备任务在跑。

## 3. 工具速查

| 分组 | 工具（`mcp__wallbili__` 前缀省略） | 写操作 | 一句话 |
|---|---|---|---|
| 搜索 | `search_content` | 否 | 搜视频或 UP 主，先搜后选 |
| 搜索 | `list_creator_content` | 否 | UP 主投稿或原生合集列表 |
| 搜索 | `get_content` | 否 | 展开视频分 P / 合集条目（拿真实 cid） |
| 来源 | `list_sources` / `import_source` / `refresh_source` / `remove_source` | 除 list 外都是写 | 已导入来源的增删改查；import 不会播放 |
| 片单 | `list_playlists` / `get_playlist` | 否 | 列片单 / 读成员、顺序、revision |
| 片单 | `create_playlist` | 是 | 建片单，返回 playlistId + taskId，增量准备 |
| 片单 | `edit_playlist` | 是（破坏性） | rename / append / remove / reorder，需 expectedRevision |
| 片单 | `delete_playlist` / `refresh_playlist` | 是（破坏性） | 删除 / 刷新补充新集（保留顺序与移除记录） |
| 队列 | `get_queue` | 否 | 读手动队列、itemId、revision、当前源 |
| 队列 | `edit_queue` | 是（破坏性） | append / insert_next / remove / move / reorder / clear / replace |
| 队列 | `load_playlist` | 是（破坏性） | 装载片单到队列，`autoplay` 默认 false |
| 播放 | `get_status` | 否 | 实际输出状态、意图、进度、任务 |
| 播放 | `control_playback` | 是 | 播放/暂停/切集/seek/音量/模式/显隐视频 |
| 任务 | `get_task` / `cancel_task` / `retry_task` | 除 get 外都是写 | 准备任务的查询/取消/重试 |

完整参数、返回字段与枚举见 `references/tools.md`（需要精确参数时再读）。

## 4. 错误处理

错误结果形如 `structuredContent.error = {code, message, nextStep}`，`isError: true`，`nextStep` 通常就是正确的下一步。

| code | 含义与处理 |
|---|---|
| `Conflict` | revision 过旧或 requestId 换了参数 → 重读状态再改 |
| `InvalidInput` | 参数不符合 schema → 对照工具 schema 修参数 |
| `Accepted` | requestId 结果未知 → 查状态，不要直接重发 |
| `Cancelled` | 操作被取消 → 先查状态再用同一 requestId 重试 |
| `NotReady` | 应用正在退出 → 请用户重新启动 WallBili |
| `AuthenticationRequired` | B 站侧需要登录/无权限 → 请用户在 WallBili 里登录 |
| `RateLimited` | B 站风控 → 停下等待，不要连续重试 |
| `Network` | 网络问题 → 稍后重试 |
| `InvalidResponse` / `OperationFailed` | 上游返回异常 → 单次重试；连续失败就如实告知 |

## 5. 跟用户怎么汇报

- 候选内容给出**标题 / UP 主 / 时长**，不要只报 bvid。
- 播放状态一律以 `get_status` 的实际字段为准：`outputActive` 才是"真的在播"；意图 playing 但当时输出未起，可能还在解析资源或等待后续内容（`waitingForContent`）或恢复中（`recoveryPending`）。
- 长任务用"已在后台准备，现在就能先播"的表述，不要把 taskId 这类内部标识当作进度汇报给普通用户。
- 工具返回摘要（content 文本）常是中性短语（如"已读取来源"），结论要自己根据 `structuredContent` 组织。

## 6. 深入参考

- `references/tools.md`：20 个工具的完整参数表、返回字段、ContentRef 形状、分页与枚举、requestId/revision 细则。
