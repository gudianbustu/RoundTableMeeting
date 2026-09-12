# AI 参会主题测试方案

当前实现已接入 [008 多 Agent 协作与会议删除收口](../008-多Agent协作与会议删除收口/design.md)：Human 有效提及继续创建根任务；Agent 有效提及仅在会议启用边界之后派生，System 不触发。旧边界内 Agent 不追溯，旧 Human 待办仍补齐。legacy/可靠驻留模板分别为 `resident-instruction-v3-legacy` / `resident-instruction-v3-reliable`，仅可靠 join/resume 声明 `multi_agent_collaboration_v1`；共 9 个工具（既有 4 个、可靠专属 5 个）。来源随任务恢复保持，删除屏障提交返回 202 后仍由后台清理父引用、边界及原有资源；008 的新增验收见其 [testPlan.md](../008-多Agent协作与会议删除收口/testPlan.md)，本文件原有通过证据不代替该验收，旧服务夹具的 v1/v2 仍保留。

## 1. 文档定位

本文定义主题五“AI 参会”的 Domain、Application、Repository.Sqlite、架构边界、MCP HTTP、消息事件消费者、SignalR、客户端、参考 Agent、浏览器和发布回归测试。实施顺序见[主题实现方案](impl.md)，业务契约和范围边界见[主题总体设计](design.md)。

本方案验证外部 AI Agent 作为会议参与者加入、等待 Human @AI、按 FIFO 取得上下文、提交幂等回复并显示在统一会议时间线中的正常闭环，同时验证会议删除和多会议隔离。主题五只把 Human 消息中的合法提及物化为 AI 回复任务；Agent 消息中的通用 mention 记录可以存在，但不在本主题创建任务。

本方案不把以下能力列为主题五成功条件：可靠投递确认、确认超时、自动重投、连接接管、服务重启后的等待恢复、多 Agent 协作链、循环保护、未读状态或 AI 专用消息流。测试中会使用范围扫描确认这些能力没有被提前实现为隐式行为。

自动化入口：`McpAgentContractTests.cs` 验证基础传输和工具契约，`McpAgentLifecycleTests.cs` 验证删除、FIFO、阻塞、并发入会/领取/回复与重启边界；`McpAgentAtomicityTests.cs` 覆盖入会回滚、初始确认与领取事务分离、Delivered 最终化失败、回复回滚和清理失败重试。Application 的上下文和 signal 测试覆盖裁剪、非法连续范围和多等待者终止；`Demo/AI参会联调客户端/index.test.mjs` 验证收到响应头后正文不结束时的超时与协议关闭。真实浏览器和模型 Agent 场景仍按第 16、17 节独立验收，自动化结果不能替代这些场景。

## 2. 测试目标

必须证明：

1. 同一会议内每个 Agent 拥有独立且名称唯一的长期参与者身份、连接会话、上下文游标和任务队列。
2. 首次 join_meeting 在一个会议事务中原子完成参与者、连接会话、游标和初始快照边界；并发同名入会不会产生第二个身份。
3. 初始上下文按明确快照高水位构造，首次 wait_for_mention 才确认初始上下文并推进 Delivered 游标，重复等待不会重复推进。
4. 人类消息中的合法 @AI 由持久化事件消费者创建任务，普通消息、System 消息和主题五阶段的 Agent 消息不会创建任务。
5. 消息事件即使乱序到达也能按 Agent 的分配游标补齐全部缺失任务；并发消费者不会重复创建任务或形成倒置范围。
6. 上下文包含游标到触发消息之间的连续消息，触发消息始终完整保留，裁剪结果和元数据稳定一致。
7. wait_for_mention 按服务端 Sequence 执行 FIFO；Delivered、Processing、Failed 队首会阻塞后续任务，不自动越过或重投；Delivered 是两阶段事务之间真实可观察的持久化状态，并在 Agent activity 中显示为 blocked 而不是 processing。
8. reply_to_mention 严格绑定当前 MCP 协议会话、连接、Agent、任务和投递尝试；回复由 Agent 外层事务调用主题四内部追加核心写入并广播，不产生嵌套事务。
9. 相同任务和幂等键的相同回复只产生一条消息；不同正文或伪造身份不能覆盖既有事实。
10. 浏览器通过订阅快照和 agentActivityChangedV1 增量恢复 Agent 状态，会议切换、刷新、重连和删除不会串会或接收迟到状态。
11. 删除屏障建立后新的 Agent 操作、任务创建、回复写入和状态广播均被拒绝或抑制，新增资源可以按约定顺序重入清理。
12. 主题二至主题四的会议、消息、SignalR、授权、删除、构建和客户端回归继续通过。

## 3. 优先级和完成规则

- P0：跨会议或跨 Agent 数据泄露、同名身份重复、消息/任务重复或丢失、上下文空洞、FIFO 越序、伪造回复作者、删除屏障失效、统一消息流绕过、构建阻断。
- P1：正常协议闭环、连接状态、乱序恢复、裁剪元数据、事件重试、客户端刷新/重连、参考 Agent 联调和真实浏览器流程。
- P2：视觉细节、诊断信息、性能基线、日志可读性和维护性扫描。

所有 P0/P1 用例通过后才能进入主题审阅。单元测试通过但真实 SQLite、MCP HTTP、SignalR 或浏览器闭环失败，仍视为主题失败。已知的环境限制必须记录为明确豁免并得到用户确认，不能以跳过集成测试代替通过。

## 4. 环境与隔离

- 每个服务端测试使用独立临时 SQLite 数据库；不读取或修改开发数据库、发布目录中的数据库或其他测试用例的文件。
- Migration 测试分别从空库和主题四已有数据库副本开始；升级前复制输入库，不能直接修改仓库内固定夹具。
- Application 和 Repository 并发测试为每个操作创建独立 DI scope、DbContext、UnitOfWork 和事务 Runner；不得共享 Scoped DbContext。
- 测试注入可控 TimeProvider、会议 gate、事件 dispatcher、Agent task signal、连接注册表和传输会话访问器；不以固定 sleep 作为唯一同步手段。
- MCP HTTP 测试使用真实 ASP.NET Core host 和真实 Streamable HTTP Transport；工具发现、协议会话绑定、请求取消和会话关闭在支持的 SDK 能力上做集成验证。
- SignalR 测试优先连接真实 Hub；若 TestServer 无法覆盖目标 transport，保留至少一组真实本地进程 WebSocket/Server-Sent Events/LongPolling 验收。
- 客户端使用 Vitest、jsdom、Testing Library 和 user-event；只 mock API/连接适配边界，不整体 mock Ant Design，也不把所有生成客户端类型替换成宽泛 any。
- 浏览器使用独立 Chrome Profile 和仅绑定 127.0.0.1 的本地服务；每个场景结束关闭页面、MCP 会话、SignalR 连接和临时数据库。
- 测试输出和日志不得包含会议码、Cookie、MCP 传输内部标识、完整上下文、完整回复正文或完整请求体；失败报告使用会议/参与者/任务/尝试的 Guid 摘要。

## 5. 测试数据约定

