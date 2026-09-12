# AI 任务连续性与接管测试方案

当前实现已接入 [008 多 Agent 协作与会议删除收口](../008-多Agent协作与会议删除收口/design.md)：Human 有效提及继续创建根任务；Agent 有效提及仅在会议启用边界之后派生，System 不触发。旧边界内 Agent 不追溯，旧 Human 待办仍补齐。legacy/可靠驻留模板分别为 `resident-instruction-v3-legacy` / `resident-instruction-v3-reliable`，仅可靠 join/resume 声明 `multi_agent_collaboration_v1`；共 9 个工具（既有 4 个、可靠专属 5 个）。来源随任务恢复保持，删除屏障提交返回 202 后仍由后台清理父引用、边界及原有资源；008 的新增验收见其 [testPlan.md](../008-多Agent协作与会议删除收口/testPlan.md)，本文件原有通过证据不代替该验收，旧服务夹具的 v1/v2 仍保留。

## 1. 定位与完成规则

本方案验收[主题设计](design.md)及[实现步骤](impl.md)，下表 140 项列出本主题完整验收要求及当前逐项证据，协议、领域、持久化、恢复并发、真实模型与浏览器验收均已完成。

逐项核对全部 140 项均已通过。每行“当前证据”列出来源；测试总数不代替整项验收。CT-L05、CT-G04、CT-G05 使用真实供应商模型执行，未用固定回复或人工延迟替代。

P0 为身份串用、消息/任务丢失或重复、越序、旧连接写入、删除复活、迁移破坏和构建阻断；P1 为恢复协议、状态收敛、兼容、参考 Agent 和真实浏览器业务路径；P2 为视觉细节和诊断可读性。所有 P0/P1 通过后才能宣布主题完成。环境阻塞不能按通过计；若需要豁免，必须明确影响并由用户确认。

每个服务端用例使用独立临时 SQLite，跨请求并发使用独立 DI scope/DbContext/事务。真实 MCP 用固定 SDK 和真实 Streamable HTTP，不仅直接调用 Application。涉及提交与响应丢失的场景必须明确故障点并查询数据库事实；涉及进程崩溃必须重启真实服务进程。使用可控时间和同步屏障，不用固定 sleep 作为唯一证据。

当前已执行的部分验证：

| 自动化测试文件 | 已验证的边界 | 对应计划覆盖及限制 |
| --- | --- | --- |
| [AgentRecoveryRequestValidatorTests.cs](../../../Server/Tests/RoundTableMeeting.Application.Tests/AgentRecoveryRequestValidatorTests.cs) | legacy 缺省、可靠模式必填、凭据规范编码、初始/任务确认互斥、resume/retry ID 校验 | CT-M20 与 design §9.2 的校验器部分；生产入口适配另由 McpAgentReliableProductionTests 验证；生产非法参数组合另见 McpAgentJoinValidationTests 与 CT-M20 |
| [McpAgentRecoveryContractTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentRecoveryContractTests.cs) 与 [AgentToolSchemaTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentToolSchemaTests.cs) | 真实 SDK 的可靠 DTO 样例与 schema 全值校验、schema 引用和错误分支、legacy 真实 join/错误原字段集 | CT-M12/CT-M14 的响应契约部分；样例工具只在测试宿主启用，不是可靠业务服务 |
| [McpAgentReliabilityTransportTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentReliabilityTransportTests.cs) | 同绑定挂起 wait 期间并发 status、显式协议取消后会话可用、SDK 关闭会话使等待结束并持久化离线 | CT-G09/CT-G10 的 SDK 调度与当前关闭钩子部分 |
| [AgentConnectionBindingTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentConnectionBindingTests.cs) 与 [McpAgentBindingCancellationTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentBindingCancellationTests.cs) | 传输预留串行化与取消释放、注册版本比较、退役占用、旧关闭隔离；撤销绑定结束实际 SDK wait 并拒绝后续读取 | CT-C13/C18/C19 的注册表与等待部分；尚不证明数据库当前指针和两阶段激活 |
| [AgentJoinTransportReservationTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentJoinTransportReservationTests.cs) | 生产 join 同传输跨会议竞争只创建一个身份；数据库写入后注册前注入关闭，已提交连接得到断开补偿 | CT-C18/C19 的 legacy 两阶段激活路径；可靠收据与注册失败另见 AgentReliableActivationFaultTests |
| [McpAgentRecoveryCapabilityTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentRecoveryCapabilityTests.cs) | 实际 SDK 挂起 wait 期间写入测试专用 SQLite 租约行；受控新会话绑定原业务 ID，旧会话关闭不使其断开 | CT-G09/G10 技术可行性部分；专用测试适配器不包含生产恢复鉴权、收据、激活或租约状态机，不计为生产 resume/renew 验收 |
| [AgentRecoveryFoundationTests.cs](../../../Server/Tests/RoundTableMeeting.Application.Tests/AgentRecoveryFoundationTests.cs) 与 [AgentRecoveryConfigurationTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentRecoveryConfigurationTests.cs) | 配置跨字段校验及 Host 启动失败、规范密钥摘要与指纹、来源/连接恢复限流、失败额度与容量/窗口/并发边界 | design §6/6.1 与密钥基础规则；恢复入口已有接入及部分鉴权测试；入口限流顺序与错误矩阵另见 AgentRecoveryRateLimitEntryTests |
| [AgentTaskFailurePolicyTests.cs](../../../Server/Tests/RoundTableMeeting.Domain.Tests/AgentTaskFailurePolicyTests.cs) 与 [AgentRecoveryRecordTests.cs](../../../Server/Tests/RoundTableMeeting.Domain.Tests/AgentRecoveryRecordTests.cs) | 全部失败分类与额度交叉组合、饱和退避、固定窗口与初始确认、不可变收据字段 | CT-R15 的纯规则及模型部分；持久化失败、显式重试和收据重放另由可靠业务测试覆盖，并发矩阵另见 AgentReliableConcurrencyTests |
| [AgentRecoveryStateTests.cs](../../../Server/Tests/RoundTableMeeting.Domain.Tests/AgentRecoveryStateTests.cs) | 连接代数/绑定及租约、永久模式升级、确认幂等、失败周期与替代投递预算 | 领域转换验证；不等同生产恢复/重试流程 |
| [AgentRecoveryMigrationTests.cs](../../../Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests/AgentRecoveryMigrationTests.cs) | 旧身份预检、Unicode/转义/截断窗口回填、故障全回滚、legacy 升降级、跨 Agent 归属触发器、错误指针拒绝、条件激活/续租/断开 | CT-M01/M05/M11/M16 与持久化边界部分；旧 Failed 保留原始原因与显式重试初始化；状态矩阵与进程恢复另列于下表 |
| [McpAgentAtomicityTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentAtomicityTests.cs) 与 [McpAgentLifecycleTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentLifecycleTests.cs) | 初始投递随 join 同事务创建、wait 确认时间幂等、投递失败保持游标和队首、删除清理 | legacy 生产回归；可靠显式确认与接管另由可靠业务测试覆盖；删除竞争矩阵另见 AgentReliableDeletionConcurrencyTests 与 AgentDatabaseLockOrderingTests |
| [AgentContextBuilderTests.cs](../../../Server/Tests/RoundTableMeeting.Application.Tests/AgentContextBuilderTests.cs) | 固定窗口重放保留原元数据和消息，忽略后来消息；已截断历史不参与重新选择；缺失已投递历史或未知格式拒绝 | CT-X 的上下文构造部分，可靠 wait/resume 已接入；缺失历史与响应丢失组合另见 AgentContextFailureBoundaryTests 与 AgentReferenceProcessAcceptanceTests |
| [McpAgentOwnershipFenceTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentOwnershipFenceTests.cs) | 注册仍存在但当前指针已撤销或绑定版本已变化时，status/wait/reply 均拒绝且不确认初始上下文 | 当前所有权栅栏；真实 takeover/resume 竞争另见 AgentReliableConcurrencyTests 与 AgentReferenceProcessAcceptanceTests |
| [AgentReliableJoinServiceTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableJoinServiceTests.cs) | Application 可靠加入收据重放不创建/重绑；显式接管在线 legacy 身份，失效在途尝试；再次接管保持失败决定、耗尽状态和预算 | CT-C/R 的应用服务部分；生产 SDK 已接入；该文件本身为应用层测试，不计为完整真实协议验收 |
| AgentReliableTaskBoundaryMatrixTests.cs、AgentReliableLeaseOrderingTests.cs、McpAgentLegacyCapabilityBoundaryTests.cs | 三任务 FIFO、固定窗口配置变化、错误码/收据/超时矩阵、真实长 wait 与乱序续租、legacy 五工具模式隔离 | 8 项新增测试通过；不替代所有错误注册表分支 |
| AgentReferenceProcessAcceptanceTests.cs | 真实 CLI 丢 wait/confirm、阻塞跨租约周期及 stdin retry、在线接管旧进程退出、远程 DELETE 失败退出码和持久化事实 | 5 项通过；固定回复模式，未调用真实模型 |
| AgentReliableRegistrationCloseTests.cs、AgentDatabaseLockOrderingTests.cs | 实际 SDK 注册切点关闭、真实 SQLite 写锁与删除/扫描/取消交错 | 8 项定向测试通过；无生产故障钩子 |
| AgentSignalAndTakeoverProjectionTests.cs、AgentSubscriptionSnapshotTests.cs | 丢任务唤醒后的数据库轮询、接管两阶段完整版本 payload、生产混合订阅快照 | 3 项通过；订阅 Hub 上下文为替身，其他服务使用真实 DI/SQLite |
| AgentReferencePendingRecoveryTests.cs、McpAgentErrorEnvelopeBoundaryTests.cs、AgentRecoveryConditionalConflictTests.cs | CLI pending 与补偿完整链路、实际错误 envelope/schema/敏感日志、条件写失败保持状态并允许原参数重试 | 7 项定向通过；SDK 统一错误 schema 检查已接入，全量 600 项回归通过 |
| 客户端 RealtimeChatRegion.test.tsx、messageReducer.test.ts | 主动注入切会/删除后迟到事件；重连保留草稿和待重试提交的幂等 ID | 已复现并修复旧删除通知和重连清空草稿；客户端全量 103 项通过 |
| AgentInfrastructureLogPrivacyTests.cs、AgentSharedLogPrivacyTests.cs、McpAgentConfirmationErrorSchemaTests.cs | 补偿/activity/订阅/分发异常只记录安全诊断字段；确认超时保持任务和游标且返回正确 SDK 错误 schema | 五个日志故障切点及一个确认边界通过；不记录原始依赖异常 |

