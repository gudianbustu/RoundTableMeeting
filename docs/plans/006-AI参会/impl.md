# AI 参会主题实现方案

## 1. 文档定位

本文把[AI 参会主题总体设计](design.md)拆解为可执行的服务端、数据库、MCP、事件处理、网页状态、联调和验收步骤；测试矩阵见[测试方案](testPlan.md)。本文只描述主题五的实现顺序和落点，不记录实施日志，也不提前实现主题六的可靠确认、接管和恢复，或主题七的 Agent 协作链与循环保护。

主题五在现有 002、003、004、005 工程基础上增量实现。正式系统沿用 `.NET 10`、ASP.NET Core、Application/Domain/Abstraction/Repository.Sqlite 分层、EF Core SQLite、显式 `IUnitOfWork` 和 `IMeetingTransactionRunner`；客户端沿用 React、TypeScript、Vite、Kiota、Ant Design、SignalR 和 Vitest。

`Demo/驻留参与者协作实验`只作为 MCP 工具调用顺序和驻留等待方式的参考。不能把它的单例内存会话、字符串入会码、内存 Channel 或实验消息模型直接复用为正式业务存储。

## 2. 实施前提

### 2.1 已有基线

本主题开始前应确认以下能力可用：

- 主题三提供 `Meeting`、`MeetingParticipant`、会议码、`Active/Deleting` 生命周期、参与者名称规范化、成员授权和可重入删除恢复器。
- 主题四提供 `MeetingMessage`、`MessageMention`、`MessageAppendedEvent`、`MessageEventDelivery`、统一消息追加服务、历史接口、SignalR 会议组、presence 和持久化事件 Dispatcher。
- `ParticipantType.Agent` 已存在并与 Human 共享 `(MeetingId, NormalizedDisplayName)` 唯一名称空间。
- 主题四的事件消费者目录支持版本化 `ConsumerName`，并且每个消息事件可以为新增消费者建立独立 delivery。
- 现有 Web 组合根可以注册 `ModelContextProtocol.AspNetCore`，开发环境可以使用 `/mcp`，生产环境使用同源 HTTPS。
- 现有客户端可以在会议工作台中刷新会议详情、订阅 SignalR、合并历史消息和显示 Agent 类型参与者。

### 2.2 关键实现约束

1. 不在 Controller、Hub、MCP Tool 或客户端中直接访问 `DbContext`、领域实体或事件 delivery。
2. 不新增 `AgentMessage`、AI 专用历史表、AI 专用消息广播或平行的回复存储。
3. 所有 AI 消息最终调用主题四统一消息追加核心；作者身份由服务端根据受信连接会话解析。主题四追加核心提供仅供服务端内部使用的 `IResolvedMessageAppendCore`，只在调用方持有的事务中工作，不开启或提交嵌套事务；Agent 回复服务负责外层事务和任务状态更新。
4. 所有跨会议查询首先绑定 `MeetingId`，所有跨参与者操作首先绑定 `AgentParticipantId`，所有跨连接操作还必须匹配当前 MCP 协议会话。
5. `MessageEventDelivery` 的租约只负责把消息追加事件交给消费者；它不代替 `AgentTaskDeliveryAttempt`，也不表示 Agent 已确认收到上下文。
6. 新增或修改函数、类、方法前，按仓库约定先执行 GitNexus `impact`；文档阶段不执行代码影响分析。

## 3. 阶段总览

| 阶段 | 目标 | 主要交付物 | 完成门 |
| --- | --- | --- | --- |
| 0 | 契约冻结与技术验证 | DTO、状态枚举、配置、MCP 请求上下文验证 | design、impl、testPlan 字段一致；确认 SDK 可取得协议会话绑定 |
| 1 | 工程和宿主接入 | MCP `/mcp`、服务注册、Agent 模块骨架、删除扩展点 | 空工具可发现；匿名/跨会话/错误边界测试通过 |
| 2 | 领域与 SQLite 持久化 | 连接会话、上下文游标、回复任务、投递尝试和索引 | 空库迁移、CHECK、复合外键和唯一键通过 |
| 3 | Agent 入会与状态 | 名称确认、唯一参与者、初始快照、连接 presence | 首次入会完整持久化；重复入会不产生同名参与者 |
| 4 | 上下文与任务消费者 | 初始上下文、乱序补齐、任务 FIFO、裁剪 | 事件乱序和并发消费者不漏任务、不形成非法范围 |
| 5 | MCP 等待与回复 | `wait_for_mention`、`reply_to_mention`、状态查询 | 正常 Human `@AI` 闭环和回复幂等通过 |
| 6 | 网页 Agent 状态 | 订阅快照、状态事件、刷新/重连恢复 | Agent 出现、waiting/processing/blocked/disconnected 可观察 |
| 7 | 删除闭环 | 删除 gate、终止等待、资源清理 | 删除后所有 AI 操作拒绝，资源可重入清理 |
| 8 | 网页客户端接入 | Agent 状态 reducer、参与者列表和消息时间线接入 | 刷新/重连、会议切换和窄屏状态显示通过 |
| 9 | 参考 Agent 与联调 | 最小 MCP 参考客户端、固定调用顺序和联调脚本 | 正常 mention、FIFO、idle、幂等和删除场景通过 |
| 10 | 生成、测试与发布检查 | OpenAPI/Kiota、全量测试、浏览器验收和发布检查 | P0/P1 全部通过，主题范围扫描通过 |