- 固定使用会议 A、会议 B、账号 Alice、Bob、Carol；每个测试重新生成 Guid，只有幂等重放测试复用明确的键。
- 人类参与者使用 Alice、Bob；Agent 使用 分析师、Reviewer、AI-观察员，并额外覆盖与 Human 重名的场景。
- 名称数据覆盖首尾空白、大小写、组合字符与预组字符、中文标点、换行边界、最长合法名称和超长名称。
- 消息作者覆盖 Human、Agent、System；消息序号由服务端分配。需要乱序时先持久化多个正常序号，再延迟或重排 MessageEventDelivery 的消费顺序，不直接伪造非法消息序号。
- 上下文测试使用明确的短消息块和超长单条触发消息；长度断言按 UTF-16 code unit，与服务端字符串预算和浏览器 JS .length 语义一致。
- 每个 Agent 至少准备一条独立的 AgentConnectionSession、AgentContextCursor 和任务序列，以确认多个 AI 之间没有共享可变游标或队列。
## 6. Domain 状态机测试

测试项目：Server/Tests/RoundTableMeeting.Domain.Tests。测试实体工厂、状态转换和不变量，不依赖 MCP、EF Core 或 SignalR。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-D001 | P0 | 创建合法 Agent 连接会话 | MeetingId、ParticipantId、ConnectionSessionId 和协议绑定摘要非空；状态为 Connected；传输为 mcp-streamable-http |
| AI-D002 | P0 | 连接会话绑定不变量 | 连接会话不能变更会议、参与者或传输绑定；空 Guid、非法字符数和无效时间关系被拒绝 |
| AI-D003 | P0 | 连接状态转换 | Connected -> Disconnected 合法且记录断开时间；Disconnected 不可直接回 Connected，主题五不提供接管转换 |
| AI-D004 | P0 | 单次等待取消 | Domain/Application 状态模型不因请求取消或 idle 超时自动写入 Disconnected |
| AI-D005 | P0 | 初始上下文状态 | Prepared -> Confirmed 只允许一次；Confirmed 不回退，确认时间只在首次确认时写入 |
| AI-D006 | P0 | 上下文游标关系 | DeliveredThroughSequence、AllocatedThroughSequence 非负；Delivered 不得超过已确认的分配位置 |
| AI-D007 | P0 | 合法回复任务 | 任务绑定会议和 Agent，终点等于 TriggerSequence，ReplyIdempotencyKey 稳定且非空 |
| AI-D008 | P0 | 任务范围不变量 | FromExclusive < ToInclusive；不得产生 (20, 10]、跨会议或跨 Agent 范围 |
| AI-D009 | P0 | 正常任务状态机 | Queued -> Delivered -> Processing -> Replied 合法；Replied 为终态且不可重新排队 |
| AI-D010 | P0 | 失败和取消状态机 | 允许设计中的 Queued/Delivered/Processing -> Failed 以及未完成状态 -> Cancelled；Failed 在主题五不自动回 Queued |
| AI-D011 | P0 | 投递尝试状态机 | Prepared -> Delivered 合法；主题五不产生 Abandoned；主题六仅在旧尝试被明确弃用且同事务建立替代尝试时可标记 Abandoned；终态不能重复覆盖；TaskId、ConnectionSessionId 绑定不可改变 |
| AI-D012 | P0 | 投递尝试与任务区分 | 两个任务即使使用相同连接也有独立 DeliveryAttemptId；同一任务的不同尝试 ID 不相等，不能使用 TaskId 代替 |
| AI-D013 | P1 | 状态时间一致性 | Created、Delivered、Processing、Replied、Failed、Cancelled 时间符合状态顺序，取消后不写回复时间 |
| AI-D014 | P0 | 任务来源类型 | 主题五可持久化的触发来源为 Human；System/Agent 来源不会绕过 Application 规则成为可领取任务 |
| AI-D015 | P0 | Agent activity 映射 | 有效连接且无活动任务为 waiting；Processing 队首为 processing；Delivered 或 Failed 队首为 blocked，分别返回 `delivery_not_finalized` 或任务持久化的 failureCode；无有效连接为 disconnected |
| AI-D016 | P1 | activity 不污染身份 | activity 状态不会写入 MeetingParticipant 的长期类型、显示名称或历史消息作者字段 |

## 7. 上下文构造器测试

测试项目：Server/Tests/RoundTableMeeting.Application.Tests，重点覆盖 AgentContextBuilder 和共享消息块格式化器。加入上下文与任务上下文必须调用同一构造器或同一格式化核心。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-X001 | P0 | 空候选范围 | 返回空消息列表，候选/实际范围为空，实际字符数为 0，truncated=false，overLimit=false |
| AI-X002 | P0 | 正常连续上下文 | 输入按 Sequence 升序输出；每条消息包含序号、作者类型、作者显示名称、正文和必要标识 |
| AI-X003 | P0 | 包含所有作者类型 | Human、Agent、System 均保留在候选上下文中；不能因消息未提及当前 Agent 而过滤 |
| AI-X004 | P0 | 触发消息完整保留 | 预算足够时所有候选消息保留；预算不足时触发消息仍完整保留 |
| AI-X005 | P0 | 从较早端连续裁剪 | 加入最近消息后，前一条放不下时停止向前扩展；不得跳过该消息选择更早消息 |
| AI-X006 | P0 | 单条触发消息超限 | 触发消息完整输出；overLimit=true；truncated 按是否存在被裁剪候选正确计算 |
| AI-X007 | P0 | 裁剪元数据 | CandidateFirst/Last、DeliveredFirst/Last、实际字符数、truncatedMessageCount 与返回消息一致 |
| AI-X008 | P0 | UTF-16 字符预算 | Emoji 代理对、组合字符和非 ASCII 正文按约定 code unit 计数，不按 UTF-8 字节或 token 计数 |
| AI-X009 | P0 | 触发端点 | 任务上下文的 CandidateToSequenceInclusive 等于触发消息 Sequence；不会包含触发消息之后的消息 |
| AI-X010 | P0 | 游标边界 | FromExclusive 之后的候选按范围读取；恰好等于游标的消息不重复加入，序号范围不倒置 |
| AI-X011 | P1 | 稳定格式 | 入会上下文和任务上下文均使用 agent-context-v1 的 messageId/sequence/authorKind/authorDisplayName/content 顺序、固定换行和反斜杠/回车/换行转义；结构化消息可复算 DeliveredCharacterCount，格式化器不依赖当前 UI |
| AI-X012 | P1 | 非法候选 | 跨会议消息、重复 Sequence、缺失作者快照或非法正文被拒绝或由调用方转换为稳定错误，不静默拼接 |
## 8. Application 入会、等待、回复和状态测试

