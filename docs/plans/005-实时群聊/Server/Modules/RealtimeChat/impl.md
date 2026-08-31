# 实时群聊服务端模块实现方案

## 1. 文档定位

本文将[模块设计](design.md)落实为 Domain、Abstraction、Application 的类型、用例顺序、仓储接口和结果映射。EF Core、HTTP、SignalR 和 Dispatcher 的工程接入见[服务端实现方案](../../impl.md)。

## 2. Domain 类型

新增实体均继承现有 `BaseEntity`，可由 `IUnitOfWork.RegisterAdd/Edit/Delete(IEntity<Guid>)` 登记：

- `MeetingMessage`：工厂区分 Participant/System 作者，负责内容 Trim+NFC、长度和 Sequence 正数约束。
- `MessageMention`：负责目标、范围和快照字段基本约束。
- `MessageAppendedEvent`：只保存事件 ID、会议、消息、序号和时间。
- `MessageEventDelivery`：负责 Pending/Leased/Completed/Suspended 状态、租约、尝试次数和显式 Resume 转换。
- `MessageAuthorKind`：Human、Agent、System。
- `MessageEventDeliveryState`：Pending、Leased、Completed、Suspended。

Domain 工厂不接收 HttpContext、ClaimsPrincipal、HubConnection、EF EntityEntry 或 IConfiguration。

## 3. Abstraction 契约

### 3.1 请求/响应

`SubmitMessageRequest`：ClientSubmissionId、Content、MentionCandidates。

`MessageDto`：MessageId、MeetingId、Sequence、Author、Content、Mentions、CreatedAt。

`MessagePageRequest`：BeforeSequence、AfterSequence、ThroughSequence、Limit。

`MessagePageDto`：Items、HasMoreBefore、HasMoreAfter、FirstSequence、LastSequence、NextBeforeSequence、NextAfterSequence。

`MeetingChatSubscriptionAccessDto`：ParticipantId、HighWatermarkSequence。

`MessageAppendedEnvelope`：EventId、MessageId、MeetingId、Sequence、OccurredAtUtc。

`ParticipantPresenceDto`：ParticipantId、State、ChangedAt。

`MeetingDeletedDto`：MeetingId、DeletedAt、ReasonCode 固定为 `meeting_deleted`。

### 3.2 服务接口

`IMessageAppendService` 只公开 Human 和 System 两类入口；内部统一到 `AppendResolvedAsync`。Agent 适配器等待主题五提供受信连接上下文后再公开，当前不得接受普通 participant DTO。

`IMessageHistoryService` 提供按会议和游标读取一页的方法。

`IMeetingChatSubscriptionService` 提供 Hub 所需的 Active/成员授权和高水位查询，不暴露仓储或完整会议详情。

`IMessageEventConsumer` 只暴露 `ConsumeAsync`；ConsumerName 由 `MessageEventConsumerDescriptor` 唯一提供。

`IMessageEventRecoveryService.ResumeAsync(deliveryId)` 是受信内部维护入口，将 Suspended delivery 显式恢复为 Pending；本主题不开放公共 HTTP 管理端点。

`MessageEventConsumerDescriptor(string ConsumerName, Type ServiceType)` 只在 Web 组合根注册；`IMessageEventConsumerCatalog` 暴露不可变 `ConsumerNames` 和按名称返回 ServiceType 的方法。目录在启动时构建并验证，不保存 `IMessageEventConsumer` 实例；Dispatcher 每次在独立 scope 中解析消费者。

`IMeetingRealtimeGate` 提供 `AcquireSharedAsync`、`AcquireExclusiveCloseAsync`、独占租约 `CommitClosed()` 和物理清理后的 `Forget(meetingId)`；失败路径通过 Dispose 释放未提交独占租约。

接口只使用 DTO、Guid、long、DateTimeOffset、CancellationToken、Result 和委托，不泄露 EF/SignalR 类型。

## 4. 内容和提及算法

### 4.1 正文

