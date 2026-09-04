# AI 参会主题总体设计

## 1. 文档定位

本文是“AI 参会”主题的总体设计，负责冻结外部 AI Agent 作为会议参与者加入和参与讨论所需的业务边界、AI 参与者与连接会话契约、MCP 能力、上下文投递规则、回复任务状态、网页展示和删除闭环扩展。服务端、客户端、模块、参考 Agent 和测试的具体实现分别由本目录后续子文档负责。

本主题目录编号为 006，对应[总体设计](../001-总体计划/design.md)中的“主题五：AI 参会”。目录 005 是“实时群聊”主题，对应总体计划中的主题四；后续文档中的“主题六”指总体计划中的“AI 任务连续性与接管”，不表示本目录编号。

本主题的产品依据为[圆桌会议网站需求](../../需求分析/圆桌会议网站需求.md)、[圆桌会议网站总体设计](../001-总体计划/design.md)、[会议工作台主题](../004-会议工作台/design.md)和[实时群聊主题](../005-实时群聊/design.md)。主题三已经提供会议、会议码、参与者身份、成员授权和删除访问屏障；主题四已经提供统一消息模型、顺序分配、提及记录、历史读取、实时广播和持久化消息事件。

本主题不把 MCP Demo 直接升级为正式业务结构。`Demo/驻留参与者协作实验`只作为驻留等待、工具调用顺序和联调方式的参考；正式系统必须使用数据库持久化的会议、参与者、连接会话、上下文进度和回复任务，并复用主题四的统一消息追加边界。

## 2. 主题目标

主题完成后，已登录用户和外部 AI Agent 应能够在真实会议中完成以下参会闭环：

1. Agent 通过 MCP 提交会议码、显示名称和上下文字符数，加入一个现有会议。
2. 服务端为每个首次参会的 AI 创建独立、会议内名称唯一的参与者身份，并创建当前 MCP 连接会话。
3. Agent 获得会议基本信息、参与者摘要、驻留参与指令和按自身字符数裁剪的最近入会历史。
4. Agent 持续等待明确提及自己的消息；普通未提及消息不会单独唤醒 Agent。
5. 人类消息中的 `@AI名称` 形成持久化回复任务，任务按消息顺序进入该 AI 的 FIFO 队列。
6. Agent 获得从自己的上下文投递游标到触发消息之间的连续候选消息，并收到按字符数裁剪后的最近上下文。
7. Agent 通过 MCP 提交带任务标识、投递尝试标识和回复幂等标识的回复。
8. 回复经统一 Agent 消息追加服务写入会议消息流，并通过主题四的事件分发器实时广播给网页参与者。
9. 网页参与者能够看到 AI 参与者、在线状态、等待、处理、阻塞或断开状态和 AI 回复。
10. 会议删除开始后，新的 AI 读取、等待、回复和任务创建立即被拒绝；已登记的 AI 连接和基础任务纳入删除清理。

同一会议允许多个 AI 分别参会，每个 AI 都有独立的参与者身份、连接会话、上下文游标和任务队列。本主题以一个 AI 的正常在线参会闭环和基本 FIFO 排队作为最低验收场景，不以一次成功的内存回调或模拟消息替代真实 MCP、数据库和网页集成；多个 AI 之间的协作链和循环保护仍由主题七负责。

## 3. 范围与非目标

### 3.1 必须实现

- MCP Streamable HTTP 接入，正式服务地址固定为 `/mcp`，具体宿主配置由 Server 子文档确定。
- 使用会议码和 AI 显示名称加入会议。
- AI 显示名称复用主题三的 NFC、Trim、大小写无关和会议内唯一规则。
- AI 参与者身份与 AI 连接会话分离。
- 连接会话绑定单个会议、单个 Agent 参与者和单个 MCP 传输连接。
- 建立连接时声明并校验上下文字符数。
- 返回最近入会上下文和固定版本的驻留参与指令。
- 以持久化消息事件消费者创建 AI 回复任务。
- 同一个 AI 的任务按触发消息的服务端 `Sequence` FIFO 排队。
- 为每个 AI 独立维护上下文分配位置和实际投递游标。
- 上下文包含游标之后到触发消息为止的所有连续消息，不按作者类型过滤。
- 上下文超限时只裁剪较早消息，触发消息必须完整保留。
- 对外返回候选范围、实际范围、裁剪数量和超限标记。
- 持久化任务状态和投递尝试标识。
- Agent 回复幂等，重复提交同一任务不会产生第二条消息。
- Agent 回复进入主题四的统一消息顺序、提及解析、事件分发和 SignalR 广播。
- 参与者列表显示 AI 连接和处理状态。
- MCP 协议会话明确关闭时更新 AI 状态为断开，不删除参与者关系；单次等待超时或请求取消不等同于连接断开。
- 会议删除清理连接会话、上下文游标、回复任务、投递尝试和相关事件投递。
- 提供可重复运行的最小参考 Agent 或 MCP 测试客户端。

### 3.2 明确不在本主题实现

- 连接租约、连接抢占和严格的多连接竞争治理。
- 原连接断线恢复、新 Agent 使用原名称接管和旧连接失效的完整语义。
- 服务端重启后的 AI 等待恢复、任务恢复和投递重试收口。
- 可靠投递确认、确认超时、崩溃恢复、迟到回复隔离和跨进程可靠租约。
- 多 Agent 同时入会后的独立协作链、协作速率限制和循环保护。
- 平台内置模型、Agent 角色配置、系统提示词、记忆或外部工作目录。
- AI 专用消息表、AI 专用历史流和绕过统一消息追加服务的发送入口。
- 未读数、已读位置、通知、附件、语音和音视频。

以上延期能力不能阻断本主题的协议扩展。连接会话、任务、投递尝试和游标的标识必须稳定，后续主题可以在不改变本主题成功路径的前提下增加租约、恢复、接管和多 Agent 策略。

## 4. 核心设计判断

### 4.1 消息核心只有一套

AI 入会和回复只是主题四消息能力的另一种入口。MCP Handler 负责校验连接会话并转换为受信 Agent 提交；Application 负责会议删除屏障、作者解析、正文校验、序号分配、幂等、提及解析和持久化事件。任何 MCP 工具、后台任务或参考 Agent 都不能直接写 `MeetingMessages`。

AI 回复与人类消息共用：

- `MeetingMessage`、`MessageMention` 和 `MessageDto`；
- 会议范围内的服务端 `Sequence`；
- 主题四统一消息追加核心的事务和幂等边界；
- `MessageAppendedEvent` 和实时广播消费者；
- 主题三和主题四的会议授权、成员隔离和删除访问屏障。

为同时满足主题四的统一消息追加边界和本主题的回复任务原子性，追加能力分为两层：

