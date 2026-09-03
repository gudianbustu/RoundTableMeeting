# 实时群聊主题实现方案

## 1. 文档定位

本文把[实时群聊主题设计](design.md)拆解为可执行的服务端、客户端、数据库、实时连接、事件分发、生成代码和联调步骤。服务端具体文件与管道见[服务端实现方案](Server/impl.md)，消息 Application 模块见[服务端模块实现方案](Server/Modules/RealtimeChat/impl.md)，客户端工程见[客户端实现方案](Client/impl.md)，客户端模块、页面和组件见各目录下的 `impl.md`，验证矩阵见[测试方案](testPlan.md)。

本主题是统一消息基础设施的第一次正式实现。当前实现入口是浏览器人类参与者，但领域模型、作者类型、历史接口、持久化事件和实时广播必须直接兼容后续 Agent 入口。本文不实现 MCP、AI 回复任务、上下文裁剪、接管、循环保护或未读状态。

## 2. 实施前提

### 2.1 已有基线

直接扩展 002 系统框架、003 账号访问与用户管理、004 会议工作台已经建立的工程：

- 服务端：`.NET 10`、ASP.NET Core Controller、Application/Domain/Abstraction/Repository.Sqlite 分层、EF Core SQLite、显式 `IUnitOfWork`、`IMeetingTransactionRunner`、ProblemDetails、Serilog、OpenAPI 和 Scalar。
- 客户端：React、TypeScript、Vite、Ant Design、React Router、Kiota、公共 API Client、统一 `ApiError` 和 Vitest。
- 会议能力：`MeetingId`、`ParticipantId`、`ParticipantType`、成员授权、会议 Active/Deleting 屏障和可重入删除恢复器。
- 开发联调：Vite 以 `http://localhost:5173` 提供页面并代理 `/api` 到 ASP.NET Core；本主题增加 `/hubs` WebSocket/长轮询代理。
- 生产形态：ASP.NET Core 同源托管 Vite 构建产物，Cookie 认证和 AllowedOrigins 继续作为浏览器身份边界。

本主题不新建横向 Infrastructure 项目，不修改通用 `IUnitOfWork`，不复制会议授权，不在 Controller、Hub 或客户端组件中直接访问数据库。

### 2.2 交付物

- `MeetingMessage`、`MessageMention`、`MessageAppendedEvent`、`MessageEventDelivery` 领域实体和仓储契约。
- 消息作者类型 `Human | Agent | System`、事件投递状态和消息操作结果。
- `MeetingMessages`、`MessageMentions`、`MessageAppendedEvents`、`MessageEventDeliveries` SQLite 映射、索引、CHECK 约束和 `AddRealtimeChat` Migration。
- 统一 `IMessageAppendService`、`IMessageHistoryService`、提及解析器和 Human 作者解析器。
- 消息历史和发送 HTTP API、Cookie/Origin/限流/禁缓存保护。
- `MeetingChatHub` 会议组订阅、消息推送、presence 和删除通知。
- 持久化事件 Dispatcher、实时广播消费者、消息事件与会议删除协调器的集成。
- `meetingWorkspace` 中的消息历史、增量合并、SignalR 连接、发送幂等重试、提及候选和在线状态 UI。
- Domain/Application/Repository/Architecture/HTTP/SignalR/事件分发/OpenAPI/Kiota/Vitest/浏览器验收测试。

## 3. 阶段总览

