# AI 任务连续性与接管主题设计

## 1. 定位与依据

本目录编号 007，对应总体计划中的主题六“AI 任务连续性与接管”。本主题设计已冻结，当前已接入生产可靠工具、持久化恢复状态、连接/任务扫描、版本化 activity 与可靠参考客户端；实现进度见 impl.md。文中能力、配置和测试是本主题完整实施要求，不代表已经全部实现或验收通过。

依据：[产品需求](../../需求分析/圆桌会议网站需求.md)、[总体计划](../001-总体计划/design.md)、[AI 参会设计](../006-AI参会/design.md)、[现行 MCP 接入规范](../006-AI参会/ReferenceAgent/README.md)。实现步骤见 [impl.md](impl.md)，验收矩阵见 [testPlan.md](testPlan.md)。

主题五已经建立持久化参与者、连接、双游标、FIFO 任务、投递尝试、回复幂等和统一消息流。当前 wait 在响应返回前完成 Processing 和游标推进，不能证明客户端已收到；已有身份重新加入仍可能返回 agent_recovery_not_supported。本主题在这些实体和服务上扩展可靠恢复。

## 2. 产品范围与确定的语义

1. 原 Agent 临时断线可以恢复原业务连接，保留字符预算、上下文确认位置和未完成任务。
2. 新 Agent 用会议码和原 AI 名称接管，创建新业务连接，重新声明字符预算，继承身份和任务。必须显式请求接管；用户已确认允许立即替换仍在线的旧 Agent。普通 join 不隐式踢走在线 Agent。
3. 同一参与者只有一个当前连接；接管提交成功后，旧连接不能再读取任务、确认、续租或提交回复。
4. 每个任务保持稳定 TaskId、触发消息、候选范围和 ReplyIdempotencyKey；重投创建新 DeliveryAttemptId。
5. 用户已确认：自动重试耗尽后保留失败队首并阻塞后续任务，等待显式重试。断线恢复和接管本身不重置耗尽计数；可恢复的非耗尽任务在恢复后继续。
6. 不设置模型生成回复的固定截止时间；保持有效续租的 Processing 任务可以长期执行。连接存活、收到上下文和完成回复分别判断。
7. 会议删除屏障优先于全部恢复操作，删除后不能由旧会话、重试、接管或重启复活。
8. 继续支持多个 AI 各自独立排队，本主题仍只由 Human 的有效提及创建任务。AI 相互触发、协作链、循环保护归主题七。

本主题不增加平台模型、角色管理、外部记忆迁移、消息补发分页、人工跳过失败任务、管理后台或新传输协议。接管只接续会议身份与服务端任务，不声称恢复外部 Agent 的思考、文件或工具副作用。

## 3. 身份、会话和所有权

### 3.1 标识边界

| 标识 | 生命周期与用途 |
| --- | --- |
| ParticipantId | 同一会议 AI 的长期身份；接管、恢复、重试均不更换 |
| ConnectionSessionId | 一次业务连接；resume 保持，takeover 新建 |
| ConnectionGeneration | 参与者当前连接的单调递增代数；每次新建或接管增加 |
| BindingVersion | 同一业务连接绑定 MCP 传输的版本；resume 到新传输时增加 |
| TaskId / ReplyIdempotencyKey | 单次有效提及的稳定身份与回复去重键 |
| DeliveryAttemptId | 一次具体上下文投递；替代投递必须新建 |
| InitialDeliveryId | 一次连接的入会上下文投递标识；与任务投递分开 |
| operationId | join、resume、显式 retry 的客户端请求去重标识 |

ConnectionGeneration 和 BindingVersion 由服务端分配，均为正整数并遵守现有 JavaScript 安全整数上界，递增溢出时拒绝操作。所有权凭据是“会议 + 参与者 + 当前连接 + 代数 + 当前 MCP 绑定版本”，不能仅靠注册表、ConnectionSessionId 或任务幂等键授权。旧请求即使已进入 Application，也必须在最终事务中重新校验。

### 3.2 原连接恢复与新 Agent 接管

原连接恢复要求客户端保存原 ConnectionSessionId 和恢复密钥。可靠模式 join 前，客户端生成 32 字节加密随机 recoveryKey（base64url）和 operationId，并保存在客户端私有状态；服务端仅保存密钥 SHA-256 摘要，恒定时间比较。恢复密钥只用于证明“继续这次业务连接”，不改变新 Agent 凭会议码和名称接管的产品信任模型。

resume 可以把尚未被替换的 Connected/Disconnected 业务连接绑定到一个新的、尚未绑定其他业务连接的 MCP 会话，保持业务 ID、字符预算和密钥，增加 BindingVersion 并撤销旧绑定。原传输仍可用时直接继续业务调用，不必 resume。恢复不限定固定天数；直到被接管或会议删除。主动关闭 MCP 会话将连接置为 Disconnected，保留恢复资格。丢失私有状态时走显式 takeover，不尝试猜测原密钥。

takeover 必须提交新 operationId、新 recoveryKey、会议码、原名称及新的字符预算；同名 Human 始终拒绝。事务以 Activating 状态创建新连接、增加代数、将旧连接标记 Superseded，并改变当前连接指针。只有传输注册和激活最终化成功才返回接管成功。密钥和业务 ID 都不能授权已被替换的连接。可靠 create 在参与者锁内先收敛已过期或注册丢失的 Connected：已有参与者已升级为可靠模式时，只有当前 Connected、租约未过期且绑定有效才视为活动；已有参与者尚未升级（legacy）时按 Connected 加有效注册判定，不要求其具备租约。此处以已有参与者的 RequiredReliabilityMode 分类，不以本次请求的 reliabilityMode 分类；即使本次 create 请求可靠模式，也不能把无租约的有效 legacy 连接误判为离线；仍在合法激活流程中的 Activating 视为占用。上述两种占用返回 agent_connection_already_active；Disconnected 或已收敛的过期连接返回 agent_takeover_required；takeover 找不到既有 AI 返回 agent_not_found，不能把拼错名称当作新建。legacy 仍保留原错误边界。

同一目标上并发接管按事务顺序生效，最后一次成功提交成为当前连接。操作返回前再查所有权；已经被后续接管替换的请求返回 connection_superseded，不能继续返回任务正文。后续又主动接管属于新的明确操作，不能自动循环抢占。

客户端选择恢复路径时，以持有的凭据和目标操作为准：

| 客户端持有/观察到的状态 | 动作 | 是否创建新业务连接 |
| --- | --- | --- |
| 原协议会话及租约仍有效 | 继续原业务循环 | 否 |
| 原 connectionSessionId + recoveryKey，连接过期、关闭或协议失效 | 新建 MCP 会话，以新 operationId 调用 resume_agent | 否 |
| 丢失 join 响应，但保留原 join 参数、operationId 和 recoveryKey | 重放 join 查询原结果，取得 recovery_required 后走 resume | 否 |
| 仅会议码和原名称，或已丢恢复密钥 | 显式 joinMode=takeover，并提交新密钥与预算 | 是 |
| 收到 connection_superseded | 停止旧循环；不自动恢复或反向接管 | 否 |

agent_takeover_required 是普通 create 的身份冲突结果，不表示持有原密钥的客户端必须放弃 resume。kind=recovery_required 表示命中了自己已提交的 join 收据，只须恢复该连接。

### 3.3 join / resume 响应丢失与收据重放

操作收据使用固定作用域：join 为 (MeetingId, NormalizedDisplayName, operationId)，指纹包含 joinMode、预算、模式和密钥摘要；resume 为 (MeetingId, ConnectionSessionId, operationId)，指纹包含目标 MCP 绑定摘要；retry 为 (MeetingId, ParticipantId, TaskId, operationId)。类型由独立 OperationKind 区分。相同 ID 的不同参数返回 operation_conflict；joinMode 不作为唯一键的一部分，不能用同一 ID 把 create 改成 takeover。

收据保存不可变的原操作结果，包括 ResultConnectionId、ResultBindingVersion 或 ResultRetryCycle。业务已完成后重放收据只查询结果，不再次执行绑定替换、失效任务或重置计数。所有重放仍先校验会议和当前身份；不能因为命中收据就绕过接管或删除屏障。