- `IMessageAppendService` 继续作为 Human/System 等入口的公开应用服务，负责自行取得会议 gate 和事务；
- 主题四追加核心另外提供仅供服务端内部使用的 `IResolvedMessageAppendCore`，只在调用方已经持有的会议事务中工作，不自行开启或提交事务；
- Agent 回复服务是 Agent 回复事务的唯一事务拥有者，在同一个 `IMeetingTransactionRunner` 事务中先调用该核心，再把 `AgentReplyTask` 更新为 `Replied`；MCP Tool、Controller、Hub 和客户端都不能直接调用该核心或写库；
- 该内部契约不接受客户端提供的作者、参与者、序号、时间或消息 ID。作者由 Agent 回复服务根据已验证连接会话解析后传入，消息的提及、幂等、Sequence、事件和 delivery 仍全部由主题四核心完成。

这样，所有入口共享同一套消息事实生成算法，同时避免 Agent 服务调用公开追加服务而产生嵌套事务。

### 4.2 参与者身份和连接会话分离

`MeetingParticipant` 是长期保留的会议内身份。AI 连接会话是某个外部 Agent 当前使用的 MCP 传输实例，连接结束只改变连接状态，不删除参与者，不创建同名参与者。主题五只支持正常的首次连接；重连和接管入口保留稳定字段，但完整语义留给主题六。

### 4.3 持久化事实优先

数据库是 AI 任务、上下文进度和会议删除状态的事实来源。进程内等待信号、连接注册表和 MCP 长调用只负责唤醒或传输，不能作为唯一任务队列。消息保存成功以后，即使实时广播或 MCP 等待信号失败，网页仍可通过历史接口恢复，任务仍可通过持久化状态继续处理。

### 4.4 正常路径简单，扩展位置稳定

主题五在单实例、每个 AI 单个当前连接和 Agent 正常在线的条件下完成业务闭环，但不把这些条件写进跨端字段的身份语义。所有连接、任务和投递标识使用稳定 Guid；状态转换使用显式状态字段；上下文范围使用消息 `Sequence`；这样主题六可以增加租约、可靠确认和恢复而不更换 MCP 基础协议。

## 5. 领域模型

本主题中 `ConnectionSessionId`、`TaskId` 和 `DeliveryAttemptId` 均直接对应各自实体继承自 `BaseEntity` 的主键；这些名称是领域和协议中的语义别名，不是另外生成的一组 ID。`AgentContextCursor` 不作为对外标识，其 `BaseEntity.ID` 仅作为数据库记录主键。数据库索引和排序使用实体主键列时，必须与对应的语义标识保持一一对应。

### 5.1 MeetingParticipant 扩展

继续使用主题三的 `MeetingParticipant` 表示 AI 的会议内身份：

- `ParticipantType = Agent`；
- `UserId = null`；
- `DisplayName` 保存规范化后的原始显示名称；
- `NormalizedDisplayName` 用于会议内唯一比较；
- `MeetingId` 决定所有消息、任务和连接的隔离边界。

同一会议内的 AI 名称与人类名称共享唯一索引和比较规则。加入请求不能提交 `ParticipantId`、`ParticipantType` 或作者身份来绕过名称解析；服务端根据会议码和连接上下文决定目标参与者。

### 5.2 AgentConnectionSession

`AgentConnectionSession` 表示一个外部 Agent 的连接实例，至少包含：

- `ConnectionSessionId`：稳定 Guid，对外工具调用和日志关联使用；
- `MeetingId`、`ParticipantId`：不可变绑定，禁止跨会议或跨参与者使用；
- `Transport`：当前固定为 `mcp-streamable-http`；
- `State`：`Connected`、`Disconnected`；
- `DeclaredContextCharacterLimit`：该连接协商得到的上下文字符数；
- `ConnectedAtUtc`、`LastActivityAtUtc`、`DisconnectedAtUtc`；
- `InitialSnapshotSequence`：该连接首次入会响应使用的历史快照高水位；
- `InitialContextState`：`Prepared`、`Confirmed`；
- `InitialContextConfirmedAtUtc`；
- `McpTransportBinding`：仅用于服务端内部绑定 MCP 传输连接，不对外返回原始传输标识；
- `CreatedAtUtc`。

`McpTransportBinding` 绑定的是 MCP 协议会话，不是某一次 `wait_for_mention` HTTP 请求。单次工具请求超时、取消或网络写入失败只结束该次调用，不自动把连接会话改为 `Disconnected`；只有 MCP 协议会话明确关闭、服务端终止该会话或会议删除时才确认断开。无活动探测、租约到期和异常网络断开的可靠判定由主题六实现。

连接会话不拥有会议参与者名称，不保存完整消息上下文，不以连接会话 ID 代替参与者 ID。主题六可以在此模型上增加租约版本、接管来源和恢复标记。

### 5.3 AgentContextCursor

`AgentContextCursor` 按 `ParticipantId` 保存 AI 的上下文进度，至少包含：

- `ParticipantId`、`MeetingId`；
- `DeliveredThroughSequence`：已经成功构造并投递到 Agent 的最新消息位置；
- `AllocatedThroughSequence`：已经为入会上下文或回复任务分配范围的最新位置；
- `UpdatedAtUtc`。

数据库必须对 `AgentContextCursor (MeetingId, ParticipantId)` 建立唯一约束；一个 Agent 参与者只能有一条当前游标。该唯一约束与 Agent 参与者身份约束不同，不能用“Agent 身份唯一”这句概括替代。

`DeliveredThroughSequence` 和 `AllocatedThroughSequence` 必须分开。任务排队时可以先为后续任务保留独立范围，任务上下文进入当前主题的投递边界后才推进 Delivered 游标；任务状态不能由任一游标推断。加入事务只把 `AllocatedThroughSequence` 初始化到入会快照位置，`DeliveredThroughSequence` 保持 0；Agent 使用加入结果中的连接会话 ID 首次调用 `wait_for_mention` 时，才确认初始上下文已经被 Agent 接收，并把 Delivered 游标推进到快照位置。

### 5.4 AgentReplyTask

`AgentReplyTask` 表示一次消息对一个 AI 目标产生的回复工作，至少包含：