| 阶段 | 目标 | 主要交付物 | 完成门 |
| --- | --- | --- | --- |
| 0 | 冻结跨端契约 | DTO、错误码、作者类型、序号、游标、事件和配置 | 与 design/testPlan 一致，无 AI 专用消息模型 |
| 1 | 依赖和工程接入 | SignalR 客户端依赖、服务端 Hub 组合根、Vite `/hubs` 代理 | 依赖版本精确、开发握手可达、未改变旧认证边界 |
| 2 | 领域和持久化 | 消息、提及、事件、delivery 实体、映射、Migration | 空库迁移、唯一索引、CHECK、外键和顺序约束通过 |
| 3 | 统一追加核心 | 内容规范化、提及解析、作者 resolver、序号分配、幂等追加 | Application fake 和并发事务测试通过 |
| 4 | 历史和 HTTP | 历史游标、发送 API、ProblemDetails、Origin、限流和 no-store | HTTP 契约与成员/删除边界通过 |
| 5 | SignalR 和 presence | Hub 订阅、会议组、presence 注册表、删除通知 | 授权订阅、事件去重、断开和多连接测试通过 |
| 6 | 持久化事件分发 | Dispatcher、租约、实时广播消费者、删除 gate | 广播失败可重试，删除后无迟到广播 |
| 7 | OpenAPI/Kiota 和客户端 | 生成模型、消息区域、编辑器、时间线、重连补齐 | 生成、Vitest、类型检查和真实 API 页面通过 |
| 8 | 集成、浏览器和回归 | 双浏览器流程、断网恢复、删除和发布验证 | P0/P1 全部通过，主题可冻结验收 |

## 4. 阶段实施步骤

### 4.1 阶段 0：实现契约冻结

1. 从 `design.md` 固定 `MessageDto`、`SubmitMessageResultDto`、`MessagePageDto`、`SubmitMessageRequest`、`MessageAuthorDto`、`MessageMentionDto`、`ParticipantPresenceDto` 和 `MeetingDeletedDto`。
2. 固定作者传输值为小写 `human | agent | system`，不允许整数枚举；内部 Domain 枚举可以使用 PascalCase 字符串转换。
3. 固定 `MeetingMessage.Sequence` 为会议内从 1 开始的 `long`，上限为 `9,007,199,254,740,991`，`(MeetingId, Sequence)` 唯一；服务端不使用客户端时间排序。
4. 固定 `ClientSubmissionId` 为浏览器发送的非空 Guid；相同会议、同一作者和同一 ID 的正文一致时返回原消息，正文不一致时返回 `message_submission_mismatch`。
5. 固定历史方向参数：`beforeSequence`、`afterSequence`、可选 `throughSequence` 和 `limit`；before+after、before+through 均非法。
6. 固定事件与消息同事务保存，实时广播和后续 AI 处理使用独立 `ConsumerName` delivery。
7. 固定消息正文先 NFC+Trim，保留内部换行；首版默认 `MaxContentLength = 16384` 个 UTF-16 代码单元。
8. 固定 `GET/POST /api/meetings/{meetingId}/messages`、`/hubs/meeting-chat`、`SubscribeMeeting`、`UnsubscribeMeeting` 和事件名称。
9. 固定错误 code：`validation_failed`、`unauthenticated`、`meeting_not_found`、`meeting_delete_busy`、`message_submission_mismatch`、`message_append_conflict`、`message_send_rate_limited`、`origin_not_allowed`、`unsupported_media_type`、`internal_error`。

验收：本主题各 impl/testPlan 文档、OpenAPI 注释和客户端类型使用同一字段名称、大小写和状态码。

### 4.2 阶段 1：依赖和工程接入

1. 在客户端 `package.json` 增加精确版本的 `@microsoft/signalr`，不得使用 `^` 或 `~`；版本必须与 ASP.NET Core SignalR 运行时兼容，并同步 `pnpm-lock.yaml`。
2. 在 `vite.config.ts` 增加 `/hubs` 代理，开发环境保持 `changeOrigin: false`，允许 WebSocket；不得把 Cookie 或 Origin 由 JavaScript 手工拼接。
3. 服务端注册 SignalR、不可变 `IMessageEventConsumerCatalog`、`MeetingPresenceRegistry`、`MessageEventDispatcherService`、Suspended delivery 的受信恢复服务和消息 Application 服务。
4. 在现有 `SensitiveResponseHeadersMiddleware` 范围中纳入消息 HTTP API；`MapHub` 显式 RequireAuthorization 并附加 `RequireBrowserOriginAttribute(allowMissingOriginForGet: true)` 和 `NoStoreResponseAttribute` endpoint metadata，使 negotiate/写入请求执行 AllowedOrigins 校验，同源 SSE/Long Polling GET 在缺失 Origin 时可继续且显式外部 Origin 仍拒绝；SPA fallback 明确排除 `/hubs`。
5. 组合根配置 `RealtimeChat` Options，启动时校验正数、默认页大小、最大页大小、正文上限、presence 宽限期、gate 超时、事件租约/退避/告警阈值和限流窗口。
6. 先添加最小空 Hub 和匿名/非成员拒绝测试，确认不影响 002/003/004 既有 HTTP 路由，再进入消息实现。