生产可靠工具、租约/接管/重试、版本化 activity 和参考 Agent 已有实现与下述部分验证；竞争/崩溃矩阵及真实模型验收均已有逐项证据，不将部分验证折算为整项 CT 用例通过。

新增业务覆盖（仍不作为完整 CT 验收结论）：

| 自动化测试文件 | 已验证的边界 | 限制 |
| --- | --- | --- |
| [AgentReliableRecoveryServiceTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableRecoveryServiceTests.cs) | 恢复同 ID/代数/预算、旧绑定失效、收据重放不续租、错误密钥、初始确认幂等和租约精确边界 | Application 与真实 SQLite；网络丢失和崩溃证据另见 AgentReferenceProcessAcceptanceTests、AgentRecoveryProcessTests 与 AgentRecoveryTransactionCrashTests |
| [AgentReliableTaskServiceTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableTaskServiceTests.cs) | 显式任务确认、固定窗口重放、回复提及、恢复后回复重放、永久失败阻塞、显式 retry 周期及旧失败收据隔离、确认超时替代计数 | 覆盖正常闭环与部分精确时间边界；CT-R/CT-X 的并发证据另见下文逐项表 |
| [McpAgentReliableProductionTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentReliableProductionTests.cs) | 真实 SDK 调用九个生产工具与 schema 校验；可靠失败/重试/回复/恢复闭环、旧模式参数拒绝、在线接管和禁止降级；挂起 wait 时并发续租、resume 取消旧 wait、实际关闭钩子隔离 | 不代替参考客户端、真实模型和全部 MCP 错误矩阵 |
| [AgentRecoveryMaintenanceTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentRecoveryMaintenanceTests.cs) | Delivered/Processing 在传输关闭、租约到期、注册遗失时原子失败；重复扫描不改写决定；旧关闭不影响恢复后的绑定；确认超时不使有效连接离线 | 可控时钟与实际 SQLite；进程崩溃与删除屏障另列于下表，分页边界另由 Repository 测试覆盖 |
| [McpAgentExceptionContractTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentExceptionContractTests.cs) | 九个工具意外异常的可靠/legacy/未知模式与匿名错误格式、schema、猜测 ID 不继承其他连接模式、异常正文不泄露 | 六项真实 MCP 测试；不代替全部业务错误优先级和取消竞争 |
| [AgentRecoveryProcessTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentRecoveryProcessTests.cs) | 真实 dotnet 宿主强制终止并复用 SQLite 重启：初始未确认、Delivered、Processing、Replied、临时 Failed、永久阻塞；恢复原 ID、固定快照、新尝试、回复去重及保留失败决定 | CT-S02、CT-R10/R12 和 CT-P03 的状态边界重启；精确提交/响应前切点另见 AgentRecoveryTransactionCrashTests，不能将本文件收到回复后杀进程的场景混计为响应发送前 |
| [AgentActivityRevisionTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentActivityRevisionTests.cs) | 可见变化递增版本、普通续租/重放/后排入队不改版本、过期与注册缺失由订阅先收敛、恢复过程两次可见转换 | CT-V03/V07/V09/V11 部分；入口矩阵另见 AgentActivityTransitionMatrixTests、AgentSignalAndTakeoverProjectionTests 与 AgentSubscriptionSnapshotTests |
| 客户端 meetingChatConnection.test.ts、messageReducer.test.ts、ParticipantList.test.tsx | revision 严格映射、旧版本拒绝、重连同版本快照恢复、中文状态与接管提示 | CT-V02/V03/V08/V12 的自动化部分；不能代替浏览器视觉检查 |
| [reliable.test.mjs](../../../Demo/AI参会联调客户端/reliable.test.mjs) 与 [reliable-recovery.test.mjs](../../../Demo/AI参会联调客户端/reliable-recovery.test.mjs) | SSE 多路响应、正文超时、私有状态互斥、丢 join/confirm/wait/reply 响应重放、独立续租、显式 retry、恢复后识别已提交回复、旧尝试正文隔离、接管后停止；pending 操作有界重放、尊重服务端退避、取消退出、无业务进展恢复有界、新传输恢复及历史保留 | 参考客户端与 legacy 共 21 项 Node 测试通过；测试替身不计真实模型或真实进程崩溃 |
| [AgentReliableActivationFaultTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableActivationFaultTests.cs) | 可靠 join/resume × 注册前/后 × 返回失败/抛异常八种组合；补偿离线、原操作重放、新操作必须新传输、原身份和初始投递保留 | CT-C12/C19/C20 的实际 MCP 与 SQLite 故障注入；进程强杀另见事务切点测试 |
| [AgentRecoveryMigrationMatrixTests.cs](../../../Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests/AgentRecoveryMigrationMatrixTests.cs) | 六种旧任务状态及尝试组合 × 连接在线/离线 × 初始确认/未确认共 48 项；五项缺失尝试、最新尝试歧义和跨 Agent 归属拒绝升级 | CT-M02/M05/M11/M16；验证迁移事实和回滚，不等同每个旧状态的生产接管闭环 |
| [AgentReliableConcurrencyTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableConcurrencyTests.cs) | reply/takeover、reply/failure 两种提交顺序，双 reply 的重放/正文冲突、双 retry 的去重/拒绝、双 takeover 顺序、续租锁内跨过截止、confirm/scan 在截止前/截止时两种顺序、排队取消释放，共 16 项 | CT-C05/P01/P02/P05/R07/L10/X08/R02/L04；独立 scope、真实 SQLite 和同步屏障；不代替全部错误组合 |
| [AgentReliableDeletionConcurrencyTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableDeletionConcurrencyTests.cs) | 真实 HTTP 删除已占独占 gate 尚未提交屏障时，十种可靠业务调用与扫描全部拒绝；另一会议 Human 发言和 AI 加入/续租成功；释放后全部恢复资源清理、旧恢复/接管拒绝 | CT-D01/D04/D06 的删除先获得 gate 顺序；业务先持锁顺序另见 AgentReliableDeletionOrderingTests，数据库锁交错仍见 CT-D06 缺口 |
| [AgentLegacyFailedAttemptRecoveryTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentLegacyFailedAttemptRecoveryTests.cs) | 旧 Failed 携 Prepared/Delivered/Abandoned 历史尝试，接管保持原原因/时间/阻塞，显式 retry 后实际领取、确认、回复和一次替代链成功 | CT-M03/R06/R12；三项先复现替代异常再验证修复；测试准备旧形态持久数据，不等同执行旧版程序的端到端迁移 |
| [AgentRecoveryTransactionCrashTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentRecoveryTransactionCrashTests.cs) 与 [RecoveryProcessDiagnosticStartup.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/RecoveryProcessDiagnosticStartup.cs) | join、激活、reply、删除屏障在事务提交前/后同步暂停且尚未响应时强杀真实生产进程，复用 SQLite 重启，共八项；验证回滚、已提交身份恢复、回复/提及/事件原子性与重放、Deleting 清理含多代连接/替代尝试/失败任务的全部恢复资源 | CT-S01/P03/P06/D03/D05 的精确切点；探针只在测试程序集，未向生产增加故障入口。reply 提交前切点在任务标记与消息写入后，不等同消息写入与任务标记之间的异常注入；删除切点在屏障事务，不等同清理每一步均强杀 |
| [AgentTakeoverContextContinuityTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentTakeoverContextContinuityTests.cs) | 首次确认/未确认 × 预算缩小/放大四项；S=10、A=(10,15]、B=(15,20]、接管 S2=25，确认不跳过旧任务，恢复保持原区间和幂等键，后续任务包含 21..28 的普通及 Agent 消息 | CT-X01/X03/X04/X10/X14/X18 的实际持久化业务链路 |
| [AgentAutomaticRetryBudgetTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentAutomaticRetryBudgetTests.cs) | 额度 0/3 各执行两个完整失败周期，短退避数组饱和，耗尽后 resume/takeover 保留失败事实；显式 retry 首次领取不占额度，后续队列仍受队首约束 | CT-R04/R05/R06/R12/R14/R16；真实 SQLite 与可控时间 |
| [AgentReliableContinuityBoundaryTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableContinuityBoundaryTests.cs) | 真实 SDK 在消息追加后、任务标记前注入异常，消息/提及/事件/投递全部回滚，原确认尝试可重试成功；空首次历史及无待办/已完成任务后的接管保留分配边界 | CT-P06 与 CT-X05/X21；四项通过，补足此前事务提交前切点的位置限制 |
| [AgentReliableQueueIsolationTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableQueueIsolationTests.cs) | 离线 100 次完整扫描不改变失败决定、游标、版本或次数，恢复后才产生一次替代；同会议可靠队首阻塞时 legacy 仍隐式确认和回复、错误 data=null，Human 正常发言 | CT-R03/M19/X11；两项通过 |
| [AgentLegacyMigrationTakeoverTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentLegacyMigrationTakeoverTests.cs) | 从 AddAgentCollaboration 旧 schema 的六种任务状态及尝试形态启动生产宿主，自动升级/收敛后真实 SDK 接管；保留 TaskId、回复键和区间，旧 Failed 显式重试后完成，Replied/Cancelled 不重投，外键检查通过 | CT-M02/M03/M07/S03 的十二项完整升级链路；源初始已确认、源连接 Connected，其他初始/在线组合由持久化矩阵补充 |
| [McpAgentJoinValidationTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentJoinValidationTests.cs) | 非法模式、缺少/空操作 ID、密钥长度/编码、legacy 参数兼容、非法 UUID/预算类型共十六项；区分 SDK 拒绝与生产业务错误，验证输出 schema、匿名 stop/null data、无身份或收据残留 | CT-M20 与部分 CT-M10/M12/M14；不等同所有工具的错误优先级和归属组合 |

| [AgentActivatingReplayRaceTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentActivatingReplayRaceTests.cs) | create/resume/takeover 的 Activating 提交后中断，原收据重放与接管两种顺序；扫描补偿后恢复清理残留旧注册，旧传输迟到关闭不影响当前版本，共八项 | CT-C12/C19/C20 与部分 CT-D06；注入实际事务提交后中断，补偿事务自身失败另验 |
| [AgentActivationCompensationFailureTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentActivationCompensationFailureTests.cs) | create/resume/takeover 注册失败后的补偿事务在断开写入后、提交前抛异常；回滚保留 Activating，再分别原收据重放或扫描补偿/新传输恢复，共六项；身份、收据、版本和旧关闭隔离均正确 | CT-C12/C19/C27；真实 SDK 与实际 SQLite 事务，不混计为进程强杀 |
| [AgentContextFailureBoundaryTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentContextFailureBoundaryTests.cs) | 首次构造/已投递重放 × 缺失窗口不变量/暂时仓储异常四项；永久失败阻塞且失效原尝试，短暂错误回滚；修复数据后显式 retry 或原尝试重放可完成 | CT-X22；不变量错误与暂时故障分别断言持久化事实 |
| [AgentActivityTransitionMatrixTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentActivityTransitionMatrixTests.cs) | 实际超时、替代、确认、永久失败、显式 retry、回复和后继任务；重复扫描/领取/确认/失败/重试/回复收据/status/renew 不改可见版本或 changedAt | CT-V11 的任务入口矩阵，连接与删除入口继续单独核对 |
| [AgentRecoveryRateLimitEntryTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentRecoveryRateLimitEntryTests.cs) | 五项真实 SDK 验证来源请求/来源失败/连接请求/连接失败额度及 create/takeover 共用额度；跨会话/操作 ID 不重置；超限即使密钥正确也无持久化改动，窗口过后可恢复 | CT-L13；桶容量等基础边界由 AgentRecoveryFoundationTests 覆盖 |
| [McpAgentReliableOwnershipTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentReliableOwnershipTests.cs) | 同会/跨会两项中七工具逐一拒绝其他 Agent/随机连接 ID，以及使用自己连接访问他人任务/尝试；每次拒绝比较双方持久事实，双方随后仍可正常回复 | CT-M04/M10/M12 与工具归属；不代表所有业务状态错误优先级 |
| [reliable-state.test.mjs](../../../Demo/AI参会联调客户端/reliable-state.test.mjs) | Unix 遗留锁不被并发启动回收，失败方 release 不删除锁；停止双方后人工清理只允许一个进程取得锁，失败方不能释放成功方的所有权 | Windows 中执行 Unix 分支逻辑；未验证真实 Unix 文件系统的权限和刷盘行为 |
| [McpAgentReliableEntryBoundaryTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/McpAgentReliableEntryBoundaryTests.cs) | Human/NFC/空白/大小写保护、create 改 takeover 的收据冲突、后续 resume/takeover 后旧收据无副作用、过期 create 收敛与在线 legacy 无租约保护，以及七工具租约精确到期拒绝，共五项 | CT-C02/C03/C10/C14/C17/C21/C22/C23/L01；实际 SDK 与 SQLite 拒绝后事实比较 |
| [AgentReliableConnectionAdmissionTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableConnectionAdmissionTests.cs) | 同传输跨会议并发 create/create 和 create/takeover 仅一方成功；首次可靠加入或 legacy 升级后激活失败均永久禁止降级，共四项 | CT-C18/C26；失败竞争者不创建连接/收据、不接管目标 |
| [AgentReliableConfirmationBoundaryTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableConfirmationBoundaryTests.cs) | 300 秒任务确认时限与 120 秒租约独立；初始确认无任务截止限制、续租后长时间任务确认成功、停租即使确认未到期也拒绝；确认后超过原截止可重放，回复后确认引导 wait，共两项 | CT-X02/X19/X20/L12/P07；可控服务时钟与真实 SDK，不冒充真实模型运行时间 |
| [AgentActivityDeletionBoundaryTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentActivityDeletionBoundaryTests.cs) | 实际删除 gate 开始后及删除完成后不再发送旧 activity/presence；其他会议通知继续，推送异常后持久化快照可恢复 | CT-V11 删除入口与 CT-S04 状态通知；生产 notifier、实际删除 HTTP/SQLite，Hub 出站使用记录替身 |
| [AgentReliableDeletionOrderingTests.cs](../../../Server/Tests/RoundTableMeeting.IntegrationTests/AgentReliableDeletionOrderingTests.cs) | join/takeover/resume/reply/scan 已持共享 gate 和参与者锁后，删除进入 Closing 等待；另一会议仍可发言/加入，放开后原业务和删除有界完成，共五项 | CT-D01/D04/D06 的业务先持锁顺序；同步屏障，无固定 sleep 判定竞争 |

最新 Release 浏览器复验：通过 Chrome DevTools MCP 创建独立浏览器上下文，登录后建立两个隔离会议；实际参考 CLI 加入、等待、回复；A/B 切换时 Agent 名单及消息不串会；刷新后回复不重复；逐一删除两会议后返回首页，CLI 以 meeting_not_found 停止。Console 无 error/warn。隔离数据库 Meetings、参与者、消息及全部六类 Agent 表均清空，foreign_key_check 无异常；拥有的浏览器页面和发布宿主已关闭。

当前回归结果：服务端全量 600 项（Domain 48、Application 68、Repository 119、Architecture 17、Integration 348）通过，覆盖统一业务错误 schema、激活/补偿、上下文、限流、归属、锁序与恢复矩阵。全量完成后新增的共享日志三个故障切点和确认超时真实 SDK 边界，共 4 项 Release 定向回归通过；最后三处日志参数修复已包含于最新 Release 发布包。客户端 103 项、参考客户端 Node.js 24 的 21 项通过。Release 发布及九工具入口复验通过。客户端已有 jsdom 伪元素 getComputedStyle 与产物大包警告；真实模型 3 项已通过，完整 140 项验收完成。

Release 发布验证通过：最新生产代码以 Node 24.20.0/pnpm 11.24.0 完成发布，包含 wwwroot SPA，不含测试程序集或事务故障探针。默认 Volta 入口会让子进程落到 Node 26 并被版本检查拒绝；本地专用 pnpm 启动器明确选择 Node 24 后通过，无需改项目版本要求。Production 环境与新隔离 SQLite 验证 SPA HTML 200、九个生产 MCP 工具、永久失败后关闭/原 ID 恢复仍阻塞、显式 retry、确认/回复与同消息 ID 重放；删除后 Meetings 和全部六张 Agent 表为零，外键检查通过，隔离进程已停止。HTTP 验证按浏览器发送 Accept: text/html；普通非 HTML 根路径请求按既有约定返回 404。旧基础迁移仍有 PRAGMA 非事务警告，本主题迁移回滚矩阵通过。该路径使用固定正文，不计真实模型验收。

浏览器部分证据：隔离 SQLite、真实生产宿主和构建后的 SPA，使用 browser-debugging-and-control CLI 管理的独立 Chrome 会话（MCP profile 被占用时按技能回退）。实际验证登录、创建会议、Human 提及、初始/任务确认后中文状态、永久失败队首阻塞、resume/takeover 不重置失败、旧绑定拒绝、在线接管只保留一个同名参与者、显式 retry、回复重放只出现一条消息、DELETE 后连接中断、刷新状态保留；刷新后的实时 Console 采集无错误。初始未登录 /api/auth/me 的 401 是登录前探测。后台标签页的弹窗退场动画在置前后正常结束，未据此修改业务代码。

可靠参考客户端真实 MCP 已完成超过十分钟驻留、多次 idle 和两轮固定回复，每轮生成前等待 130 秒（超过默认 120 秒租约）且独立续租；回复响应丢弃后重放无重复消息，SSE 中断后自动恢复并继续等待。强制终止客户端进程后从私有文件恢复，原业务连接 ID 不变；重新运行后输入 quit，DELETE 成功且页面显示连接中断。该延迟是协议测试，不计真实模型执行证据；精确故障切点另见进程与事务测试，真实模型证据见下文独立验证。

补充浏览器验证：390 像素窄屏下三个参与者的状态可读、页面无横向溢出；切换到第二个测试会议未出现第一会议的参与者/消息。删除第二会议后返回第一会议，状态与六条历史消息正确。删除第一会议时可靠客户端正挂起 wait，客户端以 meeting_not_found 停止；刷新后两个测试会议均不再出现。只读查询隔离 SQLite，Meetings 及全部六张 Agent 表计数为 0，foreign_key_check 无违规。已关闭本会话创建的页面、参考客户端、MCP 测试辅助进程和隔离服务；未关闭共享 Chrome。以上正常删除不代替删除中服务进程崩溃或并发接管矩阵。

长名称/多参与者补充验证：使用 Chrome DevTools MCP 的独立浏览器上下文连接隔离宿主，经真实 MCP 加入 16 个 AI 与一名 Human；AI 名称为 64 字符的中文或连续英文。实际进入 Processing、永久失败、等待和确认等待状态。390 与 1280 像素视口下均无横向溢出或行内溢出，名称和标签可读，窄屏滚动后消息输入框未被遮挡。修复长名称压缩头像的 flex 布局问题，构建后复查 17 个头像宽高一致。删除隔离会议后各 Agent 表与 Meetings 均为零，外键检查通过；本次页面已关闭，共享浏览器保留。

恢复边界浏览器复验：最新 Debug 产物以 Production 环境运行隔离 SQLite，通过 Chrome DevTools MCP 操作专属页面；Failed 队首 resume/takeover 后仍阻塞，原连接无权限，显式 retry 后完成，重复 reply 只保存一条回复。旧连接关闭不使新连接离线，新连接关闭显示连接中断，刷新后状态正确。暂时拦截页面消息 POST 验证请求等待期间输入禁用、无失败提示或重试按钮，释放后消息只写入一次；随后已恢复 fetch 并移除临时注入。Console 无错误/警告。删除该测试会议后 Meetings 和六张 Agent 表计数为零，外键检查通过；专属页面及两个辅助进程已关闭，共享 Chrome 保留。本次服务端两处修复已通过全量测试、该宿主复验及下述最新 Release 发布包验证。

真实模型独立验证：生产 Release 宿主、隔离 SQLite、实际 reliable.mjs 与供应商模型 deepseek-v4-flash-vision-exp；租约 6 秒、续租间隔 1 秒、wait 1 秒，不设置人工生成延迟。第一轮针对 6 人团队离线会议记录应用生成 2677 字符方案，HTTP 请求及正文读取共 17.446 秒；期间 86 个持久化采样均为同一 Connected 连接/绑定和 Processing 任务，观察到 18 个不同租约截止时间。第二轮用上一轮上下文识别应用并正确回答 37×19=703，共 24 字符，真实请求 4.691 秒、23 次采样、5 个不同租约截止时间。共两次模型调用、两个任务、两个尝试、两条回复，自动重试为零、没有连接恢复；每轮回复后继续 idle/wait。调用测量仅旁路记录时间和字符数，不改请求、响应或执行时长。