join 响应完全丢失时，客户端保留原参数、operationId 和 recoveryKey 重试。若当前传输仍是原结果绑定且激活已完成，返回原初始快照和连接结果，并注明 replayed=true。若换了 MCP 会话或原绑定已离线，验证密钥后只返回 kind=recovery_required、connectionSessionId、nextAction=resume_agent，不返回上下文、不改变绑定；客户端再以新的 operationId 显式 resume。若连接已被接管，返回 connection_superseded，绝不重演 takeover。

resume 响应丢失时，同一 operationId 在原目标 MCP 会话且 ResultBindingVersion 仍是当前有效版本，返回相同结果。目标传输不一致返回 operation_conflict；原结果版本已经被后续 resume 替换或离线时返回 operation_superseded，不能重新激活它。客户端真正开始下一次恢复才生成新 operationId。retry 的旧收据只返回原周期及 currentRetryCycle/replayed，当前状态已经变化时 nextAction=get_agent_status，不再次重置计数。

注册阶段尚未完成的收据返回 operation_pending；只允许在原目标绑定和原结果版本仍匹配时完成该次激活，不能创建新版本。收据随会议保留和删除，不记录密钥明文或消息正文。

激活失败端到端流程固定为：原会话重放原 join/resume → 同一版本仍 Activating 时返回 operation_pending 或完成原激活 → 若补偿已成为 Disconnected，原 join 重放返回 recovery_required，原 resume 重放返回 operation_superseded → 客户端结算旧请求并关闭旧 MCP 会话 → 创建新 MCP 会话，用已知 connectionSessionId、recoveryKey 和新 operationId 调用 resume_agent → kind=resumed。补偿后在原已占用会话直接 resume 返回 connection_invalid，不能靠换 operationId 绕过会话占用。

### 3.4 传输绑定约束

同一 MCP 协议会话在其生命周期内只绑定一个业务连接/会议，不得用一次传输同时 join 两个参与者。join/resume 先取得传输级预留，再取得会议和参与者锁；预留覆盖数据库提交、注册和激活最终化；已绑定传输只允许匹配原操作的收据查询或激活最终化，不能在收据分流前一概拒绝所有重放。注册表保留已失效绑定的占用标记直到协议会话结束。新恢复使用新 MCP 会话，不在同一已绑定会话上反复重绑。

传输预留只属于宿主层，并发请求中只有一个可以改变该传输的业务绑定；业务数据库仍以当前连接/代数/版本条件写兜底。绑定不同目标的并发失败请求应在改变任一目标所有权之前拒绝，不能先踢掉目标 Agent 再发现传输已经被占用。

## 4. 持久化模型与不变量

延续现有 Domain、Abstraction、Application、Repository.Sqlite 分层以及 IUnitOfWork / IMeetingTransactionRunner，不另建 AI 消息表或任务通道。

| 模型 | 新增或调整内容 |
| --- | --- |
| AgentConnectionSession | ReliabilityMode、ConnectionGeneration、BindingVersion、RecoveryKeyHash、LeaseExpiresAtUtc；复用 LastActivityAtUtc；增加 DisconnectReason、SupersededByConnectionId；MCP 绑定通过受控 resume 更新 |
| AgentContextCursor | 保留 DeliveredThroughSequence、AllocatedThroughSequence；增加 CurrentConnectionId、ConnectionGeneration、FirstJoinSnapshotSequence、RequiredReliabilityMode、ActivityRevision、ActivityChangedAtUtc |
| AgentInitialContextDelivery | MeetingId、ParticipantId、ConnectionId、InitialDeliveryId、固定快照序号、候选/实际范围、预算、格式版本、确认状态/时间；每个连接一条 |
| AgentReplyTask | CurrentAttemptId、RetryCycle、AutomaticRetryCount、NextRetryAtUtc、RetryExhausted；保留现有身份、范围、失败码及回复消息 ID |
| AgentTaskDeliveryAttempt | ConnectionGeneration、BindingVersion、RetryCycle、ConfirmedAtUtc、ConfirmationDeadlineUtc、InvalidatedAtUtc、InvalidationReason、ReplacementAttemptId、ReportedFailureCode、FailureDecision；保存固定投递窗口与格式版本 |
| AgentRecoveryOperation | join/resume/retry 收据、请求指纹、结果标识、绑定与结果版本；复合关联会议和参与者 |

约束：连接主键与会议、参与者形成复合唯一/外键；当前连接指针必须属于同一参与者；任务当前尝试必须属于同一任务/会议；尝试保存的连接属于任务目标 AI。每个参与者至多一条 Activating/Connected 连接，使用筛选唯一索引，旧连接先退出这两个状态再插入新连接。可靠模式正常业务条件写入校验当前指针、代数、绑定版本、Connected 状态和未过期租约；resume、激活、过期扫描和删除分别使用与转换相符的条件，不能要求“已过期扫描”同时满足“租约未过期”。影响行数 0 表示竞争失败。legacy 不应用新增租约/可靠确认校验，但必须遵守当前连接和绑定隔离；其租约和恢复密钥可空。可靠 Activating 也尚无有效租约，转 Connected 时才初始化租约。

连接新增 Activating 中间态和 Superseded 终态，不复用 Disconnected 表示已被接管。创建/resume 的第一事务进入 Activating，注册成功后条件转 Connected；失败转 Disconnected；接管可将 Activating/Connected/Disconnected 转 Superseded。Activating 不提供业务读取、确认、续租或回复资格。尝试保留 Prepared/Delivered/Abandoned，增加 Confirmed；Prepared/Delivered/Confirmed 在失效后建立替代时都允许转 Abandoned，ConfirmedAtUtc 保留原确认事实；可靠模式 Delivered 只表示服务端已经准备并发布该尝试，不等于接收成功。Abandoned 仍只在同事务建立替代尝试时写入，并记录 ReplacementAttemptId；仅失效但尚无替代时写 InvalidatedAtUtc，不能伪造替代尝试。

游标行是当前连接分配的唯一事实：CurrentConnectionId 与 ConnectionGeneration 成对更新；连接行的 ConnectionGeneration 是该连接获配代数的不可变副本。新建/接管在同一事务内递增游标代数并创建同代连接；resume 只改 BindingVersion，不递增或重写连接代数。以 (CurrentConnectionId, MeetingId, ParticipantId, ConnectionGeneration) 复合外键引用连接对应唯一键，防止冗余字段漂移。历史连接不能反过来决定当前指针。

RequiredReliabilityMode 是参与者级单调门槛，存放在该参与者唯一的游标行。首次显式可靠 join/takeover 的 Activating 事务即设为 explicit_ack_v1，之后断线、激活失败、resume 都不降级；旧库初始化为 legacy_v1。新的 join 请求使用 legacy 模式并命中该已有参与者时，模式校验先于名称/活动冲突返回 reliability_mode_required，不因当前离线而重新开放 legacy。旧客户端正常业务兼容只承诺尚未升级的参与者；已绑定的旧 legacy 连接在被接管后仍先做所有权检查，返回 connection_superseded 和原 Data=null 形状。

CurrentAttemptId 在 Queued 且从未投递时为空；Delivered/Processing 指向本次尝试；Failed 保留失败尝试（构造失败尚无尝试时可空）；Replied 永久保留成功尝试，和 ReplyMessageId 一起作为重放依据，不能清空或替换。Cancelled 保留指针至删除清理，清理阶段显式置空。指针不等同于“尝试仍有权限”，权限还须检查状态、失效字段和当前连接。

ActivityRevision/ActivityChangedAtUtc 放在游标行，是复用“每个 AI 一条持久化协调记录”来原子更新所有权、任务可见状态和版本；它们不属于消息位置，不参与 Delivered/Allocated 的大小比较。收据、历史连接和尝试首版均随会议保留，不按时间或数量裁剪，以保留恢复和幂等证据；单实例小规模下接受线性增长，容量治理不在本主题。

AgentInitialContextDelivery 是所有模式的初始快照和确认状态唯一持久化来源。迁移从连接上的 InitialSnapshotSequence、InitialContextState、InitialContextConfirmedAtUtc 回填后移除这三个重复存储字段，连接 DTO 通过关联投影保留原字段；不维护两套可能分歧的确认事实。InitialDeliveryId 就是新实体主键的协议别名。FailureDecision 保存失败报告首次受理时的周期、是否阻塞及重试时间，以便任务变化后仍能幂等重放。