- `TaskId`：稳定 Guid；
- `MeetingId`、`AgentParticipantId`；
- `TriggerMessageId`、`TriggerSequence`；
- `TriggerMentionId`：用于诊断具体提及出现；同一消息多次提及目标 AI 时，固定取 `StartOffset` 最小的提及记录作为代表；
- `SourceAuthorKind`、`SourceParticipantId`：保存触发消息的作者类型和参与者；主题五实际只会写入 Human 作者，系统消息不会创建 AI 任务；
- `CandidateFromSequenceExclusive`、`CandidateToSequenceInclusive`；
- `State`：`Queued`、`Delivered`、`Processing`、`Replied`、`Failed`、`Cancelled`；
- `ReplyIdempotencyKey`：由服务端根据 `TaskId` 派生的稳定 Guid；算法固定为 UUIDv5，命名空间为 `8b8b9e45-72bf-46a0-a9e6-6e0f43f317c0`，名称为 `agent-reply:` 加 `TaskId.ToString("D").ToLowerInvariant()`；只在 `AgentReplyTask` 表内建立唯一约束，不给共享 `MeetingMessage` 表增加全局唯一约束；
- `FailureCode`：仅在 `Failed` 时保存不含敏感信息的稳定失败原因；
- `CreatedAtUtc`、`DeliveredAtUtc`、`ProcessingAtUtc`、`RepliedAtUtc`、`FailedAtUtc`、`CancelledAtUtc`。

约束如下：

- `(MeetingId, AgentParticipantId, TriggerMessageId)` 唯一，保证同一消息对同一 AI 只产生一个任务；
- `TriggerSequence` 是 FIFO 排序事实，不能使用到达时间或客户端时间代替；
- `CandidateToSequenceInclusive` 必须等于触发消息的 `Sequence`；
- 任务可以在 `Delivered` 或 `Processing` 状态保留较长时间，不因上下文游标推进而消失；
- `ReplyIdempotencyKey` 必须等于该任务由服务端派生的值；reply 重放首先按当前任务 ID 查找，再比较幂等键和规范化正文；
- `Failed` 是本主题用于表达失败待处理的状态，后续重试策略由主题六补充；
- `Cancelled` 用于会议删除或其他明确终止，不可再次投递。

`ReplyIdempotencyKey` 必须可在服务重启后根据同一个 `TaskId` 重算，不能使用运行期随机 Guid 或进程内映射。UUIDv5 固定按 RFC 4122 网络字节序实现：把命名空间 Guid 的规范 D 格式解析为 16 个 RFC 4122/network-order 字节，不直接使用 .NET `Guid.ToByteArray()` 的混合端序结果；追加 UTF-8 编码的名称后计算 SHA-1，取前 16 字节，将第 6 字节高 4 位设置为版本 5、将第 8 字节高 2 位设置为 RFC 4122 variant `10`，再按相同网络字节序输出规范小写 D 格式 Guid。生产重算只能使用单一 `AgentReplyIdempotencyKeyFactory`；测试必须包含独立固定向量：`TaskId=00112233-4455-6677-8899-aabbccddeeff` 时结果为 `3502c088-f79d-5fb8-86c8-97b17c43cdc1`。

### 5.5 AgentTaskDeliveryAttempt

`AgentTaskDeliveryAttempt` 记录一次向连接会话投递任务上下文的尝试，至少包含：

- `DeliveryAttemptId`：稳定 Guid；
- `TaskId`、`ConnectionSessionId`、`MeetingId`；
- `CandidateFirstSequence`、`CandidateLastSequence`；
- `DeliveredFirstSequence`、`DeliveredLastSequence`；
- `DeclaredContextCharacterLimit`、`DeliveredCharacterCount`；
- `Truncated`、`OverLimit`、`TruncatedMessageCount`；
- `State`：`Prepared`、`Delivered`、`Abandoned`；
- `CreatedAtUtc`、`DeliveredAtUtc`。

投递尝试 ID 与任务 ID 不可互相替代。重新投递同一任务必须生成新的尝试 ID；同一个正常回复只使用任务的稳定 `ReplyIdempotencyKey`。投递尝试的 `Delivered` 只表示服务端已把持久化上下文交给当前 MCP 工具响应，不表示 Agent 已完成可靠确认；确认超时、重投和接管由主题六补充。

`Abandoned` 是为主题六的接管或重投预留的尝试状态。主题五没有任何正常流程将尝试标记为 `Abandoned`；主题六未来只有在旧尝试被明确弃用并建立替代尝试的同一事务中，才可将未完成的 `Prepared` 或 `Delivered` 尝试转换为 `Abandoned`。主题五不得自行伪造该状态，也不得把它当作回复失败后的自动重试入口。

### 5.6 任务状态转换

正常流程为：

`Queued -> Delivered -> Processing -> Replied`

允许的基础异常转换为：

- `Queued -> Failed`：任务因持久化数据损坏或其他不可恢复的上下文构造错误而失败；当前没有可用连接时任务保持 `Queued`；
- `Delivered -> Failed`：投递准备阶段发生不可恢复的内部错误；
- `Processing -> Failed`：服务端确认任务已经发生不可恢复的内部错误；普通回复校验失败、请求取消和瞬时写入失败不改变任务状态，Agent 可以按原任务和幂等标识重试；
- 任意未完成状态转为 `Cancelled`：会议进入删除清理或服务端明确终止任务。

`Replied`、`Cancelled` 是终态。`Failed` 在主题五中是可见但不可自动恢复的队首阻塞状态，后续 `Queued` 任务不能越过它继续投递；`get_agent_status` 必须返回阻塞任务和 `FailureCode`。主题六再增加 Failed 的恢复、重试和接管转换。重复回复或失败处理必须按任务 ID 幂等，不能把已经回复的任务重新变成排队状态。

`Delivered` 是可持久化、可观察的中间状态，不是只存在于内存的瞬时枚举：领取事务先把任务从 `Queued` 改为 `Delivered` 并保存投递尝试；服务端完成响应对象构造后，再用第二个短事务将 `Delivered -> Processing`、投递尝试 `Prepared -> Delivered` 和 Delivered 游标推进一起提交。两次事务之间发生取消、序列化或内部错误时，任务保持 `Delivered` 并阻塞队首，不自动重投；主题六再处理该状态。

## 6. MCP 协议契约

### 6.1 传输、绑定和版本

正式服务使用 `ModelContextProtocol.AspNetCore` 提供的 HTTP MCP Transport，服务端入口为 `/mcp`。工具名称、成功返回和错误返回使用 `agent-messaging-v1` 协议版本；后续增加可选字段时保持 v1 兼容，改变既有字段语义时建立新版本。

`/mcp` 端点必须显式标记为 `AllowAnonymous`，因为 MCP Agent 不使用浏览器 Cookie，且 join 前尚无 Agent 身份。匿名只表示 HTTP/MCP 请求可以到达工具分发层，不表示工具调用天然有权访问会议：`join_meeting` 必须用会议码完成会议 Active 校验和加入限流；除 `join_meeting` 外的所有工具必须要求当前 MCP 协议会话已经建立唯一绑定，并继续执行连接、Agent、会议和删除屏障校验。`/mcp` 不附加浏览器 Origin 要求，不使用 SPA fallback；会议码是 join 的业务凭据，不作为普通 HTTP 身份或后续工具的替代凭据。