## 4. 阶段实施步骤

### 4.1 阶段 0：契约冻结与 MCP 技术验证

1. 从 `design.md` 固定传输值、字段名和状态：`agent-messaging-v1`、`join_meeting`、`wait_for_mention`、`reply_to_mention`、`get_agent_status`；状态使用结构化小写 JSON 值，不能输出整数枚举。activity 状态包括 `waiting`、`processing`、`blocked`、`disconnected`。
2. 固定所有资源标识为 Guid，消息位置为会议内 `long Sequence`；`TriggerSequence`、候选范围和游标不能使用时间或客户端序号。
3. 固定任务唯一键 `(MeetingId, AgentParticipantId, TriggerMessageId)`；回复幂等键使用总体设计冻结的 UUIDv5 固定命名空间、`agent-reply:` 前缀和小写 D 格式 `TaskId` 派生，确保跨重启可重算。实现必须按 RFC 4122 网络字节序处理命名空间和输出，不能直接把 .NET `Guid.ToByteArray()` 结果作为 UUIDv5 输入。
4. 固定 `join_meeting` 的请求不接受 `ParticipantId`、`ConnectionSessionId` 或作者字段；其他工具接受连接会话 ID，但必须同时检查当前 MCP 协议会话绑定。
5. 使用 `Demo/驻留参与者协作实验` 当前固定引用的 `ModelContextProtocol.AspNetCore 1.0.0` 做最小 Spike：启动 `AddMcpServer().WithHttpTransport()`，注册一个只返回常量的工具，确认工具方法能够取得 SDK 提供的当前 MCP 请求/协议会话上下文、请求取消令牌和会话结束信号；该版本尚未进入主解决方案，阶段 1 再决定是否采用并纳入中央包管理。
6. 将 SDK 上下文访问封装为 `IMcpTransportSessionAccessor`。业务 Application 只依赖该适配器返回的内部协议会话键，不引用 `HttpContext`、MCP SDK 类型或传输 Header。若 SDK 不暴露会话结束回调，阶段 0 必须先确认可用的宿主生命周期钩子，再实现连接状态；不能用单次请求 CancellationToken 代替会话断开。
7. 冻结 `agent-context-v1` 消息块格式、结构化消息返回字段、`contextFormatVersion`、UTF-16 计数口径和裁剪元数据；join 与 wait 只能复用同一个格式化核心。
8. 为 `contextCharacterLimit`、等待上限、回复长度、加入限流、Agent 任务唤醒和状态事件建立 `AgentCollaborationOptions`，启动时验证最小值小于等于最大值、所有上限为正、超时可取消。

验收：MCP 工具可以在真实 HTTP Transport 上被发现；没有可验证的协议会话绑定能力时，停止进入正式 Agent 工具实现并记录技术阻塞。

### 4.2 阶段 1：工程接入和分层骨架