测试项目：Server/Tests/RoundTableMeeting.Application.Tests 与 Server/Tests/RoundTableMeeting.IntegrationTests。纯规则测试使用 fake repository；事务、SQLite 和 MCP 绑定使用真实实现。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-A001 | P0 | 首次 join 原子创建 | 一个事务内创建唯一 Agent Participant、ConnectionSession、Cursor 和快照边界；任一事务写入失败全部回滚；数据库提交和连接注册成功后才返回 join 成功，online/activity 通知失败不回滚 join 且可由订阅快照恢复 |
| AI-A002 | P0 | 会议码与 Active 屏障 | 有效会议码加入成功；无效、已删除或 Deleting 会议统一返回 meeting_not_found，不创建任何 Agent 资源 |
| AI-A003 | P0 | 名称校验 | Application 在调用共享 NormalizeDisplayName 前检查空值或 Trim 空并返回 agent_name_required；只有非空输入进入共享规范化，长度非法或规范化结果非法返回 agent_name_invalid；NFC/大小写等价名称不能绕过唯一约束 |
| AI-A004 | P0 | Human 与 Agent 重名 | 会议内已有 Human 时 Agent join 返回 agent_name_in_use，不新增参与者或连接 |
| AI-A005 | P0 | 重复 Agent join | 同名 Agent 没有活动连接时返回 agent_recovery_not_supported；不创建第二个 Agent |
| AI-A006 | P0 | 活动连接重复 join | 已有活动连接返回 agent_connection_already_active；连接数、参与者数和游标数不增加 |
| AI-A007 | P0 | 并发同名 Agent join | 多个独立 scope 并发加入，最终只有一个 Agent Participant；其余请求稳定映射为活动冲突或恢复不支持 |
| AI-A008 | P0 | 入会快照边界 | InitialSnapshotSequence 是会议内明确高水位；快照只包含不高于该值的历史，边界前后消息不产生空洞 |
| AI-A009 | P0 | 初始上下文状态 | join 返回 Prepared，AllocatedThroughSequence=快照序号，DeliveredThroughSequence=0；响应不会伪称已确认 |
| AI-A010 | P0 | 历史 @AI 不回溯建任务 | 入会前已存在的 Human @AI 只作为历史上下文，不因 Agent join 在主题五创建回复任务 |
| AI-A011 | P0 | 首次 wait 确认初始上下文 | 当前绑定连接首次 wait 在短会议租约内原子执行 Prepared -> Confirmed，并把 Delivered 游标推进到 InitialSnapshotSequence；确认提交后领取前退出时任务仍为 Queued 且下次不重复推进 |
| AI-A012 | P0 | 重复 wait 不重复确认 | 已 Confirmed 的 Agent 再 wait 不重复推进游标、不重建初始投递记录，任务查询结果保持一致 |
| AI-A013 | P0 | wait 连接绑定 | 缺失、其他 Agent、其他会议或其他 MCP 协议会话的 ConnectionSessionId 返回 connection_invalid，不能读任务或上下文 |
| AI-A014 | P0 | idle 等待 | 等待上限到达且无任务返回 kind=idle、nextAction=wait_for_mention；连接仍为 Connected，数据库无任务状态变化 |
| AI-A015 | P0 | 请求取消 | 取消当前 wait 只结束本次调用；长轮询不持有会议 gate，不写 Disconnected、不取消任务、不释放其他连接绑定 |
| AI-A016 | P0 | 任务投递状态 | 领取事务提交 Queued -> Delivered 和尝试 Prepared；响应对象构造后第二个短事务提交 Delivered -> Processing、尝试 Prepared -> Delivered 和 Delivered 游标推进 |
| AI-A017 | P0 | 响应中断 | wait 在最终化前响应中断时任务保持 Delivered；最终化后响应中断时任务保持 Processing；两种状态都保留审计且后续任务不能越过 |
| AI-A018 | P0 | 队首 FIFO | 同一 Agent 连续有多个任务时只返回最早 TriggerSequence；后续任务保持 Queued，不能合并触发消息 |
| AI-A019 | P0 | 队首 Delivered 阻塞 | 两次事务之间或最终化失败后队首为 Delivered 时，后续 Queued 任务不能被返回；reply 对 Delivered 返回 task_not_available；不自动重投或跳过队首 |
| AI-A020 | P0 | 队首 Processing 阻塞 | 队首为 Processing 时后续 Queued 任务不能被返回；只有正常 reply 或后续主题处理后才解除 |
| AI-A021 | P0 | 队首 Failed 阻塞 | 队首为 Failed 时后续任务保持 Queued，get_agent_status 返回失败码和阻塞任务摘要 |
| AI-A022 | P0 | Agent 独立队列 | 两个 Agent 被同一条或不同消息提及时，各自创建独立任务、投递尝试和游标；一方 Processing 不阻塞另一方 |
| AI-A023 | P0 | reply 绑定校验 | connection/task/attempt/reply key 跨会议、跨 Agent、跨连接或伪造顺序时拒绝，不写消息和不改变任务 |
| AI-A024 | P0 | 回复正文校验 | 空白、超长或不符合主题四 NFC/Trim 规则返回 message_validation_failed；任务仍可用原幂等键修正重试 |
| AI-A025 | P0 | 受信作者解析 | reply 忽略客户端作者、ParticipantId、AuthorKind、MessageId、Sequence 和时间字段；消息作者始终为绑定 Agent |
| AI-A026 | P0 | 正常 Agent 回复 | Agent 外层事务调用受信内部追加核心写入 MeetingMessage、mentions、MessageAppendedEvent 和独立 deliveries，并将任务置为 Replied；不发生嵌套事务 |
| AI-A027 | P0 | 回复幂等重放 | 相同任务、幂等键和规范化后相同正文重放返回原消息；不产生第二个 MessageId、Sequence、event 或 delivery |
| AI-A028 | P0 | 回复幂等冲突 | 同一任务使用不同正文或不同幂等标识返回 reply_idempotency_mismatch，不覆盖原消息、不改变 Replied；不同任务误用其他任务幂等键也不能覆盖任一任务 |
| AI-A029 | P0 | 回复中的 Agent mention | Agent 回复中的 @AI 仍保存通用 mention；主题五任务消费者跳过，不创建 Agent 回复任务 |
| AI-A030 | P0 | 回复失败原子性 | 消息、mentions、事件、deliveries 或任务 Replied 任一失败时同一外层事务回滚；不存在半条回复或错误终态 |
| AI-A031 | P1 | 后续任务唤醒 | 回复提交成功后等待信号可唤醒下一次 wait；即使 signal 丢失，数据库查询仍能取得后续任务 |
| AI-A032 | P0 | get_agent_status 隔离 | 只返回当前协议会话绑定 Agent 的状态、按 queued/delivered/processing/replied/failed/cancelled 分组且缺项补 0 的计数、队首和两个游标，不枚举其他会议或 Agent |
| AI-A033 | P0 | 上下文字符数校验 | 低于最小值、高于最大值、0 或负数返回 context_limit_invalid；不创建参与者、连接或游标 |
| AI-A034 | P1 | Agent 加入限流 | 来源地址、会议码摘要、名称摘要或连接维度超限时拒绝加入；不写入资源，错误和日志不回显会议码 |
| AI-A035 | P0 | join 注册补偿 | 数据库提交后连接注册或绑定提升失败时 join 不返回成功、不发布 online；临时绑定被清除并条件更新为 Disconnected；补偿写库失败时记录诊断并继续收敛，没有注册表绑定的遗留 Connected 不算有效连接；单独的 online/activity 通知失败不回滚成功 join |
| AI-A036 | P0 | 遗留 Connected 收敛 | 数据库为 Connected 但注册表没有匹配协议绑定时不视为有效活动连接；启动或 join 路径把记录收敛为 Disconnected，不能永久返回 agent_connection_already_active |

