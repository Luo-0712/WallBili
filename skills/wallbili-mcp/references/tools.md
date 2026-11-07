# WallBili MCP 工具参考

20 个工具的完整参数与语义。所有工具都在 `mcp__wallbili__` 前缀下（下文省略前缀）。参数表里"必填"列标了工具的 required 字段。

## 通用约定

### 响应信封

每个工具返回：

```
content[0].text          → 中文摘要短语（如"已读取来源"）
structuredContent        → 实际数据（下文的"返回"字段）
isError                  → true 时 structuredContent.error = {code, message, nextStep}
```

### 分页

所有列表工具支持 `page`（默认 1）与 `pageSize`（默认 20，上限 100），返回：

```json
{ "page": 1, "pageSize": 20, "total": 37, "hasMore": true, "items": [...] }
```

用 `hasMore` 判断是否继续翻页，不要靠 total 猜。

### ContentRef（内容引用）

引用必须来自工具返回值，形状如下：

```json
{"kind":"video","bvid":"BV1GJ411x7h7"}
{"kind":"part","bvid":"BV1GJ411x7h7","cid":101}
{"kind":"collection","uploaderId":7,"collectionId":11,"collectionKind":"season"}
{"kind":"playlist","playlistId":"<GUID>"}
```

- `part` 的 `cid` 必须来自 `get_content` 返回的真实分 P，不是分 P 序号。
- `collection` 的 `uploaderId`/`collectionId`/`collectionKind` 来自 `list_creator_content` 或搜索结果。
- `video` 引用展开全部分 P；`part` 只取指定 CID；`collection` 按来源顺序展开；`playlist` 保留明确提交的顺序。

### requestId（幂等）

所有非只读工具都有可选 `requestId`（1–128 字符）。

- 同一 requestId + 相同参数：返回上次的已存结果（重放），不会重复执行。
- 同一 requestId + 不同参数：`Conflict`。
- 执行前持久登记；若结果无法确认，重试同 ID 返回 `Accepted` —— 此时先查实际状态，再决定。
- 建议格式：`<动作>-<对象>-<序号>`，如 `photography-create-1`。

### revision（乐观并发）

| 操作 | expectedRevision 来源 |
|---|---|
| `edit_playlist`（所有 action）、`delete_playlist`、`refresh_playlist` | `get_playlist` 的 `revision` |
| `edit_queue` 的 remove / move / reorder / clear / replace | `get_queue` 的 `revision` |
| `load_playlist` 的 `mode: "replace"` | `get_queue` 的 `revision` |

不匹配即 `Conflict`；重新读取、用新 revision 重试。

### 枚举

- 播放意图 `intent`：`stopped` | `playing` | `paused`
- 播放来源 `source`：`manual` | `roaming`（订阅漫游）
- 队列模式 `mode`：`sequential` | `repeatAll`
- 任务状态：`pending` | `running` | `completed` | `failed` | `partial` | `cancelled` | `interrupted`
- 搜索 `kind`：`video` | `creator`；`order`：`relevance` | `published` | `views` | `favorites`（creator 仅 relevance）
- UP 主内容 `kind`：`videos` | `collections`；`collectionKind`：`season` | `series`

---

## 搜索与浏览

### search_content

搜索公开视频或 UP 主（只读，不导入不播放）。搜索会略过付费课程等非视频条目。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `query` | string ≤512 | ✔ | 搜索词 |
| `kind` | `video`\|`creator` | | 建议显式传 `video`；`creator` 见 SKILL.md 已知问题 |
| `page` / `pageSize` | int | | 分页 |
| `order` | 枚举 | | 排序 |

返回：分页结构；视频 item 形如 `{reference, title, uploader, uploaderId, duration, publishedAt}`。`total` 沿用上游结果，可能滞后，请以 `hasMore` 为准。

### list_creator_content

列 UP 主投稿或原生合集/系列（只读）。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `uploaderId` | int ≥1 | ✔ | 来自视频搜索结果的 `uploaderId` |
| `kind` | `videos`\|`collections` | | |
| `page` / `pageSize` | int | | |

返回：分页；合集 item 含 `collectionId`/`collectionKind`，用于构造 collection 引用。

### get_content

展开一个引用：视频 → 全部分 P；part → 该分 P；collection → 按来源顺序的条目；playlist → 成员（只读）。**不解析签名播放地址**。

| 参数 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `reference` | ContentRef | ✔ | |
| `page` / `pageSize` | int | | 大合集翻页 |

返回：分页；条目含标题与可用的 `reference`（分 P 会带真实 `cid`）。

## 来源记录

### list_sources

列出已导入来源（只读）。返回分页；item 含 `sourceId`（`refresh_source`/`remove_source` 用）。

### import_source

后台导入视频/分 P/合集的元数据。返回 `taskId`；**import 不会播放，也不会自动入队**。

| 参数 | 必填 |
|---|---|
| `reference` | ✔ |
| `requestId` | |

### refresh_source

后台刷新一个已导入来源，不打断播放。

| 参数 | 必填 |
|---|---|
| `sourceId` | ✔ |
| `requestId` | |

### remove_source

删除来源记录（破坏性）。目录与队列保留。

| 参数 | 必填 |
|---|---|
| `sourceId` | ✔ |
| `requestId` | |

## 本地片单

### list_playlists

列已保存片单（只读）：含 `playlistId`、名称、`revision`、准备进度。

### get_playlist

读片单的成员顺序、展开的分 P、排除项与 `revision`（只读）。修改前必读。

