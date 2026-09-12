# 多 Agent 协作与会议删除收口测试计划

## 1. 依据、状态与执行原则

本计划验收[主题设计](design.md)，实现顺序见[impl.md](impl.md)。当前处于实施验证中；下表未更新的用例仍为未执行或尚未逐项核验，不能以测试总数推定通过。状态/证据只记录真实结果，不以文档检查或前置主题结果代替本主题验收。

P0：消息或任务丢失/重复、乱序、来源串会、删除复活、升级破坏及构建阻断。P1：协议兼容、恢复状态、真实模型和浏览器业务路径。所有 P0/P1 均通过后才可宣布本主题完成；环境阻塞记录为阻塞，不能算通过。

每个数据库用例使用独立临时 SQLite，启用真实外键与迁移；并发请求使用独立 DI scope/DbContext/事务。MCP 契约用真实 SDK 和 Streamable HTTP 验证；直接调用 Application 仅作为补充。崩溃场景重启真实服务进程，明确故障发生在事务提交前还是后。

尽量使用同步屏障、受控时间与有界轮询，不以固定 sleep 后“看起来正常”作为证据。测试故障探针只存在于测试程序集。引用清空等数据库约束须通过直接 SQL 和生产清理入口分别检查，不用内存数据库替代。

停止协作的测试由测试宿主自身限制脚本轮数或由人类操作外部工具完成，不能给生产平台增加循环截断、暂停入口或完成判定。

## 2. 公共数据与断言

默认建立会议 M1、M2，两名人类 H1（M1 创建者）、H2，以及 M1 内 AI A/B/C 和 M2 内同显示名称 AI A。AI 各有独立协议会话与私有状态。除兼容用例外默认 explicit_ack_v1；上下文预算按当前服务端允许范围选择两个不同有效值。

基础链：H1 的消息 R @A，产生 TA；A 对 TA 的回复 S @B，产生 TB。应满足 TA.ChainId=R.MessageId、TA.ParentTaskId=null、TB.ChainId=R.MessageId、TB.ParentTaskId=TA.ID、TB.TriggerMessageId=S.MessageId。任务身份与目标、消息序号和候选范围一起核对，不只检查数量。

每次幂等验证至少核对：回复消息数量、任务唯一键、ReplyMessageId、消息事件和来源、Allocated/Delivered 两个游标、当前尝试及 activity revision。重放不要求整个响应字节不变，但稳定身份、事实和既有协议含义必须一致。

根任务候选范围和多目标候选范围按各自游标独立计算；不能要求不同 AI 得到相同上下文。多轮脚本的长度是测试输入，不是服务端配置。

## 3. 领域、来源及数据库约束

| 编号 | 级别 | 前置与操作 | 预期结果 | 状态/证据 |
| --- | --- | --- | --- | --- |
| MA-D01 | P0 | 为同一 Human 消息的 A/B 分别创建根任务 | ChainId 均为根 MessageId，ParentTaskId 为空，目标与 TaskId 各自独立 | 通过：AgentCollaborationSourceTests.HumanTargetsShareMessageRootButHaveIndependentTaskIdentity |
| MA-D02 | P0 | 按基础链及多层父回复创建任务，再执行重试/接管 | 来源逐层正确，根 ChainId 不变，恢复不改父引用 | 通过：DerivedTasksInheritRootAcrossBranchesAndFortyGenerations 验证 40 层与分支来源；MultiAgentDerivedRecoveryTests 验证派生任务重试、resume/takeover 及重启后来源不变 |
| MA-D03 | P0 | 直接写入跨会议根消息、父任务、同会异链父任务 | 全部被数据库约束拒绝，无部分写入 | 未执行 |
| MA-D04 | P0 | Active 下构造 Agent 无父任务、Human 错误 ChainId/非空父任务，及父回复不等于触发消息 | Domain/数据库拒绝；不能伪造根任务或反向因果 | 未执行 |
| MA-D05 | P0 | 将两个任务关联同一非空 ReplyMessageId，并检查合法空 ReplyMessageId | 重复关联失败；多条尚未回复任务可同时存在 | 通过：ReplyOwnershipUniqueConflictRollsBackWhileNullRepliesCoexist，两个空回复任务共存；同回复归属第二任务触发 SQLite 2067，事务回滚保留原任务及第二任务 Queued |
| MA-D06 | P0 | Active 下清空/替换父引用或更改 ChainId；重复保存未改变来源 | 非法改动失败，正常状态/尝试更新可通过 | 未执行 |
| MA-D07 | P0 | Deleting 下清空父引用、重复清空、替换或恢复引用、新建任务；直接调用 Active 清理入口 | 只允许合法清空或不变；其他操作拒绝。已回复任务也可解除引用，清理入口要求现有事务和 Deleting | 未执行 |
| MA-D08 | P0 | 直接修改 SourceAuthorKind/SourceParticipantId/触发身份；父引用未解除时删除父任务；Deleting 下恢复已清空引用 | 身份修改、悬空父引用与引用恢复均拒绝；合法清理不改来源 | 通过：PersistedRootIdentityCannotBeMutated 的来源类型/作者/触发序号等修改被拒；DerivedSourceIsImmutableAndCleanupRequiresDeletingTransaction 验证父任务删除拒绝、Deleting 清空后恢复引用拒绝及合法清理 |
| MA-D09 | P0 | 检查回复部分唯一索引名称/列/过滤条件；构造特定唯一竞争并走生产事务错误映射 | 触发去重与回复归属分别约束；特定竞争映射 agent_operation_conflict，其他损坏不冒充幂等成功 | 部分通过：ReplyOwnershipUniqueConflictRollsBackWhileNullRepliesCoexist 核对部分唯一索引名称/列/过滤条件，并经生产 MeetingTransactionRunner 映射 AgentCollaborationConflictException；MCP 层 agent_operation_conflict 的特定竞争路径仍待补验 |
| MA-D10 | P0 | 直接 SQL 写边界 -1、9007199254740992、重复会议；写 Agent 触发序号等于/大于边界；Human 处于边界之前 | 非法边界拒绝；Active 无边界拒绝任务 INSERT；仅 Agent 被序号过滤，合法 Human 可建任务 | 通过：DatabaseRejectsInvalidBoundaryWithoutDomainFactory 拒绝负数、超安全整数和重复会议边界；AgentTriggerMustBeStrictlyAfterPersistedBoundary 验证相等拒绝/之后允许；MissingBoundaryRejectsHumanInsertAndBoundaryCannotBeChanged 验证无边界拒绝及边界前 Human 合法 |