## 9. 消息事件消费者与乱序补齐测试

测试项目：Server/Tests/RoundTableMeeting.IntegrationTests。使用真实消息追加和持久化 MessageEventDelivery；通过可控 dispatcher 安排事件到达顺序。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-E001 | P0 | 消费者注册 | agent-mention-task-v1 以独立 ConsumerName 注册；不复用 realtime-broadcast-v1，启动目录校验通过 |
| AI-E002 | P0 | 普通 Human 消息 | 无合法 Agent mention 时完成 delivery，不创建任务，不推进 Agent 分配游标 |
| AI-E003 | P0 | Human 单目标 mention | 合法 @AI 创建一个持久化任务，TriggerMessageId/Sequence/MentionId 与消息事实一致 |
| AI-E004 | P0 | Human 多目标 mention | 一条消息提及多个 Agent 时，各 Agent 各有一个独立任务和独立候选范围 |
| AI-E005 | P0 | 同消息重复提及 | 同一 Human 消息多次提及同一 Agent 时只创建一个任务；代表 TriggerMentionId 按最小 StartOffset 选择 |
| AI-E006 | P0 | System 消息 | System 消息即使含有可解析文本也不创建主题五任务，delivery 正常完成 |
| AI-E007 | P0 | Agent 消息 | 旧启用边界内 Agent 消息仅保留通用 mention、不追溯；边界之后的 Agent 间提及及自提及派生独立任务，新增验收归 008 |
| AI-E008 | P0 | 非 Active 会议 | 消费时会议已 Deleting/删除，完成或取消本次 delivery，不创建任务、不唤醒等待者 |
| AI-E009 | P0 | 事件 20 先于 10 | 已持久化的 10、20 均为 Human @AI 时，先消费 20 能补齐任务 10、20；范围依次为 (cursor,10]、(10,20] |
| AI-E010 | P0 | 事件 10 后到达 | 10 的 delivery 随后到达只命中已有唯一任务；不倒退 AllocatedThroughSequence，不创建重复任务或负范围 |
| AI-E011 | P0 | 乱序中混入普通消息 | 10/20 范围内的普通 Human、Agent、System 消息参与后续上下文，但只有合法 Human mention 物化任务 |
| AI-E012 | P0 | 并发同目标消费者 | 两个消费者同时补齐同一 Agent 的范围，最终任务唯一、游标单调递增、delivery 按成功事务完成 |
| AI-E013 | P0 | 不同 Agent 并发消费 | Agent A 的锁、游标和任务不阻塞或覆盖 Agent B；两方均获得自己的完整队列 |
| AI-E014 | P0 | 事件重试 | 任务事务失败时本次 MessageEventDelivery 不提前完成；重试后任务创建一次并最终完成 delivery |
| AI-E015 | P1 | 任务 signal 丢失 | 消费者提交成功但唤醒信号失败时，数据库任务仍可被后续 wait 查询；不重复创建任务 |
| AI-E016 | P0 | 已存在任务的补齐 | 范围内部分任务已存在时只创建缺失任务，已有任务的候选起点和状态不被覆盖 |
| AI-E017 | P0 | 消费者范围隔离 | 跨会议 MessageId、ParticipantId 或事件序号不能补齐到当前 Agent；数据库和 Application 都拒绝 |
| AI-E018 | P1 | 启用边界 | 主题五启用前的历史消息不会被首次启动或首次 join 批量回溯为任务；启用后的事件按 delivery 处理 |
## 10. Repository.Sqlite 与 Migration 测试

测试项目：Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-R001 | P0 | 空库 Migration | 创建 AgentConnectionSession、AgentContextCursor、AgentReplyTask、AgentTaskDeliveryAttempt 所需表、索引、外键和 CHECK |
| AI-R002 | P0 | 主题四数据库升级 | 既有 Meeting、Participant、Message、Event、Delivery 数据保持不变；升级后可创建和读取 Agent 资源 |
| AI-R003 | P0 | Agent 身份唯一 | (MeetingId, NormalizedDisplayName) 与 Human 共享会议名称唯一空间；不同会议可以使用同名 Agent |
| AI-R004 | P0 | 连接和游标唯一 | AgentContextCursor (MeetingId, ParticipantId) 只有一条；活动连接条件和跨会议绑定不能由重复记录绕过 |
| AI-R005 | P0 | 任务唯一 | (MeetingId, AgentParticipantId, TriggerMessageId) 重复写入被拒绝；不同 Agent 可以引用同一触发消息 |
| AI-R006 | P0 | 投递尝试唯一 | TaskId、DeliveryAttemptId 和 ConnectionSessionId 关系一致；同一尝试不能绑定其他任务或会议 |
| AI-R007 | P0 | 回复幂等键唯一 | AgentReplyTask 内的 ReplyIdempotencyKey 全局唯一，按固定 UUIDv5 命名空间、agent-reply: 前缀、TaskId 小写 D 格式和 RFC 4122 网络字节序跨重启稳定派生；固定向量 TaskId `00112233-4455-6677-8899-aabbccddeeff` 必须得到 `3502c088-f79d-5fb8-86c8-97b17c43cdc1`；MeetingMessage.ClientSubmissionId 仍按主题四的会议 + 作者参与者 + 标识复合范围幂等；不同任务误用键返回稳定业务错误 |
| AI-R008 | P0 | 复合外键 | 连接、游标、任务、尝试、触发消息和 Agent Participant 的 MeetingId 必须一致；单列 Guid 组合跨会议写入被拒绝 |
| AI-R009 | P0 | 状态 CHECK | 非法连接、初始上下文、任务、投递尝试状态被 SQLite 拒绝；数据库不接受整数枚举语义 |
| AI-R010 | P0 | 序号/字符数 CHECK | 序号非负且不超过约定安全整数，字符数和时间字段满足非负/可空关系 |
| AI-R011 | P0 | 状态条件更新 | 更新任务或连接时必须匹配 MeetingId、AgentParticipantId、当前状态和当前连接绑定；影响行数为 0 时不覆盖新事实 |
| AI-R012 | P0 | FIFO 查询索引 | 队列查询按 MeetingId、AgentParticipantId、状态、TriggerSequence、TaskId（即实体 ID）稳定排序，不按创建时间或事件到达时间排序 |
| AI-R013 | P0 | 并发任务领取 | 独立连接同时领取同一 Agent 队首时只有一个成功，另一个不能创建第二个活动尝试 |
| AI-R014 | P0 | 回复事务 | Agent 外层事务调用主题四内部追加核心，使消息、mentions、event、MessageEventDelivery 和任务状态在同一事务提交；公开追加服务不再嵌套调用；回滚不留下孤儿资源 |
| AI-R015 | P0 | 删除清理顺序 | 先清理尝试、任务、游标、连接会话，再由主题三/四清理消息事件、消息和参与者；外键约束通过 |
| AI-R016 | P0 | 删除清理重入 | 已删除资源视为完成；清理失败保持 Deleting，重试不会重复事实或恢复 Active |
| AI-R017 | P1 | NoTracking 投影 | Agent 状态、队首任务和 activity 查询使用只读投影，不泄漏 EF 实体引用或敏感传输绑定 |
| AI-R018 | P1 | 数据库异常分类 | 唯一冲突、跨会议引用和状态竞争转换为稳定错误码；不向 MCP 返回 SQL、表名或堆栈 |

