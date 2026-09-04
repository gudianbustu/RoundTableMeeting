# 实时群聊服务端模块设计

## 1. 文档定位

本文定义实时群聊 Application 模块的公共契约、作者解析、消息追加、历史查询、提及解析、事件处理边界和结果状态。数据库、HTTP、SignalR 与后台服务由[服务端设计](../../design.md)负责。

## 2. 模块职责

模块负责：

- 将不同入口的受信身份转换为统一消息作者。
- 通过唯一追加用例保存消息、提及和持久化追加事件。
- 按稳定游标读取会议历史。
- 把领域实体映射为统一 DTO。
- 对调用方返回稳定状态，不泄露 Repository 或入口实现。

模块不负责：

- 建立 Cookie 或 SignalR 连接。
- 直接广播 Hub 事件。
- 创建 AI 回复任务或管理 AI 连接。
- 解释消息正文中的角色指令或修改外部 Agent 行为。

## 3. 公共契约

### 3.1 IMessageAppendService

~~~csharp
Task<MessageOperationResult<MessageDto>> AppendHumanAsync(
    Guid meetingId,
    SubmitMessageRequest request,
    CancellationToken cancellationToken = default);

Task<MessageOperationResult<MessageDto>> AppendSystemAsync(
    SystemMessageSubmission submission,
    CancellationToken cancellationToken = default);
~~~

本主题公开 Human 和受信 System 路径；统一的 `AppendResolvedAsync` 作为 Application 内部的事务敏感追加核心，由 `IResolvedMessageAppendCore` 承载其受信调用契约，不在 Abstraction 中公开可直接提交 meetingId/participantId 的 Agent DTO。公开 `IMessageAppendService` 负责 Human/System 的 gate 与事务；主题五的受信 Agent 适配器在自己的外层事务中调用 `IResolvedMessageAppendCore`，不调用公开入口，避免嵌套事务。

### 3.2 IMessageHistoryService

~~~csharp
Task<MessageOperationResult<MessagePageDto>> GetPageAsync(
    Guid meetingId,
    MessagePageRequest request,
    CancellationToken cancellationToken = default);
~~~

历史服务只接受 meetingId 和游标，不接受 participantId 作为授权替代；它从当前人类上下文或未来受信调用上下文解析访问者。

### 3.3 IMessageMentionParser

输入是规范化正文、会议参与者摘要和可选候选范围；输出 `ParsedMention` 集合。解析器是纯 Application/Domain 算法，不查询数据库、不写日志、不创建任务。

正文候选片段必须先执行 NFC，再执行 `ToUpperInvariant()`，并与参与者保存的 `NormalizedDisplayName` 使用 `StringComparison.Ordinal` 比较。该规则必须直接复用目录 004 的名称比较函数，不能改用当前文化或另一套 ignore-case 算法。

### 3.4 IMessageEventConsumer

~~~csharp
Task ConsumeAsync(MessageAppendedEnvelope envelope, CancellationToken cancellationToken);
~~~

消费者接口只负责处理；稳定、版本化的 ConsumerName 由注册描述符唯一提供。Dispatcher 为每个事件调用已经创建 delivery 的消费者；消费者自行幂等，不能假设只调用一次。

### 3.5 IMessageEventConsumerCatalog

~~~csharp
IReadOnlyList<string> ConsumerNames { get; }
Type GetRequiredServiceType(string consumerName);
~~~

消费者注册描述符固定为 `MessageEventConsumerDescriptor(string ConsumerName, Type ServiceType)`，只在组合根注册，不作为业务请求输入。目录在应用启动时由这些描述符构建不可变快照，验证名称非空、唯一且符合 `name-vN` 版本格式，但不持有消费者实例。消息追加只读取 `ConsumerNames` 创建 deliveries；Dispatcher 在每次独立 scope 中使用 `GetRequiredServiceType` 解析消费者。新增消费者需要新的业务主题和迁移/启用策略，不能在运行期悄然改变目录。

### 3.6 IMeetingChatSubscriptionService

~~~csharp
Task<MessageOperationResult<MeetingChatSubscriptionAccessDto>> AuthorizeAsync(
    Guid meetingId,
    Guid userId,
    CancellationToken cancellationToken = default);
~~~

该用例只负责验证 Hub 已解析的受信 `userId`、Active 会议和 Human Participant，并返回 `ParticipantId` 与 `HighWatermarkSequence`；空历史高水位为 0。Hub 在共享 gate 租约内从 `Context.User` 的固定声明解析 `userId` 后调用该服务，再由 Web presence registry 生成在线 ParticipantId 快照。Application 不引用 Hub、group 或 presence 实现。

### 3.7 MessageAppendedEnvelope

`MessageAppendedEnvelope` 固定包含：

- `EventId`；
- `MessageId`；
- `MeetingId`；
- `Sequence`；
- `OccurredAtUtc`。

Envelope 只携带稳定引用和发生时间，不复制正文、会议码、Cookie 或作者资料；消费者使用 MessageId 回查统一 `MessageDto`。

## 4. DTO

### 4.1 SubmitMessageRequest

- `ClientSubmissionId: Guid`，必填且非 Empty。
- `Content: string`，必填。
- `MentionCandidates: IReadOnlyList<MentionCandidateDto>`，可选；每项包含目标 participantId、startOffset、length。

### 4.2 MessageDto

- `MessageId`、`MeetingId`、`Sequence`。
- `Author: MessageAuthorDto`，含 nullable participantId、`kind` 和 displayName。
- `Content`。
- `Mentions: IReadOnlyList<MessageMentionDto>`。
- `CreatedAt`。