## 4. 升级与会议边界

| 编号 | 级别 | 前置与操作 | 预期结果 | 状态/证据 |
| --- | --- | --- | --- | --- |
| MA-M01 | P0 | 空库迁移并创建会议；模拟创建事务失败，再重放成功创建请求 | 边界为 0，与会议原子建立；失败无残留，重放不新建或重置 | 通过：MeetingBoundaryCreationRollsBackWithMeetingAndReplaysWithoutReplacement，空库生产 HTTP 创建路径注入边界 INSERT 失败，会议/参与者/边界/创建收据全部回滚；同请求重试 201、再重放 200，零边界及身份保持 |
| MA-M02 | P0 | 从主题六真实 schema 的多会议副本升级，含空会议 | 各会议边界等于迁移时自身最大 Sequence，空会议为 0；重复启动不改变 | 部分通过：MigrationBackfillsRootsAndAllMeetingBoundariesWithoutChangingTaskFacts 从直接前置 schema 验证多会议含空会议，边界为最大序号或 0，重复迁移保持；主题六 schema 起点仍待补验 |
| MA-M03 | P0 | 升级含 Human 多目标及 Queued/Delivered/Processing/Failed/Replied/Cancelled 任务的数据库 | 来源正确回填；原身份、范围、幂等键、尝试、确认和重试事实逐字段保持 | 通过：UpgradePreservesEveryLegacyTaskColumnAcrossAllSixStates 验证六种状态全部旧列；UpgradePreservesMultiTargetTasksAndConfirmedFailureReceipts 补验双目标根、确认/失败回执、非零重试周期与耗尽事实、连接及重试收据逐字段保持，迁移后可反序列化且外键正常 |
| MA-M04 | P0 | 升级前 Agent 回复已 @B，事件分别为 Pending/Leased/Suspended/Completed；升级后直接消费与高序号补扫 | 旧 Agent 消息都不派生，事件状态不被迁移伪改为成功；不推进 B 游标吞掉其他有效提及 | 通过：UpgradePreservesLegacyAgentAndHumanEventDeliveryFacts 四状态逐字段保留事件/投递事实；MigratedOldAgentEventCannotSwallowPendingHumanMention 使用真实旧 schema、生产启动迁移和消费 DI，四状态直接重放/后续补扫均不派生旧 Agent，不吞 Human 待办且无重复 |
| MA-M05 | P0 | 升级前 Human @B 未消费，升级后先投递较晚 Agent/Human 事件 | 旧 Human 任务仍被按序补齐，ChainId 正确，不被启用边界过滤 | 通过：MultiAgentUpgradeBoundaryConsumerTests 分别由较晚 Human 与新 Agent 回复触发补扫，旧 Human 保留独立根、旧 Agent 不追溯，候选范围及游标正确 |
| MA-M06 | P0 | 旧未完成任务升级后提交新回复；旧已回复任务重放原回复 | 新回复序号超过边界，可派生；旧回复重放沿用旧序号，不追溯派生 | 未执行 |
| MA-M07 | P0 | 旧库存在重复 ReplyMessageId、跨会来源或非法任务来源 | 升级明确失败，整个迁移回滚；原版本可读取原数据，无正文/密钥泄漏 | 未执行 |
| MA-M08 | P0 | 升级 Deleting 会议并重启恢复；正常迁移必建边界行；另用隔离异常夹具删除该行验证恢复 | 继续清理，不为其派生任务，不复活会议；清理可重入 | 未执行 |
| MA-M09 | P0 | 隔离测试库制造 Active 会议边界缺失后消费 Agent 消息 | 不按默认 0 追溯触发，失败事件保留，游标与任务不变；修复后原事件可恢复 | 未执行 |
| MA-M10 | P0 | Agent 提及恰在边界及边界后第一条；同时重启、恢复连接 | 仅大于边界的消息可派生，恢复不改变边界或 legacy/可靠模式门槛 | 未执行 |
| MA-M11 | P0 | 含 Pending/Leased Human 事件的旧库按停机流程升级；在迁移窗口尝试通过旧入口写入 | 旧进程已退出、入口不可写；最大持久化 Sequence 快照稳定，待处理事件及任务保留；新入口只在迁移成功后开放 | 未执行 |
| MA-M12 | P0 | Active 边界缺行时消费 Human @AI，再恢复原边界并重放 | 缺行不是 0；失败不分配、不推进，修复后按原 Human 语义补齐 | 通过：RestoringMissingBoundaryAllowsOriginalHumanEventToAllocateAllTargetsOnce，缺行无任务/游标变化，恢复原非零边界后双目标原事件重放一次且不重复 |
| MA-M13 | P0 | 分别构造回复作者不等于目标 AI、非 Agent 回复、Replied 缺回复、TriggerMention 不属于触发消息或目标不符的旧库 | 每个损坏场景均阻止迁移，整体回滚，只输出必要标识诊断 | 通过：InvalidLegacyFactsAbortWholeMigration 六种损坏场景，含错误回复作者、非 Agent 回复、缺回复及提及消息/目标错配；整迁移回滚、旧任务逐列不变，错误不含正文 |

## 5. 任务创建、协作与顺序

