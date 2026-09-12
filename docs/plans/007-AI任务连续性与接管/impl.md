# AI 任务连续性与接管实现方案

当前实现已接入 [008 多 Agent 协作与会议删除收口](../008-多Agent协作与会议删除收口/design.md)：Human 有效提及继续创建根任务；Agent 有效提及仅在会议启用边界之后派生，System 不触发。旧边界内 Agent 不追溯，旧 Human 待办仍补齐。legacy/可靠驻留模板分别为 `resident-instruction-v3-legacy` / `resident-instruction-v3-reliable`，仅可靠 join/resume 声明 `multi_agent_collaboration_v1`；共 9 个工具（既有 4 个、可靠专属 5 个）。来源随任务恢复保持，删除屏障提交返回 202 后仍由后台清理父引用、边界及原有资源；008 的新增验收见其 [testPlan.md](../008-多Agent协作与会议删除收口/testPlan.md)，本文件原有通过证据不代替该验收，旧服务夹具的 v1/v2 仍保留。

## 1. 定位与实施前提

本文件拆解[主题设计](design.md)，测试要求见 [testPlan.md](testPlan.md)。当前已落地领域恢复状态、SQLite 迁移、可靠模式服务及九个生产 MCP 工具，并接入连接关闭、重启与租约/确认超时扫描、版本化 activity 与网页投影。可靠参考客户端已具备恢复、独立续租、显式确认、故障注入和模型接口；并发/崩溃矩阵、真实模型两轮回复及跨租约续租、浏览器验收均已通过，当前主题实现与验收完成。

实施前确认主题五的构建、全量测试、真实 MCP、SignalR、浏览器和参考 Agent 验收基线。代码分析绑定 RoundTableMeeting 仓库；修改每个既有函数、类或方法前必须按 AGENTS.md 执行 GitNexus impact 并报告调用方、流程和风险，UNKNOWN 补充源代码确认，HIGH/CRITICAL 先告知用户。文档阶段不执行代码影响分析。提交前按仓库规则做图变更分析并取得提交授权。

现有落点：

- Application/AgentCollaboration/AgentCollaborationService：JoinAsync、WaitAsync、ReplyAsync、初始确认与投递最终化。
- AgentMentionTaskConsumer：按分配游标补齐任务，保持离线分配，Human 根任务及 008 启用边界后的 Agent 派生任务均按序补齐。
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

当前实现状态：