1. 在 `Server/RoundTableMeeting.csproj` 增加与 Demo 相同的精确 `ModelContextProtocol.AspNetCore` 包版本；版本统一放入 `Server/Directory.Packages.props` 时，Demo 的现有版本也同步评估，不引入浮动版本。
2. 在 `Program.cs` 注册 MCP Server、HTTP Transport、主题五工具程序集和 `/mcp` 映射；`/mcp` 显式 `.AllowAnonymous()`，不使用浏览器 Cookie、浏览器 Origin 校验或 SignalR 授权策略，也不能被 SPA fallback 截获。匿名只允许请求进入 MCP 工具分发层：`join_meeting` 自行执行会议码、Active 状态和限流校验，其他工具必须先通过当前 MCP 协议会话绑定校验。MCP SDK 自身的 `JsonSerializerOptions` 必须显式加入 camelCase `JsonStringEnumConverter` 且 `allowIntegerValues=false`；不能假设 MVC `AddJsonOptions` 会影响 MCP Transport。
3. 在 `Hosting/ServiceCollectionExtensions.cs` 注册 Agent Application、Repository、MCP 上下文适配器、Agent 连接注册表、任务唤醒器、状态事件发布器、Agent 任务消费者和 `AgentConnectionStartupReconciler`。该 HostedService 在对外接收 join 前扫描持久化 `Connected` 记录，把注册表中没有匹配协议绑定的记录条件更新为 `Disconnected`；更新按主键、当前状态和绑定摘要限定，失败记录诊断并重试，不恢复任务或接管参与者。
4. 使用 `AgentCollaborationOptions` 的配置节，不能把会议码、MCP 传输令牌或真实测试凭证写入 `appsettings.json`、仓库或普通日志。
5. 为 MCP 入口建立统一错误映射：领域/数据库异常在 Tool 边界转换为设计中的稳定错误码；不把堆栈、SQL、会议码或完整正文作为 MCP 错误返回。
6. 为 Agent 模块建立独立的 Application service facade。MCP Tool 只做输入反序列化、当前传输会话读取、取消令牌传递和结果序列化；所有会议、参与者、任务和消息规则进入 facade。
7. 先接入 `get_agent_status` 的空状态实现和 `join_meeting` 的输入校验，再接入持久化，确保 MCP 工具发现、错误格式和会话绑定先稳定。
8. 固定 `residentInstructionVersion=resident-instruction-v1`，指令模板内容与版本由 `AgentResidentInstruction` 单点维护；版本字段与指令文本一起返回。

### 4.3 阶段 2：领域模型与 SQLite 持久化

在 Domain 中新增继承 `BaseEntity` 的实体和显式状态机：

- `AgentConnectionSession`：`MeetingId`、`ParticipantId`、传输类型、状态、声明字符数、初始快照序号、初始上下文状态、协议会话绑定摘要和活动时间。
- `AgentContextCursor`：`MeetingId`、`ParticipantId`、`DeliveredThroughSequence`、`AllocatedThroughSequence` 和更新时间；每个 Agent 只有一条当前游标。
- `AgentReplyTask`：任务身份、目标 Agent、触发消息/序号/提及、来源作者类型、候选范围、状态、回复幂等键、失败码和各状态时间。
- `AgentTaskDeliveryAttempt`：任务、连接会话、候选/实际范围、字符数、裁剪元数据、尝试状态和时间。

实施步骤：

1. 为实体提供无参数 EF 构造函数，但所有业务创建和状态转换使用领域工厂或方法；不允许 Repository 通过公共 setter 构造非法组合。
2. 新增 `AgentConnectionState`、`InitialContextState`、`AgentReplyTaskState`、`AgentTaskDeliveryAttemptState`，并为每个状态转换提供显式方法。
3. 显式建立以下唯一约束：Agent 参与者继续复用 `MeetingParticipant (MeetingId, NormalizedDisplayName)`；`AgentContextCursor (MeetingId, ParticipantId)`；`AgentReplyTask (MeetingId, AgentParticipantId, TriggerMessageId)`；任务表内全局唯一的 `ReplyIdempotencyKey`；`AgentTaskDeliveryAttempt (TaskId, DeliveryAttemptId)`。不要给共享 `MeetingMessage.ClientSubmissionId` 增加全局唯一约束，消息表继续沿用主题四的复合幂等范围。
4. 为所有从属实体增加 `MeetingId` 复合外键，确保连接、游标、任务、尝试、触发消息和参与者来自同一会议；普通单列 Guid 外键不足以表达隔离时必须使用复合主键/唯一键。
5. 为状态、序号、字符数、时间和可空字段组合增加 SQLite CHECK。复杂的消息范围、裁剪和状态转移规则由 Domain/Application 负责，不能只依赖 SQLite。
6. `ConnectionSessionId`、`TaskId` 和 `DeliveryAttemptId` 直接使用各实体 `BaseEntity.ID`。为任务 FIFO 查询建立 `(MeetingId, AgentParticipantId, State, TriggerSequence, ID)` 索引，其中 `ID` 即 `TaskId`；为活动连接建立 `(MeetingId, ParticipantId, State)` 查询索引。
7. 为任务、尝试和连接会话提供 Repository 只读投影与条件更新方法，条件更新必须包含 `MeetingId`、目标 Agent、当前状态和当前连接绑定。
8. 新增 `AddAgentCollaboration` Migration。Migration 不补造 Agent、任务、上下文游标或入会系统消息，不修改主题四既有消息序号。
9. 删除顺序固定为 Agent 任务尝试、回复任务、上下文游标、连接会话，再由主题四删除消息事件/消息/参与者；如果外键顺序要求不同，在删除协调器中显式执行，不能依赖数据库级级联删除产生不可观察行为。

