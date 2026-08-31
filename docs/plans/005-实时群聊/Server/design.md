# 实时群聊服务端设计

## 1. 文档定位

本文定义实时群聊主题在服务端的工程分层、消息持久化、统一追加边界、历史接口、SignalR 实时适配、presence、持久化事件分发和会议删除扩展。主题范围与统一消息契约以[主题总体设计](../design.md)为准；具体应用用例和结果状态以[实时群聊模块设计](Modules/RealtimeChat/design.md)为准。

本主题在现有模块化单体中扩展消息能力，不新增 AI 专用项目或消息通道。浏览器、未来 MCP 和内部系统消息都必须进入相同的 Application 服务和 Repository 事务边界。

## 2. 服务端目标

服务端完成后应能够：

1. 以统一实体保存人类、Agent 和系统消息。
2. 为每个会议分配严格递增且唯一的消息序号；正常追加事务不产生服务端断号，同时对实时投递缺口提供客户端补齐能力。
3. 把提及解析为稳定参与者引用，与消息在同一事务提交。
4. 提供完整历史、向前增量和浏览器发送 HTTP API。
5. 通过 SignalR 向授权成员推送已提交消息和 presence 变化。
6. 使用持久化追加事件隔离消息事务、实时广播和未来 AI 任务处理。
7. 在删除屏障建立后拒绝所有消息和实时入口，并可重入地清理新增资源。

## 3. 项目职责

| 项目 | 本主题职责 |
| --- | --- |
| `RoundTableMeeting.Domain` | `MeetingMessage`、`MessageMention`、`MessageAppendedEvent`、作者类型、内容规则和消息仓储契约 |
| `RoundTableMeeting.Abstraction` | HTTP/SignalR DTO、结果状态、`IMessageAppendService`、历史服务、事件消费者目录、实时 gate 和 presence 契约 |
| `RoundTableMeeting.Application` | 作者解析、成员与删除屏障校验、消息追加编排、提及解析、历史投影和幂等结果 |
| `RoundTableMeeting.Repository.Sqlite` | EF Core 映射、Migration、消息查询、序号分配、事件租取和消费确认 |
| `RoundTableMeeting` | MessagesController、MeetingChatHub、Cookie/Origin/限流、事件后台服务、presence 注册表、删除实时通知和组合根 |

Domain、Application 和 Abstraction 不引用 SignalR、`HubConnectionContext`、`HttpContext`、EF Core 或 SQLite。Hub 和 Controller 不直接持有 DbContext。

## 4. 持久化模型

### 4.1 MeetingMessages

字段固定为：`ID`、`MeetingId`、`Sequence`、`AuthorParticipantId` nullable、`AuthorKind`、`AuthorDisplayName`、`Content`、`ClientSubmissionId`、`CreatedAtUtc`。

约束：

- `ID` 为 `Guid` 主键。
- `MeetingId` Restrict 外键指向 `Meetings`；删除协调器显式清理。
- 参与者作者使用复合 Restrict 外键 `(AuthorParticipantId, MeetingId) -> MeetingParticipants(ID, MeetingId)`；`MeetingParticipants` 为 `(ID, MeetingId)` 建立候选键。`human | agent` 必须非空，`system` 必须为空，数据库不能接受其他会议的参与者作为作者。
- Domain `AuthorKind` 使用 PascalCase 字符串存储并加 CHECK，只允许 `Human | Agent | System`；传输层使用独立的 `MessageAuthorKind` 枚举，经 camelCase JSON 转换输出 `human | agent | system`，Application 显式映射两种枚举。
- `(MeetingId, Sequence)` 唯一；`Sequence` 必须在 1 至 `9,007,199,254,740,991` 之间。
- 参与者消息对 `(MeetingId, AuthorParticipantId, ClientSubmissionId)` 建立 `AuthorParticipantId IS NOT NULL` 过滤唯一索引；系统消息使用 `(MeetingId, AuthorKind, ClientSubmissionId)` 的 `AuthorKind = System` 过滤唯一索引。System 的确定性键由消息 code 与稳定业务发生键共同派生，避免把不同参与者或不同业务事实的同类系统消息错误合并；两个过滤索引都不受复合索引 NULL 语义影响。
- `AuthorDisplayName` 只由追加事务内解析到的参与者当前显示名称赋值，不接受调用方提交；它是历史快照，不是未来修改参与者名称时需要持续一致的数据库约束。系统消息使用固定服务端名称。
- 内容长度由 Domain/Application 校验，SQLite CHECK 只作为额外防线。

