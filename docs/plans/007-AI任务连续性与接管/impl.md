# AI 任务连续性与接管实现方案

## 1. 定位与实施前提

本文件拆解[主题设计](design.md)，测试要求见 [testPlan.md](testPlan.md)。当前只完成设计，不把下列步骤记为已完成，也不在本阶段修改业务代码。

实施前确认主题五的构建、全量测试、真实 MCP、SignalR、浏览器和参考 Agent 验收基线。代码分析绑定 RoundTableMeeting 仓库；修改每个既有函数、类或方法前必须按 AGENTS.md 执行 GitNexus impact 并报告调用方、流程和风险，UNKNOWN 补充源代码确认，HIGH/CRITICAL 先告知用户。文档阶段不执行代码影响分析。提交前按仓库规则做图变更分析并取得提交授权。

现有落点：

- Application/AgentCollaboration/AgentCollaborationService：JoinAsync、WaitAsync、ReplyAsync、初始确认与投递最终化。
- AgentMentionTaskConsumer：按分配游标补齐任务，保持离线分配和 Human 触发边界。
- Domain/Entities：AgentConnectionSession、AgentContextCursor、AgentReplyTask、AgentTaskDeliveryAttempt。
- Repository.Sqlite：AgentCollaborationRepository、AgentCollaborationConfigurations、AgentCollaborationCleanupService。
- Web host/AgentCollaboration：McpAgentTools、AgentConnectionRegistry、AgentConnectionStartupReconciler；现有 Server/RoundTableMeeting/Hosting/ServiceCollectionExtensions.cs 的 WithHttpTransport.options.RunSessionHandler，在 server.RunAsync 的 finally 中移除注册并调用 DisconnectAsync；阶段 0 验证这个真实挂接点在重绑、并发关闭下的语义。
- Client/roundTableMeeting/src/features/meetingWorkspace：实时契约镜像、状态 reducer、参与者列表及消息流。
- Demo/AI参会联调客户端：固定回复参考客户端；真实模型接入规范沿用主题五 ReferenceAgent/README.md，在实现完成后更新到支持的模式。

以上 Server 路径分别位于 Server/RoundTableMeeting.Application、RoundTableMeeting.Domain、RoundTableMeeting.Repository.Sqlite、RoundTableMeeting 等现有项目。新增代码跟随现有项目，docs/plans 下只保存文档。

## 2. 阶段与交付门

| 阶段 | 交付 | 完成门 |
| --- | --- | --- |
| 0 | 协议 DTO、兼容分支和 SDK 能力验证 | 旧四工具闭环保留；显式模式和新增工具可发现；同 MCP 会话续租并发及现有关闭钩子验证通过，形成 go/no-go 结论 |
| 1 | 领域状态机、迁移与 Repository 条件写 | 空库/旧库迁移通过；代数、绑定、尝试及复合外键约束可验证 |
| 2 | 可靠 join、resume、takeover 和操作收据 | 丢失 join 响应不占死身份；在线接管生效；原 ID 恢复与新 ID 接管可区分 |
| 3 | 初始/任务确认、固定范围与窗口重放 | 确认前不推进游标；接管快照不覆盖任务；重复响应不重复领取 |
| 4 | 租约、失败、自动重投和显式重试 | 无固定模型回复时限；有界自动重试；耗尽阻塞 FIFO |
| 5 | 回复幂等、重启和删除并发收口 | 旧连接/尝试不能写；同一任务一条回复；重启恢复且删除不可复活 |
| 6 | 网页状态、参考客户端和接入说明 | 接管、离线、确认等待、重试耗尽可见；参考客户端可恢复并续租 |
| 7 | 完整验收 | P0/P1、真实模型、浏览器和已完成主题核心回归全部通过 |

## 3. 阶段 0：协议与技术验证