验收：空库和主题四已有数据库均可迁移；非法状态、跨会议引用、重复同名 Agent 任务和重复回复幂等键由数据库或 Application 稳定拒绝。

### 4.4 阶段 3：唯一 Agent 参与者与 `join_meeting`

入会在一个会议事务中完成，事务边界必须覆盖名称确认、参与者创建、连接会话创建、游标创建和快照边界：

1. 通过会议码查询 `Active` 会议，并取得会议级共享 gate；会议处于 `Deleting` 或已删除时统一返回 `meeting_not_found`。
2. 先在 Application 边界检查 `displayName` 是否为 null 或 Trim 后为空；该分支直接返回 `agent_name_required`，不能交给会把空值和长度非法统一抛为 `ArgumentException` 的共享 `MeetingParticipant.NormalizeDisplayName`。只有非空输入才调用主题三规范化方法，若长度、字符或规范化结果非法则返回 `agent_name_invalid`，得到 `DisplayName` 与 `NormalizedDisplayName`。
3. 按 `(MeetingId, NormalizedDisplayName)` 查询已有参与者：没有记录时创建 `ParticipantType.Agent/UserId=null`；已有 Human 时返回 `agent_name_in_use`；已有 Agent 时不创建第二个参与者。
4. 已有 Agent 且存在活动连接时返回 `agent_connection_already_active`；已有 Agent 但只有断开连接时，主题五返回 `agent_recovery_not_supported`，不在本阶段偷偷实现接管。
5. 在名称查询和创建之间依赖数据库唯一约束处理并发竞争；捕获唯一冲突后重新查询并映射到稳定错误，不能返回裸数据库异常。
6. 在同一事务取得 `InitialSnapshotSequence`，该值是当前会议消息高水位的明确快照；创建连接会话时写入 `InitialContextState=Prepared`。
7. 从 `Sequence <= InitialSnapshotSequence` 的历史读取候选消息，使用共享的 `AgentContextBuilder` 按声明字符数选择最近连续上下文；上下文构造规则不能在 join 和 wait 两处复制。
8. 创建 `AgentContextCursor`，`AllocatedThroughSequence=InitialSnapshotSequence`、`DeliveredThroughSequence=0`；返回响应只表示服务端准备了初始上下文。
9. 事务提交后向 Agent presence registry 注册当前协议会话。成功 join 的边界是数据库提交和注册表绑定均成功；注册或绑定提升失败时不返回成功，清除临时注册，使用独立 scope 将本次新建连接条件更新为 `Disconnected`，并返回可重试的 `agent_operation_unavailable`。补偿写库失败时记录诊断并由请求尾部或启动协调器继续收敛；没有匹配注册表绑定的遗留 `Connected` 记录不算有效活动连接，不能永久占用同名 Agent。注册成功后的 `online` presence 和 activity 增量采用可恢复通知发布，通知失败不回滚 join、不改变成功结果，浏览器通过订阅快照最终收敛。
10. 成功结果包含稳定 `connectionSessionId`、参与者摘要、`residentInstruction`、初始上下文和 `nextAction=wait_for_mention`，不包含会议码回显以外的秘密，也不返回内部 MCP 传输标识。

这里的“创建或确认唯一的 AI 会议参与者”指服务端在会议和规范化显示名称范围内原子地确认唯一身份：首次没有同名身份才创建；已有同名 Agent 复用其身份但本主题不接管断开连接；任何路径都不能因为重复请求产生第二个参与者。

### 4.5 阶段 4：上下文构造与消息事件消费者

#### 4.5.1 共享上下文构造器

1. 新增 `AgentContextBuilder`，输入会议、候选消息序号范围和声明字符数，输出按 `Sequence` 升序的消息块与候选/实际范围元数据。
2. 消息块拼接格式只定义为总体设计中的 `agent-context-v1`：按 `messageId`、`sequence`、`authorKind`、`authorDisplayName`、`content` 顺序和固定换行生成；加入上下文与任务上下文共用同一格式化器。响应仍返回结构化消息列表，并带 `contextFormatVersion`，字符预算只计算规范化消息块，不计算 JSON 包装。
3. 使用 UTF-16 code unit 计算预算；触发消息始终完整保留，较早消息只能从前端连续裁剪，不能跳过一条更早消息继续选择更早内容。
4. `overLimit`、`truncated`、`truncatedMessageCount` 和实际字符数全部由构造器计算，投递尝试保存相同结果，避免响应和数据库诊断不一致；格式化器和构造器不得将 mentions、createdAt、JSON 引号或传输编码计入 `DeliveredCharacterCount`。

#### 4.5.2 `agent-mention-task-v1` 消费者