新实体使用 Guid 主键、UTC 时间和现有序号安全上界。SQLite CHECK 覆盖计数非负、确认字段组合、时间关系和当前尝试关系；不能仅依赖进程锁保证唯一性。

## 5. 上下文连续性与确认

### 5.1 三个不同的位置

- AllocatedThroughSequence：已经为有效提及分配候选区间的位置，任务创建时推进。
- DeliveredThroughSequence：已按当前协议完成投递确认的位置，可靠模式只在显式确认成功时推进；不等待回复。
- InitialSnapshotSequence：本次连接入会时看到的最近历史上界，保存在连接初始投递中。

任务区间沿用创建时的 (CandidateFromSequenceExclusive, TriggerSequence]，以后不能依据当前游标重新推导或改写。事件消费者仍按消息序号补齐缺失任务，断线不停止任务分配。Human、Agent、System 消息均进入上下文候选范围。

### 5.2 首次加入与接管快照

首次创建参与者时，分配位置初始化为快照 S，确认位置为 0；确认初始投递后推进到 S，再领取任务。加入快照、监听边界、连接和初始投递必须同事务建立；确认前到达的新提及从 S 后分配，不漏消息。

接管已有参与者时返回截至新快照 S2 的最近历史，但不改变既有两个游标、任务范围和事件分配边界。确认该快照只确认新连接已接收入会历史，不把 Delivered 或 Allocated 跳到 S2。随后仍领取最早未完成任务，按新预算重新裁剪其固定范围。没有未完成任务时，下一次有效提及仍从既有分配位置建立连续范围。

因此接管快照可能和未完成任务或下一任务的上下文重叠；这是新连接初始化与任务处理两类用途的明确重叠，通过 contextPurpose=initial/task、稳定 messageId 和 replayReason 标识，不能当作重复触发任务。首次连接初始快照尚未确认就被接管时，确认新快照时统一使用 Delivered=max(原 Delivered, FirstJoinSnapshotSequence)，不改变 Allocated，因此只完成原首次加入边界的确认，不能跳过其后的任务。

例：原入会边界为 10，任务 A=(10,15] 已确认但未回复（此时 Delivered=15），B=(15,20] 排队；接管时历史到 25。新连接先读截至 25 的历史，再处理 A、B；任务身份及范围不变，确认 A 使用 max(当前 Delivered,15)，确认 B 推进至 20，不退回 15，也不跳到 25。

### 5.3 投递与显式确认

1. wait 在短事务中领取 FIFO 队首，构造固定上下文窗口，持久化新尝试和确认截止时间，将任务 Queued -> Delivered、尝试直接持久化为 Delivered，返回 nextAction=confirm_context。可靠路径不额外暴露“已提交 Prepared、尚未最终化”的窗口；Prepared 仅兼容旧数据。
2. 返回 HTTP/MCP 响应不推进 DeliveredThroughSequence，不自动切换 Processing。
3. Agent 已收到并保存该上下文及任务标识后调用 confirm_context。服务端在同一事务内确认尝试、Delivered -> Processing，并以 max(原确认位置, TriggerSequence) 推进游标。确认表示已接收，不表示已读完或已完成模型处理。确认截止仅约束第一次 Delivered -> Processing；当前尝试已经确认后，即使超过原截止时间也应幂等返回成功，只要身份、租约及尝试仍有效。
4. confirm 响应丢失可按相同 ID 重放；已确认的当前尝试返回相同确认结果，时间和游标不重复修改。
5. 回复只接受当前有效且 Confirmed 的尝试。未确认返回 context_confirmation_required，不让回复隐式确认。

初始确认使用 kind=initial 和 InitialDeliveryId；任务确认使用 kind=task、TaskId 和 DeliveryAttemptId，两组字段互斥。可靠模式首次 wait 不再隐式确认初始快照；未确认时返回 initial_context_unconfirmed 及 nextAction=confirm_context。

原连接下重复 wait：Delivered 且尚未到确认截止时间时，返回同一尝试及同一窗口，标记 replayed=true，这属于同次响应重放，不创建新尝试或重置截止时间；Processing 时返回 task_in_progress 和状态摘要，不自动把正在执行的任务发给第二个执行器。需要主动放弃处理时调用 report_task_failure。

若 Delivered 的确认截止已过而扫描器尚未运行，wait 必须先调用与扫描器共用的条件失败转换，再按 Failed 处理：未耗尽且退避未到返回 retry_pending，已到退避时间可建立替代投递，耗尽返回 retry_exhausted。不能重放已过期窗口再让客户端确认，也不能等扫描器才能使进度前进。转换使用当前尝试和锁后时间，只生效一次；get_agent_status 仍只读，以 confirmation_overdue 提示该窗口并指向 wait。

### 5.4 裁剪与重投

继续使用 agent-context-v1 和 UTF-16 code unit 预算。按完整消息块从较早端裁剪，保留最近连续消息；触发消息必须完整保留，超预算则 overLimit=true。返回候选/实际首尾序号、字符数、truncatedMessageCount 和格式版本。

同一次尝试响应重放使用持久化窗口和格式版本，从不可修改的会议历史重建，不能按当前预算重裁。新的重投尝试按当前连接预算重建，TaskId、候选区间、幂等键不变，DeliveryAttemptId 必须变化，旧尝试同事务标记 Abandoned。因断线或失败重投已确认的未完成任务是恢复例外，以 replayReason=recovery/retry/takeover 说明；不回退全局游标，不分页补发原本裁剪掉的历史。

## 6. 租约与长驻留

所有 wait 的 mention/idle 响应返回前重新检查会议屏障和当前连接/绑定，可靠模式另外检查租约；挂起前的检查不能授权恢复或接管之后的返回。租约以持久化 LeaseExpiresAtUtc 为事实，当前 MCP 绑定和进程注册表辅助验证。now >= expiresAt 即失效，即使扫描器尚未执行也不能继续读写。注册表残留和进程仍在运行都不能代替续租。

可靠模式激活完成时初始化租约；成功 confirm/reply/失败报告/显式 retry 和 renew_agent_lease 按 max(原 LeaseExpiresAtUtc, now+ConnectionLeaseSeconds) 续租。已完成收据的只读重放不续租；无效字段、旧绑定和失效尝试请求不续租。wait 先校验输入、绑定、初始确认和可领取状态，再按下述等待公式一次续租；返回 task_in_progress/retry_exhausted 等拒绝结果不额外续租，需独立 renew。普通 get_agent_status 保持只读。续租独立于任务确认与任务进度，不能自动确认上下文。

| 配置 | 设计默认值 | 约束 |
| --- | --- | --- |
| ReliableWaitTimeoutSeconds | 30 秒 | 可靠模式默认单次等待 |
| waitTimeoutSeconds | 客户端可选 | 有效范围 1～现有 WaitTimeoutSeconds 上限，取客户端值或可靠默认 |
| ConnectionLeaseSeconds | 120 秒 | 基础租约；默认可靠等待加安全余量必须小于此值，单次较长 wait 按公式扩展，不要求基础值大于所有允许的 wait |
| LeaseRenewIntervalSeconds | 30 秒 | 返回客户端建议，须小于租约时长 |
| LeaseSafetyMarginSeconds | 30 秒 | 用于 wait 截止后的续接余量 |
| ContextConfirmationTimeoutSeconds | 60 秒 | 接收确认时限，不是模型回复时限 |
| RecoveryScanIntervalSeconds | 5 秒 | 有界批量扫描租约和待恢复任务 |
| MaximumAutomaticRetries | 3 次 | 首次投递之外每个重试周期最多 3 次替代投递 |
| RetryBackoffSeconds | 2、5、15 秒 | 持久化 NextRetryAtUtc；不持锁 sleep |

wait 的条件续租值固定为 max(原 LeaseExpiresAtUtc, now+ConnectionLeaseSeconds, 本次 wait 截止+LeaseSafetyMarginSeconds)。后续短续租不得缩短已经承诺的等待租约；业务验证、租约更新共用事务内取得锁后的时间。now>=原租约截止时先拒绝，不用续租覆盖过期事实。