### 4.3 SubmitMessageResultDto

- `Message: MessageDto`；
- `Replayed: bool`。

### 4.4 MessagePageRequest/Dto

Request：nullable `BeforeSequence`、nullable `AfterSequence`、nullable `ThroughSequence`、nullable `Limit`。

Dto：`Items`、`HasMoreBefore`、`HasMoreAfter`、nullable `FirstSequence`、nullable `LastSequence`、nullable `NextBeforeSequence`、nullable `NextAfterSequence`。

空会议历史返回 200 和空 items，不返回 MeetingNotFound；只有会议不存在、Deleting 或访问者非成员时返回 MeetingNotFound。

## 5. 结果状态

`MessageOperationStatus` 固定包含：

- `Created`；
- `Replayed`；
- `Success`；
- `SubmissionMismatch`；
- `MeetingNotFound`；
- `Unauthenticated`；
- `ValidationFailed`；
- `Conflict`，仅用于无法安全收敛的序号/数据库冲突，Web 映射为 `409 message_append_conflict`。

Application 不返回 HTTP 状态码；Controller 映射。异常数据库细节不能塞入 Result message。

## 6. Human 追加流程

1. 检查当前用户已认证且 UserId 非空。
2. 校验请求幂等标识和正文存在性。
3. 进入 `IMeetingTransactionRunner`。
4. 查询 Active 会议和当前 UserId 的 Human 参与者；失败返回 MeetingNotFound。
5. 对正文执行 NFC+Trim；空白返回 ValidationFailed。
6. 查询幂等消息，并以规范化正文按 Ordinal 比较：相同返回 Replayed，不同返回 SubmissionMismatch。
7. 幂等未命中后校验最大长度、读取会议参与者并解析 mentions。
8. 分配序号并创建 `MeetingMessage.CreateParticipant`。
9. 创建 mentions、`MessageAppendedEvent` 和目录中全部消费者 deliveries。
10. 提交后通知 dispatcher，构建完整 DTO 返回 Created。

若消息幂等或序号唯一约束命中，Repository/Runner 转换为 `MessageAppendConflictException`。Runner 完成回滚并清空 ChangeTracker 后，`RealtimeChatService` 在同一请求中最多再开启一次新的 Runner 事务，只按 ClientSubmissionId 重查：找到则按规范化正文收敛为 Replayed/SubmissionMismatch，仍未找到则返回 Conflict。不得在第一次污染的事务或跟踪状态中继续查询，也不得无限重试。

## 7. Agent 与 System 扩展

### 7.1 Agent

未来 Agent resolver 只传递受信的 connectionSessionId、meetingId、participantId、task/idempotency key 和 content。Application 必须复查连接绑定、参与者类型和 Active 屏障，再进入统一追加流程。Agent 回复使用任务标识派生的幂等键，但最终仍返回相同 `MessageDto`。

### 7.2 System

System submission 使用固定 `SystemMessageCode`、稳定业务发生键、显示名称和内容；Application 由 code + occurrence key 派生确定性幂等 Guid。系统消息不解析 mentions，author participantId 为 null。系统消息仍分配会议序号、写追加事件并广播。

本主题不会为已有参与者补造历史系统消息；新系统事实是否产生消息由相应业务用例显式调用，presence 抖动只发实时状态事件。

## 8. 历史查询流程

1. 校验当前人类身份、方向游标组合和可选 ThroughSequence 高水位。
2. 读取 Active 会议并验证参与关系。
3. 使用 Message Repository NoTracking 投影读取一页消息、作者快照和 mentions。
4. Repository 分别用边界查询判断 `HasMoreBefore` 和 `HasMoreAfter`，不执行全表 Count；无方向最新页和 before 页固定 `HasMoreAfter=false`，after+through 追平页才计算 `HasMoreAfter`。
5. Application 按 sequence 升序返回，并计算下一游标。

历史查询不依赖 SignalR 是否在线，不更新已读位置，也不推进未来 AI 上下文游标。

## 9. 持久化事件与处理器

消息提交只负责写 event/deliveries 和唤醒 dispatcher，不在请求事务中同步调用 SignalR 或 AI 处理器。这样 HTTP 成功只表示消息、提及和待处理事件已经可靠保存。

实时消费者：

1. 处理前验证 meeting 仍为 Active。
2. 查询 MessageDto。
3. 广播会议组。
4. 不在线时同样成功，因为历史是事实来源。

未来 AI 消费者：

1. 只处理 participant message，系统消息跳过。
2. 只针对目标类型为 Agent 的 mentions。
3. 在自己的事务中幂等创建任务，并在创建前通过会议实时 gate/Active 屏障复查删除状态。
4. 任务创建成功后才完成自己的 delivery。

## 10. 删除与并发

- Append 事务和删除状态转换都使用同一 MeetingTransactionRunner；一旦删除屏障先提交，后续追加找不到 Active 会议。
- 若追加先提交，消息属于会议并由删除协调器清理；删除不能遗漏刚提交消息。
- Dispatcher 在广播/后处理前取得会议实时 gate 共享租约并复查 Active；删除取得独占关闭权后不再产生新外部行为。
- 历史查询在屏障建立后立即不可访问，即使物理消息尚未删除。

## 11. 模块完成门

- Human、System 和预留 Agent 作者映射到同一消息 DTO 和 Repository。
- 幂等、序号、mentions、event/deliveries 在同一事务。
- 历史游标无 offset 漂移，增量补齐不漏序号。
- Application 无 ASP.NET Core、SignalR、EF Core 和 SQLite 引用。
- 未来 AI 消费者可以通过稳定接口接入，不修改 Human 发送流程。