正文处理拆成 `NormalizeForComparison` 与 `ValidateContentLength`：前者执行 null->empty、NFC、Trim 并用于幂等比较；空白立即失败。仅在幂等未命中后执行长度校验。长度按 UTF-16 code units 与浏览器 JS `.length` 对齐。

### 4.2 MentionParser

1. 输入规范化正文和当前会议参与者摘要。
2. 从每个 ASCII `@` 位置尝试最长名称匹配。
3. 正文候选片段执行 NFC + `ToUpperInvariant()`，与参与者已保存 NormalizedDisplayName 按 Ordinal 比较；文本范围包含 `@` 符号和名称。
4. 名称后只接受末尾、空白或 Unicode 标点。
5. 重复 TargetParticipantId 保留每个合法出现；范围重叠时只保留从同一起点得到的最长匹配。后续业务消费者按 TargetParticipantId 去重任务目标。
6. 解析异常只降级为普通文本，不让消息因未知 `@` 失败。

候选范围先校验正文范围、目标属于会议和候选不重叠；不合法候选被忽略并重新解析，不能信任客户端 participantId。

## 5. RealtimeChatService 用例

### 5.1 AppendHumanAsync

按服务端实现方案的事务顺序执行。返回 `Created` 或 `Replayed` 时携带完整 MessageDto；SubmissionMismatch、Conflict、MeetingNotFound、Unauthenticated、ValidationFailed 映射稳定结果。发送限流属于 Web ResourceFilter，不进入 Application 状态枚举。

### 5.2 AppendSystemAsync

固定 System 作者，并由 SystemMessageCode + 稳定业务发生键派生确定性 ClientSubmissionId；不解析 mentions，仍分配 Sequence、写 event/delivery 并触发广播。

### 5.3 GetPageAsync

1. 认证和成员检查。
2. 校验 before/after/through/limit。
3. Repository NoTracking 投影。
4. 通过方向边界查询分别判断 HasMoreBefore/HasMoreAfter，不用单个 hasMore 混淆历史加载与追平。
5. 以 Sequence ASC 返回，不更新任何已读或 AI 游标。

## 6. 事务和并发

- Application 不调用 Commit；`IMeetingTransactionRunner` 负责唯一提交。
- 幂等重放不重新分配 Sequence、不创建 event 或 mentions。
- 并发发送都在 BEGIN IMMEDIATE 内串行取得不同序号；一条失败不影响其他消息。
- 删除先提交屏障时，追加返回 MeetingNotFound；追加先提交时消息由删除协调器清理。
- 唯一冲突异常只转换已知消息幂等/序号场景，其他数据库异常继续抛出安全异常。
- `MessageAppendConflictException` 发生后，Runner 已回滚并清空跟踪状态；Service 最多启动一次新的 Runner 事务重查幂等记录，未收敛则返回 Conflict。

## 7. Event Consumer

`RealtimeMessageBroadcastConsumer`：

1. 取得会议 gate 共享租约。
2. 复查 Meeting Active。
3. 查询完整 MessageDto。
4. 调用 HubContext 广播。
5. 释放租约。

若会议 Deleting，消费者不广播外部事件，delivery 以可重入方式完成或等待删除清理；不创建 AI 任务。

## 8. 模块测试落点

- `Server/Tests/RoundTableMeeting.Domain.Tests/RealtimeChatDomainTests.cs`
- `Server/Tests/RoundTableMeeting.Application.Tests/RealtimeChatServiceTests.cs`
- `Server/Tests/RoundTableMeeting.Application.Tests/MessageMentionParserTests.cs`

测试使用 fake current user、repositories、runner、TimeProvider 和事件唤醒器；不引用 Web 或 SignalR。

## 9. 完成门

- Human、System 和未来受信 Agent resolver 最终使用同一个 AppendResolvedAsync；本主题只公开 Human/System 契约。
- mentions、event、delivery 和 message 在同一事务中注册。
- 历史游标和序号逻辑可独立测试，Application 不依赖入口层。
- Agent 预留契约存在但本主题不开放 MCP 入口或 AI 任务状态机。