最新发布包重连复验：Chrome DevTools MCP 独立上下文登录并创建隔离会议；输入草稿后停止本任务服务，待自动重连耗尽再启动最新发布包并点击重新连接，草稿完整保留，发送后只出现一条消息。九工具闭环再次通过。删除会议后 Meetings、MeetingParticipants、MeetingMessages 及六张 Agent 表为零，外键检查通过；MeetingCreationReceipts 按既有创建幂等约定保留。最终导航后 Console 无错误/警告；停服期间的连接失败属于本次主动故障注入。本任务页面和服务已关闭，共享 Chrome 保留。

真实模型浏览器复验使用 Chrome DevTools MCP 独立上下文，无 CLI 回退。观察到处理中、等待中、两条不同回答，刷新保持总计四条消息且不重复；真实客户端 quit 返回 0、MCP DELETE 成功，网页显示连接中断。删除本次隔离测试会议后 Meetings、参与者、消息及六张 Agent 表为零，外键检查通过，Console 无错误/警告。客户端日志不含供应商密钥、会议码或恢复密钥；供应商密钥仅在本地子进程环境中使用，未写入仓库。测试页面和宿主已关闭，共享 Chrome 保留。

## 2. 数据和观察约定

会议 A/B；Human Alice/Bob；AI Reviewer/分析师；每个测试生成独立 ID。准备首个入会快照 S=10、任务 A=(10,15]、B=(15,20]、接管快照 S2=25，并在区间穿插 Human/Agent/System 消息。只有 Human 有效提及创建本主题任务。

覆盖四类断点：数据库事务提交前、提交后响应前、客户端收到后确认前、确认提交后回复前。断点必须分别断言数据库任务、当前连接、尝试、两个游标、消息数量及客户端观察，不能只看 HTTP 状态码。

日志和测试失败输出不得包含会议码、recoveryKey、MCP 会话令牌、完整上下文或回复正文；使用 ID 摘要、状态、序号、字符计数和数据库计数。测试凭据来自隔离环境，不能写入仓库。

## 3. 连接、恢复与操作幂等

| 编号 | 级别 | 场景 | 预期 | 当前证据 |
| --- | --- | --- | --- | --- |
| CT-C01 | P0 | 可靠首次 join | 身份、连接、当前指针、初始快照、收据同事务创建；返回下一步确认 | 通过：McpAgentReliableProductionTests、AgentReliableJoinServiceTests；生产 joined 与 SQLite 身份/收据原子事实。 |
| CT-C02 | P0 | Human 同名、大小写及 Unicode 变体 | Human 不可接管；统一 NFC/Trim/大小写唯一规则，无重复参与者 | 通过：McpAgentReliableEntryBoundaryTests.ReliableNamesUseHumanProtectionAndOneNormalizedAgentIdentity；Human 及 NFC/空白/大小写变体。 |
| CT-C03 | P0 | 普通 create 遇在线 AI | 返回 agent_connection_already_active，不改变当前连接 | 通过：McpAgentReliableEntryBoundaryTests 的名称与租约入口测试；有效在线身份保持不变。 |
| CT-C04 | P0 | 显式 takeover 在线 AI | 新业务 ID/新代数/新预算，原参与者 ID 和消息作者不变；旧连接立即失去读写资格 | 通过：McpAgentReliableProductionTests、AgentReliableJoinServiceTests；旧连接无权、新代数及预算。 |
| CT-C05 | P0 | 两个并发 takeover | 每次提交有确定顺序，只有最终当前连接可继续；不出现两个 Connected | 通过：AgentReliableConcurrencyTests 双 takeover 顺序与唯一 Connected。 |
| CT-C06 | P0 | resume 到新 MCP 会话 | 保持原业务 ID、预算和初始快照，增加绑定版本；旧绑定不能继续调用 | 通过：AgentReliableRecoveryServiceTests、Repository 的 ResumeKeepsIdentityBudgetGenerationAndRejectsLateOldBindingWrites。 |
| CT-C07 | P0 | 恢复密钥错误、跨会议或已接管 | 拒绝，不泄露会议/任务；密钥不能恢复 Superseded | 通过：AgentReliableRecoveryServiceTests 错密钥、McpAgentReliableEntryBoundaryTests 已接管身份拒绝、McpAgentReliableOwnershipTests 跨会隔离。 |
| CT-C08 | P0 | join 已提交但响应完全丢失 | 同 operationId/参数/密钥找回原结果；新 MCP 会话上重放只返回 recovery_required，无绑定副作用；再用新 operationId resume，无第二个身份/连接 | 通过：AgentRecoveryTransactionCrashTests 的 join 提交后响应前切点、AgentReliableJoinServiceTests 新传输收据查询；原 ID 恢复。 |
| CT-C09 | P0 | 旧 takeover 收据在后续接管后重放 | connection_superseded，不重新执行抢占 | 通过：AgentActivatingReplayRaceTests 中 takeover-first 对原 takeover 收据返回 connection_superseded。 |
| CT-C10 | P0 | 同 operationId 不同参数/目标绑定 | operation_conflict，不覆盖旧收据 | 通过：AgentReliableJoinServiceTests 改预算冲突、AgentReliableRecoveryServiceTests 改目标绑定冲突。 |
| CT-C11 | P0 | resume 响应丢失后重放 | 同目标绑定返回同一版本，不反复增加 BindingVersion | 通过：AgentReliableRecoveryServiceTests.ResumeServiceReplaysOneVersionAndRejectsOldKeyBindingAndOperation。 |
| CT-C12 | P0 | 接管提交后新注册失败 | 旧连接不复活，新连接条件离线；Activating 经原操作同版本最终化或条件补偿为 Disconnected；离线后需新 resume，旧连接不复活 | 通过：AgentActivationCompensationFailureTests 的 takeover 两种恢复顺序、AgentReliableActivationFaultTests 注册故障；旧身份不复活。 |
| CT-C13 | P0 | 旧 MCP 关闭回调晚于 resume | 回调因旧 BindingVersion 被拒绝，不断开恢复后的连接 | 通过：McpAgentReliableProductionTests 真实旧会话关闭、AgentActivatingReplayRaceTests 迟到关闭。 |
| CT-C14 | P1 | 密钥丢失后的接管 | 凭会议码与名称显式 takeover 可用，无额外外部 Agent 身份验证 | 通过：McpAgentReliableEntryBoundaryTests 用新密钥显式接管；无需原恢复密钥。 |
| CT-C15 | P1 | 主动关闭与原会话短断 | 关闭后 Disconnected 可 resume；仅取消一次请求不立即离线 | 通过：McpAgentReliabilityTransportTests 的取消/关闭钩子、McpAgentReliableProductionTests 原 ID resume；取消与关闭分别断言。 |
| CT-C16 | P0 | resume B2 后重放旧 B1 操作 | operation_superseded，不返回 B1 成功，不改变 B2 或失效其任务 | 通过：AgentReliableRecoveryServiceTests 重绑后旧操作返回 operation_superseded，绑定版本保持 3。 |
| CT-C17 | P0 | join 重放发生于后续 resume 后 | 最多返回 recovery_required 与原业务 ID，不直接重新绑定或踢掉当前传输 | 通过：McpAgentReliableEntryBoundaryTests.JoinReceiptCannotChangeModeAndCannotDisplaceALaterResumeOrTakeover；旧/新传输重放均不改 B2。 |
| CT-C18 | P0 | 同 MCP 会话并发加入两个 AI/会议 | 传输预留只允许一个绑定；失败者不创建连接、不接管目标 | 通过：AgentReliableConnectionAdmissionTests 的同传输跨会议并发 create/create、create/takeover；失败方无连接或收据。 |
| CT-C19 | P0 | Activating 注册前、注册后最终化前分别断开 | 从未返回可用连接；按绑定版本最终化失败或补偿，旧连接不复活 | 通过：AgentReliableRegistrationCloseTests 实际 SDK DELETE 关闭在可靠 TryActivate 注册前/后精确屏障，等待真实 RunSessionHandler 撤销注册，再释放激活；请求无可用成功，持久化离线，原 join 收据和新传输 resume 恢复同身份。 |
| CT-C20 | P0 | 原 Activating 操作重放与新 takeover 竞争 | 只可完成仍匹配的版本；已替换收据不能把旧版本转 Connected | 通过：AgentActivatingReplayRaceTests 的三类操作 × 重放/接管先后共六项。 |
| CT-C21 | P0 | 同 operationId 从 create 改为 takeover | 指纹冲突，无隐式接管；joinMode 不形成绕开幂等的新作用域 | 通过：McpAgentReliableEntryBoundaryTests 同操作 ID 把 create 改成 takeover 返回 operation_conflict，无收据覆盖。 |
| CT-C22 | P1 | create 遇离线 AI、takeover 拼错名称 | 分别返回 agent_takeover_required/agent_not_found，不隐式接管或新建 | 通过：McpAgentReliableEntryBoundaryTests 的离线 create 与拼错名称 takeover；无新建身份。 |
| CT-C23 | P0 | Connected 租约已过期但扫描未执行时 create | 先条件收敛离线，返回 agent_takeover_required，不误报活动连接；不自动接管；另验证可靠 create 遇未升级且 Connected+有效注册的 legacy 时返回 agent_connection_already_active，不因无租约误判离线 | 通过：McpAgentReliableEntryBoundaryTests.ReliableCreateConvergesExpiredLeaseButPreservesLiveLegacyWithoutLease 两种模式。 |
| CT-C24 | P0 | 当前指针与代数配对、resume/并发接管 | 同事务创建并匹配复合外键；resume 不递增代数，错代指针写入拒绝 | 通过：AgentRecoveryMigrationTests 复合指针及条件写、AgentReliableConcurrencyTests 双 takeover、AgentReliableRecoveryServiceTests resume 不改代数。 |
| CT-C25 | P1 | 原密钥齐全、join 响应丢失、只有名称会议码三类恢复 | 分别 resume、收据查询后 resume、显式 takeover；不靠“离线”二字决定新建连接 | 通过：恢复服务/收据/接管真实入口组合，参考客户端 README 按凭据区分三类入口，未按离线状态自动接管。 |
| CT-C26 | P0 | 可靠 Activating 失败后 legacy 重入 | RequiredReliabilityMode 保留，返回 reliability_mode_required，不能因离线或激活失败降级 | 通过：AgentReliableConnectionAdmissionTests.FailedReliableActivationPermanentlyPreventsLegacyDowngrade 首次可靠加入与 legacy 升级两项。 |
| CT-C27 | P0 | operation_pending 后补偿完成的完整客户端链路 | 原 join 重放 recovery_required、原 resume 重放 operation_superseded；原已占用会话新 resume 拒绝，新 MCP 会话用新操作成功 kind=resumed | 通过：AgentReferencePendingRecoveryTests 两种原操作收据下，实际 CLI 经 HTTP/SSE 连接带故障注入的生产应用，观察 operation_pending，扫描补偿后原 join/resume 收据分别恢复指引/失效，原占用会话新 resume 拒绝，新 CLI 传输恢复同 ID 并回复。测试桥接仅转发字节，不伪造协议响应。 |