本主题不提供单条删除，因此按 `MAX(Sequence) + 1` 分配不会复用旧序号。分配必须发生在现有 `IMeetingTransactionRunner` 的 `BEGIN IMMEDIATE` 事务中；Repository 提供只在该事务中调用的 `GetNextSequenceAsync`。

### 4.2 MessageMentions

字段固定为：`ID`、`MeetingId`、`MessageId`、`TargetParticipantId`、`TargetDisplayName`、`StartOffset`、`Length`。

- 使用复合 Restrict 外键 `(MessageId, MeetingId) -> MeetingMessages(ID, MeetingId)` 和 `(TargetParticipantId, MeetingId) -> MeetingParticipants(ID, MeetingId)`；两个 principal 都建立对应候选键。会议清理先删提及再删消息，数据库不能接受跨会议消息或目标参与者。
- `(MessageId, StartOffset)` 唯一，并对 `(MessageId, StartOffset, Length)` 建立范围一致性约束；Application 保证同一消息内范围不重叠。相同 TargetParticipantId 可以出现多次，供客户端完整高亮。
- `StartOffset >= 0`、`Length > 1`，范围必须落在规范化后的消息正文内。
- Mention 只保存结构化目标，不保存 AI 任务状态或投递进度。

### 4.3 MessageAppendedEvents

字段固定为：`ID`、`MeetingId`、`MessageId`、`Sequence`、`OccurredAtUtc`。

事件与消息在同一事务创建。使用复合 Restrict 外键 `(MessageId, MeetingId, Sequence) -> MeetingMessages(ID, MeetingId, Sequence)`，消息表建立对应候选键；`MessageId` 仍唯一，确保一条消息只有一个追加事件，并防止事件的会议或序号与消息不一致。事件载荷由处理时查询消息读模型生成，不复制正文到第二份 JSON；删除会议时事件随消息清理。

### 4.4 MessageEventDeliveries

每个注册消费者对事件有独立 delivery：`ID`、`EventId`、`ConsumerName`、`State`、`AttemptCount`、`AvailableAtUtc`、`LeaseId` nullable、`LeaseExpiresAtUtc` nullable、`CompletedAtUtc` nullable。

- `(EventId, ConsumerName)` 唯一。
- 状态固定为 `Pending | Leased | Completed | Suspended`；失败回到 Pending 并增加 AttemptCount，达到 `EventRetryAlertAfterAttempts` 后转为 Suspended、记录结构化告警并等待 `IMessageEventRecoveryService.ResumeAsync` 显式恢复，不进行无限热重试。Suspended 不是消息业务状态，恢复后仍从同一 delivery 继续。
- 当前主题注册 `realtime-broadcast-v1` 消费者；后续 AI 主题注册自己的版本化消费者名。
- 新增消费者只为注册之后的新事件创建 delivery，不回溯旧事件；移除或重命名消费者前必须先迁移、完成或清理其未完成 deliveries，不能让 Dispatcher 遇到无法解析的 ConsumerName。需要回溯或停用时必须由对应主题明确设计迁移。
- delivery 的租约和重试只是后台分发基础，不等同于 AI 连接租约或 AI 回复任务状态。

该结构保证实时广播失败不会影响消息事务，未来 AI 任务创建也不会因为进程在消息提交后崩溃而永久漏掉提及。

### 4.5 Migration

Migration 命名建议为 `AddRealtimeChat`，同时更新 DbContext、Snapshot 和 SQLite 索引。Migration 不写示例消息，不为已有参与者补造系统消息，也不把 Demo 数据迁入正式表。

## 5. 消息内容与提及解析

### 5.1 内容规则

`MeetingMessage.NormalizeContent`：

1. null 按空值处理。
2. 转为 Unicode NFC。
3. 去除首尾空白，保留内部空格、换行和标点。
4. 规范化后长度必须为 1 至配置 `MaxContentLength`，默认 16,384 个 UTF-16 代码单元。

配置节固定为：

~~~json
{
  "RealtimeChat": {
    "MaxContentLength": 16384,
    "HistoryPageSize": 100,
    "MaxHistoryPageSize": 200,
    "PresenceDisconnectGraceSeconds": 5,
    "GateCloseTimeoutSeconds": 5,
    "EventDispatcherBatchSize": 50,
    "EventLeaseSeconds": 30,
    "EventRetryBaseSeconds": 2,
    "EventRetryMaxSeconds": 300,
    "EventRetryAlertAfterAttempts": 10,
    "SendRateLimit": {
      "WindowSeconds": 60,
      "PerUserLimit": 120,
      "PerParticipantLimit": 60,
      "PerSourceLimit": 200
    }
  }
}
~~~