| 编号 | 级别 | 前置与操作 | 预期结果 | 状态/证据 |
| --- | --- | --- | --- | --- |
| MA-A01 | P0 | 真实 MCP 执行 Human→A→B | TA/TB 与消息来源正确；B 获得任务并可回复统一消息流 | 通过：ProductionSdkAgentsCollaborateAcrossBothModesWithoutDuplicateReplies 四种模式组合均经真实 SDK 完成 Human→Alpha→Beta，并验证任务来源和统一回复消息 |
| MA-A02 | P0 | B 回复 @A，A 再回复普通文本 | 新 A 任务仍属原来源链，排在已有任务之后；普通文本不再派生 | 通过：McpMultiAgentCollaborationTests 完成 Beta→Alpha 回传，Alpha 普通回复不再派生，来源延续；MultiAgentLongCollaborationTests 验证每目标序号递增 |
| MA-A03 | P0 | 一条 Agent 回复重复 @B 多次并 @C | B/C 各一个任务，代表提及按既有最小 StartOffset 规则；同事务成功 | 通过：AgentReplyFansOutToSelfAndPeerWithStableSourceAndIdempotentReplays，重复目标取最早提及，两目标各一任务，三次并发消费无重复；Q10 另验证多目标事务回滚 |
| MA-A04 | P0 | 同一根消息产生 A/B 两分支，两条回复先后 @C | C 有两个不同任务，ChainId 相同但父任务不同，按触发 Sequence 排队 | 通过：TwoBranchesOfOneHumanRootConvergeOnThirdAgentInSequenceOrder，三个真实 SDK 完成双根任务向 Gamma 汇合；两个子任务保持同 ChainId/不同 ParentTaskId，按触发序号 2、3 执行，最终四任务均 Replied |
| MA-A05 | P0 | 新 Human 消息继续相同话题 @A | 使用新根 MessageId，不合并前一来源链，也不越过 A 的旧队列 | 通过：IdenticalHumanAndAgentBodiesKeepDistinctTaskIdentityAndRoots 验证相同话题两条 Human 消息具有独立根和连续范围；MultiAgentDerivedRecoveryTests 验证新根不越过失败队首，恢复后按序执行 |
| MA-A06 | P0 | A 回复 @A，自身回复提交与消费者交错 | 原任务先持久化 Replied，后续自提及为独立任务；无自锁或覆盖 | 通过：真实 SDK 长链及派生恢复场景包含自提及，父任务先 Replied、子任务身份独立；分配提交前/后崩溃恢复保持父子来源，无重复或自锁 |
| MA-A07 | P0 | 两条不同消息具有相同正文和同目标，分别触发 | 两个独立任务；不会以正文、重复参与者或相同链去重 | 通过：IdenticalHumanAndAgentBodiesKeepDistinctTaskIdentityAndRoots 对相同 Human 正文和相同 Agent 回复正文分别验证独立消息/任务/幂等键，旧事件重放不重复 |
| MA-A08 | P0 | System 含 @A、Agent 仅 @Human、无提及消息 | 不创建 AI 任务、不推进任何目标分配游标，后续有效目标事件仍补扫完整范围；Agent @Human 保留正常提及；普通消息仍可进入后续上下文 | 通过：NonTargetMessagesDoNotAllocateOrAdvanceButStayInLaterCandidateHistory 验证 System 提及、Agent 仅提及 Human 及普通消息均不分配/推进，Human 提及记录保留，后续任务候选历史包含全部消息 |
| MA-A09 | P0 | 同目标早期 Human/Agent 事件延迟，先消费后期多目标事件 | 为各目标补齐全部较早有效任务，顺序与范围准确；再消费旧事件无重复 | 通过：MultiAgentMentionConsumerTests 的延迟旧事件/后期多目标补扫覆盖各目标完整范围、序号顺序和重放无重复 |
| MA-A10 | P0 | 提交同一事件两次并并发消费，在一次多目标分配中故障回滚 | 无重复任务；失败事务的任务/游标/activity 不部分提交，恢复后完整创建 | 通过：AgentReplyFansOutToSelfAndPeerWithStableSourceAndIdempotentReplays 三次并发重放不重复；CorruptEarlierTargetRollsBackWholeFanoutWhileIndependentTargetCanCatchUp 明确核对失败后仅原任务、双目标游标与 activity revision 不变，修复重放完整恢复 |
| MA-A11 | P0 | A 离线期间收 Human/Agent 提及；另一个 AI 在历史消息之后首次加入 | 离线 A 继续排队；新参与者不补发入会前任务，不按新昵称重解析历史正文 | 未执行 |
| MA-A12 | P1 | 脚本完成至少 40 次 Agent 派生，含 A→B、A→C、B→C 交叉分支及自我提及，最后由宿主停止 | 40 仅是测试输入规模而非平台上限；有限测试完整执行，无深度/累计数量/重复参与者截断；测试结束后宿主不再发新回复 | 通过：MultiAgentLongCollaborationTests，三个真实 SDK 客户端处理 47 个任务（46 个派生），覆盖交叉分支、自提及、每目标序号递增、同根来源、最终全部 Replied 及无悬空外键；由测试宿主结束派生 |

## 6. 上下文、事务与恢复