## 4. 上下文、确认和 FIFO

| 编号 | 级别 | 场景 | 预期 | 当前证据 |
| --- | --- | --- | --- | --- |
| CT-X01 | P0 | 可靠 join 未确认时新消息与提及到达 | 快照 S 稳定，新任务从 S 后分配；确认前 wait 不领取 | 通过：AgentTakeoverContextContinuityTests 未确认首次边界与提及；McpAgentReliableProductionTests 初始未确认 wait 拒绝。 |
| CT-X02 | P0 | 初始 confirm 响应丢失 | 相同 InitialDeliveryId 重放幂等，确认位置只推进一次；初始投递超过任务确认时限仍可在有效连接下确认 | 通过：AgentReliableConfirmationBoundaryTests 初始超过任务确认时限后确认与重复确认；可靠参考客户端确认响应丢弃后重放测试。 |
| CT-X03 | P0 | 初次快照未确认即接管 | 新历史可确认，完成原首次边界确认；边界之后旧任务全部保留 | 通过：AgentTakeoverContextContinuityTests 未确认分支，接管确认后仍保留 A/B。 |
| CT-X04 | P0 | A 已确认未回复、B 排队、快照到 25 | 接管初始确认不跳到 25；A/B 原区间不变，依次恢复 | 通过：AgentTakeoverContextContinuityTests 已确认分支，S=10、A/B、S2=25 的真实持久化链路。 |
| CT-X05 | P0 | 接管时无未完成任务但有普通新消息 | 下次提及从既有分配位置取连续范围，不能因新快照漏掉间隔 | 通过：AgentReliableContinuityBoundaryTests 无待办/已完成后的接管，后续区间不跳过普通消息。 |
| CT-X06 | P0 | 可靠 wait 提交后响应丢失 | 任务 Delivered，游标未提前推进；截止前重复 wait 返回同 ID/同窗口 | 通过：AgentReferenceProcessAcceptanceTests 真实 Node CLI 在实际 wait 响应收到后注入丢失并同传输重放，单尝试、单生成、单回复。服务端固定窗口/游标断言共同覆盖。 |
| CT-X07 | P0 | 确认事务提交后响应丢失 | 任务 Processing、尝试 Confirmed、游标原子一致，confirm 可安全重放 | 通过：AgentReferenceProcessAcceptanceTests 真实 Node CLI 丢失任务 confirm 响应后重放并完成唯一回复；服务端确认原子性断言共同覆盖。 |
| CT-X08 | P0 | confirm 与确认截止竞争 | 以事务内取得锁后条件写时刻判断首次确认，截止前成功或截止后拒绝，不能既确认又创建有效替代尝试 | 通过：AgentReliableConcurrencyTests.ConfirmationAndTimeoutScanHaveOneOutcomeAtTheExactDeadline 四种时间/顺序。 |
| CT-X09 | P0 | 旧尝试迟到确认/回复 | delivery_attempt_stale 或 confirmation_expired，无游标/消息修改 | 通过：AgentReliableTaskBoundaryMatrixTests 实际 SDK 验证替代后旧尝试 confirm/reply 返回 delivery_attempt_stale，全部持久化事实不变。 |
| CT-X10 | P0 | 已确认未回复任务恢复重投 | 新 DeliveryAttemptId，稳定 TaskId/幂等键/范围；确认位置不回退 | 通过：AgentTakeoverContextContinuityTests、AgentRecoveryProcessTests；原任务/幂等键/区间及已确认位置保留，新尝试重投。 |
| CT-X11 | P0 | 同一 AI 连续 3 次提及 | 最早未回复队首阻塞后续；不同 AI 与 Human 不受阻 | 通过：AgentReliableTaskBoundaryMatrixTests 三任务同时排队、队首阻塞后完整依次回复；AgentReliableQueueIsolationTests 验证其他 AI/Human 独立。 |
| CT-X12 | P0 | Processing 期间重复 wait | task_in_progress，不启动第二个任务或新尝试 | 通过：AgentReliableTaskServiceTests.ReliableTaskCycleConfirmsExplicitlyAndReplaysReplyAfterResume。 |
| CT-X13 | P0 | 离线时乱序消息事件、消费者并发 | 按 Sequence 补齐独立任务，任务唯一键有效，无遗漏或倒置区间 | 通过：AgentMentionTaskConsumerTests 的无连接 Agent 乱序补齐与四并发消费者，真实 SQLite 唯一任务及连续游标。 |
| CT-X14 | P0 | 接管改小/改大字符预算 | 初始历史和新尝试按新预算裁剪；已持久化任务区间不变 | 通过：AgentTakeoverContextContinuityTests 两种初始确认状态 × 预算缩小/放大四项。 |
| CT-X15 | P0 | Unicode、系统/AI 消息、超长触发消息 | UTF-16 计数一致，保留所有来源与完整触发消息，元数据准确 | 通过：AgentContextBuilderTests 的 UTF-16/系统消息/完整超限触发，AgentTakeoverContextContinuityTests 混合 Agent 消息。 |
| CT-X16 | P1 | 同次响应重放时服务配置变化 | 使用尝试保存的窗口和格式版本，正文/范围/字符元数据一致 | 通过：AgentReliableTaskBoundaryMatrixTests 运行中改变上下文预算和确认时限，同次 wait 的 ID/窗口/截止不变；合法 wait 的续租单独验证。 |
| CT-X17 | P0 | 可靠模式未确认直接 reply | context_confirmation_required，不隐式确认或追加消息 | 通过：McpAgentReliableProductionTests 与 AgentReliableTaskServiceTests；未确认 reply 拒绝且游标不推进。 |
| CT-X18 | P1 | 接管快照与任务上下文重叠 | 标明 contextPurpose/replayReason，同 messageId 不产生新任务 | 通过：AgentTakeoverContextContinuityTests 的 contextPurpose/task、replayReason/takeover、区间及任务总数。 |
| CT-X19 | P0 | confirm 已提交，过原截止后重放 | 当前已确认且租约有效则成功，无超时失败或重投 | 通过：AgentReliableConfirmationBoundaryTests 续租有效，超过原截止重放不改 ConfirmedAt/租约或创建新尝试。 |
| CT-X20 | P0 | 任务已回复后重复 confirm | 返回确认事实及 wait 下一步，不再次要求回复 | 通过：AgentReliableConfirmationBoundaryTests 已回复 confirm 返回 replayed 与 wait_for_mention，成功尝试保留。 |
| CT-X21 | P0 | 初始空历史、首次边界 0、确认前接管 | 空上下文合法，初始确认不生成非法区间；后续提及仍完整分配 | 通过：AgentReliableContinuityBoundaryTests 的空首次历史和未确认接管。 |
| CT-X22 | P0 | 上下文构造失败的两类原因 | 数据不变量错误保留失败阻塞任务且无空尝试；短暂数据库故障回滚，不伪造永久失败 | 通过：AgentContextFailureBoundaryTests 首次/重放 × 数据不变量/短暂仓储异常四项，恢复后可继续完成。 |
| CT-X23 | P0 | 确认截止已过，wait 先于超时扫描执行 | wait 共用条件超时转换后返回 retry_pending/exhausted 或新尝试，绝不重放过期窗口；扫描随后无重复副作用 | 通过：AgentReliableTaskBoundaryMatrixTests 额度 0/3 的 wait 先超时随后扫描，失败决定和次数仅修改一次。 |
| CT-X24 | P1 | status 查询可靠 Delivered/过期未扫描/Failed 耗尽 | reason、FailureCode、nextAction 符合模式表；查询不推进状态或确认，客户端不凭 ID 盲确认 | 通过：AgentReliableTaskBoundaryMatrixTests 完整断言 Delivered、过期未扫描和 Failed 的 reason/failureCode/nextAction，status 不写状态。 |


## 5. 租约、失败与显式重试