所有值必须为正数，默认页大小不能超过最大页大小，退避最大值不能小于基础值，Options 使用 `ValidateOnStart`。

### 5.2 提及解析器

`IMessageMentionParser` 接收规范化正文和当前会议全部参与者摘要，返回每一次合法提及的目标与文本范围；业务消费者再按 TargetParticipantId 去重任务目标：

- 只识别 ASCII `@` 起始标记。
- 在每个 `@` 后对正文候选片段执行 NFC 后 `ToUpperInvariant()`，再与参与者已保存的 `NormalizedDisplayName` 使用 `StringComparison.Ordinal` 做最长匹配；该函数与目录 004 的名称唯一约束完全一致，不使用当前文化或 `OrdinalIgnoreCase` 的另一套规则。
- 相同长度且规范化结果冲突理论上已被数据库名称唯一约束阻止；若读取到异常数据，解析器不猜测目标并记录安全诊断。
- 目标名称结束后必须是正文末尾、空白或 Unicode 标点；否则视为普通文本。
- 同一目标多次出现时保存每个合法且不重叠的范围；多个目标分别保存。未来任务消费者按 TargetParticipantId Distinct 去重业务目标。
- 系统消息跳过提及解析；参与者消息无论作者是 Human 还是 Agent 都执行相同解析。

浏览器可提交可选候选范围，但范围必须相对请求中实际发送的 NFC+Trim 正文计算；Application 对正文执行同一规范化后重新读取目标参与者并验证范围。候选范围不得重叠。候选无效时不信任目标 ID，继续用服务端解析器处理正文；候选元数据错误本身不使普通文本消息失败。

## 6. Application 与事务

### 6.1 浏览器作者解析

`HumanMessageAuthorResolver` 使用 `ICurrentUserContext.UserId` 查询 `MeetingId + UserId` 的 Human 参与者。找不到 Active 会议或参与关系时统一返回 `MeetingNotFound`。Controller 不能从请求接收作者参与者 ID。

后续 MCP 主题实现 `AgentMessageAuthorResolver`，根据受信连接会话解析绑定的 Agent 参与者；两种 resolver 最终都生成内部 `ResolvedMessageAuthor` 并调用相同 `IMessageAppendService`。

### 6.2 追加事务

事务内固定执行：

1. `FindActiveByIdAsync` 和作者参与者复查。
2. 对非空正文执行 NFC+Trim，得到用于幂等比较的规范化正文；空值或空白立即返回 ValidationFailed。
3. 按 `MeetingId + AuthorParticipantId + ClientSubmissionId` 查询重放，并用规范化正文执行 Ordinal 比较；相同返回 Replayed，不同返回 SubmissionMismatch。幂等未命中后才执行最大长度校验、读取参与者列表并解析提及。
4. 获取下一个 `Sequence`。
5. 从 `IMessageEventConsumerCatalog` 读取当前已注册的稳定消费者名，为每个名称注册独立 delivery；目录必须拒绝空名、重复名和运行期变化。
6. 由 TransactionRunner 唯一调用一次 `IUnitOfWork.CommitAsync` 并提交事务。

幂等重放返回原 `MessageDto` 和 `Replayed` 状态，不再次发布事件。相同幂等标识但正文不同返回 `SubmissionMismatch`，防止客户端误把旧标识用于新消息。

### 6.3 历史查询

历史读取使用 NoTracking 投影，严格按 `Sequence ASC` 返回。`throughSequence` 若提供则作为包含式上界，必须大于等于 0。查询模式：

- 无方向游标：返回 `throughSequence` 或当前最后序号以内的最新一页，但响应 items 仍按升序排列。
- `beforeSequence`：返回目标序号之前最近一页，供加载更早历史。
- `afterSequence`：返回目标序号之后、`throughSequence` 以内最早一页，供重连补齐。

请求同时带两个方向游标、`beforeSequence + throughSequence`、游标小于 0、`throughSequence < afterSequence` 或 limit 越界时返回 `validation_failed`。无方向最新页和 before 页的 `HasMoreAfter` 固定为 false；只有 after+through 追平模式计算 HasMoreAfter。响应包含继续向前或向后请求所需游标，不使用数据库 offset 分页。

## 7. HTTP API

`MeetingMessagesController` 使用 `[ApiController]`、`[Authorize]`、`[Route("api/meetings/{meetingId:guid}/messages")]` 和 `[NoStoreResponse]`。