1. 在主题四 `IMessageEventConsumerCatalog` 注册新的 `agent-mention-task-v1`，使用独立 `MessageEventDelivery`，不复用 `realtime-broadcast-v1`。
2. 消费者通过事件 `MessageId` 回查完整消息、提及和会议状态；会议非 `Active` 时不创建任务并完成本次 delivery。
3. 只把 Human 作者消息中的合法提及物化为任务；System 消息不触发，Agent 消息的通用提及记录保留但主题五消费者跳过。
4. 按目标 Agent 去重，同一消息对同一 Agent 只创建一个任务；只处理当前会议内仍存在的 `ParticipantType.Agent`。
5. 对每个目标 Agent 启动参与者级顺序补齐事务：锁定 `AllocatedThroughSequence`，查询从该位置之后到当前事件序号之间所有尚未物化的 Human `@AI` 消息，按 `Sequence` 升序创建缺失任务。
6. 第一个缺失任务的 `CandidateFromSequenceExclusive` 使用当前分配游标，后续任务使用前一任务的 `TriggerSequence`；每个任务的终点是自己的触发序号；每成功创建一个任务就推进分配游标。
7. 即使较晚事件先到，也必须发现并补齐较早的已持久化消息；较早事件随后到达只命中唯一约束或已存在任务，不倒退游标、不产生负范围。
8. 任务创建事务提交后，通过 `AgentTaskSignalRegistry` 唤醒该 Agent 的等待调用；Signal 只降低等待延迟，数据库任务才是事实。
9. 任务创建成功后才允许 Dispatcher 完成本次 `MessageEventDelivery`；异常由主题四 delivery 重试承接，不能用进程内回调代替。连续失败进入 `Suspended` 时，运维使用主题四的 `IMessageEventRecoveryService.ResumeAsync` 恢复该 delivery，本主题不新增公共管理端点。

### 4.6 阶段 5：MCP 等待、FIFO 和回复

#### 4.6.1 `wait_for_mention`

1. Tool 读取当前 MCP 协议会话，通过 `IMcpTransportSessionAccessor` 与输入的 `connectionSessionId` 做精确匹配，再进入 Agent Collaboration Application。
2. 先取得短的会议共享 gate 租约并复查绑定、Active 状态与删除屏障；在该短边界内把 `InitialContextState` 从 `Prepared` 原子改为 `Confirmed`，并把该 Agent 的 `DeliveredThroughSequence` 推进到 `InitialSnapshotSequence`。确认事务与任务领取事务分开；确认后进程退出时任务仍为 `Queued`，下一次 wait 不重复推进初始游标。
3. 初次查询无任务后注册 Agent 级唤醒信号，并在进入长轮询前释放所有会议 gate 租约和数据库事务；等待期间不得持有 `IMeetingRealtimeGate`。唤醒、超时、取消或删除终止后重新取得短租约复查 Active 状态和持久化任务，不能假设 signal 与任务一一对应。
4. 单次等待达到配置上限且没有任务时返回 `kind=idle`，不修改连接状态；请求取消只结束当前调用，不标记 `Disconnected`。
5. 在重新取得的短会议租约和 Agent 互斥事务中选择最早可投递任务；队首存在 `Failed`、`Delivered` 或 `Processing` 时，不能越过队首。租约获取、数据库事务和 Agent 互斥的顺序保持一致，响应对象构造不持有会议租约。
6. 领取事务构造当前任务上下文，创建 `AgentTaskDeliveryAttempt`，提交 `Queued -> Delivered` 和尝试 `Prepared`；完成响应对象构造后，再用短事务提交 `Delivered -> Processing`、尝试 `Prepared -> Delivered` 和 Delivered 游标推进，随后返回 `kind=mention`。
7. `Delivered` 是可持久化、可观察的队首阻塞状态；两次事务之间发生取消、序列化或内部错误时保持 `Delivered`，不自动重投。它在 Agent activity 读模型中映射为 `blocked`，并使用派生的 `delivery_not_finalized` 作为阻塞码；该码不写入任务 `FailureCode`。`Processing` 表示服务端已将任务交给当前连接；不添加 `acknowledge_mention` 工具，也不把一次 HTTP 响应的完成当作可靠确认。
8. 响应中断时任务保持 `Processing`，尝试保持已投递状态，后续任务继续阻塞；主题五不自动重投，主题六负责确认超时、重投和接管。

#### 4.6.2 `reply_to_mention`