1. 在既有 AgentCollaborationContracts 中添加可选模式和增量 DTO，保留外层 agent-messaging-v1 及原四工具字段。协议状态仍为 camelCase 字符串，拒绝非法整数枚举。
2. 可靠模式固定 explicit_ack_v1，新增 confirm_context、resume_agent、renew_agent_lease、report_task_failure、retry_task。Tool 层只作参数和传输上下文适配，规则由 Application 执行。
3. 原客户端省略模式继续 legacy_v1。当前 AgentOperationResult / Tool JSON 的兼容性必须用真实工具调用验证，不只检查 C# 编译。
4. 按 design §9.4 的字段 → DTO 映射逐字段冻结 schema/kind/时间字段；按 §9.7 验证错误码、工具适用范围、可重试性和两种 data 形状。CT-M14 须覆盖全部映射 DTO 的真实工具 JSON 序列化与 schema 一致性，再进入业务实现；join 非法新参数和未知模式的错误形状按 CT-M20 验证。用现有 ModelContextProtocol.AspNetCore 固定版本验证：同一 MCP 会话在 wait 或模型处理期间可执行续租；DELETE、请求取消和会话失效可区分；新协议会话可以在受控 resume 后绑定原业务连接；撤销绑定能终止旧挂起 wait。
5. 固定操作去重键作用域、参数规范化、错误码、replayReason、nextAction 和 capability 表；给可靠模式失败响应新增有 schema 的 AgentRecoveryErrorData，legacy 保持 Data=null；不存在能力时客户端不能猜测恢复工具可用。
6. 为测试提供可控 TimeProvider、数据库提交前后故障点、响应发送前故障点、注册失败注入和会话关闭钩子。不靠定时 sleep 模拟所有竞争。

阶段 0 是 go/no-go 门：必须验证真实 SDK 的业务绑定、关闭钩子、独立续租及成功/失败 DTO 序列化后再扩展恢复实现。SDK/宿主限制下可选兼容路径为：（1）仅空闲 wait 并发受限时，使用较短 wait，idle 间续租；模型处理期间仍由客户端独立续租；（2）当前通用宿主不能运行独立续租时，使用本仓库升级后的 SDK 参考客户端驻留，由它持有同一绑定并独立驱动续租和外部模型。两条路径都必须真实验证。

不能用无授权独立连接心跳、服务器替客户端永久续租、改成无租约或冒充宿主支持来绕过门槛。若两条兼容路径仍无法维持模型处理期租约，阶段 0 判 no-go，报告具体失败证据并修订技术方案；不取消已确认的产品要求。

## 4. 阶段 1：模型、迁移和持久化

1. 扩展现有实体的领域工厂和转换方法，加入当前连接代数、绑定版本、Activating/Connected/Disconnected/Superseded 转换、恢复摘要、租约、初始确认、尝试确认/失效、重试周期、次数和重试时间。不开放任意公共 setter。
2. 新建 AgentInitialContextDelivery 和 AgentRecoveryOperation 两个持久化实体；它们是必要的确认事实和幂等收据，不是执行日志。保留首次加入快照边界用于迁移和初始确认恢复。初始投递表作为唯一确认事实，迁移回填后移除连接上的重复初始状态字段，原 DTO 改为关联投影；复用 LastActivityAtUtc，不新增语义重叠的 LastSeenAtUtc。
3. 为当前连接、当前尝试、替代尝试建立复合关联和唯一约束，明确可空指针以及 ReplacementAttemptId/SupersededByConnectionId 的清除顺序以支持删除；连接筛选唯一索引覆盖 Activating 和 Connected；确认索引适合按租约截止、任务重试时间和 FIFO TriggerSequence 查询；当前指针与代数用复合外键配对，resume 不改代数；CurrentAttemptId 在 Replied 后保留成功尝试。
4. AgentTaskDeliveryAttempt 的 Delivered 范围在准备可靠响应时完整持久化；确认只改变确认字段，不重裁剪。保存 ContextFormatVersion，以便同次响应重建一致。
5. Repository 增加校验当前所有权、条件续租、绑定替换、确认、失效、替代投递、原子回复、操作收据重放所需方法；每次条件修改必须返回是否真正发生，不能无条件覆盖新代数。
6. 将迁移列为阶段 1 独立完成门：先检查唯一旧连接、任务与尝试关系，有歧义则终止升级并输出 ID 摘要，保留原数据库，不能任意取最新；迁移失败须事务回滚。Migration 在独立数据库副本上验证：旧连接标记 legacy_v1；原 ID/任务范围/幂等键不变，回填 CurrentAttemptId 和周期计数；旧尝试没有伪造确认；旧 Connected 按启动恢复规则离线；已回复任务不重投。
7. 新增配置按 design §6 的跨字段公式全集和 §6.1 的恢复限流参数校验；确认时限与连接租约独立，不添加大小强依赖。配置错误启动失败，避免运行时使用不可续接的时间组合。

