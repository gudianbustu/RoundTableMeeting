# 实时群聊服务端实现方案

## 1. 文档定位

本文将[服务端设计](design.md)具体化为项目文件、包引用、配置、EF Core 模型、Migration、Application 接入、HTTP Controller、SignalR Hub、事件 Dispatcher、presence 和删除恢复。主题阶段顺序见[主题实现方案](../impl.md)，消息用例内部实现见[实时群聊模块实现方案](Modules/RealtimeChat/impl.md)，验证编号见[测试方案](../testPlan.md)。

本文不实现 MCP、Agent 入会、AI 回复任务、上下文游标、接管、循环保护、消息编辑删除或未读状态。

## 2. 项目和引用

沿用现有五个生产项目，不新增横向 Infrastructure 项目：

| 项目 | 本主题新增内容 |
| --- | --- |
| `RoundTableMeeting.Domain` | `MeetingMessage`、`MessageMention`、`MessageAppendedEvent`、`MessageEventDelivery`、作者/投递枚举、消息仓储契约 |
| `RoundTableMeeting.Abstraction` | 消息/历史/提及/presence DTO、消息服务、历史服务、事件消费者目录和实时 gate 契约 |
| `RoundTableMeeting.Application` | `RealtimeChatService`、内容规则、提及解析、Human 作者解析和消息 DTO 映射 |
| `RoundTableMeeting.Repository.Sqlite` | DbSet、配置、Migration、消息/事件仓储、序号分配和 delivery 租约 |
| `RoundTableMeeting` | `MeetingMessagesController`、`MeetingChatHub`、presence、gate、Dispatcher、广播消费者、OpenAPI transformer |

Application 不引用 Web、SignalR、EF Core、SQLite 或 `IHubContext`。Controller 和 Hub 不直接访问 DbContext。

## 3. 包和配置

### 3.1 服务端包

SignalR Server 能力沿用 `Microsoft.AspNetCore.App` 共享框架，不额外引用第三方实时库。若项目显式添加包，必须使用与 `net10.0` 匹配的精确版本并在 `Directory.Packages.props` 统一管理；不能在主题内引入第二套 WebSocket 框架。

### 3.2 客户端依赖提示

客户端 `@microsoft/signalr` 精确版本、`/hubs` Vite 代理和前端依赖变更以[客户端实现方案](../Client/impl.md)为准；服务端只负责确认 SignalR Server 与 `net10.0` 共享框架兼容。

### 3.3 RealtimeChat Options

固定绑定 `RealtimeChat` 配置节：

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

Options 验证：所有数值为正数；默认页大小不大于最大页大小；退避最大值不小于基础值；租约、gate 超时和退避窗口不超过合理的 `TimeSpan` 范围；正文上限至少为 1。生产配置和测试配置必须显式提供有效值，启动失败不能以零值运行。

## 4. EF Core 模型和 Migration

### 4.1 MeetingMessageConfiguration

`MeetingMessages` 字段：`ID`、`MeetingId`、`Sequence`、`AuthorParticipantId` nullable、`AuthorKind`、`AuthorDisplayName`、`Content`、`ClientSubmissionId`、`CreatedAtUtc`。

- `ID` 为 Guid 主键；`MeetingId` Restrict 外键。
- `MeetingParticipants` 增加 `(ID, MeetingId)` 候选键；参与者作者使用 `(AuthorParticipantId, MeetingId)` 复合 Restrict 外键，确保作者属于消息会议。作者类型为 Human/Agent 时非空，System 时为空。
- Domain AuthorKind 使用 `HasConversion<string>()` 存储 PascalCase `Human | Agent | System` 并增加 CHECK；Abstraction MessageAuthorKind 经 camelCase JSON 输出 `human | agent | system`，Application 显式映射。
- `(MeetingId, Sequence)` 唯一索引；Sequence CHECK 为 1 至 `9,007,199,254,740,991`。
- 参与者消息的 `(MeetingId, AuthorParticipantId, ClientSubmissionId)` 建立 `AuthorParticipantId IS NOT NULL` 过滤唯一索引；System 消息的 `ClientSubmissionId` 由 `SystemMessageCode + 稳定业务发生键` 派生，并用 `(MeetingId, AuthorKind, ClientSubmissionId)` 的 System 过滤唯一索引防止同一事实重复。
- `Content` 和 `AuthorDisplayName` 的列长度是映射防线，真实长度仍由 Domain/Application 校验。

### 4.2 MessageMentionConfiguration

`MessageMentions` 字段：`ID`、`MeetingId`、`MessageId`、`TargetParticipantId`、`TargetDisplayName`、`StartOffset`、`Length`。