MCP 工具不使用浏览器 Cookie 作为 Agent 身份。加入成功后的工具调用必须同时满足：

1. `ConnectionSessionId` 绑定当前 MCP 传输连接。
2. 连接会话仍属于 `Active` 会议。
3. 参与者仍是当前会议内的 Agent。
4. 会议删除屏障仍允许该操作。

连接会话 ID 不是可脱离 MCP 协议会话长期转移的通用访问令牌。主题六实现接管时可以扩展为明确的新连接凭证，但不能让主题五的工具参数隐式获得跨会议访问能力。

### 6.2 join_meeting

工具输入：

~~~json
{
  "meetingCode": "string",
  "displayName": "string",
  "contextCharacterLimit": 4096
}
~~~

成功结果至少包含：

- `protocolVersion`；
- 会议 ID、名称和可选说明；
- Agent 参与者 ID、固定显示名称和类型；
- 连接会话 ID、当前状态和已声明字符数；
- 当前会议参与者摘要；
- 固定版本的 `residentInstruction`；
- `initialContext`：快照序号、候选范围、实际范围、裁剪元数据和消息列表；
- `residentInstructionVersion = resident-instruction-v1`；
- `nextAction = wait_for_mention`。

加入事务必须完成：

1. 通过会议码找到 `Active` 会议，并执行主题三的加入限流和删除访问屏障。
2. 对显示名称执行主题三统一规范化；空名称、非法长度和同名参与者拒绝。
3. 创建 Agent `MeetingParticipant` 和 `AgentConnectionSession`。主题五不允许已有活动连接再创建第二个同名活动连接。
4. 建立入会历史快照位置，并读取该位置之前的最近连续历史。
5. 创建 `AgentContextCursor`，把初始快照位置写入分配游标，投递游标保持 0；连接会话的初始上下文状态为 `Prepared`。
6. 按协议返回驻留指令、参与者摘要和初始上下文。

如果同名 Agent 参与者已经存在，主题五不创建第二个参与者。已有活动连接返回 `agent_connection_already_active`；已断开连接的接管或恢复语义由主题六正式定义。

有效活动连接的判定必须同时满足持久化连接状态为 `Connected` 且当前进程注册表存在匹配的 MCP 协议绑定；只有数据库中遗留的 `Connected` 记录不视为当前有效连接，启动或 join 路径应将其收敛为 `Disconnected` 后再作恢复边界判断。数据库提交与进程内连接注册不是可跨系统原子事务，因此成功边界明确为“数据库提交和连接注册均成功”。数据库提交后如果注册表注册或绑定提升失败，不向 Agent 返回成功 join，不发布 `online`，清除临时注册，并使用独立 scope 条件更新刚创建的连接为 `Disconnected`；返回可重试的 `agent_operation_unavailable`。如果该补偿写库失败，记录可恢复诊断并由请求尾部或启动协调器继续收敛；在收敛完成前，由于注册表不存在匹配绑定，该记录仍不算有效活动连接，不能永久占用同名 Agent。注册成功后的 `online` presence 和 activity 增量是可恢复通知，不属于 join 的跨系统原子成功条件；通知发送失败只记录诊断，浏览器通过订阅快照最终收敛。该补偿只处理本次新建连接，不实现主题六的连接接管或任务恢复。

### 6.3 驻留参与指令

服务器返回的指令使用固定模板，版本字段固定为 `resident-instruction-v1`，并且至少表达：

- 当前 Agent 已加入哪个会议和使用什么显示名称；
- 只在明确被 `@` 时处理消息；
- 普通未提及消息不会单独触发回复；
- 每次得到任务后都要处理对应上下文，并在回复后继续调用 `wait_for_mention`；
- 回复必须携带服务端返回的任务 ID、投递尝试 ID 和回复幂等标识；
- Agent 的角色、系统提示词、记忆和外部工具由 Agent 自己负责。

指令不是会议消息，不进入 `MeetingMessage`，不会触发任何 AI 任务。

### 6.4 wait_for_mention

工具输入只有当前 `connectionSessionId`。该调用支持长轮询等待，遵守 MCP 请求取消和服务端配置的单次等待上限。服务端首先使用连接会话 ID 确认加入响应中的初始上下文：若 `InitialContextState = Prepared`，则原子改为 `Confirmed`，并把 Delivered 游标推进到 `InitialSnapshotSequence`。之后先查询持久化任务，再等待进程内唤醒信号；信号丢失时重新查询数据库，不依赖内存队列保存任务。

等待只在进入等待前持有一次短的会议共享 gate 租约，用于复查当前 MCP 绑定、会议 Active 状态和删除屏障，并完成必要的初始上下文确认；完成复查后必须释放租约，长轮询期间不得持有 `IMeetingRealtimeGate`。等待被唤醒、超时、取消或收到删除终止信号后，重新取得短租约复查会议状态，再决定返回 `idle`、任务结果或 `meeting_not_found`。任务查询和领取也只在短租约及 Agent 互斥事务内完成，不能把租约延伸到信号等待或响应对象构造。

初始上下文确认事务与任务领取事务分开：确认提交后若进程在领取前退出，`InitialContextState` 保持 `Confirmed`、任务仍保持 `Queued`，下一次等待只重试任务查询和领取，不重复推进初始 Delivered 游标，也不会造成上下文范围空洞。两个事务都不跨越长轮询，并按固定的会议屏障、数据库事务、Agent 互斥顺序执行，避免反向持锁。

等待上限到达且没有任务时，成功返回 `kind = idle` 和 `nextAction = wait_for_mention`，连接会话继续保持 `Connected`。Agent 应立即再次调用等待。单次请求被取消时不修改连接状态；MCP 协议会话明确关闭时才执行断开流程。

成功结果至少包含：

- `kind = mention`；
- `taskId`、任务状态、触发消息 ID、触发序号、触发提及 ID、来源参与者 ID；
- `replyIdempotencyKey`；
- `deliveryAttemptId`；
- 候选范围、实际投递范围、声明字符数、实际字符数和裁剪元数据；
- 按 `Sequence` 升序排列的上下文消息；
- `nextAction = reply_to_mention`。

上下文中的每条消息至少包含 `messageId`、`meetingId`、`sequence`、作者类型、作者参与者 ID、作者显示名称、正文、提及和创建时间。服务端必须把人类、AI 和系统消息放入候选范围，不能因为消息没有直接提及当前 AI 而过滤。