### 4.3 阶段 2：领域和 SQLite 持久化

1. 在 Domain 新增继承 `BaseEntity` 的 `MeetingMessage`、`MessageMention`、`MessageAppendedEvent` 和 `MessageEventDelivery`，确保兼容现有 `IUnitOfWork.RegisterAdd/Edit/Delete(IEntity<Guid>)`。
2. 新增 Domain `MessageAuthorKind`/`MessageEventDeliveryState` 和 Abstraction `MessageAuthorKind` 枚举；Domain 按 PascalCase 存储，Abstraction 按 camelCase 传输，Application 显式映射。
3. 新增消息、事件和历史仓储接口；接口只返回领域对象或投影，不暴露 EF 查询类型。
4. 在 Repository.Sqlite 增加 DbSet、实体配置和 `AddRealtimeChat` Migration。
5. 建立 `(MeetingId, Sequence)`、参与者提交和 System 提交的两个过滤幂等唯一索引、`(MessageId, StartOffset)` 提及出现唯一索引、`EventId` 唯一索引和 `(EventId, ConsumerName)` 唯一索引。
6. 采用复合 Restrict 外键保护会议隔离：Message 作者、Mention 的消息/目标参与者、AppendedEvent 的消息/会议/序号必须来自同一会议；删除协调器先清理 deliveries、events、mentions、messages，再清理目录 004 资源。
7. 为作者类型、delivery 状态、消息 Sequence 正数、系统作者与 participantId 空值关系增加 SQLite CHECK；长度和复杂规则仍由 Domain/Application 负责。
8. Migration 不写样例消息、不为已有参与者补造系统消息、不修改目录 004 的历史会议数据。

验收：独立临时 SQLite 可从空库迁移；重复序号、幂等 ID、事件 delivery、mention 目标和非法作者组合均被数据库拒绝。

### 4.4 阶段 3：统一追加核心

1. 实现 `MeetingMessage.NormalizeContent` 和 `MessageMentionParser`，统一使用 NFC、Trim、不区分大小写和参与者名称最长匹配。
2. 实现 `HumanMessageAuthorResolver`，从 `ICurrentUserContext.UserId` 解析会议内 Human Participant；不接受客户端提交的作者字段。
3. 实现统一私有 `AppendResolvedAsync`，本主题只有 Human 和 System 公共入口调用它；不在 Abstraction 中公开可伪造 participantId 的 Agent DTO，后续“单 AI 入会与 @ 回复”主题通过受信连接 resolver 接入该内部核心。
4. 追加事务内依次完成 Active/成员屏障、正文 NFC+Trim、幂等查询与 Ordinal 比较；未命中后再执行长度校验、参与者快照、提及解析、Sequence 分配、消息/mentions/event/deliveries 注册和提交。
5. Sequence 在 `IMeetingTransactionRunner.RunAsync` 的 `BEGIN IMMEDIATE` 事务内按当前会议最大值加一分配；不在客户端或内存中分配。
6. 幂等重放不重新分配 Sequence、不创建第二个 event、不重复创建 mention；正文不一致返回稳定冲突。
7. 提及候选范围相对实际 NFC+Trim 正文重新验证；候选失效时使用服务端解析器，不信任 participantId。
8. 追加成功后只唤醒持久化事件 Dispatcher，不在 HTTP 事务中同步调用 SignalR 或未来 AI 处理器。