| 编号 | 级别 | 场景 | 预期 | 当前证据 |
| --- | --- | --- | --- | --- |
| CT-L01 | P0 | now 等于租约截止，扫描尚未执行 | 读写/确认/续租已拒绝，必须 resume；不能依赖注册表判在线 | 通过：McpAgentReliableEntryBoundaryTests 七工具精确到期拒绝，数据库事实不变；恢复另由恢复服务测试验证。 |
| CT-L02 | P1 | 默认 30 秒 wait，60 秒客户端请求上限 | 跨多个 idle 持续等待，工具不超宿主时限，会话复用 | 通过：可靠参考客户端真实 MCP 超十分钟驻留，多次默认 30 秒 idle；默认 wait 请求上限为 45 秒，小于 60 秒。 |
| CT-L03 | P1 | 请求长 wait 或无效 wait 参数 | 基础租约可以小于长 wait；取 max(原租约,now+基础,wait截止+余量)，越界参数拒绝 | 通过：AgentReliableLeaseOrderingTests 实际 SDK 的非法 wait 无续租、300 秒长 wait 与基础租约的 max 公式。 |
| CT-L04 | P0 | 请求取消、进程静默消失 | 取消不立即离线；没有后续有效操作时租约到期，离线最终可见 | 通过：McpAgentReliabilityTransportTests 协议取消仍可用，AgentRecoveryMaintenanceTests 到期收敛及浏览器离线。 |
| CT-L05 | P0 | 模型处理时间超过多个租约周期 | 独立续租有效则保持 Processing，不因处理慢自动重试 | 通过：真实 deepseek-v4-flash-vision-exp 请求耗时 17.446 秒，超过两个完整的 6 秒租约周期；1 秒独立续租，86 个持久化采样保持同一连接/绑定和 Processing，18 个不同租约截止时间，无自动重试。 |
| CT-L06 | P1 | 同 MCP 会话 wait 与 renew 并发 | 支持续租，不误把业务串行循环当成所有请求串行 | 通过：McpAgentReliableProductionTests.ProductionReliableWaitAllowsConcurrentRenewalAndResumeCancelsOnlyOldBinding。 |
| CT-L07 | P0 | 旧绑定续租与接管竞争 | 不能延长新连接租约或使旧连接复活 | 通过：AgentReliableLeaseOrderingTests 固定 renew/takeover 两种锁顺序，旧绑定不能修改或复活新连接。 |
| CT-L08 | P1 | status 反复查询、非法请求 | 不续租；查询本身不令离线连接恢复 | 通过：AgentActivityRevisionTests 无变化读取/续租区分、McpAgentReliableEntryBoundaryTests 非法过期读取不续租。 |
| CT-R01 | P0 | 确认超时后再次领取 | 保留原任务，旧尝试失效；建立替代时同事务 Abandoned+ReplacementAttemptId | 通过：AgentReliableTaskServiceTests 可靠超时替代，AgentAutomaticRetryBudgetTests 完整额度周期与尝试计数。 |
| CT-R02 | P0 | 扫描器与请求重复处理同一失败 | 条件更新只生效一次，不重复计数或延期 | 通过：AgentReliableConcurrencyTests confirm/scan 截止竞争、AgentRecoveryMaintenanceTests 重复失败不延期。 |
| CT-R03 | P0 | 离线 100 次扫描 | 不创建替代尝试、不耗尽计数，恢复后最早任务继续 | 通过：AgentReliableQueueIsolationTests.OneHundredOfflineScansDoNotSpendRetriesOrMoveTheFailedHead。 |
| CT-R04 | P0 | 连续达到 3 次自动替代投递后再次失败 | RetryExhausted=true，第四次替代不会自动发生，队列保持阻塞 | 通过：AgentAutomaticRetryBudgetTests 自动额度 3 的完整两周期，耗尽队首阻塞。 |
| CT-R05 | P0 | 耗尽后 resume/takeover | 不重置周期和次数，不绕过失败队首 | 通过：AgentAutomaticRetryBudgetTests 耗尽后 resume/takeover 与浏览器阻塞状态复验。 |
| CT-R06 | P0 | 当前 Agent 显式 retry_task | 重试周期增加、计数归零，本周期首次投递不占自动名额、之后最多自动替代 3 次；原 TaskId/幂等键不变，后续任务不越序 | 通过：AgentAutomaticRetryBudgetTests 额度 0/3 两周期，首次免费、稳定任务/幂等键，后继仍排队。 |
| CT-R07 | P0 | retry_task 响应丢失重放 | 同 operationId 只增加一次周期；旧周期收据不重新激活任务 | 通过：AgentReliableTaskServiceTests 重复 retry 收据与 AgentReliableConcurrencyTests 同/不同操作双 retry。 |
| CT-R08 | P0 | Processing/非耗尽/跨 Agent 的 retry | 分别返回 task_in_progress/retry_not_required/拒绝，不破坏正常任务 | 通过：AgentReliableTaskBoundaryMatrixTests Processing/Delivered/未耗尽失败的 retry 错误与无修改；跨 Agent 归属测试共同覆盖。 |
| CT-R09 | P1 | 临时失败、永久失败和未知错误码 | 临时失败退避；永久失败阻塞；无任意敏感文本写入失败码 | 通过：AgentReliableTaskBoundaryMatrixTests 实际临时失败退避和非法敏感错误码拒绝；已有永久失败闭环验证阻塞。 |
| CT-R10 | P1 | 服务重启发生在退避期间 | NextRetryAt 和次数持久化，重启不清零、不提前越序 | 通过：AgentRecoveryProcessTests.RealServerCrashAndResumePreserveAnExistingFailureDecision 的临时失败分支。 |
| CT-L09 | P0 | 300 秒 wait 后并发执行 120 秒基础续租 | LeaseExpiresAtUtc 不缩短；旧续租响应乱序也不改变服务端事实 | 通过：AgentReliableLeaseOrderingTests 实际挂起 300 秒 wait 并续租，先释放较新响应再释放较旧响应；持久化租约不缩短。 |
| CT-L10 | P0 | 续租请求在截止前排队、截止后才取得锁 | 使用锁后时间拒绝，不用请求到达时间复活租约 | 通过：AgentReliableConcurrencyTests 续租取得锁后跨截止，两种时序及持久化租约断言。 |
| CT-R11 | P0 | 新尝试产生后重放上一尝试失败报告 | 同码返回原决定和当前摘要，新尝试不失败；改码冲突 | 通过：AgentReliableTaskBoundaryMatrixTests 替代前后同码重放原决定，改码 operation_conflict，新尝试状态不变。 |
| CT-R12 | P0 | 已 Failed 的任务经历多次关闭/恢复/接管 | NextRetryAt、周期、耗尽和失败决定不被重置或不断后延 | 通过：AgentAutomaticRetryBudgetTests、AgentRecoveryProcessTests 与 AgentReliableJoinServiceTests 保留失败原因/时间/周期/额度。 |
| CT-R13 | P1 | 非法外部 failureCode | failure_code_invalid，当前 Processing 不受影响，不误写永久失败 | 通过：AgentReliableTaskBoundaryMatrixTests 实际 report_task_failure 非法代码返回 failure_code_invalid，Processing 与持久化数据不变。 |
| CT-R14 | P1 | 最大自动重试为 0、退避数组较短 | 首次失败即阻塞；配置为多次重试时退避索引饱和至最后一项，不越界 | 通过：AgentAutomaticRetryBudgetTests 额度 0 和短退避数组的两个完整周期，AgentTaskFailurePolicyTests 饱和退避。 |
| CT-L11 | P0 | 跨字段配置非法 | 覆盖默认 wait 超上限、wait+余量不小于租约、renew+余量不小于租约、负重试、空/非正退避数组、时间计算溢出，启动拒绝 | 通过：AgentRecoveryConfigurationTests 生产 Host 启动拒绝与 AgentRecoveryFoundationTests 跨字段/溢出校验。 |
| CT-L12 | P1 | 确认时限大于基础租约且独立续租有效 | 配置允许，确认时限内仍可成功；停止续租后按租约失效，不能伪造长期连接 | 通过：AgentReliableConfirmationBoundaryTests 的 300 秒确认与 120 秒租约，持续续租/停止续租两项。 |
| CT-L13 | P1 | join/takeover 共用限流和 resume 多维限流 | 换操作/会话不能清零来源等计数；密钥错误触发失败额度；超限前不改所有权，可靠 data 含 retryAfterSeconds | 通过：AgentRecoveryRateLimitEntryTests 五项实际 SDK 来源/连接及失败额度、join/takeover 共用限流。 |
| CT-L14 | P1 | 限流桶容量与到期清理 | 达到 RecoveryRateLimitMaximumBuckets 后拒绝新桶，到期释放；不记录原始会议码/密钥 | 通过：AgentRecoveryFoundationTests 容量拒绝、过期回收、迟到失败与并发额度，不存原始密钥或会议码。 |
| CT-R15 | P1 | 固定 FailureCode/stopReason 和未知旧原因 | 单一判定函数交叉覆盖全部原因与剩余额度：可恢复原因有额度为 false/null，无额度为 true/attempts_exhausted；永久原因无论额度均为 true/permanent_failure；耗尽保留最近原因，未知旧原因转 internal_failure 阻塞；显式重试清除停止标志，原收据决定不变 | 通过：AgentTaskFailurePolicyTests 全原因/额度交叉、迁移未知原因阻塞、可靠显式重试及原失败收据隔离。 |
| CT-R16 | P1 | 退避期间 wait → retry_pending → renew → wait | 根据 nextRetryAt 调度，保持租约；到期只建立一次替代投递 | 通过：AgentReliableTaskBoundaryMatrixTests 同一任务 wait/retry_pending/renew/到期 wait 连续执行，替代投递仅一次。 |


## 6. 回复、崩溃与删除