| 方法 | 路径 | 成功 | Origin/正文 |
| --- | --- | --- | --- |
| `GET` | `/api/meetings/{meetingId}/messages` | `200 MessagePageDto` | 无 |
| `POST` | `/api/meetings/{meetingId}/messages` | 首次 `201`、重放 `200` | `RequireBrowserOrigin(true)` |

POST 请求：`clientSubmissionId`、`content` 和可选 mention candidates。`clientSubmissionId` 必须为非空 Guid。成功响应固定为 `SubmitMessageResultDto { message, replayed }`，不回显未验证候选元数据；Application 的 `MessageOperationResult<MessageDto>` 只在内部表达状态，Controller 负责映射为该传输 DTO。

错误契约：

- `400 validation_failed`；
- `401 unauthenticated`；
- `404 meeting_not_found`；
- `409 message_submission_mismatch`；
- `409 message_append_conflict`；
- `415 unsupported_media_type`；
- `429 message_send_rate_limited`，含 `Retry-After`；
- `500 internal_error`。

OpenAPI 必须声明 200/201 双成功、字符串作者枚举、long sequence、mentions 数组和错误映射。Kiota 生成目录继续使用 UTF-8 无 BOM、CRLF，并由现有 staging 流程替换。

## 8. SignalR

### 8.1 Hub 路由与认证

Hub 路由固定为 `/hubs/meeting-chat`，使用现有 Cookie Authentication 和默认授权策略，不允许匿名连接。`MapHub` 必须显式附加 `RequireBrowserOriginAttribute(false)` 与 `NoStoreResponseAttribute` endpoint metadata，使现有 Origin 和敏感响应头中间件同时覆盖 negotiate、WebSocket、SSE 和长轮询请求；不能只在 Hub 方法内部检查。生产同源托管；开发由 Vite 代理 `/hubs` 和 WebSocket。首版不开放跨源 credentialed CORS。

### 8.2 订阅方法

`MeetingChatHub.SubscribeMeeting(meetingId)`：

1. 从当前用户上下文取得 UserId。
2. 通过 Active 会议和 Human 参与关系校验。
3. 一个连接只绑定一个会议；切换时先离开旧组并更新 presence 计数。
4. 加入名称仅由服务端生成的 `meeting:{meetingId:N}` 组。
5. 返回当前在线 ParticipantId 列表和数据库中当前最后消息序号 `highWatermarkSequence`；缺席的会议参与者视为 offline，空历史高水位固定为 0。该高水位与先加入组的顺序共同建立有界补齐边界。

`UnsubscribeMeeting` 和 `OnDisconnectedAsync` 幂等释放绑定。Subscribe 共享租约只在 Active/成员复查、加入组、登记 presence、读取高水位的单次方法调用期间持有，方法返回前释放，连接存续期不持有 gate 租约。客户端不能自选组名、参与者 ID 或作者类型。已认证但非成员或 Deleting 会议的 Hub 调用抛出只含稳定 code `meeting_not_found` 的 `HubException`，不返回会议存在性、参与者资料或内部 detail。

### 8.3 实时事件

事件使用小写 camelCase 方法名和结构化 DTO：

- `messageAppended(MessageDto)`；
- `participantPresenceChanged(ParticipantPresenceDto)`；
- `meetingDeleted(MeetingDeletedDto)`。

Hub 不承担消息历史、发送确认或恰好一次投递。单条事件可重复；事件顺序不能替代 `MessageDto.Sequence`。

## 9. 持久化事件分发

`MessageEventDispatcherService` 为 BackgroundService。`IMessageEventConsumerCatalog` 在应用启动时从消费者注册描述符建立不可变的 `ConsumerName -> ServiceType` 目录，验证名称非空、唯一且版本化；ConsumerName 只存在于描述符/delivery，不在消费者实例中重复声明。Catalog 不持有消费者实例。消息追加只依赖目录提供的名称集合，Dispatcher 每次消费在独立 DI scope 中按 ServiceType 解析具体消费者，避免把 Scoped 消费者提升为 Singleton：

1. 启动和周期扫描 `(Pending AND AvailableAtUtc <= now)` 或 `(Leased AND LeaseExpiresAtUtc <= now)` 的 delivery。
2. 每批在短事务中租取有限数量，设置新的 LeaseId/到期时间。
3. 在事务外调用注册消费者。
4. 成功后用 LeaseId 条件更新为 Completed；更新 0 行且事件或 delivery 已被会议删除清理时视为终态完成，LeaseId 已更换则视为当前 worker 失去所有权。失败增加 AttemptCount，使用有上限的指数退避设置 AvailableAtUtc；达到告警阈值后记录结构化告警并降低重试频率，但不进入永久死信状态，以保留恢复能力。
5. 服务端重启后租约过期可再次投递。