## 5. 阶段 2：连接所有权与恢复

建议在现有 facade 后增加 AgentConnectionRecoveryService 负责 join/resume/takeover 事务，复用既有名称规范化、会议授权、事务 Runner 和注册补偿。避免再把租约和恢复状态机堆入 MCP Tool。

1. 实现私有客户端 recoveryKey 生成约定、服务端摘要比较；takeover/join 共用既有四维限流，resume 增加来源/业务连接尝试与失败计数，阈值、窗口和错误 data 以 design §6.1 为准；密钥不得进入 DTO 日志、异常、SignalR、普通状态响应或源码配置。
2. 可靠 join 将身份、连接、当前指针、首次快照、初始投递和操作收据一起提交；连接先以 Activating 提交，再注册传输，最后以第二短事务条件转 Connected；注册/最终化失败按当前代数条件补偿。激活成功前不返回业务成功，legacy 共用内部激活边界。
3. 预留目标 MCP 传输，拒绝已绑定/已退役传输再次入会及同传输的并发跨目标操作，失败前不得修改目标 AI 所有权。create 判断前收敛过期/注册缺失 Connected，区分有效活动与离线 AI；RequiredReliabilityMode 优先拦截已升级身份的 legacy 请求，不自动接管。takeover 在持久化事务里先失效旧连接/尝试，再创建新连接和初始快照，保留既有游标和全部任务。
4. resume 验证密钥摘要与当前指针，不改变业务 ID、预算或初始快照；增加绑定版本并经 Activating 最终化，旧传输立即失去权限。仅 Delivered/Processing 尝试标记恢复待重投；已有 Failed 周期、决定和退避时间保持。
5. 实现不可变结果收据。join 在新传输上仅返回 recovery_required 和原业务 ID，之后用新 operationId 调用 resume，不由 join 重放改绑定。resume 收据仅在原结果版本仍有效时重放成功，过时结果返回 operation_superseded；原 Activating 操作只可在同目标同版本上继续最终化，不生成新版本。
6. 注册表登记加入 ConnectionGeneration/BindingVersion，所有异步会话关闭回调与补偿均校验版本，旧回调不得把恢复后的新绑定标记断开。
7. 统一锁顺序为传输预留（仅 join/resume）、会议 gate、名称/参与者锁、数据库事务；扫描和业务路径不得反向获取传输预留；检查现有 Start/Reconcile、join、reply、删除路径的顺序，避免互相等待。

先用 CT-C18/C20、CT-P05、CT-D04/D06 的同步屏障验证全局锁序和取消释放，再展开阶段 2/4 的业务实现；并发测试使用有界完成断言检出死锁，不能依赖测试进程最终被超时杀掉。

## 6. 阶段 3：上下文与可靠确认