| 编号 | 级别 | 场景 | 预期 | 当前证据 |
| --- | --- | --- | --- | --- |
| CT-P01 | P0 | 同任务并发相同回复 | 只追加一条消息，一组 mentions/event/deliveries，另一请求重放 | 通过：AgentReliableConcurrencyTests 同正文双回复；只提交一组消息/提及/事件/投递。 |
| CT-P02 | P0 | 幂等键相同正文不同 | reply_idempotency_mismatch，不覆盖既有消息 | 通过：AgentReliableConcurrencyTests 不同正文双回复及 AgentReliableTaskServiceTests 已回复正文冲突。 |
| CT-P03 | P0 | 回复提交后响应丢失并 resume | 查询已完成摘要和原消息 ID，同成功尝试/正文重放原结果，不重新回复 | 通过：AgentRecoveryTransactionCrashTests 的回复提交后响应前强杀/重启/resume/重放，参考客户端真实丢 reply 响应复验。 |
| CT-P04 | P0 | 已接管的旧连接请求幂等重放 | 先拒绝身份，不能以幂等键读取旧结果或写消息 | 通过：AgentReliableConcurrencyTests takeover 先提交则旧 reply 拒绝，所有权校验先于回复收据处理。 |
| CT-P05 | P0 | reply 与 takeover 并发 | 先提交回复则保留唯一消息；接管先提交则旧 reply 无任何写入 | 通过：AgentReliableConcurrencyTests reply/takeover 两种确定锁顺序。 |
| CT-P06 | P0 | 消息追加后任务标记前事务失败 | 消息、任务和事件全部回滚，无半提交 | 通过：AgentReliableContinuityBoundaryTests.ReliableReplyFailureBetweenMessageAppendAndTaskMarkRollsBackEveryWrite。 |
| CT-S01 | P0 | join 提交前/后分别杀进程 | 未提交无残留；已提交 Activating/Connected 都可收敛，收据查询后发起 resume，无同名占死 | 通过：AgentRecoveryTransactionCrashTests 的 join/activation 两阶段各提交前/后真实进程切点。 |
| CT-S02 | P0 | Delivered/Processing/Failed/Replied 状态分别重启 | 未完成任务可恢复，已回复不重投；旧传输全部失效 | 通过：AgentRecoveryProcessTests 六种任务/初始状态的真实崩溃重启与原 ID 恢复。 |
| CT-S03 | P0 | 启动扫描与新 join/resume | 启动门生效，旧扫描不能把新代数断开；初始化未完成不开放 Agent 接入 | 通过：Program 在 app.Run 前完成初始化，生产旧库启动迁移/恢复测试与条件绑定栅栏；旧版本扫描不能断开新版本。 |
| CT-S04 | P1 | 信号/状态通知丢失 | 数据库轮询、状态快照能收敛；任务不依赖内存信号保存 | 通过：AgentSignalAndTakeoverProjectionTests 实际 SDK 等待空队列后丢弃唤醒信号，超时轮询仍从数据库领取并确认；已有丢通知后快照恢复断言共同覆盖。 |
| CT-D01 | P0 | 删除与 join/resume/takeover/confirm/reply/renew 竞争 | 屏障后不得成功读写，旧会议码与密钥都不能恢复 | 通过：AgentReliableDeletionConcurrencyTests 删除先持 gate 拒绝十种可靠操作，AgentReliableDeletionOrderingTests 业务先持锁，删除后凭据不可恢复。 |
| CT-D02 | P0 | 删除含初始投递、收据、耗尽任务和租约的会议 | 所有新增资源清理，当前指针及外键顺序正确 | 通过：AgentRecoveryTransactionCrashTests 删除含多代连接/初始投递/收据/阻塞队首的会议，发布包和浏览器删除后七表为零。 |
| CT-D03 | P0 | 删除中进程崩溃重启 | Deleting 不回 Active，清理可重入，无恢复扫描复活 | 通过：AgentRecoveryTransactionCrashTests 屏障提交后强杀，重启清理 Deleting；McpAgentAtomicityTests 清理事务失败回滚并重试。 |
| CT-D04 | P0 | 删除一个会议期间其他会议运行 | 其他会议 Human 和 AI 正常工作，无全局串行阻塞 | 通过：AgentReliableDeletionConcurrencyTests、AgentReliableDeletionOrderingTests 和 AgentActivityDeletionBoundaryTests 另一会议业务/通知继续。 |
| CT-D05 | P0 | 多代连接、多次替代尝试的删除 | 先清 CurrentConnectionId/CurrentAttemptId，再清 SupersededByConnectionId/ReplacementAttemptId，最后删关联行，真实 SQLite 外键检查通过 | 通过：AgentRecoveryTransactionCrashTests 实际 SupersededBy/Replacement 链、清理后 SQLite 外键检查及资源归零。 |
| CT-P07 | P0 | Replied 后 CurrentAttemptId 生命周期 | 保留成功尝试并可重放原回复，不能替换或清空；仅会议删除清理时清空 | 通过：AgentReliableConfirmationBoundaryTests 成功尝试保留、AgentRecoveryTransactionCrashTests 原回复重放及删除清理。 |
| CT-D06 | P0 | 传输预留/会议 gate/参与者锁/数据库锁交错 | join/resume/reply/delete/scan 各路径有界完成，取消释放持有资源，无反向等待；其他会议可继续 | 通过：AgentDatabaseLockOrderingTests 六项实际 SQLite 写锁与 join/takeover、resume、reply、scan/删除交错，含 resume/reply 取消；释放写锁后有界完成、传输预留可重新获取，另一会议继续；此前 gate/参与者锁两顺序与同传输预留矩阵共同覆盖。 |


## 7. 兼容、迁移与约束

| 编号 | 级别 | 场景 | 预期 | 当前证据 |
| --- | --- | --- | --- | --- |
| CT-M01 | P0 | 空库迁移 | 所有表、复合外键、唯一索引和 CHECK 生效 | 通过：AgentRecoveryMigrationTests、AgentCollaborationPersistenceTests 在独立空 SQLite 的迁移与 CHECK/唯一/复合外键约束。 |
| CT-M02 | P0 | 主题五数据库副本升级 | 参与者/消息/任务/幂等键/候选范围保持，不伪造客户端确认 | 通过：AgentRecoveryMigrationMatrixTests 48 项旧事实组合与 AgentLegacyMigrationTakeoverTests 十二项生产升级接管。 |
| CT-M03 | P0 | 旧未完成任务首次可靠接管 | 回填初始投递、CurrentAttemptId 与周期；旧尝试失效后可重投，游标已推进也不能吞任务 | 通过：AgentLegacyMigrationTakeoverTests 各未完成旧状态继续，AgentLegacyFailedAttemptRecoveryTests 三种旧失败尝试可显式重试。 |
| CT-M04 | P0 | 非法跨会议连接/尝试/指针写入 | Domain 或 SQLite 约束拒绝，数据不串会 | 通过：Repository 归属触发器/复合外键拒绝与 McpAgentReliableOwnershipTests 同会/跨会七工具隔离。 |
| CT-M05 | P0 | 多条旧 Connected 或非法旧状态 | 明确迁移诊断，不静默选择一条覆盖事实 | 通过：AgentRecoveryMigrationTests 歧义身份及 AgentRecoveryMigrationMatrixTests 五类非法尝试中断升级。 |
| CT-M06 | P1 | 老客户端省略 reliabilityMode | 原四工具正常闭环，既有字段/nextAction 语义兼容 | 通过：原 McpAgentContractTests/McpAgentLifecycleTests 四工具闭环及真实 legacy DTO 字段测试。 |
| CT-M07 | P0 | 新 Agent 可靠接管 legacy 连接 | 合法升级；之后尝试降级返回 reliability_mode_required | 通过：McpAgentReliableProductionTests 在线 legacy 接管，AgentReliableConnectionAdmissionTests 激活失败仍禁止降级。 |
| CT-M08 | P1 | 工具发现与能力协商 | 工具/schema/版本一致；不支持能力时不冒充可恢复 | 通过：McpAgentRecoveryContractTests、AgentToolSchemaTests 与 ReliableTransport.open 的九工具能力检查；生产可靠 join 能力表。 |
| CT-M09 | P0 | 可靠新工具误用于 legacy 连接 | 明确 capability/模式错误，不能半执行可靠状态机 | 通过：McpAgentLegacyCapabilityBoundaryTests 五个可靠新工具对已绑定 legacy 返回 reliability_mode_required；匿名 resume 对 legacy/随机 ID 统一 resume_rejected，不泄露模式。 |
| CT-M10 | P1 | 敏感字段与错误输出扫描 | 密钥、会议码、绑定及正文不进入日志、网页或一般错误响应 | 通过：SDK 业务错误统一 schema/字段白名单、McpAgentExceptionContractTests 与 McpAgentErrorEnvelopeBoundaryTests 验证响应及敏感正文隔离；AgentInfrastructureLogPrivacyTests/AgentSharedLogPrivacyTests 五个依赖异常日志切点通过；真实参考 CLI 失败日志敏感值断言及网页字段隔离通过。 |
| CT-M11 | P0 | 两个模式的初始状态迁移与确认 | 初始投递表是唯一事实，旧 DTO 投影一致，不残留双写确认状态 | 通过：AgentRecoveryMigrationTests 初始确认唯一事实、降级投影与 AgentRecoveryMigrationMatrixTests 全确认组合。 |
| CT-M12 | P1 | 可靠 retry_pending 等失败序列化 | succeeded=false，data 含准确 nextAction/时间；legacy 失败仍 Data=null；未授权不泄漏摘要 | 通过：McpAgentRecoveryContractTests 失败 schema、McpAgentReliableProductionTests retry 状态及 McpAgentReliableOwnershipTests 匿名/归属拒绝无摘要。 |
| CT-M13 | P0 | 激活状态唯一索引 | Activating+Connected 组合不能同时拥有两个连接；枚举升级保留旧值语义 | 通过：AgentRecoveryMigrationTests 接管前向关联原子性与 AgentCollaborationPersistenceTests 活动连接唯一约束，领域枚举保留旧值。 |
| CT-M14 | P1 | tools/list、DTO、成功 kind、错误注册表一致性 | schema.properties 中可识别 reliabilityMode；逐一用真实工具 JSON 序列化验证全部映射 DTO、schema 与 kind/错误可重试性/data，外层投递元数据不进入消息 DTO | 通过：McpAgentRecoveryContractTests/AgentToolSchemaTests 验证全部 DTO、kind 与工具 schema；业务错误统一经过 SdkPayload schema/字段白名单，入口/归属/任务/条件冲突测试覆盖注册表分支，McpAgentConfirmationErrorSchemaTests 补齐确认超时真实 SDK 边界；operation_pending 由实际 CLI 激活补偿测试验证，冲突可重试性由 reliable-conflict.test.mjs 验证。 |
| CT-M15 | P0 | 两种 wait 投递与初始确认分支 | legacy 仍隐式确认并最终化；可靠未显式确认不能推进游标，两个模式不可串用 finalize | 通过：McpAgentReliableProductionTests 显式确认、AgentReliableQueueIsolationTests 同会两模式的确认/队列独立。 |
| CT-M16 | P0 | 迁移歧义或外键错误中断 | 诊断 ID 摘要，事务回滚且原库仍可读取；不任取最新连接/尝试，源确认事实只保留一份 | 通过：AgentRecoveryMigrationTests 与 AgentRecoveryMigrationMatrixTests 升级失败事务回滚，原 schema/数据可读。 |
| CT-M17 | P1 | 旧 legacy 连接被可靠 Agent 接管后的 wait | connection_superseded 且 Data=null，旧客户端停止；未升级离线 legacy join 仍为 agent_recovery_not_supported | 通过：McpAgentReliableProductionTests 接管后旧三工具 connection_superseded/data=null；旧离线 join 由既有合同测试覆盖。 |
| CT-M18 | P1 | legacy 携带 waitTimeoutSeconds/taskId 新参数 | 非空合法新参数明确 reliability_mode_required，不静默忽略；提示移除参数或可靠接入，与已升级参与者的必须可靠接入提示区分；两者仍 data=null，客户端不解析提示文案分支；原输入/DTO 兼容 | 通过：McpAgentReliableProductionTests/McpAgentJoinValidationTests 的合法新参数拒绝、原字段及 null data；两类提示由现行模式分派分别提供。 |
| CT-M19 | P0 | 同会议 legacy AI 与可靠 AI 并行 | 各自任务、确认、错误形状及队列独立；一方阻塞不影响另一方 | 通过：AgentReliableQueueIsolationTests.ReliableBlockedHeadDoesNotChangeLegacyConfirmationOrStopOtherParticipants。 |
| CT-M20 | P1 | join 新参数非法与模式错误分流 | 非法 reliabilityMode/joinMode、operationId/密钥格式、可靠模式缺少必填参数均为 agent_request_invalid；未知模式 data=null，已知可靠但未认证只给 nextAction=stop；合法参数不兼容才为 reliability_mode_required；分别验证 SDK 协议拒绝与业务适配器错误 | 通过：McpAgentJoinValidationTests 十六项实际 SDK/业务错误分流及无残留，与校验器全部参数规则测试组合。 |