客户端声明的 wait 时长仍需小于宿主与代理限制并留余量，不能靠服务器设置延长宿主硬限制。一次正常 idle 或请求取消不立即离线；无后续有效操作时租约自然到期。等待期间不由服务器无限续租，最多延长至该次请求确定的截止和余量，防止无人客户端永久占用。

客户端根据 renew 响应中的 serverTime 和 leaseExpiresAt 计算剩余时间，本地保存的租约截止也只能取已收到结果的最大值，避免乱序响应使客户端误判过期。Agent 长时间推理时由外部客户端/宿主独立续租；模型本身不必打断推理。支持 MCP 并发续租与串行业务循环，不错误规定 MCP 所有请求串行。不能续租的客户端应配置适合实际执行时长的租约；验收必须证明宿主确实能维持租约，不承诺断线后仍保存外部推理结果。

耗尽后的 await_explicit_retry 只暂停领取和处理任务，参考客户端的续租循环继续调用 renew_agent_lease，直到明确离开、被接管或主动放弃驻留。续租失败或客户端退出后仍按真实租约离线，不把“阻塞必须永远在线”写成服务器保证；队首任务在两种情况下都保留。

配置按以下全集校验，任何服务端配置错误在启动时拒绝：

- 所有时长、限流窗口/阈值和上下文预算为正整数；MaximumAutomaticRetries 为非负整数；计算使用受检算术，拒绝时间相加溢出。
- MinimumContextCharacterLimit <= DefaultContextCharacterLimit <= MaximumContextCharacterLimit；实际 Agent 仍必须声明预算，不能用默认值替代协商。
- 1 <= ReliableWaitTimeoutSeconds <= WaitTimeoutSeconds。
- ReliableWaitTimeoutSeconds + LeaseSafetyMarginSeconds < ConnectionLeaseSeconds。
- LeaseRenewIntervalSeconds + LeaseSafetyMarginSeconds < ConnectionLeaseSeconds；扫描间隔为正，过期判断不能依赖扫描恰好及时。
- RetryBackoffSeconds 非空、元素均为正整数；数组短于最大次数时使用末项饱和，不要求两者长度相等。
- ContextConfirmationTimeoutSeconds 为正；允许大于基础租约，只要客户端持续续租。默认 60 < 120 是部署选择，不是确认与连接状态机的硬依赖。
- 请求 W=waitTimeoutSeconds（省略则可靠默认）必须满足 1 <= W <= WaitTimeoutSeconds，越界返回 agent_request_invalid，不静默裁剪。

服务端无法获知外部宿主/代理的硬时限，不对未知值伪造配置校验。客户端根据已知宿主、代理时限减去本地处理余量，再与期望等待和服务端上限取最小正值；无正值时报告该客户端不可驻留。客户端与服务器的这些不同校验不得混为一条服务端公式。

### 6.1 join、takeover 与 resume 限流

join/create、takeover 和 join 收据查询共用既有 AgentJoinRateLimiter 四维计数：来源、会议码、规范化名称、MCP 传输绑定；继续沿用 JoinRateLimitWindowSeconds=300、PerSource=100、PerMeetingCode=30、PerName=30、PerConnection=10 的默认配置及现有维度作用域。换操作类型、operationId 或新 MCP 会话不能清零仍适用的来源/会议/名称计数。

resume 增加 ResumeRateLimitWindowSeconds=300、ResumeRateLimitPerSource=100、ResumeRateLimitPerConnection=30、ResumeFailureLimitPerSource=20、ResumeFailureLimitPerConnection=10。每次尝试（含收据重放）先消耗来源/业务连接 ID 的尝试计数，密钥错误额外计入失败计数；计数以窗口到期释放，不因单次成功清零，不通过错误暴露目标是否存在。超限在密钥比较和任何所有权修改前拒绝。

超限沿用 agent_operation_unavailable；可靠错误 data 返回 retryAfterSeconds，legacy 保持 Data=null。来源由宿主受信网络上下文提供，不信任工具输入；计数键使用摘要，内存桶有到期清理与容量上限（RecoveryRateLimitMaximumBuckets 默认 10000，正整数），达到容量时拒绝创建新桶。单实例计数重启清零属于现有部署边界，不宣称是分布式持久限流。限流不改变已确认的接管凭据或任务重试计数。

## 7. FIFO、失败和恢复

| 原状态 | 事件 | 结果 |
| --- | --- | --- |
| Queued | 当前连接确认初始上下文后领取 | Delivered，新建尝试 |
| Delivered | 当前尝试确认 | Processing，确认尝试和游标原子提交 |
| Delivered | 确认超时 | Failed，失效当前尝试，记录确认超时与下次时间 |
| Processing | 成功回复事务 | Replied，唯一回复消息和事件同时提交 |
| Delivered / Processing | 连接关闭、租约到期、resume 重绑、接管、进程重启 | Failed，旧尝试失效，保留任务与候选范围，等待恢复重投 |
| Processing | Agent 报告可重试失败 | Failed，失效尝试并安排退避 |
| Failed | 已到重试时间、有当前可靠连接、未耗尽 | 原子建立本次尝试并转 Delivered；有旧尝试时同时替换，没有则首次建立 |
| Failed | 达到自动重试上限或不可重试错误 | 保持 Failed + RetryExhausted，阻塞队列 |
| Failed 且耗尽 | 当前 Agent 显式 retry_task | 增加 RetryCycle，计数归零，设定待重试；响应丢失不重复重置 |
| 任意未完成状态 | 会议删除 | Cancelled 并清理 |

初始投递未确认时反复 join 重放同一快照，等待 confirm 或连接恢复，不创建任务尝试、不消耗任务重试次数。初始投递不适用任务的 60 秒确认截止；请求超时或连接失效时仍保留未确认快照，经 resume 后可以确认，status 明确提示确认初始上下文。

每个 RetryCycle 的首次实际投递不计入 AutomaticRetryCount（包括显式重试开启的新周期）；同周期后续真正建立替代投递时才增加，默认每周期最多首次 1 次加自动替代 3 次。尝试保存 RetryCycle，以便原子判断本周期是否已经投递；离线等待、扫描、同次响应重放和失败报告重放不消耗次数。失败事件按当前尝试条件更新，不能重复计数。断线没有可用连接时不后台制造投递尝试。接管时即使没有待处理任务也正常创建连接和初始快照。

retry_task 只允许当前有效 Agent 对自己的耗尽 Failed 队首操作，不提供浏览器按钮或跳过任务。对非耗尽自动等待中的任务返回 retry_not_required，对 Processing 返回 task_in_progress。默认可重试原因限于确认超时、传输/租约失效、重启和 Agent 显式报告的临时失败；不认识的内部原因作为需人工处理的阻塞原因，不盲目自动循环。对非法的外部 failureCode 则直接返回 failure_code_invalid，不修改任务。RetryExhausted 表示停止自动重试，stopReason 区分次数耗尽和永久失败，FailureCode 保留最近失败原因，不把永久失败伪报成已经重试 3 次。

重试配置允许 MaximumAutomaticRetries=0，此时周期首次失败后直接阻塞。退避数组必须非空且均为正数，第 n 次自动替代使用数组第 min(n,数组长度) 项；显式新周期首次投递立即可领。任务存在数据不变量错误时 CurrentAttemptId 可以为空，状态检查必须允许这种失败形态。

初次 report_task_failure 只允许当前 Processing 且已确认的尝试，原子保存 ReportedFailureCode 和不可变 FailureDecision，再使尝试失效。相同尝试、相同失败码重放时，先验证当前身份，再返回原决定及当前任务摘要；即使新尝试已建立也不能使新尝试失败。改用另一个失败码返回 operation_conflict；其他原因已经失效的尝试不能补报失败覆盖事实。接管、resume、重启和扫描只把仍在 Delivered/Processing 的队首转为恢复失败；已经 Failed 的周期、耗尽标志、失败决定和 NextRetryAtUtc 保持不变。

wait 构造上下文前发生可确定的持久化数据/格式不变量错误时，不能无限保持 Queued；独立短事务将该队首置为 Failed、RetryExhausted=true、FailureCode=context_unavailable，不创建虚假的投递尝试或推进游标。暂时的数据库不可用导致事务回滚并返回 agent_operation_unavailable，下一请求重试即可，不把基础设施短故障写成永久任务失败。