| 编号 | 级别 | 前置与操作 | 预期结果 | 状态/证据 |
| --- | --- | --- | --- | --- |
| MA-Q01 | P0 | A/B 使用不同预算和游标，同条消息提及两者 | 各自范围/裁剪元数据正确，触发消息完整；无共享游标 | 通过：SharedMentionPreservesIndependentBudgetsAndMixedHistory 两个真实可靠 SDK 使用 1000/12000 预算，先形成不同游标，再接收同条双目标提及；候选范围分别 2..5/1..5，裁剪/预算/任务身份独立，触发消息完整 |
| MA-Q02 | P0 | 两次有效提及之间插入 Human/Agent/System 消息，造成裁剪和单触发超预算 | 候选范围包含全部类型，裁剪只丢较早记录；超预算触发完整保留并标记 | 通过：SharedMentionPreservesIndependentBudgetsAndMixedHistory 两场景在有效提及间加入 Agent/System/Human 历史；小预算仅裁掉较早连续三条，普通触发不过限、长触发完整保留并标记 OverLimit，大预算返回全部五条原文 |
| MA-Q03 | P0 | 一个 AI 的耗尽 Failed 队首后加入新来源链任务，再显式 retry | 新任务不越过失败队首；原任务恢复后按序继续，来源不变 | 通过：MultiAgentDerivedRecoveryTests 耗尽队首阻塞后续独立根任务，显式 retry 后队首先成功回复，尾部再领取并回复，来源分别保持 |
| MA-Q04 | P0 | 两个独立子场景：①派生任务确认后断线，resume 原连接；②相同初始状态下，takeover 并声明不同有效预算 | ①保留 ConnectionSessionId、连接代数和原预算，更新绑定版本；②新建 ConnectionSessionId、递增连接代数并使用新预算。两者均保留 TaskId/ChainId/ParentTaskId，按既有失效/重投规则建立新尝试，旧尝试不能回复；两个子场景分别留证，全部通过才算本项通过 | 通过：MultiAgentDerivedRecoveryTests 的断线/重启 × resume/takeover 四种组合，真实进程及 SDK 验证来源、预算、连接身份和旧尝试拒绝 |
| MA-Q05 | P0 | 回复在提交后丢失响应，原参数重放并重复消费事件 | 一条回复、一个来源关联和每目标一个任务；幂等键及当前尝试规则保持 | 通过：RealProcessCrashBeforeReplyResponseRollsBackOrReplaysTheWholeReply 提交后强杀丢响应，原参数重放保持消息 ID/幂等键，下游仅一任务；DerivedAllocationSurvivesCrashBeforeOrAfterCommitWithoutCompletingTheEvent 补证未 Completed 事件重放不重复任务 |
| MA-Q06 | P0 | 回复消息追加后、任务 MarkReplied 前注入异常；另做事务提交后强杀 | 未提交整体回滚；已提交重启后由事件继续派生，无半来源状态 | 通过：ReliableReplyFailureBetweenMessageAppendAndTaskMarkRollsBackEveryWrite 两种回复目标验证追加后异常，消息/提及/事件全部回滚且任务保持 Processing，原尝试重试后仅一子任务；RealProcessCrashBeforeReplyResponseRollsBackOrReplaysTheWholeReply 补证提交后强杀重启正确派生 |
| MA-Q07 | P0 | 消费者提交前故障，及提交后分发器标 Completed 前进程退出 | 前者无部分任务，后者重放不重复；游标/来源/任务最终一致 | 通过：DerivedAllocationSurvivesCrashBeforeOrAfterCommitWithoutCompletingTheEvent 两种真实进程切点，提交前任务/游标回滚；提交后事件仍 Leased，重启重放保留子任务 ID，原事件最终 Completed，来源/数量/游标及外键正确 |
| MA-Q08 | P1 | 丢唤醒信号、后排任务入队、重复事件与普通状态查询 | 数据库领取仍可推进；无可见变化不增加 activity revision，不伪报离线 | 未执行 |
| MA-Q09 | P0 | 新 Agent 有效触发缺少唯一可信父任务，注入到隔离损坏库后消费/补扫 | 事件失败保留，相关事务不推进游标或建任务，不猜测根链；诊断不含正文 | 未执行 |
| 编号 | 级别 | 前置与操作 | 预期结果 | 状态/证据 |
| --- | --- | --- | --- | --- |
| MA-Q10 | P1 | B 候选范围内较早的 B-only 消息来源损坏，当前合法消息 @A @B；失败后修复并重放；另做后续 A-only 事件 | 第一次 A/B 分配全部回滚；修复后完整恢复。A-only 仅在自身候选不含损坏来源时可补齐，不宣称 B 自愈 | 通过：CorruptEarlierTargetRollsBackWholeFanoutWhileIndependentTargetCanCatchUp，损坏来源令双目标回滚；A-only 独立补齐，修复父回复后重放原双目标事件两次，B 完整补齐、A 不重复、来源和范围正确 |
| MA-Q11 | P1 | 10 个 AI，每目标 1 万历史任务，追加 100 个事件；与小历史基线比较 SQL、读取行数、事务耗时 | activity 只读必要投影，读取不随历史任务量线性增加，无逐候选 N+1；记录环境与测量，不设产品轮数上限 | 实测通过：MultiAgentAllocationScaleTests，10 AI × 每目标 1/10000 历史任务，各消费 100 个事件。Windows 10.0.26200、.NET 10.0.12、真实 SQLite/外键开启；两组 SQL reader 调用均 11800、载入任务均 1990，消费者合计 5426/6295 ms（不含建库/造数）；完整回归中新增相对耗时断言通过 |
| MA-R01 | P1 | 只停止外部模型处理，独立宿主继续 renew | 不自动离线或完成，任务保持原状态；平台不假定收到停止命令 | 未执行 |
| MA-R02 | P1 | 关闭真实 MCP 传输，再用原私有状态恢复 | 正确离线，参与者/任务保留；恢复同身份，任务来源连续 | 未执行 |
| MA-R03 | P1 | 直接结束宿主进程且不发送 DELETE，等待租约到期后接管 | 按租约真实收敛，不立即伪报；新连接接管任务，旧连接无权限 | 未执行 |
| MA-R04 | P0 | 派生任务经历自动重试耗尽、离线、重启、显式 retry | 自动次数有界，离线/重启不清零；显式重试不改变来源或幂等键 | 通过：MultiAgentDerivedRecoveryTests 耗尽分支，三次自动重试后重启保留计数及来源，显式 retry 建立新周期且保留幂等键 |
| MA-R05 | P0 | 恢复/接管与回复并发，分别安排两种提交顺序 | 仅有效拥有者提交，旧尝试无迟到写入，源回复及下游任务最多一份 | 未执行 |
| MA-R06 | P1 | 让事件达到 Suspended，修复测试故障后由可信本地 DI 维护宿主按投递 ID、事务内确认 Active 后调用 ResumeAsync 恢复；另验证 Deleting 被拒绝 | 技术事件恢复继续原事件，无伪造新消息或丢任务，不引入业务暂停入口 | 通过：MessageEventMaintenanceContractTests 的生产 DI 命令恢复、参数/重复恢复拒绝；MultiAgentDatabaseBoundaryTests 验证 Active/Suspended、Deleting/Suspended、Active/Pending，保留原事件身份与失败次数，无新增消息 |
| MA-R07 | P0 | 同有效尝试相同/不同正文并发回复；另将旧尝试与接管后的新尝试按两种顺序竞争 | 原回复重放或稳定冲突；不同正文为 reply_idempotency_mismatch，旧拥有者为既有失效错误；至多一个新回复/下游任务，不退化为通用不可用 | 未执行 |