## 8. 网页、参考客户端与真实模型

| 编号 | 级别 | 场景 | 预期 | 当前证据 |
| --- | --- | --- | --- | --- |
| CT-V01 | P1 | 在线接管 | 列表保持一个 AI，名称和历史作者不变，有接管提示 | 通过：真实浏览器在线接管、单一参与者行及接管提示，ParticipantList.test.tsx 补充自动化。 |
| CT-V02 | P1 | 确认等待、退避、耗尽、过期、恢复 | 各状态中文文案与服务端一致；presence 与 activity 不矛盾 | 通过：ParticipantList 中文状态映射与浏览器确认/处理/失败/重试/关闭/恢复复验。 |
| CT-V03 | P0 | 状态事件乱序/重复、刷新与重连 | revision 拒绝旧状态；快照在扫描器执行前遇已过期租约也能先收敛并增加 revision，无同版本不同状态或倒退 | 通过：messageReducer.test.ts 的版本乱序/同版本重连与 AgentActivityRevisionTests 订阅先收敛过期。 |
| CT-V04 | P0 | 会议切换与删除后迟到事件 | 不串会，不让已删除会议重新出现 | 通过：RealtimeChatRegion.test.tsx 主动注入旧会议/旧 generation 的 activity、消息、重新订阅与删除回调；切会后不串会，删除后迟到回调不复活页面。浏览器正常切换/删除已有验证。 |
| CT-V05 | P1 | 长名称、窄屏与多参与者 | 状态不遮挡操作，不新增无授权的接管/重试管理页面 | 通过：真实 Chrome 的 16 个长名 AI 与 Human，390/1280 像素视口无溢出、头像尺寸及输入可达。 |
| CT-V06 | P0 | HTTP 历史和实时消息重复 | AI 回复仍按 messageId/Sequence 去重，统一消息流 | 通过：messageReducer 历史/事件去重与真实浏览器 reply 重放、刷新保持单条回复。 |
| CT-G01 | P1 | 参考客户端长驻留至少 10 分钟 | 跨多次 idle 及续租，两个提及依次回复后继续等待 | 通过：参考客户端真实 MCP 超十分钟驻留、两个提及固定回复、多次 idle 和继续等待；本项不计真实模型。 |
| CT-G02 | P0 | 参考客户端丢 wait/confirm/reply 响应 | 按错误与状态恢复，不重复模型执行同一个有效尝试，不重复写消息 | 通过：AgentReferenceProcessAcceptanceTests 补齐实际宿主丢 wait/confirm 的 CLI 链路，生成和回复各一次；实际丢 reply 的已验证链路共同覆盖。 |
| CT-G03 | P1 | 新 MCP 会话 resume / 新 Agent takeover | 原恢复保留 ID/预算，新接管更换 ID/预算，旧循环停止 | 通过：AgentReferenceProcessAcceptanceTests 实际在线 takeover 后旧 CLI 以 connection_superseded 退出且不恢复，数据库仅新代 Connected；已有真实私有状态 resume 验证 ID/预算保留。 |
| CT-G04 | P1 | 模型实际处理超过一个租约周期 | 宿主独立续租有效，模型完成后正常提交，不伪造支持 | 通过：实际 reliable.mjs 调用真实模型，第一轮 17.446 秒跨租约执行后正常提交；生成前延迟为零，两个任务各只有一个尝试和一条回复。 |
| CT-G05 | P1 | 两轮内容不同的真实模型问题 | 每轮按上下文作答并继续等待；固定回复客户端不得代替 | 通过：第一轮生成离线会议记录应用方案，第二轮根据上下文识别该应用并正确计算 37×19=703；两轮内容不同且每轮回复后继续等待。浏览器实时消息与刷新后均为两条回复，正常 quit 后显示连接中断。 |
| CT-G06 | P1 | 重试耗尽及显式重试 | 客户端先报告阻塞，明确重试后恢复，无自动重置循环 | 通过：AgentReferenceProcessAcceptanceTests 实际 CLI 报告 blocked 后接受 stdin retry，两任务按 FIFO 回复且不自动重置失败。 |
| CT-G07 | P1 | 明确离开与远程关闭失败 | 成功关闭可观察离线；失败报告实际状态，不靠本地退出推断 | 通过：AgentReferenceProcessAcceptanceTests 实际 CLI quit 成功关闭持久化离线；测试进程 fetch 边界注入 DELETE 503，退出码 1/transport_close_failed，持久化连接仍在线，不伪称关闭成功。 |
| CT-V07 | P0 | Activating、Connected 与普通续租快照 | 每次可见改变有新 revision；普通续租不改变 activity.changedAt，同 revision 的 payload 一致 | 通过：AgentActivityRevisionTests、AgentActivationCompensationFailureTests 的状态转换及普通续租版本/时间不变。 |
| CT-V08 | P0 | 接管/离线后到达旧无版本 presence 事件 | Agent presence 不被旧增量覆盖，Human presence 保持原流程 | 通过：messageReducer.test.ts 与 ParticipantList.test.tsx 旧无版本 presence 不覆盖 Agent，Human 保持独立。 |
| CT-V09 | P0 | 注册表缺失但持久化补偿尚未完成 | 先收敛产生新 revision 再发布离线，不在相同 revision 下产生不同状态 | 通过：AgentActivityRevisionTests.SubscriptionSnapshotConvergesExpiredOrMissingBindingOnceAndReturnsThatSameRevision。 |
| CT-V10 | P0 | 混合 Human/Agent 的订阅快照合并 | onlineParticipantIds 仅 Human，Agent 在线来自 agentActivities，不能互相补齐或覆盖 | 通过：AgentSubscriptionSnapshotTests 直接调用生产 SubscribeMeeting，真实授权/持久化/在线注册：onlineParticipantIds 仅 Human，Agent 只在 activity；重复订阅一致。Hub 上下文和发送端为测试替身，已有浏览器真实订阅补充网络路径。 |
| CT-V11 | P0 | revision 全变化点与无变化点矩阵 | design §11 每个入口逐项验证；相同过期状态重复读/扫描不空转，后排入队或普通续租不误改版本 | 通过：AgentSignalAndTakeoverProjectionTests 精确比较 takeover 的 Activating/Connected 两个完整 payload 与 revision，收据重放无新事件；已有任务/断开/恢复/删除矩阵共同覆盖。 |
| CT-V12 | P1 | agentActivityChangedV1 增量兼容 | 新 DTO/快照/map/reducer 同步；旧客户端忽略新增字段，新客户端按 revision，无未声明 V2 依赖 | 通过：现行 agentActivityChangedV1 通知、服务 DTO、meetingChatConnection 映射及 reducer 的新旧字段兼容测试。 |
| CT-G08 | P1 | 耗尽等待明确重试超过多个租约周期 | 业务循环暂停、renew 循环持续；显式 retry 后继续；主动离开才停止续租 | 通过：AgentReferenceProcessAcceptanceTests 实际宿主配置 6 秒租约/1 秒续租，阻塞等待 13 秒，至少 8 次续租、绑定不变且失败决定完整保留；stdin retry 后继续。 |
| CT-G09 | P1 | 阶段 0 支持性与两种兼容运行路径 | 原 SDK 或短 wait/专用 SDK 宿主路径实际维持处理期租约才 go；都失败则 no-go，不用无绑定心跳假通过 | 通过：真实 SDK 同绑定 wait/renew 并发及专用可靠参考宿主 130 秒处理等待跨租约；该 SDK 路径已满足支持性门。 |
| CT-G10 | P1 | 实际 RunSessionHandler 关闭路径与接入说明 | finally 清理/补偿有版本隔离；参考客户端说明接管凭据与恢复密钥区别，以及 Activating 事务提交后即使激活失败也不允许 legacy 回退（未提交不升级）；固定回复不计为真实模型验收 | 通过：真实 RunSessionHandler 关闭隔离、参考客户端 README 的恢复/接管凭据及不可降级说明；固定回复明确排除真实模型验收。 |

CT-G02 中客户端可通过本地保存 TaskId/DeliveryAttemptId 和已生成正文避免同一有效尝试重复生成；进程丢失本地状态后，新尝试可能再次执行模型或外部工具。本系统只保证回复消息幂等，不声称外部副作用 exactly-once。


## 9. 构建和回归基线

服务端在仓库根目录执行：

```powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug
```

客户端在 Client/roundTableMeeting 执行：

```powershell
pnpm install --frozen-lockfile
pnpm generate:api
pnpm test
pnpm build
```

发布验证从现有 Server/RoundTableMeeting/RoundTableMeeting.csproj 输出到隔离的 artifacts 子目录，使用独立 SQLite。验证 /mcp 工具发现、应用启动、SPA、SignalR、数据库迁移与删除恢复。HTTP OpenAPI/Kiota 与 SignalR 镜像契约分开验证，不把 MCP DTO 误当 REST 自动生成能力。

必须回归账号认证和授权、会议创建/加入/切换/删除、Human 消息发送与历史补齐、presence、乱序广播去重、事件分发器重试，以及主题五 Human @AI 正常参会路径。System 消息仍不触发任务；旧边界内 Agent 提及不追溯，008 启用边界之后的 Agent 提及应派生任务，可靠模式和 legacy 均适用。

## 10. 完成门

- 连接恢复和接管具有不同身份/预算语义，主动在线接管符合已确认规则，旧绑定不再有权限。
- 入会响应丢失可恢复；未确认上下文不丢失；接管快照不覆盖原任务或分配边界。
- 重复确认、回复、失败报告、resume 和 retry 收据幂等，真实 SQLite 并发验证通过。
- 连接租约与模型处理解耦；自动重试有界且可恢复；耗尽失败队首不被自动跳过。
- 服务进程崩溃后未完成任务继续；已完成任务不重复；删除后全部新增资源不可恢复。
- 旧客户端兼容与可靠模式升级分别通过，UI 状态和真实模型驻留可见。
- P0/P1、发布与前置主题核心回归通过，未执行项明确列出，不以文档检查代替业务验收。