1. 抽出 AgentContextDeliveryService，继续复用 AgentContextBuilder 和共享格式化器；不改动消息来源、Unicode/UTF-16 预算及触发消息保留规则。
2. legacy_v1 保留原首次 wait 确认与服务端最终化流程；explicit_ack_v1 只在 confirm_context 事务内确认并推进 Delivered 游标。两种模式必须显式分支，避免共用一个方法时误触旧最终化。
3. 初始投递持久化固定快照，重复响应重建同一窗口；首次创建参与者与接管已有参与者采用不同游标推进规则，特别覆盖首次快照未确认就接管的情况。
4. 可靠 wait 领取时同事务将尝试持久化为 Delivered、任务持久化为 Delivered，并保存确认截止与当前尝试，不沿用旧两阶段投递最终化；尚未超时的重复 wait 重放相同窗口；已超时但扫描未执行时调用共用的条件失败转换再分流，不返回过期窗口。Processing 只返回任务进行中。两模式由 CT-X06/X07 与 CT-M15 分别验证。
5. confirm 校验当前连接、绑定版本、租约、尝试 ID、有效期和任务状态。先处理已确认当前尝试的幂等重放，再对首次确认校验截止；原截止过去也不影响合法已确认重放。截止以取得锁后事务内条件写入时刻为准，不能把不可预知的物理提交时间写成判断依据。
6. 对已确认未回复的恢复任务使用原候选范围、新尝试和新预算；确认以 max 更新游标，不能回退。恢复摘要说明重复上下文用途，避免客户端误认为新任务。
7. 给 AgentMentionTaskConsumer 增加旧事件迟到、离线提及和接管快照并发回归；保持按 Allocated 补齐而不依据连接是否在线过滤。

## 7. 阶段 4：租约与重试

建议新增 AgentLeaseService、AgentTaskRecoveryService 和一个有界后台扫描器，所有持久化操作复用 Application 事务服务。数据库访问使用每批独立 scope；进程信号只唤醒等待者。

1. 可靠 wait 接受客户端等待预算，启动时续租至 max(原 LeaseExpiresAtUtc, now+租约时长, wait截止+安全余量)，不在挂起期间无期限续租；取消只终止请求，租约自然到期。
2. renew_agent_lease 用当前代数和绑定版本条件写，新租约取 max(原截止, now+基础时长)，不能缩短已取得的长 wait 租约；get_agent_status 保持只读；同一租约过期后不得直接续租复活，必须 resume。
3. 处理租约到期、明确关闭、resume 重绑、接管和进程重启：仅将仍在投递/处理的任务转 Failed，条件失效其尝试；已 Failed 的计数、耗尽状态和退避时间不重写。对同一次失败并发触发只记录一次结果。
4. 确认超时使用时间条件与 confirm 竞争；成功处理任务不得因模型执行时间长自动判失败。
5. 到 NextRetryAt 且有可用连接时，wait 原子建立新尝试、旧尝试 Abandoned、记录 RetryCycle 并按周期计数、任务转 Delivered；无可用连接时保持 Failed，不消耗次数。
6. report_task_failure 仅支持受控失败类别；首次报告与尝试上的 ReportedFailureCode/FailureDecision 一起原子保存；重复同尝试同失败码返回原决定，不影响后来新尝试，改失败码返回冲突。非法外部码拒绝且不修改任务；上下文数据不变量错误单独转明确的阻塞状态，不制造空投递。用封闭枚举及单一失败判定函数落实 design §7 的 FailureCode/RetryExhausted/stopReason 组合，转换、状态投影与首次 FailureDecision 共用该规则，收据重放读取原决定；CT-R15 覆盖原因与额度交叉组合。
7. retry_task 仅处理当前耗尽队首，以 operationId 保证周期只增加一次。新的客户端明确调用才能重开；新周期首次实际投递不占自动重试名额，之后最多自动替代 3 次；客户端自动恢复不得隐式调用。
8. 耗尽、不可重试和离线状态不会阻塞其他 AI 或人类消息发送。

## 8. 阶段 5：回复、重启与删除