1. 验证当前 MCP 协议会话、连接会话、Agent 参与者、任务、投递尝试和幂等键属于同一会议与同一 Agent。
2. 只允许当前任务为 `Processing`，且投递尝试为当前有效尝试；任务为 `Delivered` 或其他不可回复状态时返回 `task_not_available`，不推进状态或建立替代尝试；已 `Replied` 的任务按回复幂等键返回原消息。
3. 回复正文使用主题四同一 NFC、Trim 和长度校验，失败返回 `message_validation_failed`，不改变任务状态，允许 Agent 修正后重试同一任务。
4. 通过受信 `AgentMessageAuthorResolver` 从已验证连接会话取得 ParticipantId 和显示名称，并调用主题四的 `IResolvedMessageAppendCore`；该核心只在当前事务中工作，忽略客户端作者字段。
5. 消息、mentions、追加事件、事件 deliveries 和任务 `Replied` 状态在同一会议事务中提交；不调用会自行开启事务的公开 `IMessageAppendService`。已有回复先按任务 ID 定位，再比较服务端派生幂等键和规范化正文；相同正文返回原消息，正文或幂等键不一致返回 `reply_idempotency_mismatch`。
6. 提交成功后发布 Agent activity `waiting`，唤醒或允许下一次等待；回复消息中的 Agent `@` 只保存主题四通用提及，主题五消费者不因其创建新任务。

#### 4.6.3 `get_agent_status`

状态查询必须只返回当前协议会话绑定的 Agent：连接状态、初始上下文状态、按 `queued/delivered/processing/replied/failed/cancelled` 分组且缺项补 0 的任务计数、队首任务摘要、失败码和两个上下文游标。查询不到当前连接、会议不可用或绑定不匹配时统一返回设计中的错误，不能枚举其他会议或 Agent。

### 4.7 阶段 6：Agent presence、activity 和订阅快照

1. 新增 `AgentConnectionRegistry`，只保存协议会话到 Agent 的临时映射和关闭回调；它不保存任务正文，不作为任务队列，不跨进程持久化。
2. 只有 `join_meeting` 数据库提交和注册表绑定均成功后才发布 `participantPresenceChanged(online)`；MCP 协议会话明确关闭或服务端终止时发布 `offline`。传输会话仅建立但尚未 join 时没有会议和参与者绑定，不能发布 online；单次 Tool 超时、请求取消和网络写入失败不直接调用 disconnect。
3. 新增 `AgentActivityDto`，包含 `meetingId`、`participantId`、`state`、可空 `activeTaskId`、可空 `failureCode`、`changedAt`；由持久化队首任务与当前有效连接共同计算 `waiting/processing/blocked/disconnected`。队首为 `Processing` 才映射为 `processing`；队首为 `Delivered` 或 `Failed` 都映射为 `blocked`，前者使用派生的 `delivery_not_finalized`，后者使用任务持久化的 `FailureCode`。
4. 在主题四已冻结的 `MeetingChatSubscriptionDto` 增加 `AgentActivities` 字段，保留 `OnlineParticipantIds` 和 `HighWatermarkSequence` 的既有字段及 JSON 语义；`OnlineParticipantIds` 仍只表示浏览器连接注册表中的在线参与者，不作为 Agent 在线来源。SignalR `SubscribeMeeting` 一次返回完整 Agent activity 快照。该 Hub DTO 使用客户端显式 TypeScript 镜像类型，不归 OpenAPI/Kiota 生成。
5. 新增 `agentActivityChangedV1` Hub 事件，载荷显式携带 `MeetingId`。事件发布使用会议组，不带会议码、MCP 连接内部标识或完整任务上下文。对 Agent，activity 状态是在线和处理状态的权威来源；`participantPresenceChanged` 只服务于通用 presence 通知，不能覆盖较新的 Agent activity。
6. 客户端先合并订阅快照，再合并 activity 增量；每个 action 携带 `meetingId` 与 connection generation，切换会议或旧连接的迟到事件直接丢弃。Agent 在线判定只依据 activity 状态是否为 `disconnected`，不得因为 Agent 不在 `OnlineParticipantIds` 而渲染为离线。
7. Agent activity 不能写入 `MeetingParticipant` 长期身份、`MeetingMessage` 作者字段或历史消息；Agent 回复继续通过 `messageAppended` 进入统一时间线。

### 4.8 阶段 7：删除闭环