- 原四个 MCP 工具已增加模式分派与可选参数，五个可靠工具已注册到生产宿主；九个 inputSchema/outputSchema 可发现。可靠 join/resume/confirm/renew/failure/retry/wait/reply/status 使用真实 SQLite 事务；legacy 保留原响应字段和 Data=null 错误。已升级身份禁止 legacy 重入，在线显式接管后旧 legacy 工具返回 connection_superseded。
- 真实 SDK 已验证显式确认、失败报告、FIFO 阻塞、显式重试、回复与恢复后的回复重放，逐个使用生产 outputSchema 校验响应。独立 SDK 能力实验仍作为调度与关闭行为的基础证据；并发、响应丢失、参考客户端协议与真实模型能力门的逐项证据见 testPlan。
- AgentRecoveryRequestValidator 校验可靠必填字段、规范凭据编码与确认目标互斥。恢复入口已接入来源/业务连接限流、固定时间密钥摘要验证、当前连接指针和作用域内操作收据；密钥不出现在响应和请求诊断字符串中。
- MCP 异常封装按工具显式模式或匹配业务 ID 的既有注册模式返回：可靠错误使用 AgentRecoveryErrorData，旧模式/无法识别的旧工具匿名错误保持 null。注册模式只用于错误格式，不代替持久化所有权校验；匿名新工具仅返回 nextAction=stop，异常正文不外泄，取消仍保持协议取消语义。初始化、订阅与后台扫描构造注册信息时同步携带模式。
- AgentConnectionRegistry 支持传输预留、完整版本比较、退役占用与取消令牌。join/resume 使用传输预留 → 会议 gate → 参与者锁 → 数据库事务的顺序；两阶段 Activating/Connected 激活和条件补偿不跨数据库事务等待网络。resume 保留 ID、代数、预算和初始快照，增加绑定版本；takeover 创建新 ID/代数且永久升级参与者模式。
- 显式初始确认只推进首次入会边界；任务确认、失败与回复均校验当前尝试。wait 保存并重放固定窗口，确认超时与扫描共用失败转换；替代投递才计入自动重试次数，失败报告和显式 retry 保存不可变收据。已完成收据重放与 status 不续租；耗尽队首阻塞 FIFO，resume/takeover 不重置已有可靠失败决定。
- AddAgentRecoveryState 在同一 EF 事务中重建表、预检歧义并回填当前指针和 UTF-16 初始窗口。旧连接上的重复初始字段已移除；复合外键、归属触发器、回滚与 legacy 升降级已有验证。旧版 Failed 保留原因和时间，初始化为需要显式重试的永久阻塞；升级后新产生的 legacy Failed 在收敛/接管时补齐相同决定，不改写已有可靠失败周期。
- 关闭补偿、启动/定时扫描在当前指针、代数与绑定条件下原子断开连接、失效 Delivered/Processing 并安排恢复，已 Failed/Replied 不重新分配。扫描使用独立 scope、UUID 边界分页和每批 100 条上限；确认超时扫描只使任务失败，不断开有效连接。提交后撤销旧等待并通知状态，激活补偿复用已持有的锁以免自锁。
- AgentActivityTransactions 在同一事务内比较操作前后的纯持久化投影，仅可见 payload 改变时增加 revision/changedAt；提交后发布。普通续租、收据重放、后排任务入队不改版本。订阅快照在会议 gate 和有序参与者锁内收敛过期租约/缺失注册，重查名单并在新增 Agent 时重试；事件发布读取持久化事实，不通过时钟生成相同 revision 的不同状态。网页映射、reducer 和列表均接入版本及中文状态，Agent presence 来自 activity。
- Demo/AI参会联调客户端/reliable.mjs 已实现私有状态、独立续租、同传输请求重放、新传输 resume、旧尝试正文隔离、耗尽等待显式 retry 和可选模型接口；原 index.mjs 保留 legacy。operation_pending 在同传输上有界重放同一操作，遵守 retryAfterSeconds；operation_superseded 改用新传输/新恢复操作，connection_superseded 停止。恢复响应没有初始上下文时保留本地历史，仅有握手而无业务进展的恢复循环有连续失败上限，退出可取消续租退避。21 项 Node 测试覆盖这些恢复行为及传输、私有状态互斥。浏览器已验证确认/处理/失败/重试、在线接管单行、刷新和回复去重，完整范围见 testPlan。
- 可靠激活故障已覆盖 join/resume 在注册前/后返回失败或抛异常的八种组合，验证提交身份保留、条件断开与收据重放。注册前失败也禁止同一传输用新 resume 操作再次绑定，返回 connection_invalid；恢复须使用新传输，原操作收据判定仍优先。
- 旧库迁移增加六种任务状态与连接在线/离线、初始确认/未确认的 48 项组合，以及尝试缺失、同时间歧义和跨 Agent 归属的五项拒绝升级测试。保留历史/当前尝试、回复、幂等键和候选区间，不伪造客户端确认；异常升级原库可读且无半迁移。
- 并发测试通过参与者锁同步屏障固定竞争顺序，覆盖 reply/takeover、reply/failure、相同或不同正文的双 reply、相同或不同操作 ID 的双 retry、双 takeover、续租锁内跨过截止、确认与扫描在截止前/截止时的两种顺序，以及排队取消的 16 项边界；独立 scope 和真实 SQLite 验证仅合法结果提交。删除独占 gate 屏障下 join/create、takeover、resume、confirm、reply、renew、wait、status、failure、retry 和扫描均拒绝，另一会议 Human 发言和 Agent 加入/续租不受阻，释放后资源清理完成。
- 旧模式 Failed 若有 Prepared/Delivered/Abandoned 历史尝试，接管补齐尝试失效事实，同时保留原失败原因、时间和阻塞决定。已 Abandoned 且无后继的旧尝试允许链接一次替代；已有替代及可靠模式的 Abandoned 仍拒绝二次替换。三种旧形态均已验证显式 retry 后实际领取、确认和回复成功，新周期首次领取不消耗自动额度。
- 真实服务进程重启已有初始未确认、Delivered、Processing、Replied、临时 Failed 与永久阻塞六种场景。另有八项精确事务切点：join、连接激活、reply、删除屏障各自提交前/后，用测试程序集中的 EF DiagnosticSource 同步探针暂停真实生产入口，尚未返回请求时强杀，再复用原 SQLite 重启。未提交写入回滚；已提交身份经收据 resume；回复、提及和事件无半提交或重复；Deleting 重启清理多代连接、替代尝试、失败队首及全部恢复资源。故障探针不进入生产程序集或发布产物。
- 网页已补验 16 个 64 字符长名称 AI 与一名 Human 的状态列表，在 390/1280 像素视口验证换行、输入操作可达和无横向溢出；头像禁止 flex 收缩以保持圆形。隔离会议删除后资源计数和外键检查通过。
- 完整升级链路补验十二种旧任务/尝试形态：生产宿主从旧 schema 自动迁移并收敛，真实 MCP 接管后未完成任务继续、旧 Failed 等待显式重试、已回复或取消任务不重投。补验接管前后上下文连续范围、空首次历史、预算变化与无待办间隔；两个完整自动重试周期与离线 100 次扫描不重置失败事实；同会议可靠/legacy 队列独立。可靠回复在消息追加后任务标记前异常可整体回滚。生产 join 十六项非法参数与模式错误验证身份零残留，SDK 类型拒绝与业务错误分开断言。
- Activating 持久化提交后、退役旧注册前发生中断时，重放或后续恢复主动清除同参与者严格较旧的注册，避免旧注册永久阻止新绑定激活；保留同版本和较新版本。六项 create/resume/takeover 与重放/接管顺序测试、两项扫描补偿后恢复测试覆盖这一边界，旧传输迟到关闭不影响当前连接。
- 激活失败后的断开补偿事务若再次失败，已写入的断开事实完整回滚，原 Activating 与收据保留。六项 create/resume/takeover × 原操作先重放/扫描先补偿验证均通过：前者同版本完成激活，后者通过新传输恢复；没有重复收据或额外 activity 版本，旧关闭不影响当前连接。
- 已投递窗口重建遇到数据不变量错误时，与首次构造一致地失效当前尝试、保留永久失败队首；短暂仓储异常仍回滚并返回暂不可用。四项首次/重放与永久/短暂错误交叉测试验证修复后显式 retry 或直接重放继续完成。
- 真实 SDK 补验同会议/跨会议的七工具归属隔离与来源/连接的恢复限流及失败额度；被拒绝请求不修改双方租约、绑定、任务或收据。任务 activity 矩阵验证确认超时、替代领取、永久失败、显式重试、回复及下一任务的可见版本变化，重复扫描/查询/续租/收据重放均不空转版本。
- 连接入口补验名称规范化、收据模式冲突及后续重绑隔离、过期 create 与在线 legacy 的不同判定、七工具精确租约到期拒绝、同传输跨会议竞争及激活失败后禁止降级。确认补验长确认时限与租约独立，已提交确认在原截止后及回复后安全重放。删除补验五类业务先持锁的反向顺序，以及屏障后旧 activity/presence 不再发送、其他会议通知和订阅快照可继续。
- 参考客户端 Unix 文件锁拒绝自动删除遗留锁，避免多个启动进程同时回收并覆盖私有状态；崩溃后须确认所有使用该状态的客户端已停止再人工移除 `.lock`，保留状态文件恢复。Windows 命名管道继续由操作系统自动释放。参考客户端共 21 项测试通过；Unix 分支逻辑在 Windows 测试进程中执行，不冒充真实 Unix 平台验收。
- 浏览器补验失败队首恢复/接管/重试、旧连接拒绝与关闭隔离、刷新和回复去重，并修复正常发送时短暂显示失败提示的问题；发送组件仅在 send=failed 时展示失败和重试入口，细节维护于主题四的 MessageComposer 实现文档。隔离会议与恢复资源已清理。
- 真实 SDK 补齐三任务 FIFO、非法 failureCode、Processing/非耗尽 retry、旧尝试 confirm/reply、确认超时后扫描、固定窗口重放期间配置变更、300 秒 wait 与乱序续租。已绑定 legacy 的 resume 统一走所有权栅栏后返回模式错误；匿名恢复仍统一校验密钥不泄露目标模式。恢复限流五项回归通过。
- 实际 Node 参考 CLI 验证丢 wait/任务 confirm 后不重复生成/回复、永久阻塞保持两个租约周期并接受 stdin retry、在线 takeover 后旧循环退出，以及 DELETE 503 时明确失败而不推断持久化离线。固定回复模式尚不代表真实模型验收。
- 网页连接回调绑定创建时的 generation；切换/重连/删除使旧回调失效，阻止迟到删除误报当前会议不可用，旧重新订阅不触发新历史请求，异步发送与历史结果保留原作用域。手动重连保留草稿及原提交幂等 ID，忽略旧请求成功回调；组件与 reducer 回归通过，前端全量 103 项通过。
- activity 补齐 takeover 两阶段完整 payload/revision 比较及生产混合订阅快照；丢弃任务唤醒后仍由数据库轮询交付。
- 参考 CLI 的 pending/扫描补偿/原收据/新传输恢复已在真实应用链路串联。条件写冲突 agent_operation_conflict 现在保持原参数和 operationId 最多退避重试两次；指纹冲突 operation_conflict 仍立即停止。Node 全套 21 项及实际进程回归通过。
- 错误契约与敏感输出验证已补齐：统一 SDK 错误 schema/字段白名单、入口与归属优先级、确认超时以及真实 CLI 条件冲突退避均通过。补偿、activity 通知、实时订阅与消息分发的五个异常日志切点只保存安全诊断字段和异常类型。testPlan 共 140 项证据完整；服务端全量 600 项及新增 4 项 Release 定向回归通过，客户端 103 项、参考客户端 21 项通过。最新 Release 发布、九工具与浏览器重连保留草稿/发送/删除复验通过。CT-L05/G04/G05 已由真实模型 deepseek-v4-flash-vision-exp 验收：6 秒租约、1 秒独立续租，第一轮真实请求耗时 17.446 秒、采集到 18 个不同租约截止时间，全程 Processing；第二轮 4.691 秒，正确引用上一轮应用并计算 37×19=703。两轮各一任务/尝试/回复，无自动重试或恢复，随后继续等待，正常退出后网页显示连接中断。主题完成门已满足。
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

接入文档随已验证能力更新；主题五 README 继续描述 legacy 语义并链接可靠入口。固定回复和真实模型证据分开记录。不将根目录联调建议中的 SSE/WebSocket 改造或“所有 MCP 请求只能串行”直接写入实现要求。

## 10. 验证与交付

按 testPlan.md 执行单元、真实 SQLite、MCP、SignalR、客户端、参考 Agent、真实模型和浏览器验收。将 CT-C24 与 CT-V11 纳入持续回归，任何协调游标、所有权或 activity 变更均保持指针/代数配对与 revision 全变化点校验。测试使用隔离数据，不修改开发会议或数据库。新增/修改文本为 UTF-8 无 BOM、CRLF；检查本主题设计、实现、测试中的字段、状态和错误码一致。

文档只保留当前有效方案，不新建实施日志、迁移记录或验收报告文件。实现阶段完成后更新最贴合的现有文档并在交付说明中报告实际测试结果和未通过项；未执行场景不能写成通过。没有用户明确授权不创建分支、不提交代码。