## 11. Architecture、DI 和范围测试

测试项目：Server/Tests/RoundTableMeeting.ArchitectureTests 与 IntegrationTests 的服务注册扫描。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-C001 | P0 | Domain 引用边界 | Domain 不引用 ASP.NET Core、MCP SDK、SignalR、EF Core 或 JSON Transport 类型 |
| AI-C002 | P0 | Abstraction 契约边界 | 契约不暴露 DbContext、SqliteConnection、HttpContext、MCP SDK、Cookie 或领域实体 |
| AI-C003 | P0 | Application 引用边界 | Application 不直接引用 Web、MCP SDK、SignalR、EF Core 或 Repository.Sqlite；通过内部适配器和接口协作 |
| AI-C004 | P0 | MCP Tool 边界 | Tool 只做输入、会话、取消令牌和序列化；不直接查询 DbContext、不修改实体、不实现会议规则 |
| AI-C005 | P0 | Hub 边界 | MeetingChatHub 只负责授权、订阅、组和事件入口；不直接写 Agent 任务、消息或连接实体 |
| AI-C006 | P0 | 单一消息模型 | 不存在 AgentMessage、AI 专用历史表、AI 专用消息 Controller 或平行回复存储 |
| AI-C007 | P0 | 单一追加边界 | Agent 回复只能经受信作者解析进入主题四统一追加核心；不能复制 Sequence、mention 或幂等算法 |
| AI-C008 | P0 | Delivery 责任隔离 | MessageEventDelivery 只用于消息事件消费者投递；AgentTaskDeliveryAttempt 只审计上下文任务投递，二者不能混用 |
| AI-C009 | P0 | 删除清理边界 | 会议删除恢复流程清理连接、游标、任务、尝试和 Agent task delivery；资源顺序可检查，且清理发生在主题四共享消息清理之前 |
| AI-C010 | P0 | DI 生命周期 | Repository、UnitOfWork、TransactionRunner、DbContext 同 scope；连接注册表、任务 signal、presence/activity 发布器的生命周期符合设计，不能持有已释放 Scoped 对象 |
| AI-C011 | P0 | MCP 会话适配 | 只有 MCP 宿主适配器接触 SDK 会话类型；业务层只接收内部协议会话键和结束信号 |
| AI-C012 | P0 | 客户端连接适配 | 只有 meetingChatConnection.ts 直接创建或使用 HubConnection；组件不接触 SignalR 实例或生成客户端底层类型 |
| AI-C013 | P0 | 主题范围扫描 | 代码中不出现 acknowledge_mention、主题五自动重投、连接接管、可靠确认、多 Agent 协作链或循环保护的实现入口 |
| AI-C014 | P1 | 配置和日志边界 | 会议码、Cookie、MCP 内部绑定、完整消息正文和完整上下文不进入配置文件、普通日志或异常返回 |
## 12. MCP HTTP Transport 集成测试

测试项目：Server/Tests/RoundTableMeeting.IntegrationTests；必要时新增 McpAgentContractTests.cs。测试使用真实 /mcp endpoint 和 MCP SDK 客户端，不把工具调用伪装成普通 REST 请求。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-M001 | P0 | 匿名 MCP 工具发现 | 未携带浏览器 Cookie 的真实 MCP 请求可以到达 /mcp 并发现四个工具；端点不被全局 FallbackPolicy 拒绝，也不被 SPA fallback 截获 |
| AI-M002 | P0 | 工具发现和版本 | 真实 MCP 会话可发现 join_meeting、wait_for_mention、reply_to_mention、get_agent_status；协议版本为 agent-messaging-v1 |
| AI-M003 | P0 | join 成功契约 | 输入 meetingCode/displayName/contextCharacterLimit 后返回 Agent 摘要、connectionSessionId、residentInstruction、initialContext 和 nextAction=wait_for_mention；只在注册成功后返回成功 |
| AI-M004 | P0 | 枚举和字段序列化 | MCP Transport 自身显式使用 camelCase JsonStringEnumConverter 且 allowIntegerValues=false；状态和作者类型输出小写字符串，不输出整数枚举、内部绑定或未声明字段事实 |
| AI-M005 | P0 | join 输入伪造 | join 提交 ParticipantId、ConnectionSessionId 或作者字段不能改变服务端身份；额外字段按协议策略忽略或拒绝 |
| AI-M006 | P0 | 工具级授权 | 未 join 的匿名 MCP 会话调用 wait/reply/status 返回 connection_invalid；只有 join_meeting 使用会议码建立绑定 |
| AI-M007 | P0 | MCP 会话绑定 | 使用同一 connectionSessionId 的其他 MCP 协议会话调用 wait/reply/status 均返回 connection_invalid |
| AI-M008 | P0 | 跨会议和跨 Agent | 任务、投递尝试或状态 ID 来自其他会议/Agent 时拒绝；错误不泄露目标是否存在 |
| AI-M009 | P0 | 结构化错误 | 无效会议、名称、连接、任务、正文和幂等冲突分别映射设计中的稳定错误码；不含堆栈、SQL、会议码或完整正文 |
| AI-M010 | P0 | idle 长轮询 | 无任务等待到配置上限后返回 idle；HTTP 请求完成且 MCP 会话仍可继续 wait |
| AI-M011 | P0 | wait 请求取消 | 客户端取消长等待只取消调用；后续 status 仍显示 Connected，任务没有被自动取消或重投 |
| AI-M012 | P0 | 协议会话关闭 | MCP 协议会话明确关闭后 Agent activity 变为 disconnected；单次请求完成、idle 或取消不触发该状态 |
| AI-M013 | P0 | Human mention 闭环 | 通过真实消息 API 发送 Human @AI 后，MCP wait 返回 mention 任务、投递尝试、范围、裁剪元数据和上下文 |
| AI-M014 | P0 | FIFO wait 闭环 | 连续两个 Human mention 按 TriggerSequence 返回；首个未 reply 时第二个 wait 被队首 Processing 阻塞 |
| AI-M015 | P0 | 正常 reply 闭环 | 使用返回的 taskId、deliveryAttemptId、replyIdempotencyKey 提交后成功返回统一 MessageDto，任务为 Replied |
| AI-M016 | P0 | reply 重放 | 重复提交同一规范化正文返回同一消息；不同正文返回 reply_idempotency_mismatch，没有第二条消息 |
| AI-M017 | P0 | 回复作者不可伪造 | reply 请求中伪造作者、消息 ID、序号和时间不改变服务端消息事实 |
| AI-M018 | P0 | 回复取消/写入失败 | 取消或可控内部失败不产生半条消息；可按设计保留 Delivered 或 Processing，不自动进入 Replied 或自动重投 |
| AI-M019 | P1 | 会话多 Agent | 同一 MCP host 中两个独立 Agent 会话可同时 join 同一会议，彼此任务、状态和回复不串线 |
| AI-M020 | P1 | 配置约束 | contextCharacterLimit、等待上限、回复长度和限流配置启动时校验；非法配置阻止启动或返回稳定配置错误 |
| AI-M021 | P1 | 同源部署边界 | /mcp 使用同源 HTTPS/真实传输配置；浏览器 Cookie 不作为 Agent 身份凭证，不向页面暴露 MCP 内部标识 |
| AI-M022 | P1 | 驻留指令契约 | join 返回 residentInstructionVersion=resident-instruction-v3-legacy 和固定版本 residentInstruction，包含等待、任务处理、回复参数和继续等待要求；指令不写入 MeetingMessage |