持久化 Task.FailureCode 的固定清单如下；这些是任务失败原因，不能直接当作 MCP 操作 errorCode：

| FailureCode | 来源 | 默认策略 |
| --- | --- | --- |
| confirmation_timeout | Delivered 首次确认截止已过 | 退避重试 |
| transport_closed | MCP 会话明确关闭 | 等待恢复后重试 |
| connection_lease_expired | 未续租导致连接过期 | 等待恢复后重试 |
| connection_resumed | 原业务连接重绑使在途尝试失效 | 当前新绑定下重试 |
| connection_taken_over | 新业务连接接管在途任务 | 新连接初始确认后重试 |
| server_restart | 服务启动收敛前进程在途尝试 | 等待恢复后重试 |
| agent_transient_failure | Agent 主动报告临时失败 | 退避重试 |
| agent_permanent_failure | Agent 主动报告永久失败 | 停止自动重试，显式 retry 才重开 |
| context_unavailable | 可确定的上下文数据/格式不变量错误 | 停止自动重试 |
| internal_failure | 未分类内部业务失败 | 停止自动重试，不输出异常正文 |

次数耗尽保留最近 FailureCode，另以 RetryExhausted=true 和 stopReason=attempts_exhausted 表达；永久原因停止时 stopReason=permanent_failure。由同一失败判定函数统一计算组合：agent_permanent_failure/context_unavailable/internal_failure 优先判为 RetryExhausted=true、stopReason=permanent_failure；其余表内可恢复原因仅在本周期自动替代额度用完时设 true/attempts_exhausted，否则为 false/null 并安排重试。非失败状态不保留停止原因；显式重试按新周期清除耗尽标志与 stopReason。FailureCode 与 stopReason 使用封闭枚举并按表序列化，不能反推或篡改次数；收据重放仍返回已保存的原 FailureDecision。legacy 原有失败原因保留；首次可靠恢复仍认识的原因按表处理，未知旧原因映射 internal_failure 阻塞，不自动吞掉任务。

## 8. 回复幂等和竞争边界

reply_to_mention 保留现有字段。先检查会议 Active、当前连接与绑定、有效租约，再校验任务归属与幂等键。未完成任务仅接受 CurrentAttemptId 且该尝试已确认、未失效。旧连接、旧绑定或旧尝试即使带正确幂等键也不能写入。

当前合法连接查询已 Replied 任务时，可重放已经成功提交的那次尝试和相同规范化正文，返回原消息；这允许回复响应丢失后经 resume 查询结果。先校验当前身份和已成功尝试归属，再作重放，不能让被替换的旧连接借幂等绕过失效检查。不同正文返回 reply_idempotency_mismatch。status 可按 TaskId 查询已完成摘要及 ReplyMessageId，恢复客户端据此避免重新推理；不向网页暴露幂等键或传输绑定。

AI 回复仍由外层 Agent 事务调用 IResolvedMessageAppendCore.AppendAgentAsync，统一追加消息、Sequence、mentions、事件和 deliveries，并同时提交任务 Replied。竞争回复只有一个成功，另一个收敛为重放或冲突。

接管/回复、确认/超时、恢复/删除必须在相同事务序列中竞争：回复先提交则保留该条消息并接管下一任务；接管先提交则旧回复失败；确认是否超时以取得锁后、事务内条件状态写入时的服务端时间为准（与 ConfirmedAtUtc 一致），截止已到的首次确认拒绝；不以客户端时间或不可提前获知的物理提交完成时间判定；删除屏障建立后全部新操作拒绝。已在接管或删除前完成授权并发出的网络字节无法收回，承诺以最终事务及返回前复查为边界。

## 9. MCP 契约与兼容

### 9.1 协议协商

继续使用 /mcp、Streamable HTTP、agent-messaging-v1 外层结构、camelCase、Guid 和现有四个工具名。join 新增可选 reliabilityMode：省略时为 legacy_v1，显式 explicit_ack_v1 启用本主题完整能力；不能静默改变旧客户端 wait -> reply 顺序。响应增加 capabilities 和模式，客户端按能力调用。

旧客户端保留主题五的初始隐式确认、Processing 投递与错误边界，其连接不宣称可靠恢复；lease 仅适用于可靠模式。新 Agent 可用 explicit_ack_v1 显式接管旧模式连接，升级该参与者的后续连接能力。已经启用可靠模式的参与者不允许再降级为 legacy_v1，返回 reliability_mode_required。现行参考客户端在本主题实施时升级为显式确认流程。

### 9.2 工具表

下表说明可靠模式的增量契约；legacy 继续原四工具的响应形状和语义，不增加强制确认字段。

| 工具 | 输入与结果要点 |
| --- | --- |
| join_meeting | 现有三个字段；新增 reliabilityMode、joinMode=create/takeover（默认 create）、operationId、recoveryKey。可靠模式后两项必填；takeover 必须可靠模式。正常激活结果 kind=joined，增加 joinDisposition=created/taken_over、初始投递 ID、租约配置、capabilities；待确认时 nextAction=confirm_context，收据查询分支见下文 |
| resume_agent | connectionSessionId、recoveryKey、operationId；成功 kind=resumed，保留原业务 ID、原预算，返回 bindingVersion、leaseExpiresAt、未确认初始快照（若有）、队首/最近回复摘要、nextAction。不产生新入会历史快照 |
| confirm_context | connectionSessionId、kind；initial 使用 initialDeliveryId；task 使用 taskId、deliveryAttemptId。结果含 confirmed、replayed、确认位置、nextAction |
| wait_for_mention | connectionSessionId；新增可选 waitTimeoutSeconds。legacy 首次 wait 仍隐式确认初始上下文；可靠模式必须显式确认，mention 返回 taskState=delivered、原有任务和上下文字段、confirmationDeadline、replayed、replayReason，nextAction=confirm_context |
| renew_agent_lease | connectionSessionId；返回服务器时间、leaseExpiresAt、建议续租间隔；不领取任务或更新确认位置 |
| report_task_failure | connectionSessionId、taskId、deliveryAttemptId、failureCode=agent_transient_failure/agent_permanent_failure；只允许受控错误码，无任意正文。结果含 retryExhausted、nextRetryAt、nextAction |
| retry_task | connectionSessionId、taskId、operationId；当前耗尽队首重开重试周期。重复请求返回同一周期 |
| reply_to_mention | 保留现有五个输入字段和结果；可靠模式强制已确认尝试 |
| get_agent_status | connectionSessionId、可选 taskId；要求当前有效绑定，可靠模式还要求有效租约，只读返回队首、任务计数、回复摘要、模式、连接原因、租约、确认位置、重试次数和 nextAction |

除 join/resume 外，所有工具均要求当前 MCP 绑定，不接受 recoveryKey 作为普通业务调用捷径。恢复密钥不在成功响应中回显。对 MCP 工具描述更新驻留指令版本 resident-instruction-v2，明确确认、续租、错误分流和停止旧循环。

### 9.3 响应结构与动作

现有 AgentOperationResult 失败时 Data=null，不能直接承载重试时间和下一步。本主题对可靠模式显式扩展：继续保留 protocolVersion/succeeded/errorCode/message/data；成功 data 包含结果，失败 data 使用 AgentRecoveryErrorData，允许 nextAction、serverTime、taskSummary、nextRetryAt、retryAfterSeconds、retryExhausted、currentBindingVersion。未认证失败只返回 nextAction=stop，不泄露任务或连接细节。legacy 错误继续 Data=null。Tool JSON schema 必须真实声明这两类形状，不能只写工具描述。

join 成功正常返回 kind=joined；只查询到需要恢复的结果时成功返回 kind=recovery_required 和连接 ID，不返回初始上下文。该分支说明原 join 已提交，不声称当前传输已入会。join/resume 正常激活返回租约元数据；若初始上下文待确认，nextAction=confirm_context，否则根据队首返回 wait_for_mention/get_agent_status。confirm initial 成功返回 wait_for_mention；confirm task 成功返回 reply_to_mention；reply 成功返回 wait_for_mention。耗尽使用 nextAction=await_explicit_retry，被接管或删除用 stop，这两个值是客户端动作，不是假 MCP 工具名。