1. 修改现有 `MeetingDeletionRecoveryService` 的固定恢复流程，注入 Agent 连接终止与 `IAgentCollaborationCleanupService`；不存在可插拔的资源登记机制，不能只新增一个无人调用的清理服务。
2. `Active -> Deleting` 屏障提交后，所有 MCP Tool、任务消费者、Agent 回复追加和状态事件发布都先检查会议状态；新的操作统一返回 `meeting_not_found`。
3. 删除流程先向协议会话发送终止信号并取消长等待，再把 `Queued`、`Delivered`、`Processing`、`Failed` 转为 `Cancelled`；随后在 `IMessageCleanupService.DeleteForMeetingAsync` 之前按“尝试 -> 任务 -> 游标 -> 连接会话 -> `agent-mention-task-v1` delivery”清理，避免 `TriggerMessageId` Restrict 外键阻断消息删除。
4. 删除和任务领取/回复使用现有会议 gate 或等价的会议级互斥边界，保证屏障建立后没有新的任务投递、回复消息或 Agent activity 广播；`wait_for_mention` 只在检查和领取短边界持有共享租约，长轮询期间必须释放，因此不会让删除持续返回 `meeting_delete_busy`。
5. 清理失败保持 `Deleting`，由现有恢复器重试；重启后从持久化 `Deleting` 状态继续，不能恢复 `Active`。
6. `reply_to_mention` 在删除期间即使持有旧任务 ID 也只能返回 `meeting_not_found`；不能因为幂等记录存在而重新写入消息。
7. 删除成功后 `MeetingParticipant` 由主题三统一清理；旧会议码、连接会话 ID、任务 ID 和投递尝试 ID 不能重新访问该会议。

### 4.9 阶段 8：网页客户端接入

1. 扩展 `src/features/meetingWorkspace/types/realtimeChat.ts`，增加 `AgentActivityState`、`AgentActivity`、订阅快照的 `agentActivities` 和 ChatState 的 activity Map。
2. 扩展 `meetingChatConnection.ts` 适配 `SubscribeMeeting` 新字段，注册 `agentActivityChangedV1`，只向 reducer 发送规范化后的 activity action。组件不直接接触 `HubConnection`。
3. 扩展 `messageReducer.ts`：处理 activity 快照、activity 增量、会议/代次隔离、删除清空和旧状态丢弃；快照中的 participantId 必须与当前会议详情合并，不创建浏览器侧参与者。对 Agent，activity 是在线和处理状态的权威来源；`OnlineParticipantIds` 及 `participantPresenceChanged` 不得把较新的 activity 覆盖为离线。
4. 扩展 `ParticipantList.tsx`，Agent 显示类型，以及由 activity 派生的 online/offline 和 waiting/processing/blocked/disconnected 状态；blocked 显示失败码或阻塞摘要，Human 继续使用既有 presence 且不显示 Agent 状态。长名称、失败状态和窄屏布局必须不溢出。
5. 保持 `RealtimeChatRegion` 的历史高水位和消息去重逻辑不变；AI 回复走既有 `messageAppended`。
6. 刷新和 SignalR 重连必须依靠 `SubscribeMeeting.agentActivities` 快照恢复，不能假设客户端收到了之前的 activity 事件。
7. 会议删除、路由切换、注销和组件卸载时清空 activity、取消请求、关闭连接、递增 generation；旧连接回调不得重新写入新会议状态。

### 4.10 阶段 9：参考 Agent 与联调

1. 在 `docs/plans/006-AI参会/ReferenceAgent/README.md` 记录启动参数、会议码输入、工具调用顺序、环境变量和重复运行方式；不写入真实会议码。
2. 可执行参考客户端放在 `Demo/AI参会联调客户端` 或等价独立 Demo，不修改 `Demo/驻留参与者协作实验` 的业务语义。
3. 参考客户端按固定顺序调用：`join_meeting` -> 首次 `wait_for_mention` 确认初始上下文 -> 循环等待 -> 处理 `kind=mention` -> `reply_to_mention` -> 再次等待。
4. 参考客户端记录 TaskId、DeliveryAttemptId 和结果状态，不记录会议码、完整上下文或完整回复正文。
5. 联调脚本支持普通消息不触发、单次 `@AI`、连续两个 `@AI`、重复回复幂等、idle 超时和删除期间拒绝。

### 4.11 阶段 10：生成、测试与发布检查

1. 生成 OpenAPI 时确认浏览器 HTTP 契约未把 MCP Tool 当作普通 REST API 暴露；MCP 工具契约由工具描述和参考客户端测试。
2. `MeetingChatSubscriptionDto` 和 `agentActivityChangedV1` 属于 SignalR Hub 契约，不由 OpenAPI/Kiota 生成。为其维护显式 TypeScript 镜像类型，并用真实 Hub 序列化/集成测试验证 `AgentActivities`、`meetingId`、activity 状态和字段兼容；`pnpm generate:api` 只验证 HTTP API 契约没有被 MCP Tool 或 SignalR DTO 意外混入。
3. 执行 Domain、Application、Repository.Sqlite、Architecture、Integration、客户端 Vitest 和真实浏览器验收；P0/P1 全部通过才进入主题审阅。
4. 检查 `git diff --check`、文档和源代码 CRLF、日志脱敏、没有 `acknowledge_mention`、没有主题五 Agent 协作循环实现。
5. 发布配置仍按主题四的单实例约束执行；多实例部署不得默认为可用，因为 Agent connection registry、任务 signal 和会议 gate 尚未引入 backplane。