1. 调整 ReplyAsync 的验证顺序，先验证当前身份，再区分当前未完成尝试与已成功回复的重放；已回复路径允许当前合法恢复连接查询相同成功结果，旧连接始终拒绝。
2. 保留 IResolvedMessageAppendCore.AppendAgentAsync 的单事务调用，任务 Replied、ReplyMessageId、消息/mentions/event/deliveries 原子提交。不能先回复再单独改任务。
3. 扩展启动协调器为持久化恢复：先建立删除中屏障，收敛前进程连接和尝试，再开放 Agent 接入；定时补偿使用相同条件写逻辑，不误伤当前注册窗口。
4. 删除清理纳入恢复收据、初始投递、当前指针、租约、确认/重试元数据及新后台任务；按 design.md 清除指针和外键依赖。所有恢复入口查询 Active。
5. 状态通知使用同事务更新的 revision/ActivityChangedAtUtc 和一致性快照 payload，不能在读取后拼接不同版本的数据；普通续租不更新 activity.changedAt；逐项覆盖 design §11 的全部 revision 变化点和不变化点。通知在提交后发出且重新检查会议屏障；通知失败不推翻数据库事实，也不静默丢失重试时间。
6. 用真实进程强制终止覆盖事务提交前后、注册前后、确认前后、回复后响应前、删除清理中等切点。

## 9. 阶段 6：客户端与参考 Agent

网页沿用原会议工作台：增加状态字段与 revision 比较，更新 Agent 参与者状态文案、接管提示和快照恢复；Activating 与初始未确认均显示对应 blocked 原因。Agent presence 从 activity revision 派生，忽略旧无版本 presence 对 Agent 的覆盖，Human 保持原流程；不要为幂等/租约实现细节增加配置页。保持长名称、窄屏、刷新、重连、会议切换和消息去重回归。

参考 Agent 流程：

1. 初始化 MCP、发现能力，生成并私有保存 operationId/recoveryKey，可靠 join；响应丢失后重放原操作，遇 recovery_required 保存原业务 ID 并在新 MCP 会话以新 operationId resume；只有用户明确选择接管时才提交 takeover。
2. 保存业务连接及初始上下文，确认 initial，并维持独立续租与一个串行任务循环；耗尽等待明确 retry_task 时仅暂停业务循环，续租继续。
3. wait 收到 mention 后保存任务/尝试/固定正文或本地生成结果，confirm，再执行模型或固定回复逻辑；两者验收结果分开。
4. 回复响应丢失时优先查询任务状态，再按同正文重放；wait 响应丢失在截止前可重放同次投递，超时后按状态恢复。
5. 会话失效后先停止旧业务循环，重新初始化，再带原私有状态 resume。被接管时停止且不反向抢占；不把 operation_superseded 当作旧 resume 成功。
6. 退避期间保持续租；retry_exhausted 时报告阻塞，不自动 retry_task。用户明确重试才发送新 operationId。
7. 关闭时停止续租、取消并结算 wait、关闭远端 MCP 会话，检查关闭结果；不把本地进程退出当作远端成功关闭。

阶段 4 提前升级 Demo/AI参会联调客户端 的续租、恢复、故障注入与私有状态保存能力，支撑阶段 5 的真实协议验证；阶段 6 完成 CLI 使用说明和真实模型对接。固定回复仍只计协议验证。接入说明须明确会议码+名称的接管信任模型及 recoveryKey 只用于恢复原业务连接；首次可靠 join/takeover 的 Activating 事务提交后，参与者永久升级，即使随后激活失败也不能回退 legacy。仅参数校验失败或事务未提交不会升级。

接入文档更新必须等功能实现并验证后进行；当前主题五 README 继续描述真实可用能力，仅增加主题六设计入口。不将根目录联调建议中的 SSE/WebSocket 改造或“所有 MCP 请求只能串行”直接写入实现要求。

## 10. 验证与交付

按 testPlan.md 执行单元、真实 SQLite、MCP、SignalR、客户端、参考 Agent、真实模型和浏览器验收。将 CT-C24 与 CT-V11 纳入持续回归，任何协调游标、所有权或 activity 变更均保持指针/代数配对与 revision 全变化点校验。测试使用隔离数据，不修改开发会议或数据库。新增/修改文本为 UTF-8 无 BOM、CRLF；检查本主题设计、实现、测试中的字段、状态和错误码一致。

文档只保留当前有效方案，不新建实施日志、迁移记录或验收报告文件。实现阶段完成后更新最贴合的现有文档并在交付说明中报告实际测试结果和未通过项；未执行场景不能写成通过。没有用户明确授权不创建分支、不提交代码。