## 7. MCP 契约与兼容

| 编号 | 级别 | 前置与操作 | 预期结果 | 状态/证据 |
| --- | --- | --- | --- | --- |
| MA-C01 | P1 | 真实 tools/list、join 能力发现与 wait Human/Agent 任务 | 能力声明和真实行为一致；collaboration 的 Guid、作者及根 null/派生父 ID 与 schema 一致 | 通过：ProductionSdkAgentsCollaborateAcrossBothModesWithoutDuplicateReplies 四种组合，经真实 SDK tools/list、join、wait 校验九工具名单、能力及根/派生 GUID、作者、父引用和输出 schema |
| MA-C02 | P1 | 查询队首、指定自己的任务，并恢复含摘要的连接 | 各处复用任务摘要来源一致；空队列和旧成功形状不损坏，未授权无来源泄漏 | 通过：McpMultiAgentCollaborationTests 的 queueHead、根/派生 requestedTask、resume.lastRepliedTask 来源一致；结束后 queueHead 为 null；跨 Agent 指定任务被拒且不泄露来源 |
| MA-C03 | P1 | legacy→legacy、legacy→可靠、可靠→legacy、可靠→可靠各做一次协作 | 均能派生；旧四工具与显式确认语义分别保持，不静默降级可靠参与者 | 通过：McpMultiAgentCollaborationTests.cs 中 ProductionSdkAgentsCollaborateAcrossBothModesWithoutDuplicateReplies，四种组合均经真实 SDK 验证 Human→A→B→A、来源、回复重放、显式确认与 schema |
| MA-C04 | P1 | 用忽略新增字段的旧客户端等待和回复，再让新版客户端连接前置版本 | 旧客户端可工作；新版面对缺少能力的旧服务不宣称支持相互触发，不强制新字段使旧参会失败 | 部分通过：参考宿主 reliable.test.mjs 验证无新字段/能力的旧服务夹具仍可完成回复，新能力仅随服务响应出现；根/派生字段可保存并传给生成器。独立旧客户端对新服务的兼容路径仍待补验 |
| MA-C05 | P0 | 伪造另一会议/参与者任务或旧连接调用 status/reply/confirm | 按既有所有权错误拒绝，无其他任务、连接凭据或上下文泄漏 | 未执行 |
| MA-C06 | P1 | 回复提交后暂缓消费者，观察 reply 及后续消费 | reply 表示消息已接受，不声称下游已完成；消费者恢复后正常派生，客户端不重新发新回复 | 未执行 |
| MA-C07 | P1 | 检查真实输出、工具输入、驻留说明和参考宿主解析 | 无新暂停/冻结工具或客户端可指定的来源身份；既有 kind/nextAction/错误形状保持，业务流程不固化 | 未执行 |
| MA-C08 | P1 | 遍历四处 DTO 构造与 queueHead/requestedTask/lastRepliedTask/错误 taskSummary，含根、派生、空值和无权访问 | 来源一致；外层 null 省略，根 parentTaskId 显式 null；无权不泄露来源 | 未执行 |
| MA-C09 | P1 | 两模式 join/resume、tools/list 和真实驻留输出；旧服务夹具兼容 | 仅可靠 capabilities 含新能力，legacy 无新增能力字段但可协作；模板为 v3-legacy/v3-reliable（带 resident-instruction- 前缀），旧夹具 v1/v2 保留；工具总数 9 | 通过：McpMultiAgentCollaborationTests 四组合校验两模式 join 的 v3 模板、仅可靠模式声明新能力，可靠 resume 保留该能力，工具精确名单为九个；既有旧服务夹具保留，参考宿主五文件 25 项全部通过 |

## 8. 删除、并发与崩溃