## 5. 文件落点

~~~text
Server/RoundTableMeeting.Domain/
  Entities/AgentConnectionSession.cs
  Entities/AgentContextCursor.cs
  Entities/AgentReplyTask.cs
  Entities/AgentTaskDeliveryAttempt.cs
  Enums/AgentConnectionState.cs
  Enums/InitialContextState.cs
  Enums/AgentReplyTaskState.cs
  Enums/AgentTaskDeliveryAttemptState.cs
  Repositories/IAgentCollaborationRepository.cs

Server/RoundTableMeeting.Abstraction/
  Contracts/AgentCollaboration/AgentCollaborationContracts.cs
  Services/IAgentCollaborationServices.cs
  Services/IRealtimeChatServices.cs

Server/RoundTableMeeting.Application/AgentCollaboration/
  AgentCollaborationService.cs
  AgentContextBuilder.cs
  AgentContextFormatter.cs
  AgentReplyIdempotencyKeyFactory.cs
  AgentMentionTaskConsumer.cs
  AgentTaskSignalRegistry.cs
  AgentConnectionActivityService.cs
  AgentResidentInstruction.cs
Server/RoundTableMeeting.Application/RealtimeChat/
  ResolvedMessageAppendCore.cs

Server/RoundTableMeeting.Repository.Sqlite/
  Database/Configurations/AgentCollaborationConfigurations.cs
  Repositories/AgentCollaborationRepository.cs
  Services/AgentCollaborationCleanupService.cs
  Migrations/<timestamp>_AddAgentCollaboration.cs

Server/RoundTableMeeting/
  AgentCollaboration/McpAgentTools.cs
  AgentCollaboration/McpTransportSessionAccessor.cs
  AgentCollaboration/AgentConnectionRegistry.cs
  AgentCollaboration/AgentConnectionStartupReconciler.cs
  Meetings/MeetingDeletionRecoveryService.cs
  RealtimeChat/MeetingChatHub.cs
  Hosting/ServiceCollectionExtensions.cs

Client/roundTableMeeting/src/features/meetingWorkspace/
  api/meetingChatConnection.ts
  chat/messageReducer.ts
  components/ParticipantList.tsx
  pages/RealtimeChatRegion.tsx
  types/realtimeChat.ts

docs/plans/006-AI参会/ReferenceAgent/README.md
~~~

文件名是默认实施基线。若实现中合并文件，必须同步更新本文、子文档和测试定位；职责不能因此移动到 Controller、Hub 或客户端组件。

## 6. 命令基线

~~~powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet ef migrations add AddAgentCollaboration --project Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj --startup-project Server/RoundTableMeeting/RoundTableMeeting.csproj --context RoundTableMeetingDbContext --output-dir Migrations
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

参考联调使用独立临时数据库和显式测试会议码；不直接操作 `bin/Debug` 中的开发数据库，不把会议码写入命令历史或仓库。

## 7. 完成定义

- `design.md`、本文、`testPlan.md` 和后续 Server/Client/模块子文档使用同一工具名、字段名、JSON 值、状态转换和错误码。
- 首次 Agent 入会只创建一个 Agent 参与者；同名重复入会不会创建第二个身份，活动连接会被拒绝，断开连接的接管留给主题六。
- 初始上下文、消息事件乱序补齐、每 Agent 独立游标、连续候选范围、裁剪元数据和 FIFO 任务状态通过持久化测试。
- Human `@AI` 经真实消息事件消费者创建任务；普通消息、System 消息和主题五阶段的 Agent 消息不会创建回复任务。
- `wait_for_mention` 的 idle、请求取消、初始上下文确认、Delivered/Processing 阻塞和 `reply_to_mention` 幂等通过真实 MCP HTTP 测试。
- AI 回复使用统一消息追加核心，进入原有 Sequence、mentions、事件 delivery、SignalR 和浏览器时间线。
- Agent activity 快照和增量事件支持页面刷新、SignalR 重连、会议切换和旧事件丢弃。
- 删除开始后新的 MCP 操作、任务领取、回复和状态事件被屏障拒绝；连接等待终止，基础资源可重入清理。
- P0/P1 测试、主题二至主题四回归、`dotnet build/test`、`pnpm test/build`、HTTP OpenAPI/Kiota 生成、SignalR 镜像契约测试和发布检查全部通过。
- 本主题没有实现可靠确认、自动重投、连接接管、服务重启恢复、多 Agent 协作链、循环保护、未读或 Agent 专用消息流。