验收：Human 和受信 System 测试通过；统一内部追加核心可供后续 Agent resolver 调用，但本主题没有公共 Agent 提交 DTO 或 MCP 入会。

### 4.5 阶段 4：历史和 HTTP

1. 新增 `MeetingMessagesController`，使用 `[Authorize]`、`[ApiController]`、`[NoStoreResponse]` 和路由约束。
2. `GET` 历史使用 NoTracking 投影，支持无方向游标、before、after+through；返回 items、hasMoreBefore/hasMoreAfter、first/last 和两个方向的下一游标。
3. `POST` 发送使用 `RequireBrowserOrigin(true)`，在认证后执行消息限流，再进入模型绑定和 Application。
4. 成功首次发送返回 `201`，幂等重放返回 `200`；两者均返回冻结的 `SubmitMessageResultDto { message, replayed }`。
5. 未认证、非成员、Deleting 会议、游标错误、正文错误、幂等冲突和限流分别映射冻结的 ProblemDetails code。
6. 成功和错误响应均使用 `Cache-Control: no-store`、`Pragma: no-cache`；不把正文、会议码、Cookie 或完整参与者集合写入日志。
7. 追加 `MessageOpenApiOperationTransformer`，与既有 `MeetingOpenApiOperationTransformer` 同时注册且只作用于消息 endpoints；声明 200/201、429 `Retry-After`、long sequence、作者字符串枚举和 mentions。

验收：真实 Cookie、Origin、成员授权、状态码、错误 code、缓存头和日志脱敏通过 IntegrationTests。

### 4.6 阶段 5：SignalR 和 presence

1. 实现 `MeetingChatHub`，路由固定 `/hubs/meeting-chat`，只负责认证、订阅、退出订阅、事件推送和连接生命周期；`MapHub` 附加授权、`RequireBrowserOriginAttribute(allowMissingOriginForGet: true)` 和 `NoStoreResponseAttribute` metadata。
2. `SubscribeMeeting(meetingId)` 通过 `IMeetingChatSubscriptionService` 复查 Active 会议和当前账号 Human Participant，生成服务端组名 `meeting:{meetingId:N}`，返回在线 ParticipantId 列表与 `highWatermarkSequence`；空会议高水位为 0，不返回服务端连接代次。
3. Hub 连接绑定一个会议；切换必须先 Unsubscribe 旧会议，不能由客户端自选组名或 participantId。
4. `MeetingPresenceRegistry` 维护 connectionId -> meetingId/participantId 和 participantId 活动连接集合；同一参与者多连接只在首连/最后断开时广播状态变化。
5. `messageAppended` 只由实时事件消费者广播；`participantPresenceChanged` 由 presence 注册表广播；`meetingDeleted` 由删除协调器广播。
6. 连接断开、取消订阅和删除都必须幂等释放注册表；presence 只保存在进程内，重启后重新连接恢复。
7. negotiate、WebSocket 和长轮询请求执行 AllowedOrigins 校验；不使用 credentialed 跨源 CORS 代替 Origin 防护。

验收：两个浏览器可同时订阅同一会议；非成员、Deleting 会议、外部 Origin 和匿名连接均无法订阅。

### 4.7 阶段 6：持久化事件分发和删除