| 编号 | 级别 | 前置与操作 | 预期结果 | 状态/证据 |
| --- | --- | --- | --- | --- |
| MA-X01 | P0 | 非创建者、无效确认名及 gate 冲突请求删除 | 原授权/校验/忙错误保持，不误报 202，屏障未提交时会议仍 Active | 未执行 |
| MA-X02 | P0 | 暂停后台清理，在真实 HTTP 删除后再次删除与读取 | 屏障提交即 202；清理尚未完成但读写被拒，再删为 404；放行后台后才物理归零 | 通过：RealProcessDeletionCleanupCrashPreservesBarrierAndRestoresParentReferences 在清理事务暂缓期间，删除已返回 202，数据库会议及两个任务仍存在；读取/发消息/重复删除均 404，重启后恢复物理清理 |
| MA-X03 | P0 | 多层来源含 Replied 父任务及未完成子任务，执行生产清理 | Deleting 下引用解除成功，所有新增/原有从属数据按外键顺序删除，账号及 M2 保留 | 通过：RealProcessDeletionCleanupCrashPreservesBarrierAndRestoresParentReferences 三切点，Replied 父任务与未完成子任务清理后全部会议从属表为空；按删除前事件 ID 核对投递清除，创建收据 Deleted 保留，账号/M2/边界/历史保留且 M2 重启后可读写 |
| MA-X04 | P0 | ParentTaskId 清空后注入异常，随后重试清理 | 同事务回滚恢复原引用与数据；保持 Deleting，重试可完整清理 | 通过：RealProcessDeletionCleanupCrashPreservesBarrierAndRestoresParentReferences 的 unlink 切点，在解除父引用后强杀，事务回滚保留父引用及 Deleting，重启完整清理 |
| MA-X05 | P0 | 在删除屏障提交前/后及物理清理提交前/后强杀并重启，共四种情况 | 屏障前回滚仍 Active；屏障后不复活；未提交清理可重入，已提交无残留 | 通过：RealProcessCrashAtDeletionBarrierPreservesActiveOrCleansAllRecoveryResources 覆盖屏障提交前/后；RealProcessDeletionCleanupCrashPreservesBarrierAndRestoresParentReferences 覆盖物理清理提交前/后及解除父引用切点，真实进程强杀重启后状态及外键正确 |
| MA-X06 | P0 | 删除分别与 join/resume/takeover/wait/confirm/reply/renew/retry/status/消费者竞争，覆盖两种顺序 | 业务先完成则全部纳入清理，屏障先提交则无新增读写；旧收据和幂等重放也不得穿透 | 部分通过：AgentReliableDeletionOrderingTests 与 AgentReliableDeletionConcurrencyTests 覆盖 join/resume/takeover/reply/scan 及另一会议并发；消费者/wait/confirm/renew/retry/status 全矩阵仍待补验 |
| MA-X07 | P0 | 屏障后注入旧广播、恢复扫描及待分发事件，丢删除通知 | 不新建任务或复活访问；客户端刷新/订阅拒绝，迟到旧会议事件被丢弃 | 未执行 |
| MA-X08 | P0 | M1 删除时保留长 wait、外部慢模型并注入通知失败，M2 继续发消息/领取任务 | M1 保持屏障，清理按既有机制重试；不等待模型，M2 可有界完成业务 | 未执行 |
| MA-X09 | P0 | 合并多代连接、替代尝试、收据、Failed 队首、旧事件与新边界，删后使用所有旧凭据 | 明确拒绝全部访问/恢复；逐表归零并通过 foreign_key_check，创建收据按原保留策略处理 | 部分通过：删除崩溃夹具逐表清理、原事件投递 ID、创建收据保留和旧恢复凭据拒绝；屏障夹具另覆盖多代连接/替代尝试。合并 Failed 队首、旧事件及全部旧凭据矩阵仍待补验 |
| MA-X10 | P0 | 真实旧库迁移后核对 design §4.1 六个既有触发器的 sqlite_master 名称及定义，并直接 SQL 触发各归属冲突 | 六个触发器均保留且拒绝原非法归属；新来源触发器同时工作 | 通过：MigrationBackfillsRootsAndAllMeetingBoundariesWithoutChangingTaskFacts 比较迁移前后六个归属触发器定义；OwnershipGuardsRejectCrossAgentAttemptsReceiptsAndReassignment 逐一触发 INSERT/UPDATE 归属拒绝，新来源约束另由 MultiAgentSourceConstraintTests 验证 |

删除逐表核对以当前 DbContext/迁移的实际表名为准，至少覆盖：AgentReplyTasks、AgentTaskDeliveryAttempts、AgentContextCursors、AgentConnectionSessions、AgentInitialContextDeliveries、AgentRecoveryOperations、新启用边界表、MessageEventDeliveries、MessageAppendedEvents、MessageMentions、MeetingMessages、会议参与者和会议。MessageEventDeliveries 表没有 MeetingId 列，通过删除前保存的 MessageAppendedEvents.ID 集合匹配其 EventId 核对归零（不能在父事件删除后再联表取集合）；不能只对带 MeetingId 的表计数而漏查。

注册表、挂起等待和会议订阅另做内存/网络断言；数据库归零不能替代连接终止。创建幂等收据按现有规则标记删除及到期处理，不要求用户表、其他会议或合法保留收据归零。

## 9. 浏览器、参考宿主与发布回归

| 编号 | 级别 | 前置与操作 | 预期结果 | 状态/证据 |
| --- | --- | --- | --- | --- |
| MA-V01 | P1 | 两浏览器观察 Human→A→B→Human，刷新页面 | 消息顺序/提及正确，无重复显示；AI @Human 不创建任务或完成状态 | 通过：旧 schema 副本升级发布包，两个隔离 Chrome 会话同步观察三条固定回复消息，刷新后顺序/重复提及一致；正文“冻结/暂停”未驱动状态，AI @Human 保持普通消息展示。任务不派生同时由消费者测试与真实 SDK 闭环验证 |
| MA-V02 | P1 | 协作期间切换 M1/M2、断网重连及注入旧订阅事件 | 消息与活动不串会，按服务端恢复，无旧 revision 覆盖或草稿/幂等键异常 | 未执行 |
| MA-V03 | P1 | 多个长名称参与者，在 390 与 1280 像素视口聊天和删除 | 输入和参与者状态可达，无影响操作的溢出，无新增暂停按钮或业务完成面板 | 未执行 |
| MA-V04 | P1 | 两个独立参考宿主完成有限多轮协作后外部 quit/关闭，另做只停模型实验 | 宿主不重复生成同一有效任务回复，独立续租正常；网页状态符合真实连接，不以本地退出推断远端已关闭 | 未执行 |
| MA-V05 | P1 | 两个真实模型通过生产 MCP 协作，至少包含一次 Agent→Agent 和后续回复 | B 的回答使用其实际上下文，网页可见全过程；模型回复后仍按协议等待，固定文案不能替代 | 未执行 |
| MA-V06 | P1 | 真实浏览器删除含多 Agent 任务的会议，清理暂缓/恢复，刷新各浏览器 | 202 后不可访问、相关列表收敛；随后数据库清理完成，无旧消息复活 | 未执行 |
| MA-V07 | P1 | Agent 回复多重提及、首尾提及、emoji/代理对/组合字符；经历史接口加载及 SignalR 展示 | UTF-16 startOffset/length 与正文一致，listMessages 严格校验通过；畸形范围仍拒绝，合法历史不整页失败 | 未执行 |
| MA-V08 | P1 | 扫描客户端路由/组件/操作及实际页面，发送含“冻结/完成/暂停”的普通正文 | 不出现协作暂停、自动完成、未读计数或站外通知新入口；普通正文不驱动业务状态，既有删除按钮保留 | 未执行 |