- `(MessageId, StartOffset)` 唯一索引；相同 TargetParticipantId 允许多条出现记录，Application 保证范围不重叠。
- `MeetingMessages` 建立 `(ID, MeetingId)` 候选键，`MeetingParticipants` 使用既有 `(ID, MeetingId)` 候选键；Mention 分别用 `(MessageId, MeetingId)`、`(TargetParticipantId, MeetingId)` 复合 Restrict 外键，删除时先显式删 mentions。
- StartOffset >= 0、Length > 1；范围完整性由 Application 验证，SQLite CHECK 作为额外防线。

### 4.3 Event/Delivery configuration

`MessageAppendedEvents`：`ID`、`MeetingId`、`MessageId`、`Sequence`、`OccurredAtUtc`；`MeetingMessages` 建立 `(ID, MeetingId, Sequence)` 候选键，Event 使用对应复合 Restrict 外键；`MessageId` 仍唯一。

`MessageEventDeliveries`：`ID`、`EventId`、`ConsumerName`、`State`、`AttemptCount`、`AvailableAtUtc`、`LeaseId` nullable、`LeaseExpiresAtUtc` nullable、`CompletedAtUtc` nullable；`(EventId, ConsumerName)` 唯一，状态 CHECK 为 Pending/Leased/Completed/Suspended。Suspended delivery 只有显式 Resume 才重新进入 Pending。

### 4.4 Migration 步骤

1. 运行 `dotnet ef migrations add AddRealtimeChat`，指定 Repository 项目和 Web startup project。
2. 检查 Migration 新增四张消息相关表，并为既有 `MeetingParticipants` 增加 `(ID, MeetingId)` 候选键；SQLite 可能重建既有表，必须保留 Meetings/UserAccounts 外键、Human UserId 过滤唯一索引和名称唯一索引，不写样例数据。
3. 检查 Snapshot 与 DbContext 的作者、序号、事件和 delivery 模型一致。
4. 用空临时 SQLite 执行 Migration，再用已有 004 数据库执行升级，确认会议和参与者数据不变。
5. 将 Migration 文件保持 CRLF、UTF-8 无 BOM，不手工修改生成的 Snapshot 语义。

## 5. Repository 和序号分配

### 5.1 IMessageRepository

提供：`FindBySubmissionAsync`、`GetNextSequenceAsync`、`ListPageAsync`、`FindByIdAsync`、`Add`、`ListForMeetingAsync` 和 `DeleteForMeetingAsync`。所有读取投影或 NoTracking；仓储不判断当前用户。

### 5.2 IMessageEventRepository

提供创建事件 deliveries、按 `(Pending AND AvailableAtUtc <= now) OR (Leased AND LeaseExpiresAtUtc <= now)` 租取、条件完成、失败退回、删除会议 deliveries/events 和查询高水位。租取与确认必须带 ConsumerName/LeaseId 条件，避免迟到消费者覆盖新租约；完成更新匹配 0 行时，资源已删除视为终态完成，LeaseId 已更换视为失去所有权。

### 5.3 GetNextSequenceAsync

在 `IMeetingTransactionRunner` 的同一 SQLite 写事务中执行：

1. 读取当前会议最大 Sequence，空会议为 0。
2. 加一并返回，不在客户端或 Singleton 内存中分配；当前值已达到 `9,007,199,254,740,991` 时拒绝追加并抛出安全的容量异常。
3. 依赖 `(MeetingId, Sequence)` 唯一索引处理异常冲突。

Repository 不使用 `MAX` 结果后再离开事务；查询、分配和消息插入必须处于同一个写锁范围。

## 6. Application 实现

### 6.1 RealtimeChatService

服务按既有 `MeetingWorkspaceService` 风格注册为 Scoped，实现 `IMessageAppendService` 和 `IMessageHistoryService`。它接收 `ICurrentUserContext`、消息/参与者仓储、账号/会议访问契约、`IMeetingTransactionRunner`、`IMessageEventConsumerCatalog`、Options、TimeProvider 和事件唤醒器。

### 6.2 Human 发送

1. 认证检查和 Guid 幂等标识校验。
2. 进入事务，复查 Active 会议和 UserId 对应 Human Participant。
3. 对正文执行 NFC+Trim；空白失败。查询同作者同会议同 ClientSubmissionId，并以规范化正文按 Ordinal 比较：一致 Replayed，不一致 SubmissionMismatch。
4. 幂等未命中后校验最大长度、读取参与者摘要、解析 mentions。
5. 获取下一个 Sequence，创建作者显示名快照。
6. 从 `IMessageEventConsumerCatalog.ConsumerNames` 为目录中的全部消费者注册一条 delivery；本主题至少包含 `realtime-broadcast-v1`。
7. Runner 提交一次，返回 Created MessageDto；提交后唤醒 Dispatcher。

### 6.3 System 发送