已确认初始快照的 join 重放不再返回 confirm_context；返回当前状态导出的动作，避免客户端对历史成功响应重复初始化。任务 confirm 的幂等重放若任务已 Replied 且尝试有效则返回确认事实和 nextAction=wait_for_mention，不再次要求生成回复；已失效或被替代的尝试仍拒绝。

capabilities 固定为字符串数组，可靠模式包含 explicit_context_ack、connection_resume、explicit_takeover、connection_lease、task_retry、client_wait_timeout；legacy 不公布这些能力。客户端入会前通过 tools/list 中的可选字段识别协商入口，入会后以响应模式和 capabilities 为准。基础上下文格式不变，新增 contextPurpose/replayReason 属于外层投递元数据。

### 9.4 字段与 DTO 映射

以下是实施时的固定落点；字段使用 camelCase，时间为 UTC ISO-8601、ID 为 Guid。现有 DTO 未列出的字段与消息模型保持，新增的可靠 DTO 不要求 legacy 客户端消费。可选值统一为缺省或 null，未确认/未过期时间不能伪填当前时间。

| 入口/DTO | 本主题字段及语义 |
| --- | --- |
| AgentJoinRequest / join_meeting.inputSchema | reliabilityMode enum[legacy_v1,explicit_ack_v1]、joinMode enum[create,takeover]、operationId、recoveryKey 均在 schema.properties；可靠模式运行时要求后两项；非法 reliabilityMode/joinMode 枚举或新参数格式归 agent_request_invalid |
| AgentWaitRequest / wait_for_mention.inputSchema | waitTimeoutSeconds 可选整数；legacy 携带非空值返回 reliability_mode_required，不能忽略后仍等待 300 秒 |
| AgentStatusRequest | 可选 taskId；legacy 携带非空值返回 reliability_mode_required，原输入继续原读模型 |
| AgentResumeRequest / AgentRetryRequest | 分别承载工具表中的 connectionSessionId/recoveryKey/operationId 和 connectionSessionId/taskId/operationId |
| AgentConfirmContextRequest | kind=initial/task 及互斥的 initialDeliveryId 或 taskId+deliveryAttemptId；不接受客户端自报游标 |
| AgentConnectionDto | 保留原四字段，可靠扩展 reliabilityMode、connectionGeneration、bindingVersion、leaseExpiresAt、disconnectReason；原 initialContextState 从新初始投递表投影 |
| AgentJoinResultDto（可靠扩展） | kind=joined、joinDisposition、replayed、connection、initialDeliveryId、contextPurpose=initial、initialContext、leasePolicy、capabilities、nextAction；保留 meeting/agent/participants 和驻留指令 |
| AgentJoinRecoveryRequiredDto | kind=recovery_required、connectionSessionId、replayed=true、nextAction=resume_agent；无历史或任务正文 |
| AgentResumeResultDto | kind=resumed、connection、replayed、可空 initialDeliveryId/initialContext、contextPurpose=initial（有初始正文时）、queueHead、lastRepliedTask、leasePolicy、capabilities、nextAction |
| AgentMentionResultDto（可靠扩展） | kind=mention、taskState=delivered、confirmationDeadline、replayed、replayReason、contextPurpose=task；保留 taskId/deliveryAttemptId/replyIdempotencyKey/触发信息/context |
| AgentWaitIdleDto | 保留 kind=idle、nextAction=wait_for_mention |
| AgentConfirmContextResultDto | kind=context_confirmed、确认目标 ID、confirmed=true、replayed、deliveredThroughSequence、nextAction |
| AgentLeaseResultDto | kind=lease_renewed、serverTime、leaseExpiresAt、leaseRenewIntervalSeconds；不含上下文 |
| AgentFailureResultDto / AgentRetryResultDto | kind=task_failure_recorded/task_retry_scheduled、taskId、原 retryCycle、currentRetryCycle、replayed、retryExhausted、stopReason、nextRetryAt、nextAction |
| AgentReplyResultDto | 保留 message/replayed/nextAction；可靠扩展 kind=replied，legacy 不必增加 kind |
| AgentTaskSummaryDto（可靠扩展） | 保留 taskId/triggerSequence/state/failureCode，增加 reason、currentAttemptId、confirmationDeadline、retryCycle、automaticRetryCount、retryExhausted、stopReason、nextRetryAt、replyMessageId、nextAction |
| AgentStatusResultDto（可靠扩展） | kind=agent_status；保留 meetingId/participantId/connection/taskCounts/queueHead/双游标；增加 initialDeliveryId、requestedTask（按可选 taskId）、nextAction。任务摘要不含消息正文 |
| AgentLeasePolicyDto | connectionLeaseSeconds、leaseRenewIntervalSeconds、leaseSafetyMarginSeconds、reliableWaitTimeoutSeconds、maximumWaitTimeoutSeconds、contextConfirmationTimeoutSeconds；来自部署校验后的配置 |
| AgentRecoveryErrorData | 上节所列错误数据，nextAction 必填；只返回当前授权范围内适用字段 |
| AgentActivityDto / 客户端 AgentActivity | 保留旧字段，增加 revision、reason、retryExhausted、nextRetryAt、connectionDisposition；与订阅快照及 agentActivityChangedV1 使用同一类型 |

contextPurpose/replayReason 是 join/mention 等一次投递的外层元数据，绝不加入 AgentContextMessageDto；同一消息在不同窗口用途不同，消息自身没有“被哪次恢复投递”的属性。初次任务 replayReason=null；同次重放为 response_replay，替代投递为 recovery/retry/takeover。工具成功 kind 按上表完整分派，不通过 message 文案判断。

协商入口就是 MCP tools/list 返回的 join_meeting.inputSchema.properties.reliabilityMode 和其他新增参数，不使用未定义的 Tool metadata 或自然语言描述作为机器契约。 join 的新增参数先做语法与形状校验：非法枚举、非 Guid operationId、非规定格式 recoveryKey，以及可靠模式缺少必填新参数，均返回 agent_request_invalid，不用 reliability_mode_required。未知 reliabilityMode 无法建立可靠响应模式时 data=null；可识别为 explicit_ack_v1 时使用 AgentRecoveryErrorData，尚未认证只给 nextAction=stop；legacy 仍为 null。合法参数的模式不兼容才返回 reliability_mode_required。SDK 在业务适配器前拒绝的协议畸形输入仍属于 SDK 协议错误，不伪称已进入业务分流。新增工具有显式 inputSchema；失败外层 schema 与成功 data 的变体均纳入真实 MCP 序列化测试。

### 9.5 get_agent_status 的模式与队首规则

legacy 使用现有状态 DTO 和 Data=null 错误：不增加可靠租约/计数/确认字段，Delivered 队首保留 failureCode=delivery_not_finalized 派生语义；已升级参与者的旧连接仍要执行所有权校验，接管后可返回 connection_superseded，但错误 data 仍为 null。

可靠状态查询只读，FailureCode 只表达已持久化的任务失败，不把等待确认当作失败；派生显示使用 reason：

| 条件 | reason / failureCode | nextAction 与处理 |
| --- | --- | --- |
| 初始快照未确认 | initial_context_unconfirmed / 无任务失败 | confirm_context；仅在已保存初始上下文后确认，正文丢失先走 join 收据或 resume 取回 |
| Delivered 且截止未到 | awaiting_context_confirmation / null | wait_for_mention 重放当前窗口再 confirm；客户端已有完整窗口时可直接 confirm |
| Delivered 但截止已到、扫描未运行 | confirmation_overdue / null | wait_for_mention 负责条件超时转换，status 不写状态 |
| Processing | processing / null | continue_processing（客户端动作），持续 renew；不另领任务 |
| Failed 且可自动重试 | retry_pending / 保留实际原因 | wait_for_mention，按 nextRetryAt 调度且保持续租 |
| Failed 且停止自动重试 | retry_exhausted / 保留实际原因 | await_explicit_retry，持续 renew；stopReason 区分耗尽和永久原因 |
| Queued 或无未完成任务 | waiting / null | wait_for_mention |
| requestedTask 为 Replied | replied / null | 返回成功 currentAttemptId 和 replyMessageId，再 wait，不重新生成回复 |