同一个 Agent 同时只能有一个 `Delivered` 或 `Processing` 任务。`wait_for_mention` 先在 Agent 互斥事务中选择最早可投递任务、构造上下文、创建投递尝试并提交 `Queued -> Delivered`；完成响应对象构造后，再提交 `Delivered -> Processing`、尝试 `Prepared -> Delivered` 和 Delivered 游标推进，然后返回上下文。`Delivered` 是真实可观察的持久化阻塞状态，表示上下文已经准备好但本次投递尚未最终化；`Processing` 表示服务端已把任务交给当前连接处理。最终化事务失败或响应中断时不自动重投，任务保持当前已提交状态并阻止后续任务越过；主题六再负责可靠确认、确认超时、重投和恢复。更早任务未回复时，后续任务保持 `Queued`，不覆盖前一个任务，也不合并两个任务的触发消息。

`reply_to_mention` 只接受 `Processing` 任务。任务仍为 `Delivered` 时，说明投递最终化尚未完成或已形成主题五的队首阻塞，回复统一返回 `task_not_available`，不自动推进状态、不创建新投递尝试；Agent 应通过 `get_agent_status` 观察状态，后续恢复由主题六负责。

### 6.5 reply_to_mention

工具输入：

~~~json
{
  "connectionSessionId": "guid",
  "taskId": "guid",
  "deliveryAttemptId": "guid",
  "replyIdempotencyKey": "guid",
  "content": "string"
}
~~~

服务端流程：

1. 验证 MCP 传输绑定、连接会话、Agent 参与者和 `Active` 会议。
2. 验证任务、投递尝试和回复幂等标识属于同一 Agent。
3. 对已存在的回复按任务幂等键收敛：相同正文返回原消息，正文不同返回 `reply_idempotency_mismatch`。
4. Agent 回复服务从已验证连接会话解析受信作者，并作为唯一事务拥有者进入主题四的 `IResolvedMessageAppendCore`；该内部核心在当前事务中执行消息、提及、Sequence、幂等、事件和 deliveries 的统一追加。
5. 消息、提及、消息追加事件、事件 deliveries 和任务 `Replied` 状态在同一个会议事务中提交；不调用会自行开启事务的公开 `IMessageAppendService`，避免嵌套事务。
6. 提交后由主题四事件分发器广播网页消息并唤醒后续处理器。

服务端派生的 `ReplyIdempotencyKey` 作为本次 Agent 回复传给主题四核心的 `ClientSubmissionId`。消息表仍沿用主题四既有的“会议 + 作者参与者 + 客户端提交标识”复合幂等范围；`ReplyIdempotencyKey` 只在任务表内全局唯一。reply 重放首先按 `TaskId` 定位任务，再验证幂等键和规范化正文；不同任务误用其他任务的幂等键返回 `reply_idempotency_mismatch`，不能覆盖任一任务。客户端不能提交消息 ID、作者参与者 ID、作者类型、序号或创建时间作为事实。

### 6.6 get_agent_status

该工具用于参考 Agent 和人工联调客户端读取当前绑定范围内的状态。返回当前参与者、连接状态、初始上下文状态、按状态分组的任务计数、最早排队或阻塞任务摘要、失败码和上下文游标摘要；不允许查询其他会议或其他 Agent，不返回会议码、Cookie 或其他会议数据。任务计数固定至少包含 `queued`、`delivered`、`processing`、`replied`、`failed`、`cancelled` 六组，均按当前会议和当前 Agent 统计；缺少某状态时返回 0。队首为 `Failed` 时返回任务持久化的 `FailureCode`；队首为 `Delivered` 时返回读模型派生码 `delivery_not_finalized`，但不把该码写回 `AgentReplyTask.FailureCode`。计数是读取时快照，不作为领取锁或任务恢复凭据。

### 6.7 MCP 错误

业务失败使用稳定的结构化错误，不把数据库异常、堆栈、会议码或传输凭证写入错误消息：

| 错误码 | 含义 | 是否允许原请求直接重试 |
| --- | --- | --- |
| `meeting_not_found` | 会议不存在、非 Active 或会议码无效 | 否 |
| `agent_name_required` | 显示名称为空 | 否 |
| `agent_name_invalid` | 名称规范化后非法 | 否 |
| `agent_name_in_use` | 名称已被会议内其他参与者占用 | 否 |
| `agent_connection_already_active` | 同一 AI 已有活动连接 | 否 |
| `agent_recovery_not_supported` | 同名 AI 已存在但当前主题尚不支持恢复或接管 | 否 |
| `context_limit_invalid` | 字符数不在部署允许范围 | 否 |
| `connection_invalid` | 连接会话不存在或不属于当前 MCP 连接 | 否 |
| `task_not_available` | 任务不属于当前连接或状态不允许操作 | 否 |
| `delivery_attempt_invalid` | 投递尝试 ID 与任务不匹配 | 否 |
| `reply_idempotency_mismatch` | 同一任务使用了不同正文或幂等标识 | 否 |
| `message_validation_failed` | 回复正文为空或超长 | 否 |
| `agent_operation_conflict` | 并发状态更新未能安全收敛 | 是 |
| `agent_operation_unavailable` | 暂时无法等待或写入 | 是 |

会议删除中的错误对 Agent 统一表现为 `meeting_not_found`，不泄露会议仍处于清理中的内部状态。

## 7. 上下文规则

### 7.1 字符数定义

`contextCharacterLimit` 表示服务端向 Agent 返回的规范化上下文文本字符预算，v1 按 UTF-16 代码单元计数，不按字节、Token 或模型专有单位计数。具体最小值和最大值由部署配置提供，不能在产品层面假设所有 Agent 使用同一默认值。

上下文预算作用于服务端生成的稳定消息块。v1 使用唯一的 `agent-context-v1` 格式：每条消息按以下顺序生成 `messageId=<Guid>\nsequence=<long>\nauthorKind=<human|agent|system>\nauthorDisplayName=<escapedDisplayName>\ncontent=<escapedContent>\n`，最后一个换行是块终止符，消息之间不追加额外分隔符。块值只转义反斜杠、回车和换行：分别替换为 `\\`、`\r` 和 `\n`；结构化响应中的显示名称和正文仍保留原始规范化值。字符预算只统计转义后的规范化消息块 UTF-16 code unit 数，不统计 JSON 字段名、数组标记或传输编码。join 和 wait 返回结构化消息列表，并在上下文元数据中返回 `contextFormatVersion=agent-context-v1`；Agent 可以依据相同字段顺序、规范化规则和转义规则复算 `DeliveredCharacterCount`。

### 7.2 初始上下文

加入事务取得一个明确的 `InitialSnapshotSequence`。服务端从 `Sequence <= InitialSnapshotSequence` 的历史中选择最近连续消息，得到入会上下文。超过预算时从较早端开始裁剪，不分页补发被裁剪历史。初始上下文没有触发任务；加入事务把分配游标推进到快照位置，但只有 Agent 使用返回的连接会话 ID 首次调用 `wait_for_mention`，才能确认初始上下文并推进 Delivered 游标。若加入响应未被 Agent 收到，不会仅因服务端已经构造响应而把该上下文标记为已确认。