系统用例传递固定 code、稳定业务发生键、显示名称和正文；Application 从 code + occurrence key 派生确定性 ClientSubmissionId。System 不执行 mentions 解析，AuthorParticipantId 为 null；仍使用同一 Sequence 和 event/delivery 流。本主题不修改 004 创建/加入用例自动生成系统消息。

### 6.4 错误映射

Application 只返回冻结的 `MessageOperationStatus` 或安全异常。Controller 将 SubmissionMismatch 映射为 `409 message_submission_mismatch`，Conflict 映射为可用同一幂等 ID 重试的 `409 message_append_conflict`，并负责其他 400/401/404/415/429/500；不返回 SQL、索引名、会议码、完整正文或参与者列表。

消息唯一约束由 Repository/Runner 转换为 `MessageAppendConflictException`。Runner 回滚并清空 ChangeTracker 后，Service 最多重新调用一次 Runner，只重查 ClientSubmissionId 以收敛为 Replayed/SubmissionMismatch；仍未找到时返回 Conflict。Application 不创建 IServiceScope，不复用污染的事务，也不无限重试。

## 7. HTTP Controller

### 7.1 路由

`MeetingMessagesController`：

- `GET /api/meetings/{meetingId:guid}/messages`
- `POST /api/meetings/{meetingId:guid}/messages`

Controller 标记 `[ApiController]`、`[Authorize]`、`[NoStoreResponse]`。POST 标记 `[RequireBrowserOrigin(true)]`，GET 不要求 Origin。

### 7.2 历史参数和响应

绑定 `beforeSequence`、`afterSequence`、`throughSequence`、`limit`。同时提交 before/after、before/through、游标为负、through 小于 after 或 limit 超界返回 `validation_failed`。

返回 `MessagePageDto`，items 按 Sequence ASC。无方向游标返回 through 或当前高水位内最新页；before 返回更早页；after 返回 after 之后且不超过 through 的最早页。响应分别含 HasMoreBefore/HasMoreAfter、first/last 和两个方向的下一游标。

### 7.3 发送响应

首次追加 `201 Created`，幂等重放 `200 OK`。当前没有单消息 GET 端点，因此不设置指向不存在资源的 `Location`。响应统一为 `SubmitMessageResultDto { Message, Replayed }`。SubmissionMismatch 为 `409 message_submission_mismatch`，瞬时追加冲突为 `409 message_append_conflict`。

### 7.4 Web 防护

发送 ResourceFilter 在模型绑定和 Application 之前执行，顺序为 Origin -> Authentication -> message rate limit -> model binding -> Application。未认证不消耗配额；限流不分配 Sequence。所有成功和错误响应 no-store/no-cache；429 带 Retry-After。

## 8. SignalR Hub

### 8.1 注册和管道

在 `ServiceCollectionExtensions` 注册 `AddSignalR`、Hub、presence、`IMeetingRealtimeGate`、消费者注册描述符/目录、Dispatcher 和广播消费者；Catalog 只保存名称到 ServiceType，不持有消费者实例。Dispatcher 每次创建独立 scope 并解析消费者。在 `Program` 中调用 `MapHub<MeetingChatHub>("/hubs/meeting-chat").RequireAuthorization().WithMetadata(new RequireBrowserOriginAttribute(false), new NoStoreResponseAttribute())`。Hub 映射位于授权已启用的管道中，metadata 必须使现有 Origin 和敏感响应头中间件覆盖 negotiate、WebSocket、SSE 和长轮询。SPA fallback 的 excluded 条件必须增加 `/hubs`，使未知 Hub 路径和失败握手保持真实 404/401/403，而不是返回 index.html。

### 8.2 SubscribeMeeting

Hub 方法只接收 Guid meetingId：

1. 取得当前 UserId，验证 Active 会议和 Human Participant。
2. 通过 gate 获取短共享租约，调用 `IMeetingChatSubscriptionService.AuthorizeAsync` 获取 ParticipantId 与 HighWatermarkSequence。
3. 清理该连接既有会议绑定，加入服务端生成的 `meeting:{meetingId:N}` 组并注册 presence。
4. 读取 registry 当前在线 ParticipantId 列表；缺席参与者由客户端视为 offline。
5. 返回在线 ParticipantId 列表和 highWatermarkSequence；共享租约在方法返回前释放，不返回服务端连接代次。

Hub 不接收作者 participantId、作者类型、组名或消息正文；当前阶段发送使用 HTTP。

已认证但非成员或 Deleting 会议抛出 `HubException("meeting_not_found")`；其他预期业务拒绝同样只使用稳定 code，不把内部消息或资源资料发送给客户端。

### 8.3 断开和删除

`UnsubscribeMeeting`、`OnDisconnectedAsync` 和删除终止均调用 presence registry 的幂等释放。删除通知发送后从会议组移除或终止连接，旧连接再次调用任何方法都必须重新通过 Active/成员检查。