## 13. SignalR Agent presence 和 activity 测试

测试项目：Server/Tests/RoundTableMeeting.IntegrationTests。消息广播仍按主题四契约测试；这里关注 Agent 快照、activity 增量和会议隔离。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-S001 | P0 | Subscribe 快照扩展 | 订阅成功同时返回既有 OnlineParticipantIds、HighWatermarkSequence 和全部 AgentActivities；OnlineParticipantIds 继续只反映主题四浏览器连接注册表，不因 MCP Agent 加入而扩展，Agent 在线状态由 AgentActivities 表达 |
| AI-S002 | P0 | Agent online presence | join 数据库提交且连接注册表绑定成功后同会议浏览器最终收到 participantPresenceChanged(online)；通知失败时不回滚 join 且后续订阅快照可恢复；仅建立 MCP 协议会话但尚未 join 不发布 online |
| AI-S003 | P0 | Agent waiting activity | Agent 无活动任务且连接有效时收到 activity state=waiting，Human 参与者不出现 Agent state |
| AI-S004 | P0 | Agent processing activity | wait 完成第二阶段最终化并使队首为 Processing 后收到 processing 和 activeTaskId；任务完成回复后回到 waiting |
| AI-S005 | P0 | Agent blocked activity | 队首 Delivered 时收到 blocked、activeTaskId 和 `delivery_not_finalized`；队首 Failed 时收到 blocked、activeTaskId 和持久化 failureCode；Human 不显示 Agent activity |
| AI-S006 | P0 | Agent disconnected presence | 明确关闭 MCP 会话后收到 offline/disconnected；参与者身份和历史消息仍保留 |
| AI-S007 | P0 | 请求取消不离线 | wait idle、请求取消、单次网络写入失败不会误发 offline 或把 activity 变成 disconnected |
| AI-S008 | P0 | activity 增量载荷 | agentActivityChangedV1 包含设计中的 meetingId、participantId、state、activeTaskId、failureCode 和 changedAt；不含完整任务上下文、会议码、MCP 内部绑定或消息正文 |
| AI-S009 | P0 | 同会议广播隔离 | Agent A 的 online/activity/reply 只到达会议 A 的浏览器，不到达会议 B |
| AI-S010 | P0 | 多 Agent 独立 activity | 同一会议两个 Agent 的 waiting/processing/blocked/disconnected 状态按 participantId 独立合并，不互相覆盖 |
| AI-S011 | P0 | 刷新恢复 | 浏览器刷新后先用 Subscribe 快照恢复 AgentActivities，再接收增量；不会依赖刷新前已经收到的事件 |
| AI-S012 | P0 | SignalR 重连恢复 | 重连重新订阅并取得最新快照；断线期间的 activity 变化不会造成错误回退 |
| AI-S013 | P0 | 删除并发 | 删除屏障后不再发送新的 Agent activity 或 messageAppended；已有连接收到终止/删除通知并退出会议组 |
| AI-S014 | P1 | 快照事件顺序 | activity 事件先于参与者详情到达时客户端暂存状态并触发既有详情刷新，不创建浏览器侧参与者 |
| AI-S015 | P1 | 事件重复/乱序 | 重复 activity 增量幂等；迟到旧状态不能覆盖较新的 changedAt 或当前连接代次 |
## 14. 删除、并发和恢复边界测试

测试项目：Server/Tests/RoundTableMeeting.IntegrationTests 与 Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests。测试使用会议级 gate、可控并发屏障和真实清理协调器。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-Q001 | P0 | 删除建立屏障 | Active -> Deleting 提交后 join、wait、reply、status 和 Agent 任务消费者统一拒绝或停止，外部错误为 meeting_not_found |
| AI-Q002 | P0 | 删除与 join 并发 | 删除先提交则 join 不创建资源；join 先提交则新增资源纳入删除，不出现半删除 Agent |
| AI-Q003 | P0 | 删除与任务消费并发 | 屏障建立后消费者不创建新任务；已登记 delivery 可完成清理或按协调器重试，不唤醒 Agent |
| AI-Q004 | P0 | 删除与 wait 并发 | 长等待被终止；终止后不把连接误标记为普通网络断开，也不返回删除会议的任务正文 |
| AI-Q005 | P0 | 删除与 reply 并发 | reply 在屏障后返回 meeting_not_found，即使持有旧 TaskId 也不能追加消息；屏障前成功的消息由清理负责 |
| AI-Q006 | P0 | 未完成任务取消 | Queued、Delivered、Processing、Failed 任务在清理前转为 Cancelled，之后不能再次 wait/reply；Replied 和已取消任务只做幂等清理 |
| AI-Q007 | P0 | 资源清理顺序 | 尝试、任务、游标、连接会话和 Agent task delivery 清理顺序满足复合外键；随后主题四消息/event/participant 清理成功 |
| AI-Q008 | P0 | 清理失败重试 | 任一步骤失败保持 Deleting；恢复器重启后继续，重复执行不产生第二次回复或 activity 广播 |
| AI-Q009 | P0 | 删除后旧标识 | 旧会议码、ConnectionSessionId、TaskId、DeliveryAttemptId 和回复幂等键不能访问或恢复已删除会议 |
| AI-Q010 | P0 | 迟到事件 | 删除后迟到 MessageEventDelivery 或 Agent signal 只能被忽略/完成，不创建任务、不写消息、不广播 activity |
| AI-Q011 | P1 | 多会议并发删除 | 删除会议 A 不影响会议 B 的 Agent wait、任务消费、回复和 activity |
| AI-Q012 | P1 | 重启语义边界 | 重启后 Deleting 会议继续清理；主题五不声称恢复旧等待或自动重投 Processing 任务 |
| AI-Q013 | P0 | 并发回复 | 同一 TaskId 的两个 reply 只有一个提交消息和 Replied 状态；另一个得到幂等重放或稳定冲突 |
| AI-Q014 | P0 | 并发领取与回复 | 领取和回复条件更新不能互相覆盖；旧连接/旧 attempt 不能完成新 attempt 或改变其他任务 |
| AI-Q015 | P1 | 删除重试与 Agent delivery | 删除第一次清理失败后，后续恢复期间迟到的 agent-mention-task-v1 delivery 在条件更新影响 0 行或已被清理时只能完成/忽略，不复活任务、不唤醒 Agent；最终仍按 Agent 资源先于主题四消息的顺序完成 |