为避免入会时序产生空洞，参与者创建、连接会话创建和快照边界必须由同一个会议事务确定。本主题不为 AI 首次加入自动追加系统消息；网页通过参与者列表和 Agent 状态事件观察入会。后续如增加该系统事实，必须通过主题四的统一系统消息入口追加，并保证它不产生 AI 任务。

### 7.3 触发任务候选范围

消息事件分发器不承诺按会议 `Sequence` 调用消费者，因此任务消费者不能把“当前收到的事件”直接当作下一个任务。处理一条人类消息事件时，消费者先取得该消息提及的各个 Agent 目标，再对每个目标执行独立的参与者级顺序补齐事务：

1. 读取并锁定该 Agent 的 `AllocatedThroughSequence`。
2. 查询 `Sequence > AllocatedThroughSequence` 且 `Sequence <= 当前事件 Sequence` 范围内，所有由 Human 作者、明确提及该 Agent、尚未物化为任务的消息。
3. 按 `Sequence` 升序逐条创建缺失任务；即使较早消息的 delivery 尚未被分发，它的消息和提及记录也已经持久化，可以在本次补齐中被发现。
4. 第一个任务以当前 Allocated 游标为 `CandidateFromSequenceExclusive`，后续任务以前一个任务的 `TriggerSequence` 为起点；每个任务的终点等于自身触发消息序号。
5. 每创建一个任务就顺序推进 `AllocatedThroughSequence`；范围内没有缺失任务时保持游标不变。
6. 范围内所有已保存消息都参与后续上下文构造，包括 Human、Agent 和 System 消息；触发任务的作者限制不等于上下文过滤。

数据库唯一约束 `(MeetingId, AgentParticipantId, TriggerMessageId)` 和参与者级互斥事务共同保证并发消费者幂等。较晚事件先到时会补齐此前缺失的任务；较早事件随后到达只会确认任务已经存在，不会倒退游标或形成 `(20, 10]` 一类非法范围。若上一任务仍在处理，后续任务仍可按序持久化，但保持 `Queued`，等待前序任务完成。

### 7.4 裁剪算法

对候选消息按 `Sequence` 升序读取，再从触发消息向前构造最近连续后缀：

1. 触发消息必须完整加入，即使单条消息本身超过字符预算。
2. 从触发消息的前一条候选消息开始，只有加入后总长度不超过预算时才继续向前加入。
3. 一旦更早消息无法加入，停止向前扩展，不跳过该消息去选择更早消息。
4. 实际投递顺序仍按 `Sequence` 升序。
5. `truncated` 表示候选消息数量大于实际投递消息数量。
6. `overLimit` 表示触发消息本身超过字符预算；此时仍完整投递触发消息。
7. `truncatedMessageCount` 表示没有投递的较早候选消息数量。

上下文元数据必须同时给出候选首尾序号、实际首尾序号、声明字符数、实际字符数、`truncated`、`overLimit` 和裁剪消息数量。这样 Agent 可以区分“没有更早消息”和“更早消息因字符数被裁剪”，也可以使用稳定消息 ID 识别重复投递。

## 8. 提及任务创建与 FIFO

### 8.1 持久化事件消费者

主题五新增消息事件消费者 `agent-mention-task-v1`，注册到主题四的 `IMessageEventConsumerCatalog`。它只处理主题五启用后产生的消息追加事件，不回溯历史消息创建任务。消费者流程为：

1. 检查会议仍为 `Active`；否则完成本次 delivery，不创建任务。
2. 按 `MessageId` 回查完整消息和提及记录。
3. 主题五只允许 Human 作者触发任务；System 消息不触发任务，Agent 消息继续保留主题四的通用提及记录，但本消费者跳过任务创建。
4. 按 `TargetParticipantId` 去重，同一消息对同一 Agent 只创建一个任务。
5. 只为当前会议内仍存在的 Agent 参与者创建任务。
6. 在 Agent 的顺序补齐事务中查询到当前事件序号为止的全部缺失 Human 提及，按序记录候选范围、推进分配位置并保存任务。
7. 提交任务后唤醒对应连接的等待调用。

任务创建成功后才完成 `MessageEventDelivery`。创建任务失败由主题四事件分发器的持久化 delivery 重试边界承接；不能把任务创建改成只存在于进程内的回调。

### 8.2 任务去重和来源

任务保存触发来源参与者和消息作者类型，便于网页显示和后续协作链扩展。主题五只把 Human 消息中的合法 `@AI` 物化为回复任务；System 消息不触发，Agent 消息中的提及继续由主题四保存为通用提及记录。主题七连同协作链标识、速率限制和循环保护一起启用 Agent 消息触发其他 Agent，避免在没有保护机制时提前开放自提及或相互提及循环。该阶段性过滤不改变总体产品最终支持 Human 和 Agent 消息触发 AI 的跨主题契约。

### 8.3 FIFO 调度

一个 Agent 同时只能领取最早的 `Queued` 任务。任务排序使用 `(TriggerSequence, TaskId)`，正常情况下 `TriggerSequence` 已唯一，TaskId 只作为稳定的并列排序兜底。任务领取、创建投递尝试和更新任务状态必须在同一 Agent 互斥边界内完成。若队首存在 `Failed`、`Delivered` 或 `Processing` 任务，后续 `Queued` 任务不得越过它被投递。

AI 回复延迟不能阻塞人类发送，因为人类消息只需完成消息追加和事件登记；事件消费者与 Agent 等待调用异步处理。

## 9. 网页状态和实时展示

### 9.1 参与者读模型

主题四已有参与者列表继续作为页面入口，`MeetingDetailsDto` 和 `ParticipantDto` 仍只表达长期身份，不把短暂连接状态写进参与者实体。主题五增加独立的 `AgentActivityDto` 读模型：`meetingId`、`participantId`、`state`、可空的 `activeTaskId`、可空的 `failureCode` 和 `changedAt`。状态包括：

- `waiting`：MCP 连接在线，当前没有 `Delivered`、`Processing` 或 `Failed` 队首；
- `processing`：连接在线，队首为 `Processing`，表示服务端已把任务交给当前连接处理；
- `blocked`：连接在线，但队首为不可在主题五自动恢复的 `Delivered` 或 `Failed` 任务，后续任务被阻塞；`activeTaskId` 指向阻塞任务。`Failed` 使用任务持久化的 `FailureCode`，`Delivered` 使用读模型派生码 `delivery_not_finalized`，不修改任务持久化状态或失败字段；
- `disconnected`：当前没有有效 MCP 连接，参与者和历史仍保留。