## 9. 事件 Dispatcher 和 Gate

### 9.1 Dispatcher

`MessageEventDispatcherService`：

1. 启动扫描 AvailableAt 已到的 Pending 和租约过期 Leased delivery。
2. 每批在独立短事务中租取 batchSize 条。
3. 在事务外创建独立 DI scope，按 Catalog 的 ServiceType 解析消费者并调用；Catalog/HostedService 不持有 Scoped 实例。
4. 成功按 LeaseId 条件完成；资源已被删除而匹配 0 行视为完成，LeaseId 已变化视为失去所有权；失败回 Pending，按有上限的指数退避计算 AvailableAt，达到 `EventRetryAlertAfterAttempts` 后转 Suspended 并记录告警，禁止无限热重试；显式 Resume 后才继续。
5. 周期性唤醒，进程重启后重新处理过期租约。

Dispatcher 不持有 DbContext 跨 await，不在一条长事务中调用 SignalR。

### 9.2 RealtimeGate

`MeetingRealtimeGate` 实现 `IMeetingRealtimeGate`，以 MeetingId 维护共享读租约和独占关闭状态：

- 广播消费者和 Subscribe 单次方法调用获取短共享租约，连接存续期不持有；
- 删除请求独占时先进入 writer-priority Closing，立即拒绝新的共享租约，再等待存量共享租约归还；
- 独占等待上限为 `GateCloseTimeoutSeconds`，超时释放 Closing 并返回 `meeting_delete_busy`；
- 独占租约 Dispose 且未提交时释放独占权，`CommitClosed()` 后永久关闭；
- 物理清理完成后 `Forget(meetingId)` 释放永久关闭的内存项；
- 进程重启后内存 gate 清空，但所有入口第一次仍检查数据库 Active 状态。

Gate 只协调实时外部行为，不替代数据库删除屏障或授权。

## 10. Presence

`MeetingPresenceRegistry` 为 Singleton，仅存储 connectionId、meetingId、participantId 和取消/宽限期任务。相同 ParticipantId 多连接时只在第一条和最后一条连接变化时广播。服务重启不恢复 presence；Hub 重连后重新注册。

## 11. 删除恢复扩展

修改现有 `MeetingWorkspaceService.DeleteAsync` 和 `MeetingDeletionRecoveryService`：

1. 删除用例先做快速权限/确认预检，再请求 writer-priority gate 独占关闭租约；超时返回 `meeting_delete_busy`，独占成立后才开启短事务，事务内重新验证并提交 Active -> Deleting，失败释放租约，成功 CommitClosed 并通知恢复器。
2. 恢复器在每次清理轮次发送 `meetingDeleted`、终止会议连接并等待连接注册释放；服务重启后以 404 兜底。
3. 在新事务内删除 deliveries、events、mentions、messages。
4. 调用目录 004 已有参与者、回执和会议删除清理。
5. 任一步失败保持 Deleting，记录安全摘要，启动或周期恢复继续；物理清理成功后 Forget gate 项。

删除协调器不把 MessageEventDispatcher 的内存队列作为事实；所有待处理资源均由数据库扫描恢复。

## 12. 日志和可观测性

结构化日志只记录 MeetingId、ParticipantId、MessageId、Sequence、EventId、ConsumerName、AttemptCount、状态和 traceId。禁止记录正文、会议码、Cookie、Origin 值、完整请求体和参与者名称全集。

指标可以记录消息追加成功/失败计数、广播延迟、delivery 重试数、连接数和历史页耗时，但标签必须限制为安全枚举和聚合维度，不使用 MeetingId、ParticipantId 或正文作为高基数标签。

## 13. 命令基线

~~~powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet tool run dotnet-ef migrations add AddRealtimeChat --project Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj --startup-project Server/RoundTableMeeting/RoundTableMeeting.csproj --context RoundTableMeetingDbContext --output-dir Migrations
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug
~~~

客户端与 OpenAPI：

~~~powershell
Set-Location Client/roundTableMeeting
pnpm install --frozen-lockfile
pnpm generate:api
pnpm test
pnpm build
Set-Location ../..
~~~

## 14. 完成门

- 空库和 004 既有数据库都能完成 `AddRealtimeChat` Migration。
- Human、Agent 预留和 System 都映射到统一消息/Sequence/mentions/event 契约；当前只开放 Human HTTP 入口和 System 内部用例。
- HTTP 历史、发送、Origin、限流、ProblemDetails、缓存头、OpenAPI 和 Kiota 通过。
- SignalR 授权订阅、presence、多连接、删除通知和重连高水位通过。
- Dispatcher 广播失败可重试，消息事务不依赖内存回调；删除后无迟到广播。
- 旧 002/003/004 构建、测试、认证和会议工作台回归通过。