1. 实现 `MessageEventConsumerCatalog`，从 `MessageEventConsumerDescriptor` 建立不可变、非空、名称唯一且符合 `name-vN` 格式的名称到 ServiceType 目录，不持有消费者实例；实现 `MessageEventDispatcherService`，扫描到期 Pending 和租约过期 Leased delivery，批量租取并在事务外的独立 scope 中解析、执行目录中的消费者；达到阈值转 Suspended，需显式 Resume。
2. 实现 `RealtimeMessageBroadcastConsumer`，消费前取得会议 gate 共享租约并复查 Active，再查询完整 DTO 并广播。
3. 消费成功按 LeaseId 条件标记 Completed；资源已删除时匹配 0 行视为完成，LeaseId 已变化视为失去所有权；失败回 Pending、递增 AttemptCount、设置有上限退避，达到阈值转 Suspended；服务重启后过期租约可重取，Suspended 不自动重试，受信恢复服务显式 Resume 后才可继续。
4. 修改真实 `MeetingWorkspaceService.DeleteAsync`：先做不改变状态的快速权限/确认预检，再请求 writer-priority `IMeetingRealtimeGate` 独占关闭租约；超时返回 `meeting_delete_busy`，独占成立后才开启短事务并重新验证后提交 Active -> Deleting，失败 Dispose 释放，成功 `CommitClosed()` 转永久关闭。
5. 删除恢复器在每个清理轮次发送 `meetingDeleted`、终止会议组连接，再在短事务中按外键顺序清理 deliveries/events/mentions/messages 与目录 004 资源。
6. 清理失败保持 Deleting，启动恢复器继续；Dispatcher 发现 Deleting 会议时跳过外部广播但按幂等方式收敛 delivery。

验收：广播失败重试不丢消息；删除与广播并发时没有删除后的 `messageAppended`；服务重启后删除继续且会议不恢复 Active。

### 4.8 阶段 7：OpenAPI/Kiota 和客户端

1. 启动服务端生成 OpenAPI；执行现有 staging `pnpm generate:api`，检查消息路径、响应、错误和作者字符串枚举。
2. 在 `features/meetingWorkspace/api` 增加历史、发送和 SignalR 适配；组件不 import generated Client 或 `HubConnection`。
3. 扩展 `useMeetingWorkspace` 或新增 `RealtimeChatRegion` Hook，按 meetingId/generation 管理消息、presence、草稿和连接。
4. 实现消息 Map、序号排序、messageId 去重、实时段缺口检测、highWatermark 补齐和 beforeSequence 更早历史；首次最新页使用 `firstSequence - 1` 初始化 `liveBaselineSequence`，使用该页最后序号/高水位初始化 `lastAppliedSequence`，不把更早未加载历史当成缺口。
5. 实现 `MessageComposer` 的正文、mention candidates、发送中、失败保留和同一 ClientSubmissionId 重试。
6. 将 `RealtimeChatRegion` 接入会议工作台消息区，扩展 `ParticipantList` 在线/离线显示；不修改会议列表和删除权限。
7. 路由切换、删除、注销和卸载时递增 generation、取消请求、关闭旧连接、清理消息与 presence。

验收：Vitest、tsc、Vite build、真实发送、重连补齐、快速切换和移动端布局通过。

### 4.9 阶段 8：集成、浏览器和发布

1. 执行服务端全量构建、测试、架构检查、OpenAPI/Kiota 生成和客户端测试；发布配置确认仅单实例运行，多实例部署阻止冻结并要求重新设计 backplane、presence 和 gate。
2. 使用两个账号创建/加入同一会议，分别打开两个浏览器，完成发送、接收、刷新、短暂断网、重连和提及高亮。
3. 建立会议 A/B，分别发送消息并快速切换；验证缓存、连接和迟到事件不会串会。
4. 关闭广播消费者或制造临时处理失败，确认消息仍可通过历史接口补齐，delivery 后续完成。
5. 创建者删除含消息的会议；验证两个浏览器收到删除通知、历史和发送均被拒绝、页面回无选择工作台。
6. 重启服务端并验证 presence 需要重新连接、Deleting 会议继续清理、Active 会议消息仍可读取。
7. 发布构建后检查 SPA 路由、`/hubs` 握手、缓存头、Origin、Console/Network 和无敏感日志。

## 5. 文件落点

~~~text
Server/RoundTableMeeting.Domain/
  Entities/MeetingMessage.cs
  Entities/MessageMention.cs
  Entities/MessageAppendedEvent.cs
  Entities/MessageEventDelivery.cs
  Enums/MessageAuthorKind.cs
  Enums/MessageEventDeliveryState.cs
  Exceptions/MessageAppendConflictException.cs
  Repositories/IMessageRepository.cs