真实模型验收可按当次环境安排重跑；失败或环境阻塞须如实保留，脚本全绿不能替代真实模型通过，不能据此宣告主题完成。测试宿主的重跑次数不是生产协作限制。

真实模型场景步骤：建立新测试会议并让两个独立 Agent 加入；人类发一条可自然拆分的文本问题 @A；让 A 根据当次消息 @B 请求补充，B 根据收到的上下文回答；检查两个任务来源及浏览器消息。若模型未产生有效提及，应据实补充当次引导并重跑，不能人工写入数据库伪造成功。话题与结束方式仅为验收输入，不写入生产驻留规则。

| 编号 | 级别 | 前置与操作 | 预期结果 | 状态/证据 |
| --- | --- | --- | --- | --- |
| MA-B01 | P0 | 当前 SDK 下 restore/build/test 服务端与客户端，执行现有格式/工具链检查 | 构建通过、架构约束不破坏，新增 schema 与生成代码一致 | 未执行 |
| MA-B02 | P1 | 回归登录/退出、账号维护、创建/加入/切会、Human 文本及完整历史 | 既有主流程通过；只调整启用边界之后“Agent 不触发”的旧断言，不削弱 Human/System 覆盖 | 未执行 |
| MA-B03 | P1 | 回归 legacy 参会、可靠确认/续租/失败重试/接管与服务重启 | 既有核心行为通过，新来源字段不改变预算、任务身份、队首或错误语义 | 未执行 |
| MA-B04 | P0 | 发布包分别使用空 SQLite 和主题六副本启动，验证 SPA、SignalR、MCP、迁移及删除重启 | 发布产物独立可运行，无测试探针；两个数据库路径各完成实际业务与删除 | 未执行 |
| MA-B05 | P1 | I02 核对新增列/边界外键、回复索引与 foreign_key_check；不重建库直接清理迁移后的合法父子任务 | 索引/归属正确，正常清理可执行，无需临时关闭外键或再建表 | 未执行 |
| MA-B06 | P0 | 同步/异步实际连接打开及迁移前后查询 foreign_keys；夹具模拟关闭外键，发布包复验 | 值必须为 1，否则拒绝使用/启动；不能仅断言连接串文字 | 未执行 |

## 10. 执行入口与证据

服务端在仓库根目录执行，使用仓库当前锁定工具链：

```powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug
```

客户端在 Client/roundTableMeeting 执行：

```powershell
pnpm install --frozen-lockfile
pnpm check:toolchain
pnpm generate:api
pnpm check:format
pnpm test
pnpm build
```

参考客户端在 Demo/AI参会联调客户端执行现有测试；新增用例随实现加入相应测试文件：

```powershell
node --test index.test.mjs reliable.test.mjs reliable-recovery.test.mjs reliable-state.test.mjs reliable-conflict.test.mjs
```

发布验证从现有 Server/RoundTableMeeting/RoundTableMeeting.csproj 发布到隔离 artifacts 子目录，使用专用端口、数据库和参考宿主状态目录。真实模型配置按 Demo README 的现有入口提供，不把凭据、会议码、恢复状态或完整上下文写入文档/日志。

自动化落点：Domain.Tests 验证纯来源规则；Repository.Sqlite.Tests 验证迁移和直接 SQL 约束；IntegrationTests 验证生产消息事务、真实 SDK、并发/进程崩溃和发布入口；客户端现有 MessageComposer、ParticipantList、RealtimeChatRegion、messageReducer 测试扩展协作显示及迟到事件。优先复用 AgentMentionTaskConsumerTests、AgentRecoveryTransactionCrashTests、AgentReliableDeletionConcurrencyTests 等现有测试设施。

浏览器验收默认使用 browser-debugging-and-control 技能。每项证据说明入口、预置数据、关键断言、实际结果和测试文件/场景，需区分脚本替身、真实宿主和真实模型。结果维护在本计划对应行，不新增会议纪要或执行日志。

## 11. 完成门

- 所有 P0/P1 用例执行通过，失败/未执行/环境阻塞均已清楚标识，不以测试总数替代具体场景。
- 合法多轮协作持续运行，技术重放不重复任务；来源与上下文独立且正确。
- 外部停止与平台状态边界符合设计，无平台循环截断、完成判断或暂停入口。
- 升级不追溯旧 Agent 消息，不吞旧 Human 提及；回复和消费者崩溃可恢复。
- 删除 202 与物理完成分开证明，父引用约束例外正确，全部资源及旧凭据不可恢复，其他会议继续运行。
- 真实双 Agent、真实浏览器、发布包及前置主题核心回归通过。

补充落点与阶段：MA-D08～D10、M11/M13、X10、B05/B06 在 I02；M12、Q10/Q11、R07 在 I03；C08/C09 在 I04；V07/V08 在 I05；发布包在 I07 复验 B06。I02 先建立 X02/X04/X05 所需后台暂缓及清理切点，I06 复用；D07 以真实 DI 服务验证无事务/Active 拒绝，并验证真实 ExecuteUpdate 的触发器行为。