`RealtimeMessageBroadcastConsumer` 查询完整 `MessageDto` 后调用 `IHubContext<MeetingChatHub>`。即使事件在无人在线时被标记完成也不丢失事实，后来的客户端通过历史 API 读取。重复广播由客户端去重。

消费者异常日志只记录 EventId、MessageId、MeetingId、ConsumerName、AttemptCount 和异常类型，不记录正文。

## 10. Presence

`MeetingPresenceRegistry` 为 Singleton 进程内注册表，键统一使用 ParticipantId；本主题注册 Human 浏览器连接，后续 MCP 主题可注册 Agent 连接而不改变 presence DTO：

- 映射 connectionId -> meetingId/participantId；
- 维护 participantId 的活动连接集合；
- 首连接触发 online；最后连接断开后建立可取消的宽限期任务，新连接到来时取消旧任务，任务到期后在同步边界内复查仍为 0 才广播 offline；
- 相同参与者另一标签页仍在线时不广播 offline；
- 会议删除时按 meetingId 枚举连接并发送删除事件，随后从组移除或主动终止。

服务端进程重启不恢复 presence；这是短暂连接状态，不是数据库事实。单实例首版不引入 Redis backplane，若未来扩展多实例必须同时重做 presence、SignalR groups 和删除连接终止测试。

## 11. 删除协调扩展

现有创建者删除用例和 `MeetingDeletionRecoveryService` 共同扩展删除流程，统一使用 `IMeetingRealtimeGate`：

1. `MeetingWorkspaceService.DeleteAsync` 请求会议级独占关闭租约；请求立即进入 writer-priority Closing 并拒绝新共享租约，最多等待 `GateCloseTimeoutSeconds`。超时返回 `409 meeting_delete_busy`、释放 Closing，会议保持 Active；独占成立后才开启短写事务，确认名称、权限或事务失败时 Dispose 释放租约，成功后调用 `CommitClosed()` 并通知删除恢复器。
2. `MeetingDeletionRecoveryService` 收到 signal 或扫描到 Deleting 后调用 `IMeetingRealtimeTermination`，向当前注册连接发送 `meetingDeleted` 并阻止/移除会议组连接；失败保持 Deleting 并在后续恢复轮次重试。服务重启后旧连接已经消失，客户端以历史/详情 404 作为兜底。
3. 在新的 `IMeetingTransactionRunner` 事务内按外键顺序删除 `MessageEventDeliveries`、`MessageAppendedEvents`、`MessageMentions`、`MeetingMessages`。
4. 再执行会议工作台已有的参与者、创建回执和会议清理。

清理查询全部按 MeetingId，重复执行时不存在的数据视为完成。聊天事件消费者只在单次读消息 DTO + 广播期间持有共享租约；删除在等待存量共享租约时尚未开启 SQLite 写事务，独占成立后才提交短写事务，避免 gate 与数据库锁反向等待。删除租约 CommitClosed 后不再授予新共享租约。物理清理完成后调用 `Forget(meetingId)` 释放内存字典项；进程重启后 gate 默认开放，但第一次 Active 检查会拒绝数据库中仍为 Deleting 的会议。

## 12. 错误、日志与安全

- 消息正文不进入 Serilog 请求属性、ProblemDetails、异常消息或指标标签。
- Hub 方法错误使用稳定 code 返回给调用方，不发送内部堆栈。
- 消息 API 继续经过 SensitiveResponseHeadersMiddleware；SignalR 协商响应不缓存。
- SignalR negotiate、WebSocket 和长轮询握手必须校验浏览器 Origin 是否属于 `Authentication.AllowedOrigins`；仅关闭 CORS 不足以替代带 Cookie WebSocket 的 Origin 校验。
- POST 发送先经过 Origin、Authentication 和资源级限流，再模型绑定和 Application；未认证请求不消耗参与者配额。
- 来源地址只使用可信代理处理后的 RemoteIpAddress；未知转发头不可信。
- 客户端正文按普通文本输出；服务端不接受 HTML 渲染模式。

## 13. 服务端非目标

本主题服务端不实现 MCP 工具、Agent 连接、AI 回复任务、上下文游标、任务租约、循环保护、未读状态、消息编辑删除、全文检索和附件。`MessageEventDeliveries` 是通用持久化后处理基础，不提前包含任何 AI 状态机字段。