查询 currentAttemptId 不等于收到上下文，客户端不得只凭 status 的 ID 盲目确认。可靠连接已失效时返回连接错误而不是假成功状态。requestedTask 不改变 queueHead/FIFO，也不赋予访问其他 Agent 任务的权限。

### 9.6 错误分流

| 错误/结果 | 客户端动作 |
| --- | --- |
| idle | 原会话立即继续 wait |
| initial_context_unconfirmed / context_confirmation_required | 保存收到的上下文后显式确认 |
| task_in_progress | 继续已有处理和续租，或显式报告失败；不启动第二个处理器 |
| retry_pending | 按 nextRetryAt 等待并保持续租，再 wait |
| retry_exhausted | 显示阻塞，等待明确 retry_task；持续 renew，不自动重置 |
| connection_expired / connection_disconnected | 尝试 resume；原密钥丢失时需要明确接管 |
| connection_superseded | 终止旧循环，不自动反向接管 |
| operation_pending | 原目标传输短退避后查询同一操作，不重新创建连接 |
| operation_superseded | 停止重放旧操作，查询当前状态；确需恢复时在新 MCP 会话使用新 operationId |
| connection_invalid / resume_rejected | 停止复用错误绑定或密钥，报告实际问题 |
| agent_connection_already_active / agent_takeover_required | 普通 create 失败；显式请求 takeover 才替换或接续旧身份 |
| agent_not_found | takeover 目标不存在，核对名称；不得自动转为 create |
| reliability_mode_required | 已升级参与者须使用可靠模式客户端；尚未升级的 legacy 使用可靠专属参数时，移除该参数继续 legacy，或显式选择可靠模式接入；不自动接管或降级 |
| agent_recovery_not_supported | 仅 legacy：报告旧模式不支持同名恢复；可靠 create 使用 agent_takeover_required |
| delivery_attempt_stale / confirmation_expired | 丢弃旧尝试的提交资格，查询状态并等待当前任务 |
| operation_conflict / reply_idempotency_mismatch | 不改写旧请求内容继续重试，报告冲突 |
| meeting_not_found | 停止业务与续租，释放本地会话 |

验证错误可保留现有细分码。对有效旧绑定且会议已删除优先返回 meeting_not_found；无法证明身份的随机 ID 返回 connection_invalid，不借错误探测会议。协议本身会话丢失仍可能先返回 transport/session 错误，客户端重新初始化后才能 resume。

reliability_mode_required 的两类来源保留同一错误码，但 message 分别说明“该参与者已升级为可靠模式，请使用可靠模式接入”与“此参数需要可靠模式，请移除参数或使用可靠模式接入”。这些提示用于解释，客户端依据请求入口和已协商模式选择动作，不解析文案分支；legacy 错误继续 data=null，不为提示引入新必填字段。

### 9.7 错误码注册表

下表为本主题 MCP 业务错误全集（SDK 自身协议错误不在此表）。R=可靠模式，L=legacy；data 形状 F 表示 R 使用 AgentRecoveryErrorData、L 为 null。无法确认模式的旧工具匿名失败保持 null；新增工具的匿名失败最多给 nextAction=stop。识别原 legacy 连接时，即使已被可靠连接接管，失败仍按 L 形状返回。

| errorCode | 适用入口 | 模式/data | 含义与可重试性 |
| --- | --- | --- | --- |
| meeting_not_found | 全部 | R/L，F | 无会议访问资格/已删除；停止 |
| agent_name_required / agent_name_invalid / agent_name_in_use | join | R/L，F | 缺名称、名称无效或同名 Human；修正输入，不能原样重试 |
| context_limit_invalid | join | R/L，F | 预算非法；修正输入 |
| agent_connection_already_active | join create | R/L，F | 存在有效当前连接/合法激活；不自动重试抢占 |
| agent_recovery_not_supported | join create | L，null | 旧模式同名恢复未支持；改用可靠客户端明确恢复/接管 |
| agent_takeover_required / agent_not_found | join | R，F | 普通 create 命中离线身份 / takeover 无目标；按恢复判据处理 |
| reliability_mode_required | join 或对 legacy 传可靠新能力 | R/L，F | 模式门槛或参数不兼容；切换正确客户端，不原样重试 |
| agent_request_invalid | join 新参数及可靠工具参数 | R/L，F；join 模式未知时 null | 非法 reliabilityMode/joinMode、新参数或互斥字段、operationId/密钥格式/等待预算非法；可靠 join 缺少必填新参数；修正输入 |
| connection_invalid | 全部绑定校验 | R/L，F | ID/绑定非法或目标会话已占用；停止复用该绑定 |
| connection_expired / connection_disconnected | 除 join/resume 的可靠调用 | R，F | 当前租约过期/连接关闭；新会话 resume |
| connection_superseded | 全部所有权校验 | R/L，F | 当前身份已被另一连接接管；停止旧循环 |
| resume_rejected | resume | R，F | 密钥验证失败；不暴露目标存在性，不原样重试 |
| operation_conflict | join/resume/retry/report failure | R，F | 幂等 ID 的参数指纹不同；不可改内容盲重试 |
| operation_superseded | resume 收据重放 | R，F | 原操作结果绑定失效；真正恢复需新操作/新会话 |
| operation_pending | join/resume 激活重放 | R，F | 同版本激活尚未收敛；有上限退避后查同操作 |
| agent_operation_conflict | 并发条件写 | R/L，F | 并发尚未安全收敛；保留参数/幂等键有界退避重试 |
| agent_operation_unavailable | 基础设施、注册、限流 | R/L，F | 暂时不可用；状态查询与有界退避；限流遵守 retryAfterSeconds |
| initial_context_unconfirmed | reliable wait | R，F | 初始上下文待确认；先保存再确认 |
| context_confirmation_required | reliable reply | R，F | 当前尝试尚未确认；先确认 |
| confirmation_expired | task confirm | R，F | 首次确认超时；query/wait 获取当前决定，不能确认旧窗口 |
| delivery_attempt_invalid / task_not_available | task confirm/reply/report/retry/status | R/L，F | ID 不匹配或当前状态/任务不允许；查询合法状态，不能原样忙重试 |
| delivery_attempt_stale | task confirm/reply/report | R，F | 曾有效尝试已失效/替换；按当前任务恢复 |
| task_in_progress | wait/retry | R，F | 队首正在处理；继续原处理和 renew |
| retry_pending | wait | R，F | 退避尚未到；按时间调度并 renew |
| retry_exhausted | wait | R，F | 自动重试已停止；await_explicit_retry 并 renew |
| retry_not_required | retry | R，F | 未耗尽而不需人工重开；遵循当前队首 |
| failure_code_invalid | report failure | R，F | 外部失败类别不在两项白名单；修正输入，不改任务 |
| reply_idempotency_mismatch | reply | R/L，F | 错幂等键或已提交正文不同；不能覆盖旧结果 |
| message_validation_failed | reply | R/L，F | 正文非法；尚未成功提交时修正正文 |

operation_conflict 是不可原样收敛的指纹冲突，agent_operation_conflict 是可退避的并发冲突，保留两者既有/新增名称并明确分工，不统一前缀而破坏既有客户端。表中入口采用 MCP 工具简称；不存在的 L 新工具路径不会执行业务，先报模式/参数错误。

## 10. 服务端恢复、锁与删除

全局锁顺序固定为“目标传输预留（仅 join/resume）-> 会议短共享 gate -> 参与者/名称协调锁 -> 数据库事务”。普通确认、失败、重投、回复和扫描不取得传输预留，不能在持有会议/参与者锁时反向等待它。已有路径需统一顺序，事务内不等待外部网络；普通消息追加沿用会议事务序列。长轮询、模型处理、退避等待不持有这些锁。进程锁减少竞争，数据库条件更新和唯一约束决定事实。

连接激活采用两个短事务，期间一直持有目标传输预留和参与者协调锁，不持有数据库事务跨越注册调用：第一事务写 Activating、当前指针、绑定版本及收据，立即撤销旧所有权；随后注册本地传输，第二事务校验仍是该 Activating 版本且注册未被关闭，再转 Connected、初始化租约、推进状态 revision。旧等待的取消、旧注册清理按版本进行，不可清掉新绑定。只有第二事务与注册都成功才返回业务连接成功。