## 15. 客户端 API、Reducer 和组件测试

测试目录：Client/roundTableMeeting/src/features/meetingWorkspace，与现有 messageReducer、meetingChatConnection 和工作台组件测试共存。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-V001 | P0 | 订阅快照映射 | AgentActivities 映射为 agentActivities Map；OnlineParticipantIds 和 HighWatermarkSequence 保持既有语义，OnlineParticipantIds 不作为 Agent 在线来源 |
| AI-V002 | P0 | activity 增量 | agentActivityChangedV1 转为规范化 reducer action；state、activeTaskId、changedAt 映射完整 |
| AI-V003 | P0 | 多 Agent 合并 | 不同 participantId 独立保存；同一 Agent 新状态覆盖旧状态但不影响其他 Agent |
| AI-V004 | P0 | 会议隔离 | action 的 meetingId 不等于当前会议时丢弃；不创建新的浏览器侧参与者 |
| AI-V005 | P0 | connection generation | 旧连接回调、旧订阅响应和旧 activity 事件不能污染新连接/新会议状态 |
| AI-V006 | P0 | 快照后迟到事件 | 先合并快照再合并增量；迟到旧事件不能回退新状态，必要时按 changedAt/代次丢弃 |
| AI-V007 | P0 | 删除清空 | meetingDeleted 清空 activity、消息和 presence，触发既有工作台回退；卸载和路由切换也清空本地状态 |
| AI-V008 | P0 | 连接生命周期 | connect、subscribe、reconnect、unsubscribe/close 和 dispose 成对调用；组件不直接访问 HubConnection |
| AI-V009 | P0 | activity 无实时连接 | 订阅失败或连接不可用时保留 Agent 身份，显示实时连接不可用/未知；不猜测 waiting、processing、blocked 或 disconnected |
| AI-V010 | P0 | Human 不显示 Agent 状态 | ParticipantList 只为 ParticipantType.Agent 渲染 activity；Human 不出现 waiting/processing/blocked/disconnected 标签 |
| AI-V011 | P1 | Agent 标识和 presence | Agent 类型、online/offline 和 activity 文案与消息作者类型区分清楚；Agent online/offline 只由 activity 是否为 disconnected 判定，participantPresenceChanged 不能覆盖较新的 activity；消息时间线仍使用统一 MessageDto |
| AI-V012 | P1 | 长名称和状态 | 长 Agent 名称、processing、blocked 和 disconnected 状态在窄屏不溢出、不遮挡参与者操作 |
| AI-V013 | P0 | 消息去重 | AI reply 经 HTTP 历史和 SignalR 重复到达时按 messageId 去重，按 Sequence 排序，不生成专用消息 Map |
| AI-V014 | P0 | AI mention 展示 | Agent 回复中的 mentions 使用主题四已有渲染和安全转义，不因来源是 AI 改变高亮或注入 HTML |
| AI-V015 | P1 | reducer 不保存秘密 | state、action、日志和错误展示不保存会议码、MCP 会话内部绑定、完整任务上下文或完整回复正文 |
| AI-V016 | P1 | SignalR 客户端契约 | 不依赖 Kiota 生成 SignalR 类型；显式 TypeScript 镜像类型包含 AgentActivities、meetingId、blocked/failureCode 等字段，真实 Hub 测试验证序列化兼容 |
## 16. 参考 Agent 联调测试

参考 Agent 不是生产实现；它用于证明 MCP 工具契约、等待顺序、任务元数据和幂等回复能够被一个最小外部客户端实际消费。接入与运行流程见 [MCP Agent 接入规范](ReferenceAgent/README.md)，可执行代码放在独立 Demo 目录。以下新增验收要求不代表测试已执行；记录实测结果时区分固定回复客户端与真实模型 Agent。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-G001 | P1 | 最小调用顺序 | 客户端按 join_meeting -> 首次 wait_for_mention -> 循环 wait -> reply -> wait 运行；首次 wait 负责确认初始上下文 |
| AI-G002 | P1 | 普通消息空闲 | Human 发送普通消息时 Agent 不收到 mention 任务；客户端保持等待并正确处理 idle |
| AI-G003 | P0 | 单次 mention | Human @AI 后客户端读取 taskId、deliveryAttemptId、幂等键和上下文，并提交一次回复 |
| AI-G004 | P0 | 连续 mention FIFO | 连续两个任务按 Sequence 处理；首个任务未回复时客户端不能越过队首处理第二个 |
| AI-G005 | P0 | 重复回复 | 客户端使用同一幂等键重放相同正文只得到原消息；不同正文清晰显示冲突，不继续伪造成功 |
| AI-G006 | P1 | idle 和取消 | idle 后继续 wait；人为取消请求后会话仍可使用，不把取消当成断开 |
| AI-G007 | P1 | 删除中操作 | 删除会议后 wait/reply/status 返回 meeting_not_found；客户端停止等待并释放会话 |
| AI-G008 | P1 | 敏感数据输出 | 参考 Agent 的日志只记录 ID、状态和序号摘要，不记录会议码、上下文全文或回复全文 |
| AI-G009 | P1 | 重复运行隔离 | 每次运行使用独立测试数据库和显式测试会议码；停止后不污染开发数据库或仓库文件 |
| AI-G010 | P1 | join 后初始上下文处理延迟 | 客户端已经捕获 join 成功响应中的 `connectionSessionId`，但初始上下文尚未被客户端处理或确认；客户端用该 ID 直接首次 wait，服务端只确认既有初始上下文并继续处理后续任务，不重复创建参与者、连接或游标。彻底丢失 join 响应且没有 `connectionSessionId` 的恢复不属于本用例，作为主题五非自恢复边界记录 |
| AI-G011 | P1 | 长驻留与时限适配 | 记录客户端、宿主、代理和服务端时限；测试实例可设置等待 30 秒、请求 60 秒并验证余量，至少运行 10 分钟跨多次 idle；复用同一协议会话，无重复 join，期间两次提及均回复后继续等待 |
| AI-G012 | P1 | 无任务取消与响应丢失区分 | 无任务等待取消后原协议会话可继续 wait；任务领取后丢失响应则通过 status 识别 Delivered/Processing 阻塞，不误报自动恢复或无限重试 |
| AI-G013 | P1 | 明确关闭与失败报告 | 本地挂起请求结算后检查远程会话关闭结果；关闭成功网页显示 disconnected 且身份和消息保留；关闭失败明确报告，不凭进程退出码推断远端状态 |
| AI-G014 | P1 | 实际模型回应 | 真实 Agent 收到两轮内容不同的提问，分别根据上下文作答并继续等待；固定文案回显不得计为模型回应验收通过 |
| AI-G015 | P1 | 错误与版本边界 | connection_invalid、agent_connection_already_active、agent_recovery_not_supported、task_not_available 分别处理；会话丢失不自动转移业务连接 ID，队首阻塞不当作 idle，会议删除后停止循环 |