AI 处理状态不是消息作者字段，不写入历史消息，也不改变参与者的长期身份。人类参与者不返回 `agentState`。

### 9.2 实时事件

复用主题四的 `participantPresenceChanged` 表达通用在线或离线通知，并新增版本化的 `agentActivityChangedV1` 事件，载荷使用包含 `meetingId` 的 `AgentActivityDto`。Human 的 presence 仍由浏览器连接注册表产生；Agent 的 presence 由 MCP 协议会话注册表产生，只有 `join_meeting` 成功且数据库连接会话与当前协议会话绑定完成后才发布 `online`，MCP 协议会话明确关闭或服务端终止时发布 `offline`，不改变既有 `ParticipantPresenceDto` 字段语义。客户端自己的 connection generation 不属于服务端事件字段，由连接适配器在派发 reducer action 时补充。对 `ParticipantType.Agent`，客户端必须以 `AgentActivityDto.state != disconnected` 作为当前在线判定，`participantPresenceChanged` 只能作为兼容通知，不能覆盖较新的 activity 状态。

主题五以向后兼容的字段扩展方式为主题四已冻结的 `MeetingChatSubscriptionDto` 增加 `AgentActivities`，浏览器 `SubscribeMeeting` 响应同时返回既有的 `onlineParticipantIds`（`Guid[]`，在 JSON 中为 GUID 字符串数组）、`highWatermarkSequence`（`long`）和新增的 `agentActivities`（`AgentActivityDto[]`）快照；前两个字段的既有语义保持不变。`onlineParticipantIds` 继续表示主题四浏览器连接注册表中的在线参与者，不因 MCP Agent 加入而扩展其来源，不能用于判定 Agent 离线；Agent 的在线与活动状态只由 `agentActivities` 快照和 `agentActivityChangedV1` 增量恢复。该 DTO 是 SignalR Hub 契约，不属于 OpenAPI/Kiota 生成范围；客户端使用显式的 TypeScript SignalR 镜像类型，服务端序列化和真实 Hub 集成测试共同验证字段。快照由持久化任务状态和当前 MCP 协议会话注册表共同计算；它是页面刷新和 SignalR 重连后的初始 AI 状态来源，不新增允许浏览器读取 MCP 连接细节的公共接口。若实时连接不可用，页面保留参与者身份并显示实时连接不可用，不猜测 Agent 当前活动状态。

网页先合并订阅快照，再接收 `agentActivityChangedV1` 增量事件；两者都按 `participantId` 合并到当前会议状态。对 Agent，activity 是在线和处理状态的权威来源；若 `participantPresenceChanged(offline)` 与较新的非 `disconnected` activity 冲突，或 Agent 不在 `onlineParticipantIds` 中，客户端都不得据此覆盖 activity。切换会议、删除会议、注销或重连时必须丢弃旧会议迟到事件。状态事件先于新参与者详情到达时，客户端暂存该参与者状态并触发既有会议详情刷新，不能丢失状态或创建浏览器侧参与者身份。

AI 回复仍通过主题四的 `messageAppended` 显示，不增加 AI 专用消息事件。

### 9.3 页面范围

本主题不增加独立的 AI 配置页。用户在现有会议工作台中看到 AI 类型标识、在线状态、等待或处理状态，以及进入统一时间线的 AI 回复。AI 处理延迟不阻塞人类继续发送消息。MCP 会议码和 Agent 连接参数不写入网页 URL、消息内容、浏览器持久化存储或普通日志。

## 10. 安全、授权和限流

1. MCP 加入必须经过会议码校验、会议 Active 校验和 Agent 名称唯一校验。
2. 加入、等待、回复和状态读取都必须经过统一会议删除屏障。
3. 连接会话只能访问其绑定会议、绑定 Agent 参与者和被投递给它的任务。
4. 回复入口忽略客户端提交的作者身份、参与者类型、消息序号、消息时间和消息 ID。
5. 会议码不在错误、日志、任务上下文或网页实时事件中回显。
6. MCP 入口按来源地址、会议码摘要、显示名称摘要和连接会话配置限流；限流失败不创建参与者、连接或任务。
7. 上下文和回复正文按主题四消息长度规则校验；日志只记录会议 ID、参与者 ID、连接会话 ID、任务 ID、投递尝试 ID、序号、状态和 traceId 等摘要。
8. 不能把完整消息正文、会议码、Cookie 或外部传输令牌写入结构化日志。
9. MCP 错误不泄露会议是否存在、删除进度或数据库约束细节。
10. 参考 Agent 使用环境变量或显式输入提供会议码，不把真实会议码提交到仓库。

会议码仍是首版外部 Agent 的主要凭证，知道会议码和可用显示名称的外部连接可以申请加入。该信任模型与需求文档一致；复杂身份认证和接管治理留给后续主题。验收时必须明确告知：主题五阶段一次传输级重连若无法恢复原 MCP 协议会话，可能留下 `Disconnected` 的同名 Agent，新的同名 join 会返回 `agent_recovery_not_supported`；这不是主题五内的隐藏恢复能力，主题六才收口。

## 11. 删除闭环

主题五新增的会议从属资源包括 Agent 连接会话、上下文游标、回复任务、投递尝试，以及消息事件消费者产生的 Agent 任务 delivery。Agent 参与者仍属于主题三的统一参与者表。实现时不是假设存在可插拔的“资源登记”机制，而是直接修改 `MeetingDeletionRecoveryService`：在调用主题四 `IMessageCleanupService.DeleteForMeetingAsync` 之前调用 Agent 资源清理服务，显式处理 Agent 任务 delivery、投递尝试、回复任务、上下文游标和连接会话。

删除流程扩展为：

1. 会议级实时 gate 取得独占关闭权，先提交 `Active -> Deleting` 屏障。
2. 所有 MCP 工具、AI 任务消费者和 Agent 消息追加入口立即拒绝新的会议操作。
3. 向当前 MCP 连接发出删除终止信号，取消长时间等待，释放连接注册表资源。
4. 将未完成的 `Queued`、`Delivered`、`Processing` 和 `Failed` 任务标记为 `Cancelled`，防止再次投递或回复；`Replied` 和已取消任务只做幂等清理。
5. 在 `MeetingDeletionRecoveryService` 中先按“投递尝试 -> 回复任务 -> 上下文游标 -> 连接会话 -> Agent task delivery”的顺序执行 Agent 资源清理；由于任务的 `TriggerMessageId` 对共享消息有 Restrict 外键，必须在主题四消息清理之前完成。
6. 继续执行主题四 `IMessageCleanupService.DeleteForMeetingAsync` 的消息、提及和消息事件清理，再执行主题三参与者和会议回执的既有清理。
7. 清理失败时保持 `Deleting`，不能重新开放会议；删除恢复器重启后继续执行。