参考宿主新增场景继续放入上述五个测试文件：index 覆盖 legacy 模板，reliable 覆盖能力/来源与通用协作，reliable-recovery 覆盖恢复摘要，reliable-state 覆盖持久状态兼容，reliable-conflict 覆盖竞争。reliable-agent.mjs 和 reliable-transport.mjs 是被测模块，不是遗漏的测试入口。客户端 V07 扩展 listMessages API 校验和提及展示测试；V08 结合代码范围扫描和页面行为验证。
### 当前已执行证据

- 客户端使用 Node 24.20.0、pnpm 11.24.0，通过工具链检查、127 项测试和生产构建。已从隔离发布包重新生成 OpenAPI 客户端并通过格式检查，API 文件无额外改动。
- 参考宿主在 Node 24.20.0 下通过五个测试文件的 25 项测试，含旧服务、根/派生来源与恢复摘要兼容场景。固定回复与真实模型的证据分开，真实模型配置待提供。
- 最近一次完整服务端回归共 683 项全部通过：Domain 60、Application 68、Architecture 17、Repository 153、Integration 385，含性能相对耗时断言。之后新增/扩展的 SDK 摘要四种组合、旧状态迁移 6 项、边界补扫/修复 3 项均定向通过。
- 当前工作区逐项目回归：Domain 60、Application 68、Architecture 17、Repository 168 全部通过；Integration 仍在运行，不能计为完成。新 artifacts 目录必须先还原依赖，不能把未还原且 --no-restore 的空输出计为测试通过。
- 数据库来源/边界扩展与 SqliteFrameworkTests 定向合计 57 项通过，包含七种伪造派生来源拒绝及合法对照、同步/异步实际外键检查、边界相等拒绝/边界之后放行、根身份篡改、Active/Deleting 技术恢复，以及预先打开并关闭外键的连接在迁移前被拒绝且不建立业务表。
- MCP 定向 8 项通过：四种 legacy/可靠组合、加入模板断言、父引用解除后/清理提交前/清理提交后三个真实进程故障切点。故障用例验证 202 后读取/重复删除为 404、未提交父引用回滚、重启清理与外键检查。生产 DI 维护命令测试另行通过，保留原事件和失败次数，无新消息。
- 三个真实 SDK 客户端长链测试通过：47 个任务全部回复，46 个派生任务的根来源保持一致，包含分支、自提及、每目标 FIFO；有限结束条件只存在于测试宿主。
- 隔离 Release 发布包在独立空 SQLite 启动，通过 Chrome DevTools MCP（未回退 CLI）完成登录、建会、两个独立可靠参考宿主加入、Human→Alpha→Beta→Human 固定回复链、刷新及浏览器删除。三条消息顺序一致，emoji 与重复提及正常；删除后列表清空，两宿主收到 meeting_not_found 并关闭传输。会议、消息、提及、任务、尝试、游标、连接、初始投递、恢复操作、边界及两类事件表均为 0，foreign_key_check 无异常，页面控制台无警告/错误。该发布包早于新增迁移前外键检查，后续需重新发布复验。
- 尚未覆盖的双浏览器、真实模型、旧库发布路径、剩余恢复/并发矩阵保持未完成；上述计数不能替代对应行的完整验收。
- 索引优化后数据库全量 153 项通过，EF has-pending-model-changes 检查通过。规模测试显示未终结队首及序号范围索引使大历史量消费耗时从 159683 ms 降至 4712 ms；读取规模保持不变。完整回归已通过，包含新耗时门和最新迁移。
- 旧 schema 发布路径已使用仅保留测试账号的隔离副本，回退至 AddUserAccountAdmin 后由最新发布包升级；浏览器实际登录与建会成功。该包尚未包含随后新增的性能索引，完整旧库协作/删除及最终发布复验仍待完成。
- AmbiguousLegacyReplyAbortsMigrationAndLeavesBothOldTasksReadable 通过：重复回复归属阻止升级、完整回滚、旧任务可读且诊断不包含正文。
- 双浏览器补充：升级后隔离发布包完成两参考宿主固定回复链，390×844 与 1280×900 视口 documentWidth 分别为 390/1280；长名称参与者可点击插入提及，输入区可滚动到达。两宿主外部 quit 均退出码 0、报告 transport_closed，网页显示连接中断。390 视口删除请求网络记录为 202，另一浏览器自动返回空列表；13 张会议相关表全部为 0，foreign_key_check 无异常。尚未替代只停模型、暂缓清理与最终含性能索引的发布复验。
- McpMultiAgentCollaborationTests 扩展后的四种组合再次通过：根/派生 requestedTask、Processing 错误 taskSummary、resume.lastRepliedTask 的来源与原 mention 一致，跨 Agent 指定任务查询返回 task_not_available 且不泄露 collaboration。
- MultiAgentLegacyStatePreservationTests 对 Queued/Delivered/Processing/Failed/Replied/Cancelled 各自比较全部既有任务列，6 项通过；MultiAgentLegacyRelatedFactsTests 补验多目标、确认尝试、失败回执与重试收据通过。MultiAgentUpgradeBoundaryConsumerTests 的 3 项通过，覆盖 Human/Agent 后续事件补扫与缺失边界修复后原事件重放。
- 最新含性能索引的隔离 Release 发布包已构建并在空库复验：Chrome DevTools MCP 登录、建会、发送普通冻结/暂停文本、刷新、删除通过；删除请求返回 202，页面回到空列表，无控制台警告/错误。13 张会议业务表清空，foreign_key_check 无异常，两个新增任务查询索引实际存在；MeetingCreationReceipts 保留 1 条创建幂等收据。该轮尚未覆盖双 Agent、旧数据升级及重启。