## 17. 浏览器验收

浏览器验收使用真实服务端、真实 SignalR 和真实参考 Agent，不使用 mock 数据。页面继续使用现有会议工作台，不新增独立 AI 配置页。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-B001 | P0 | Agent 加入会议 | 参考 Agent 使用会议码加入后，Alice/Bob 的参与者列表显示一个 Agent 身份和 online/waiting 状态 |
| AI-B002 | P0 | 普通消息不触发 | Human 发送普通消息，Agent 保持 waiting；消息进入统一时间线但不创建 Agent 回复任务 |
| AI-B003 | P0 | Human @AI | Human 发送合法 @AI名称 后 Agent 收到一个任务，网页显示 processing，消息 mentions 正确高亮 |
| AI-B004 | P0 | Agent 回复 | Agent 回复进入与 Human 相同的时间线，所有同会议浏览器收到统一 MessageDto、作者类型 agent、Sequence 和 mentions |
| AI-B005 | P0 | 连续任务 FIFO | 连续提及产生两个任务；第一个处理期间第二个不越序显示为可处理任务 |
| AI-B006 | P0 | 多 Agent | 同一消息提及两个 Agent 时两者独立处理；一个 Agent 的延迟不阻塞另一个或 Human 发送 |
| AI-B007 | P0 | 刷新恢复 | 浏览器刷新或新标签页加入后，Subscribe 快照恢复 Agent activity；不依赖之前的实时事件 |
| AI-B008 | P0 | SignalR 重连 | 短暂断网后重新订阅，Agent 状态和消息历史最终收敛；旧连接事件不污染当前会议 |
| AI-B009 | P0 | 明确断开 | 关闭参考 Agent MCP 协议会话后网页显示 disconnected，Agent 身份和历史消息保留 |
| AI-B010 | P0 | 请求取消不离线 | 仅取消一次等待请求，网页不显示 disconnected；连接可继续处理后续 wait |
| AI-B011 | P0 | 会议 A/B 切换 | 页面切换会议时消息、Agent activity、presence、草稿和迟到事件不串会 |
| AI-B012 | P0 | 删除会议 | 删除后网页收到删除通知并回退；Agent 等待终止，旧页面和旧工具调用不能读取或回复 |
| AI-B013 | P0 | Console/Network | 无未处理异常；会议码、Cookie、上下文全文和回复全文不出现在 URL、普通日志或不必要的网络载荷中 |
| AI-B014 | P1 | 长名称和窄屏 | Agent 长名称及 waiting/processing/blocked/disconnected 状态不溢出、不重叠，参与者列表和消息区域在窄屏可用 |
| AI-B015 | P1 | AI 回复中的 mention | AI 回复中的 @ 在统一时间线正确显示；不会因为来源为 Agent 在主题五再次创建任务 |
| AI-B016 | P1 | 浏览器消息回归 | 主题四 Human 消息、历史补齐、重复广播去重、presence 和删除回退场景继续通过 |

## 18. 构建、发布和主题回归

命令基线：

~~~powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
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

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-BLD001 | P0 | 服务端全量构建 | Domain、Abstraction、Application、Repository.Sqlite、Web host 和新增测试项目全部构建成功 |
| AI-BLD002 | P0 | 服务端全量测试 | AI P0/P1 测试和主题二至主题四既有测试全部通过 |
| AI-BLD003 | P0 | Migration 启动 | 空库和主题四数据库均可迁移；启动后 Agent 资源查询、消息历史和删除恢复正常 |
| AI-BLD004 | P0 | MCP host | 发布 host 可发现四个主题五工具；/mcp 不被普通 REST 或 SPA fallback 截获 |
| AI-BLD005 | P0 | HTTP API 生成 | pnpm generate:api 成功，HTTP API 类型正确且不把 MCP Tool 或 SignalR DTO 当作 REST 契约；SignalR 字段由独立镜像类型和真实 Hub 测试验证 |
| AI-BLD006 | P0 | 客户端测试与构建 | Vitest、TypeScript 检查和 Vite build 成功；不以 any 或跳过测试隐藏契约错误 |
| AI-BLD007 | P0 | 统一消息回归 | AI 回复仍经过主题四 Sequence、mentions、MessageAppendedEvent、MessageEventDelivery、SignalR 和历史接口 |
| AI-BLD008 | P0 | 删除回归 | 发布配置下 Active/Deleting gate、Agent 资源清理和服务重启后的 Deleting 恢复通过 |
| AI-BLD009 | P1 | 单实例约束 | 发布检查明确 Agent connection registry、task signal 和会议 gate 尚未支持跨实例 backplane；多实例不宣称可用 |
| AI-BLD010 | P1 | 文本与安全扫描 | 新增/修改文档和生成文件符合 UTF-8 无 BOM、CRLF；无 acknowledge_mention、敏感日志和主题越界实现 |

## 19. 完成门

- 所有 P0/P1 用例通过；失败项有明确原因、影响范围、临时豁免和用户确认。
- 首次 Agent join 的身份、连接、游标和初始快照原子性通过；并发同名 Human/Agent 冲突、活动连接拒绝和断开连接恢复边界符合主题五范围。
- 初始上下文 Prepared/Confirmed、Delivered/Allocated 双游标、消息事件乱序补齐、连续候选范围、UTF-16 裁剪和 FIFO 阻塞通过真实持久化测试。
- 普通消息、System 消息和 Agent 消息不会在主题五创建回复任务；Human 合法 @AI、同消息去重、多目标独立任务和并发消费者收敛通过。
- wait_for_mention 的 idle、取消、Delivered/Processing 队首阻塞和响应中断语义通过；没有 acknowledge_mention、自动重投或隐式接管。
- reply_to_mention 的跨范围拒绝、受信作者解析、统一消息追加、mentions/events/deliveries 原子性和回复幂等通过。
- Agent online/offline presence、waiting/processing/blocked/disconnected activity、订阅快照、增量事件、页面刷新、SignalR 重连和多会议隔离通过。
- 删除屏障建立后没有新的任务、回复消息或 Agent activity 外部行为；连接等待终止，新增资源可重入清理，Deleting 重启后不恢复 Active。
- 参考 Agent、真实 MCP HTTP、真实 SignalR、浏览器验收、HTTP OpenAPI/Kiota 生成、SignalR 镜像契约测试、客户端测试构建、服务端全量测试和发布检查全部通过。
- 主题二至主题四的认证、会议、消息、授权、历史、presence、事件 dispatcher、删除、SPA 和既有客户端回归继续通过。
- 代码和文档扫描确认没有 AI 专用消息模型、平行消息流、可靠确认、连接接管、服务重启恢复、多 Agent 协作链或循环保护的主题五越界实现。