迟到的 `reply_to_mention`、事件消费者和网页实时广播在 Active 检查失败后只能返回或忽略，不能重新创建消息或任务。删除成功后，旧会议码、旧连接会话 ID、旧任务 ID 和旧投递尝试 ID 都不可恢复该会议。

## 12. 服务端和客户端边界

### 12.1 服务端分层

服务端沿用现有模块化单体分层：

- MCP Transport 层注册工具、读取调用上下文和绑定取消信号，不实现会议规则。
- Agent Collaboration Application 编排入会、等待、回复和状态查询。
- Domain 表达连接会话、游标、任务和投递尝试的状态与不变量。
- Repository 提供会议范围查询、任务 FIFO 查询、幂等约束和删除清理。
- RealtimeChat 通过现有统一消息追加、提及和事件分发能力写入共享消息流。
- Background/Event 消费 `agent-mention-task-v1` delivery，创建任务并唤醒等待者。
- Deletion 扩展会议删除协调器和恢复器。

MCP 工具不能引用 EF Core DbContext 直接写实体，也不能在工具方法内复制主题四的提及解析、消息顺序和幂等算法。

### 12.2 客户端边界

客户端继续使用会议工作台的 `meetingId` 状态边界。主题五只扩展参与者读模型、Agent 状态事件和消息展示，不在浏览器中保存 AI 上下文、任务正文或 MCP 凭证。

客户端收到 Agent 回复时沿用主题四消息 Map 的 `messageId` 去重、`sequence` 排序和重连补齐逻辑。AI 状态事件必须匹配当前会议和当前连接 generation，不能因为切换会议后的迟到事件污染新会议。

### 12.3 后续子文档

本主题设计确认后建立：

- `Server/design.md`：MCP 宿主、依赖注入、数据库迁移、事件消费者、删除恢复和配置。
- `Server/Modules/AgentCollaboration/design.md`：Agent 入会、连接会话、上下文、任务状态、MCP DTO、Agent 回复追加和错误映射。
- `Client/design.md`：会议工作台接入、参与者状态读模型、SignalR 事件和刷新恢复。
- `Client/Modules/AgentCollaboration/design.md`：网页 Agent 状态合并、状态事件和任务可见性。
- `ReferenceAgent/README.md`：最小可执行 MCP 联调 Agent 或测试客户端的启动、加入、等待、回复和重复运行方式。

本主题不为 AI 建立独立页面；参与者列表、消息时间线和会议状态区是现有工作台的扩展点。

## 13. 主题依赖和后续输出

本主题依赖：

1. 主题二的服务端配置、错误处理和日志边界；浏览器 Cookie 不作为 MCP Agent 身份。
2. 主题三的会议码、会议 Active/Deleting 屏障、参与者名称规范化、Agent 参与者类型和会议隔离。
3. 主题四的统一 `MeetingMessage`、`MessageMention`、`MessageDto`、消息追加服务、服务端 `Sequence`、历史接口、SignalR 广播和持久化消息事件。

本主题向主题六输出：

- 稳定的 `ConnectionSessionId`、`TaskId`、`DeliveryAttemptId` 和 `ReplyIdempotencyKey`；
- 参与者级上下文分配位置和 Delivered 游标；
- 按触发消息序号保存的连续候选范围；
- 明确的 `Queued`、`Delivered`、`Processing`、`Replied`、`Failed`、`Cancelled` 状态；
- `join_meeting`、`wait_for_mention`、`reply_to_mention` 和 `get_agent_status` 的 v1 字段；
- 会议删除时终止 MCP 等待、取消基础任务并清理新增资源的入口。

主题六不得通过增加一个同名参与者来实现重连或接管，也不得改变 AI 回复进入统一消息流的事实。主题七在此基础上扩展不同 Agent 之间的独立任务、协作链和循环保护。主题五完成只代表正常 AI 参会、Human `@AI` 任务排队和回复闭环完成，不代表原始需求中服务重启恢复、连接接管、AI 间触发、循环保护和最终删除收口已经完成；首版整体验收必须在主题六、主题七完成后进行。

## 14. 完成门槛

主题五只有在以下结果全部成立时才算完成：

- 参考 Agent 或 MCP 测试客户端可以使用真实会议码加入会议，并在网页参与者列表中出现一个 AI 参与者。
- 加入结果包含稳定连接会话 ID、上下文字符数、驻留指令、参与者摘要和入会历史。
- 入会历史按声明字符数返回最近连续消息，裁剪时提供候选范围、实际范围和裁剪元数据。
- Agent 未被 `@` 时保持等待，不因为普通消息或系统消息单独创建任务。
- 人类发送 `@AI名称` 后，提及记录和 Agent 回复任务持久化存在，任务状态可见且按消息序号 FIFO 排队。
- 任务上下文从该 Agent 的上下文位置开始，包含期间所有人类、AI 和系统消息，并完整包含触发消息。
- Agent 能够从等待结果取得任务并提交幂等回复，回复以 Agent 作者进入统一消息流，并实时显示在所有在线浏览器中。
- 同一消息重复提及同一 AI 只产生一个任务；不同消息的多个提及不覆盖、不合并、不丢失。
- AI 回复延迟不阻塞人类发送；处理期间网页显示 AI 处理状态。
- 单次等待超时返回 idle 并允许 Agent 继续等待，单次请求取消不误判为断开；MCP 协议会话明确关闭后参与者保留、网页显示断开状态，异常网络断开的可靠探测和完整恢复语义不在本主题验收范围。
- `wait_for_mention` 响应中断时，已进入 `Processing` 的任务和投递尝试继续持久化并阻塞后续任务，不被误判为已回复或重新排队；自动确认超时和重投不在本主题验收范围。
- 跨会议连接会话、任务、消息和状态完全隔离。
- 会议删除后，网页和 MCP 都不能继续读取、等待、回复或创建该会议的 AI 任务；基础 AI 资源被删除或取消，旧会议码不能恢复。
- AI 回复、消息事件和网页广播均复用主题四的统一边界，没有 AI 专用消息表或平行消息流。
- 主题二、主题三、主题四的核心回归场景继续通过。
- 本主题的触发范围明确为 Human 消息中的合法 `@AI`；Agent 消息中的通用 mentions 仍进入主题四消息模型，但 Agent 间触发属于主题七，不影响主题五完成判定。

自动化测试矩阵、手工浏览器验收步骤、参考 Agent 脚本和具体实现顺序在本主题设计审阅通过后分别写入 `impl.md`、`testPlan.md`、Server/Client 子文档和 `ReferenceAgent/README.md`。