| 参数 | 必填 |
|---|---|
| `playlistId` (GUID) | ✔ |
| `page` / `pageSize` | |

### create_playlist

从有序引用创建命名片单，增量准备。**立即返回 `playlistId` + `taskId`**；准备期间即可 `load_playlist`。此时可播首项、其余逐批入队。

| 参数 | 必填 | 说明 |
|---|---|---|
| `name` | ✔ | ≤512 |
| `references` | ✔ | 有序 ContentRef 数组，≤200 |
| `requestId` | | |

### edit_playlist

重命名、追加、移除成员或整体重排（破坏性）。**所有 action 都需要 `expectedRevision`**。追加不重排；`reorder` 必须给全部 memberIds 且不得增删。

| 参数 | 必填 | 说明 |
|---|---|---|
| `playlistId` | ✔ | |
| `action` | ✔ | `rename`(带 `name`) / `append`(带 `references`) / `remove`(带 `memberIds`) / `reorder`(带全部 `memberIds`) |
| `expectedRevision` | ✔ | 来自 `get_playlist` |
| `requestId` | | |

### delete_playlist

删除片单本身（破坏性）；当前队列保留。

| 参数 | 必填 |
|---|---|
| `playlistId` | ✔ |
| `expectedRevision` | ✔ |
| `requestId` | |

### refresh_playlist

刷新原始引用、补充新发现的剧集（返回 `taskId`）；保留手动顺序与手动移除记录。

| 参数 | 必填 |
|---|---|
| `playlistId` | ✔ |
| `expectedRevision` | ✔ |
| `requestId` | |

## 队列

### get_queue

读手动队列（只读）：`revision`、`activeSource`、`manualPlayback{positionSeconds,volume,intent,mode}`、`queue`（分页 item，含稳定 `itemId`）。

### edit_queue

编辑手动队列（破坏性）。**不会隐式播放**。

| 参数 | 必填 | 说明 |
|---|---|---|
| `action` | ✔ | `append` / `insert_next` / `remove` / `move` / `reorder` / `clear` / `replace` |
| `references` | append/insert_next 时 ✔ | 已解析的 part 引用 |
| `itemIds` | remove/reorder 时 ✔ | `move` 只给一个；`reorder` 给全部 |
| `index` | move 时 ✔ | 零基目标位置 |
| `expectedRevision` | remove/move/reorder/clear/replace 时 ✔ | 来自 `get_queue` |
| `requestId` | | |

### load_playlist

把片单的已准备部分装入队列，并订阅剩余准备（破坏性）。

| 参数 | 必填 | 说明 |
|---|---|---|
| `playlistId` | ✔ | |
| `mode` | | 默认 `append`；`replace` 需要队列 `expectedRevision` |
| `autoplay` | | 默认 `false`；显式 `true` 播放第一个新分 P |
| `startIndex` | | 展开后分 P 的零基偏移 |
| `expectedRevision` | mode=replace 时 ✔ | |
| `requestId` | | |

## 播放

### get_status

读真实输出状态（只读）：`outputActive`、`playback{positionSeconds,volume,intent,mode}`、`source`、`recoveryPending`、`waitingForContent`、`queueRevision`、`tasks`（pending/running/failed/interrupted）。

**"在播"的唯一判据是 `outputActive: true`**。意图与实际输出可能不一致：解析资源、等待后续内容、自动恢复期间都会出现。

### control_playback

控制当前播放（写，非破坏性）。

| 参数 | 必填 | 说明 |
|---|---|---|
| `action` | ✔ | `play`(可带 `itemId` 选队列项) / `resume`(保持当前源) / `pause` / `stop` / `next` / `previous` / `seek`(带 `seconds`≥0) / `set_volume`(带 `volume` 0–100) / `set_mode`(带 `mode`) / `show_video` / `hide_video` |
| `itemId` | | `play` 指定队列项 |
| `seconds` / `volume` / `mode` | 按 action | |
| `requestId` | | next/previous 建议提供 |

`show_video`/`hide_video` 只切换桌面可见性，隐藏后仍在播。

## 准备任务

### get_task

查一个任务（带 `taskId`）或分页历史（不带）。任务与播放不依赖助手连接；应用重启会把未完成任务标为 `interrupted`。

### cancel_task

取消剩余准备（写）。已入队内容与当前播放保留。

| 参数 | 必填 |
|---|---|
| `taskId` | ✔ |
| `requestId` | |

### retry_task

重试 failed / partial / interrupted / cancelled 任务（写）；保留已有内容、避免重复分 P。

| 参数 | 必填 |
|---|---|
| `taskId` | ✔ |
| `requestId` | |

## 错误码

| code | 处理 |
|---|---|
| `Conflict` | 重读 revision / requestId 参数不一致 |
| `InvalidInput` | 参数不符 schema |
| `Accepted` | 同 ID 重试时结果未知 → 查询状态 |
| `Cancelled` | 先查状态再用同 requestId 重试 |
| `NotReady` | 应用退出中 → 重新启动应用 |
| `AuthenticationRequired` | 在 WallBili 登录后重试 |
| `RateLimited` | 停止请求，稍后再试 |
| `Network` | 检查网络后重试 |
| `InvalidResponse` / `OperationFailed` | 上游异常 → 单次重试，仍失败如实报告 |
| `unknown_tool` | 工具名不在 tools/list 中（传输层） |
| `internal_error` | 未预期异常（传输层） |