注册或最终化失败时撤销本次注册，以独立 scope 将该 Activating 版本条件置为 Disconnected；旧连接仍是 Superseded，不能回滚复活。如果失败补偿也失败，保留 Activating，由原操作在原绑定上的重放或启动/定时协调器收敛。收据查询不会自动把已 Disconnected 结果重新激活，必须发起新的 resume。两种协议模式共用这个内部激活边界，legacy 的成功响应和调用顺序保持兼容。

服务启动先迁移与恢复持久化删除屏障，再在允许 Agent 操作前完成连接恢复扫描：将前进程的 Activating/Connected 条件置为 Disconnected，仅失效 Delivered/Processing 尝试并安排恢复；不根据内存注册表为空删除参与者或任务。Replied 永不重排。启动失败时 Agent 接入保持不可用并报告诊断，不带着未收敛所有权接收请求。后续后台扫描使用独立 scope、有限批量、相同参与者锁、条件更新和持久化 NextRetryAtUtc；扫描 Activating 的缺失注册时必须先取得该协调锁，不能把正在提交与注册之间的合法窗口当成遗留记录；信号只是唤醒，丢失后靠数据库重新查询。

租约扫描提交离线状态后取消等待并发布状态；即使通知失败，后续业务校验和订阅快照都按数据库过期时间判离线。恢复成功后必须由客户端确认初始上下文（若未确认）并 wait 才真正投递，后台不主动调用外部模型。

删除沿用主题三的 Active/Deleting 与可重入清理，新增顺序为：建立屏障并终止等待/续租/扫描输出，取消未完成任务，显式清空 AgentContextCursor.CurrentConnectionId 和 AgentReplyTask.CurrentAttemptId，先清除 ReplacementAttemptId、SupersededByConnectionId 等同表引用，再删除恢复收据、初始投递、任务尝试、任务、连接、游标，再按既有流程删除消息与参与者。迁移检查复合外键顺序。删除中崩溃由既有恢复器重入；本主题只验收新增恢复资源及其与既有删除屏障的集成，包含这些资源在删除中重启后的清理；主题七仍负责协作链、循环保护、全部资源组合与发布前全产品删除收口，不能因本主题通过就省略主题七。

## 11. 网页表现

复用会议工作台参与者列表和统一时间线，不新增页面。保留 waiting/processing/blocked/disconnected，投影优先级为 Disconnected/Superseded、Activating、初始上下文未确认、队首状态、空闲；初始或任务确认等待映射 blocked，显示“等待接收确认”；自动退避显示“重试等待”；耗尽显示“任务失败，等待重试”；lease 失效显示“连接中断”；接管后提示“已由新连接接管”，参与者仍只有一条；Activating 显示 blocked/“连接建立中”，presence 为 offline。

Agent activity 新增 reason、retryExhausted、nextRetryAt、connectionDisposition 和单调 revision；revision 与现有序号使用相同安全整数上界。revision 与 ActivityChangedAtUtc 在参与者可见状态事务内一起持久化更新，沿用 agentActivityChangedV1 添加字段，快照返回相同 revision；快照读取前先按同一条件更新边界收敛已过期租约并增加 revision，投影必须在该次一致性读取快照内完成，之后不再按新的本地时间或注册表状态改写同一个 revision 的 payload；普通续租不修改 activity.changedAt。注册丢失先经条件补偿生成新的状态版本，不能仅靠内存注册表使同版本变离线；新客户端忽略较旧 revision，重连以快照恢复。客户端不得用本地到达时间或 changedAt 判定所有权。消息仍按既有 messageId/Sequence 去重。

浏览器不展示 recoveryKey、operationId、MCP 绑定、投递正文副本或幂等键。精确 leaseExpiresAt 等频繁变动不每次续租广播；仅连接有效性、任务阶段和阻塞原因等可见变化增加 revision，状态详情由 MCP 查询。浏览器 Agent presence 从同一 revision 的 activity 派生，不能再用无 revision 的旧 presence 增量覆盖 Agent 在线状态；Human presence 继续原流程。订阅快照中的两者同源产生，避免旧 presence 事件把已离线或已接管 Agent 改回在线。

订阅数据源保持主题五边界：MeetingChatSubscriptionDto.onlineParticipantIds 只来自浏览器 Human presence，不把 MCP Agent 加入该集合；agentActivities 由持久化 Agent 状态快照单独提供。客户端合并时 Human 查 onlineParticipantIds，Agent 查其 activity（Connected 派生状态为在线，Activating/Disconnected 为离线）；二者不互相补全。这里的“同源”仅指 Agent 的界面在线与 activity，不要求把 Human 注册表迁入数据库。

事件版本策略固定为原地兼容扩展 agentActivityChangedV1，不新建 V2：保留现有字段和事件名，新字段增量添加；服务端 DTO、MeetingChatSubscriptionDto.AgentActivities、客户端 mapAgentActivity/AgentActivity/reducer 同步更新。旧客户端可忽略新字段并保留 changedAt 行为，新客户端使用 revision；新客户端连接缺少 revision 的旧服务端时只能按旧协议兼容显示，不宣称本主题可靠状态验收通过。

revision 更新集中到一个事务内状态比较入口，比较可见 payload（不含 revision/changedAt 自身）；只有实际改变才增加 revision 并写 ActivityChangedAtUtc。完整清单如下：

| 变化入口 | 可见变化及 revision 规则 |
| --- | --- |
| 首次 join/takeover 的 Activating 事务 | 新身份建立中或旧状态被接管；更新一次 |
| 激活最终化/失败补偿 | 建立中变等待确认/离线；仅条件写成功时更新 |
| resume 重绑 | 活动状态进入建立中，随后最终化；每次可见转换各更新 |
| 初始 confirm | 移除初始等待确认原因；若 payload 改变则更新 |
| Queued 首次领取/Failed 替代投递 | 队首变等待任务确认，任务 ID/原因变化则更新 |
| task confirm / reply | 等待确认变处理中，或已回复后队首变化；同事务更新 |
| 超时、失败报告、租约/传输失效、takeover 使旧尝试失效 | 根据新的队首和当前连接一次计算最终 payload，避免同事务重复加版本 |
| retry_task | 停止自动重试变可调度；周期若未出现在 activity 且 payload 不变则不增加 |
| 消费者新任务入队 | 仅确实改变可见队首时更新，后排新任务不空转版本 |
| 启动/定时扫描、订阅前收敛 | 条件状态变化才更新；同一过期状态反复读取不递增 |
| 会议删除 | 屏障内可更新终止事实，屏障后不再向外发布 activity；由既有删除通知终止界面 |
| 单纯续租、status、相同确认/回复/收据重放 | payload 未变则不递增、不改 changedAt |

## 12. 升级与完成边界

从主题五 SQLite 副本升级时保留参与者、消息、任务 ID、幂等键和候选区间；旧游标只表示旧协议的服务端投递事实，不伪造客户端确认。旧连接标记 legacy_v1，历史尝试保留旧语义且没有 ConfirmedAtUtc；未完成旧尝试由新可靠连接接管时失效并重投，不能因为旧 Delivered 已推进就跳过。

可靠模式第一次接管尚未确认初始历史的旧参与者，保存原首次加入边界以维持确认/分配不变量。旧库中每个 AI 只有一条合法连接时，用该连接回填当前指针、初始代数、FirstJoinSnapshotSequence、初始投递及其原确认状态；旧任务 CurrentAttemptId 指向其唯一最新尝试，RetryCycle/AutomaticRetryCount 初始化为 0，已回复任务保留成功尝试；没有连接或有多条无法唯一判定的旧连接属于迁移校验错误，必须明确诊断，不能随意选最新记录吞掉异常。

完成必须包含：真实 SQLite 并发与崩溃测试、真实 MCP 响应丢失/恢复、参考客户端续租与恢复、真实模型两轮回复、浏览器接管/离线/阻塞/恢复可见、删除回归和既有主题回归。自动化和手工用例按 testPlan.md 执行。当前设计沿用单实例部署，不宣称支持跨实例注册表、SignalR backplane 或分布式租约。