Server/RoundTableMeeting.Abstraction/
  Contracts/RealtimeChat/RealtimeChatContracts.cs
  Contracts/RealtimeChat/MessageOperationResult.cs
  Contracts/RealtimeChat/MessageAuthorKind.cs
  Services/IRealtimeChatServices.cs

Server/RoundTableMeeting.Application/RealtimeChat/
  RealtimeChatService.cs
  MessageMentionParser.cs
  MessageEventConsumerCatalog.cs
  RealtimeChatOptions.cs

Server/RoundTableMeeting.Repository.Sqlite/
  Database/Configurations/RealtimeChatConfigurations.cs
  Repositories/MessageRepository.cs
  Services/MessageCleanupService.cs
  Services/MessageEventRecoveryService.cs
  Migrations/<timestamp>_AddRealtimeChat.cs

Server/RoundTableMeeting/
  Controllers/MeetingMessagesController.cs
  RealtimeChat/MeetingChatHub.cs
  RealtimeChat/MeetingRealtimeGate.cs
  RealtimeChat/MeetingPresenceRegistry.cs
  RealtimeChat/MessageEventDispatcherService.cs
  RealtimeChat/MeetingRealtimeTermination.cs
  RealtimeChat/MessageSendRateLimiter.cs
  RealtimeChat/MessageSendRateLimitFilter.cs
  RealtimeChat/RealtimeMessageBroadcastConsumer.cs

Client/roundTableMeeting/src/features/meetingWorkspace/
  api/listMessages.ts
  api/sendMessage.ts
  api/meetingChatConnection.ts
  chat/messageReducer.ts
  chat/messageSelectors.ts
  chat/mentionCandidates.ts
  components/MessageTimeline.tsx
  components/MessageComposer.tsx
  components/ChatConnectionStatus.tsx
  pages/RealtimeChatRegion.tsx
  types/realtimeChat.ts
~~~

文件名是实施基线。若实现中把同一职责合并到相邻文件，必须同步更新本主题文档和测试定位；不得因为合并而把消息规则、授权或事务移入 Controller/Hub。
本主题当前采用以下职责合并：Abstraction 的实时聊天 DTO 合并在 `Contracts/RealtimeChat/RealtimeChatContracts.cs`，服务接口合并在 `Services/IRealtimeChatServices.cs`；Repository 的四套 EF 配置合并在 `Database/Configurations/RealtimeChatConfigurations.cs`；delivery 租约扫描/条件更新由 Web 层 `RealtimeChat/MessageEventDispatcherService.cs` 负责，消息删除与显式 Resume 分别由 Repository.Sqlite 的 `MessageCleanupService.cs`、`MessageEventRecoveryService.cs` 负责。

## 6. 命令基线

~~~powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet tool run dotnet-ef migrations add AddRealtimeChat --project Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj --startup-project Server/RoundTableMeeting/RoundTableMeeting.csproj --context RoundTableMeetingDbContext --output-dir Migrations
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug

Set-Location Client/roundTableMeeting
pnpm install --frozen-lockfile
pnpm generate:api
pnpm test
pnpm build
Set-Location ../..

dotnet publish Server/RoundTableMeeting/RoundTableMeeting.csproj --configuration Release --output artifacts/publish
~~~

## 7. 完成定义

- `design.md`、本文、Server/Client/模块/页面/组件 impl.md 与 testPlan.md 契约一致。
- P0/P1 测试全部通过；目录 002/003/004 已有框架、认证、会议创建/加入/删除回归继续通过。
- 消息、提及、追加事件和 delivery 在同一事务提交；广播或消费者失败可重试。
- 人类浏览器发送和 SignalR 接收使用统一消息模型；后续 Agent 不需要平行消息表或历史接口。
- 服务端顺序、幂等、历史补齐、实时去重、多会议隔离、presence 和删除 gate 通过真实浏览器验收。
- OpenAPI/Kiota、`pnpm test`、`pnpm build`、`dotnet test`、`dotnet publish` 通过。
- 本主题没有提前实现 MCP、AI 回复任务、上下文裁剪、接管、循环保护、未读或消息编辑删除。
