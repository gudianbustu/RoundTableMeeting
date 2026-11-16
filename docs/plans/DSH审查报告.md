# DSH 接入改造设计变更独立审查报告

## 0. 审查范围与方法

### 0.1 审查对象

本次审查针对工作树中尚未提交的设计变更（`git diff HEAD`，10 个已跟踪文件，182 增 / 40 删），以及工作树根目录新增的未跟踪文档 `DSH连接错误和修改建议.md`：

| 文件 | 变更性质 |
| --- | --- |
| `docs/plans/001-总体计划/design.md` | 总体目标、架构方向、主题五范围、完成标准、总体验收 |
| `docs/需求分析/圆桌会议网站需求.md` | 4.2/4.3/5.7/5.8/7.1/7.2/9/10 的 Agent 接入表述与统一发布 |
| `docs/plans/006-AI参会/design.md` | 目标 1/7、3.1、3.2、9.3、12.3、新增第 15 节 |
| `docs/plans/006-AI参会/impl.md` | 文档定位、4.9 第 6 步、新增第 8 节 |
| `docs/plans/006-AI参会/testPlan.md` | 新增第 20 节（AI-PKG001~013） |
| `docs/plans/006-AI参会/ReferenceAgent/README.md` | 改名为“Agent 接入规范（技能、CLI 与 MCP）”，新增 1.1/1.2/1.3 |
| `docs/plans/004-会议工作台/design.md` | 非目标清单表述 |
| `docs/plans/004-会议工作台/Client/Components/InviteMeetingDialog/{design,impl}.md` | 新版技能/CLI 提示词与下载入口 |
| `docs/plans/007-AI任务连续性与接管/design.md` | 新增承接说明（正式 CLI/Runtime 复用本主题契约） |

变更主题是：把“外部 Agent 通过 MCP 直连参会”扩展为“技能（SKILL.md）+ 本地 CLI/常驻 Runtime + 原生 MCP 双通道”，并新增“统一发布包 + 同源只读静态下载 `/downloads/rtm-agent/{version}/` 与 `/latest/`”。

### 0.2 审查方法

- 通读全部变更文件当前全文，不只看 diff 片段，检查文档内部与跨文档一致性。
- 以 007 设计/测试、008 设计、服务端源码、客户端源码、构建脚本为事实依据，核对设计声明与代码事实是否一致。
- 未修改任何原文或项目代码；本报告是本次审查唯一新增产物。
- 本文中的“文档声明”与“代码事实”严格区分；“未找到证据”表示在仓库中未检索到对应实现或条款。

### 0.3 事实基线（用于核对的代码现状）

以下为本次审查实际读取并确认的代码事实，后文结论均以此为基准：

| 事实 | 证据 |
| --- | --- |
| MCP 工具共 9 个：`join_meeting`、`wait_for_mention`、`reply_to_mention`、`get_agent_status` + `resume_agent`、`confirm_context`、`renew_agent_lease`、`report_task_failure`、`retry_task` | `Server/RoundTableMeeting/AgentCollaboration/McpAgentTools.cs:16,30,39,47,56,65,74,82,90` |
| 业务连接绑定 MCP 协议会话，注册表为进程内字典并按协议会话键寻址 | `Server/RoundTableMeeting/Hosting/ServiceCollectionExtensions.cs:75-101`、`AgentCollaborationService.cs:45-50,664-691`、`AgentConnectionRegistry.cs:7` |
| 可靠契约参数：租约 120s / 续租 30s / 安全边际 30s / 可靠等待 30s / 确认 60s / 最大等待取 `WaitTimeoutSeconds`(默认 300) | `AgentRecoveryOptions.cs:8-15,27-28,39`、`AgentCollaborationOptions.cs:19` |
| `wait_for_mention` 在可靠模式接受可选 `waitTimeoutSeconds`（1~300，越界 `agent_request_invalid`） | `McpAgentTools.cs:30-37`、`AgentReliableWaitService.cs:15-16` |
| `confirm_context` 的 `kind=initial` 无时限；`kind=task` 以 `ConfirmationDeadline`(=now+60) 判定 `confirmation_expired` | `AgentRecoveryContextService.cs:45-46`、`AgentReliableWaitService.cs:80` |
| `joinMode` 仅 `create` / `takeover`（无 `resume` 值），`joinDisposition` 仅 `created` / `taken_over` | `AgentRecoveryContracts.cs:10-11`、`AgentReliableJoinService.cs:67,114` |
| 回复幂等键由 TaskId 经 UUIDv5 派生，可跨进程重算；已 `Replied` 时正文必须完全相等才重放，否则 `reply_idempotency_mismatch` | `AgentReplyIdempotencyKeyFactory.cs:7`、`AgentCollaborationService.cs:328-414` |
| 服务端无 leave 工具、无自建 DELETE 处理；会话关闭依赖 MCP DELETE（SDK 能力） | `McpAgentTools.cs`（无 leave）、`Tests/.../McpAgentContractTests.cs:531-549` |
| 静态文件仅 wwwroot；`MapFallback` 排除清单不含 `/downloads` | `Program.cs:88-89,106-131` |
| 无统一版本号来源；发布脚本用时间戳命名压缩包 | `Server/Directory.Build.props`、`RoundTableMeeting.csproj`、`Client/roundTableMeeting/package.json:4`、`publish.ps1:6` |
| 单实例仅有文档约束，实现只有 WAL + `busy_timeout=5000` + 启动迁移，无启动锁/单写互斥 | `SqliteDatabaseInitializer.cs:40-58`、`006/testPlan.md:384`、`008/design.md:165` |
| 客户端提示词为 `buildAgentInvitePrompt(agentName, meetingCode, mcpUrl)`；Vite 代理仅 `/api`、`/hubs`、`/mcp`；`base:'/'`，无子路径能力 | `invitePrompts.ts:1`、`vite.config.ts:8,14`、`apiClient.ts:33` |

### 0.4 结论摘要

变更方向本身自洽且有必要：DSH 实测（`DSH连接错误和修改建议.md`）证明了“宿主无内置 MCP 客户端”是真缺口，用技能 + Runtime + CLI 补足宿主能力，比要求每个执行者手写 JSON-RPC/SSE 客户端更可靠；把发布物与提示词同源化也修正了“提示词指向不可读入口”的隐患。

但本次变更**尚不具备冻结条件**：

- 3 项 P0 级缺陷会导致新交付物无法完整闭环（CLI 未覆盖可靠工具集的失败路径、本地通信/任务处理句柄契约未冻结、`/downloads` 静态路由与现有 SPA fallback 冲突未识别）。
- 12 项 P1 级问题会造成跨文档契约不一致或验收空转（等待时限契约过时、无统一版本号来源且发布布局与现有 `publish.ps1` 冲突、README 错误码集合不完整、需求与设计承诺的“接管”不一致、提示词地址清单不一致、发布承载方未指定、恢复密钥落盘路径未纳入约束、缺少同身份多 Runtime 的防护验收、回复正文一致性约束缺失，以及若干承载与引用问题）。
- 12 项 P2 级遗漏与文档结构问题（详见第 3、5 节），其中多数为机械修正。

因此本报告的判定是：**不建议在本次状态下冻结并启动实施**；建议按第 6.2 节的 4 项文档修订完成后再冻结。第 6 节同时给出“最小冻结路径”和“可立即并行启动的实施项”。

---

## 1. 严重问题（P0，冻结前必须修订）

### P0-1 CLI 命令族未覆盖可靠工具集，失败队首无法解除阻塞

**证据（代码事实）**：服务端唯一 `[McpServerToolType]` 注册 9 个工具，其中既有 4 个（`join_meeting`、`wait_for_mention`、`reply_to_mention`、`get_agent_status`），可靠专属 5 个（`resume_agent`、`confirm_context`、`renew_agent_lease`、`report_task_failure`、`retry_task`），与各文档头部“共 9 个工具（既有 4 个、可靠专属 5 个）”一致，且没有文档未提及的额外工具。

**问题**：`design.md` 第 15.1 节把 CLI 命令族固定为 `rtm-agent start/join/wait/reply/status/diagnose/capabilities/retry/stop`，`impl.md` 第 8 节第 2 步重复同一命令族。对照 007：

- `report_task_failure` **没有任何 CLI 出口**，`impl.md` 全文未出现该工具名（已检索确认）。
- `retry_task` 仅由 `retry` 含糊对应，未写明 `taskId` + `operationId` 语义。

007 设计第 7 节明确：`agent_permanent_failure` 之后的队首“停止自动重试，显式 retry 才重开”，`retry_exhausted` 的客户端动作是 `await_explicit_retry`。也就是说，在 CLI/Runtime 路径下：

- `Runtime` 自身无法把“任务永久失败”上报服务端（它不该替 Agent 推理，也不该替 Agent 判定失败）；
- 外部 Agent 又没有命令行可以上报失败或显式重开重试周期。

后果是失败队首永久阻塞该 Agent 的 FIFO，只能由人删除会议或绕过 Runtime 直连 MCP 才能恢复。这会直接击穿总体计划第 8 节“AI 回复任务需要支持排队、失败恢复”的完成标准，也与新 `testPlan.md` 第 20 节（AI-PKG001~013）**没有任何失败路径用例**相互印证——缺口在设计与验收两侧同时存在。

**建议**：在 15.1 与 impl 第 8 节命令族中显式补齐 `report-failure`（受控失败码，仅 `agent_transient_failure` / `agent_permanent_failure`）与 `retry`（`taskId` + 持久化 `operationId`），并在 testPlan 第 20 节增加“永久失败→阻塞→显式重试→恢复投递”的 P1 用例。

### P0-2 本地通信契约、任务处理句柄与失败动作映射未冻结

**证据（文档声明）**：

- `design.md` 15.1：“参数及本地通信契约在实现前细化，文档中的命令名不表示已有可执行产物。”
- `design.md` 15.4：“CLI 模式由技能说明业务 nextAction 与本地命令的映射。”
- `impl.md` 第 8 节第 1、2 步：把“位置、实现语言、支持平台、打包依赖、本地通信及任务处理句柄契约”列为实施第 1 步要“明确”的内容。

**问题**：本次设计的核心新增物是“CLI + 常驻 Runtime”，其中三个关键契约全部被推迟到实施阶段：

1. **本地通信契约**：传输方式（命名管道 / Unix domain socket / 本地 HTTP）、端点命名、并发语义、超时、崩溃后清理、Windows 侧 ACL 与 Unix 侧权限、多用户隔离。
2. **任务处理句柄语义**：句柄字段（taskId / deliveryAttemptId / replyIdempotencyKey / 绑定版本）、有效期、并发上限、“同一任务不得同时分配给两个处理器”的强制点（本地锁还是服务端 `task_in_progress`）、Agent 长时间推理跨越确认时限时句柄如何处理。
3. **失败动作映射**：007 的 `nextAction` 全集为 `confirm_context`、`wait_for_mention`、`reply_to_mention`、`get_agent_status`、`resume_agent`、`await_explicit_retry`、`stop`（`007/design.md` 第 9.3 节），而邀请提示词（README 1.2）与 impl 第 8 节第 3 步只描述了“入会 → 读取任务 → 回复 → 继续等待”的正常路径，`await_explicit_retry` 与 `stop` 均未映射到任何 CLI 命令或技能动作。

**为什么必须是常驻 Runtime（代码事实，设计未写明）**：连接注册表是**进程内**字典，按 MCP 协议会话键寻址（`AgentConnectionRegistry.cs:7`、`ServiceCollectionExtensions.cs:75-101`）；业务调用必须同时匹配协议会话与业务连接（`AgentCollaborationService.cs:664-691`）。因此每条 CLI 短命令若各自 `initialize`，会得到新的协议会话键，永远无法寻址已有连接——这正是 DSH 实测 P5 的现象（进程 B 携旧连接 ID 调用全部 `connection_invalid`）。设计只说“不能让每条 CLI 命令各自建立 MCP 会话”，但没有给出这个机制性理由，实施者容易低估约束强度。

**风险**：AI-PKG007/008 的验收对象正是这些未定义契约（“多个短命令复用同一 Runtime/MCP 会话”“当前尝试正文保存后提交”），验收时没有可判定的契约，只能由实现者事后解释。这属于典型的“验收标准先于契约存在”。

**建议**：在 15.1 增加一张最小契约表：本地通信方式与端点、状态文件路径与权限、任务处理句柄字段与有效期、`nextAction` → CLI 命令/技能动作的完整映射（含 `await_explicit_retry`、`stop`、`resume_agent`）。参数细节可以留到实现，但字段名、语义与所有权必须先冻结。

### P0-3 `/downloads` 静态路由与现有 SPA fallback 冲突，且当前无静态映射承载

**证据（代码事实）**：`Server/RoundTableMeeting/Program.cs`：

- 第 88~89 行：`app.UseDefaultFiles(); app.UseStaticFiles();`（仅 wwwroot，**未配置任何 `/downloads` 物理目录映射**）
- 第 106~131 行：`app.MapFallback(...)` 对**未被排除**的 GET/HEAD 请求，只要 `Accept` 含 `text/html` 且 wwwroot 下存在 `index.html`，就返回 `index.html`；排除清单只有 `/api`、`/openapi`、`/scalar`、`/hubs`、`/mcp`（第 111~115 行）。

**问题**：`design.md` 15.2 明确要求“缺失文件返回 404，不能被 SPA fallback 返回 index.html”。按当前 fallback 实现，一个指向 `https://<host>/downloads/rtm-agent/latest/SKILL.md` 的**浏览器访问**（`Accept: text/html`）在文件缺失时会拿到 `index.html`（HTTP 200 + HTML），只有非 HTML 客户端才会得到 404。这正是设计中点名要避免的行为，而设计没有指出需要在 fallback 排除清单中补 `/downloads`。

更基础的问题是**没有任何东西承载这个路由**：全仓没有 `PhysicalFileProvider`、`StaticFileOptions` 或 `FileExtensionContentTypeProvider` 的使用；`UseStaticFiles()` 默认只服务 `WebRootPath`（`Program.cs:122` 显式取 `wwwroot`），而 15.2 的产物目录在 `publish/downloads/`（wwwroot 之外），因此**不新增映射就是完全不可达**。同时现网发布产物是 IIS 进程内托管（`.gitnexus/008-publish-final/web.config` 的 `hostingModel="inprocess"`），新增目录还需确认应用池身份的读取权限。

此外，新静态目录的挂载点（物理路径相对内容根还是相对程序目录）、`OnPrepareResponse` 缓存头与 `Content-Disposition`、以及承载这些内容的 Server 子文档（见 P1-6）在本次变更中均未定义。

**建议**：在 15.2 增加实现级约束：新增静态映射须在 `UseRouting` 之后、`MapFallback` 之前注册，并把 `/downloads` 加入 fallback 排除清单；明确 `downloads` 物理路径相对内容根还是相对程序目录；明确固定版本与 `latest` 的缓存头策略。AI-PKG002 应把“浏览器 Accept: text/html 请求缺失文件也返回 404”写成显式断言。

---

## 2. 重要问题（P1，冻结前应修订或明确接受）

### P1-1 接入规范的等待时限契约与当前服务端实际契约矛盾

**证据（代码事实）**：

- `McpAgentTools.cs` 第 30~37 行：`wait_for_mention(connectionSessionId, waitTimeoutSeconds = null)`，描述为“Reliable mode only: bounded server wait in seconds; omitted uses the negotiated default”。
- `Server/Tests/.../TestApplicationFactory.cs` 第 113~116 行同时注入 `AgentCollaboration:WaitTimeoutSeconds` 与 `AgentCollaboration:ReliableWaitTimeoutSeconds`。
- `007/design.md` 第 165、191、274、300、316 行定义：`waitTimeoutSeconds` 有效范围 1~`WaitTimeoutSeconds`，越界返回 `agent_request_invalid`；`AgentLeasePolicyDto` 含 `reliableWaitTimeoutSeconds` 与 `maximumWaitTimeoutSeconds`。

**问题（文档声明）**：`ReferenceAgent/README.md` 第 62 行仍写“wait 输入只有 `connectionSessionId`，没有客户端指定的等待时长参数”，第 64 行建议管理员设置 `AgentCollaboration__WaitTimeoutSeconds=30` 来适配短时限。本次变更把这份文件提升为“Agent 接入规范（技能、CLI 与 MCP）”并被 004 组件文档指定为提示词模板权威（“模板以 … 第 1.2 节为准”），但第 3 节的可靠模式契约是过时且与 007 相反的。

**后果**：

- CLI/Runtime 若按该节实现，会在宿主时限受限时被要求修改服务端实例配置（需要重启），而正确做法是可靠模式下使用 `reliableWaitTimeoutSeconds` / `waitTimeoutSeconds`。
- `007/testPlan.md` CT-M18 明确“legacy 携带 `waitTimeoutSeconds` 等新参数必须返回 `reliability_mode_required`，不能静默忽略”，说明该参数的存在与模式约束是已冻结契约。接入规范与其矛盾会误导第三方实现。

该问题在 DSH 实测中已经暴露：`DSH连接错误和修改建议.md` 第 225 行记录了 `explicit_ack_v1` 下 `reliableWaitTimeoutSeconds=30`、`maximumWaitTimeoutSeconds=300` 的真实取值。

**建议**：把 README 第 3 节改写为“按模式分派”：legacy 沿用现描述；可靠模式说明 `waitTimeoutSeconds` 取值范围、默认值与租约策略字段来源。同时在变更说明中标注该节已被本次变更影响但尚未同步。

### P1-2 统一 `releaseVersion` 没有来源，现有发布布局与设计示意冲突

**证据（代码事实）**：

- `Server/RoundTableMeeting/RoundTableMeeting.csproj` 无 `Version`/`VersionPrefix`；`Server/Directory.Build.props` 无版本属性。
- `Client/roundTableMeeting/package.json` 版本为 `0.0.0`（脚手架默认）。
- 仓库根 `publish.ps1` 把后端发布到 `publish/RoundTableMeeting/`，把前端 `dist` 复制到 `publish/RoundTableMeeting/wwwroot/`，产物压缩包用 `Get-Date -Format 'yyyyMMddHHmm'` 命名；`.gitignore` 第 91 行忽略 `publish/`。

**问题（设计声明）**：`design.md` 15.2 的目录示意把 `downloads/rtm-agent/<version>/` 放在发布根下、与 `RoundTableMeeting.dll` 和 `wwwroot/` 平级；15.3 要求 manifest 含“统一 releaseVersion”，并要求“固定版本文件不可覆盖”“latest 是当前部署版本的稳定入口”。

三者之间存在真实落差：

1. 仓库中不存在任何“统一发布版本”的事实来源，两个版本号来源互相独立且都不可用（一个缺失、一个是 `0.0.0`）。
2. `publish.ps1` 的实际输出与 15.2 示意不一致（多一层 `RoundTableMeeting/`），照抄示意会导致静态映射指向错误目录。
3. `latest` 的实现机制（符号链接 / 目录联接 / 部署时文件复制 / 运行时解析）未定义；Windows 上创建符号链接需要额外权限，若采用复制则“固定版本不可覆盖”与“latest 切换原子性”都更难保证。
4. 旧版本目录的保留与清理策略缺失（“长期缓存”与磁盘无限增长之间没有边界）。

**建议**：在 15.3 明确版本号唯一来源（例如 `Directory.Build.props` 的 `VersionPrefix` 或 `release.json`）、`latest` 的落地机制与部署脚本的替换方式、旧版本保留数量或清理触发条件；在 15.2 示意中标注相对内容根的准确层级，与 `publish.ps1` 的输出对齐。

### P1-3 接入规范的错误码集合不完整且混合 legacy/可靠模式

**证据（代码事实）**：007 第 9.7 节的错误码注册表包含 `agent_connection_already_active`、`agent_recovery_not_supported`、`agent_takeover_required`、`reliability_mode_required`、`agent_request_invalid`、`connection_invalid`、`connection_superseded`、`resume_rejected`、`operation_conflict`、`operation_superseded`、`operation_pending`、`initial_context_unconfirmed`、`context_confirmation_required`、`confirmation_expired`、`delivery_attempt_stale`、`task_in_progress`、`retry_pending`、`retry_exhausted`、`retry_not_required`、`failure_code_invalid`、`reply_idempotency_mismatch`、`message_validation_failed`、`meeting_not_found`、`agent_operation_unavailable` 等。

**问题（文档声明）**：README 第 3 节的故障处理表只列出 `idle`、`connection_invalid`、`agent_connection_already_active`、`agent_recovery_not_supported`、`task_not_available`、`meeting_not_found` 和网络退避。本次交付的 Runtime 要面向可靠模式，却缺少 `connection_superseded`（15.1 提到了“收到 connection_superseded 后停止旧循环”，但 README 没有）、`agent_takeover_required`、`retry_exhausted`、`delivery_attempt_stale`、`confirmation_expired`、`operation_conflict` 等关键码。同时 `agent_recovery_not_supported` 是 legacy 专有码（007 第 360 行），放在通用表中未标注适用范围。

**后果**：第三方技能/CLI 实现会把可靠模式的失败并入“报告并停止”，造成误判（例如把 `retry_exhausted` 当致命错误而放弃 Agent，实际应持续续租并等待显式 retry）。这也与 P0-1 的命令族缺口互相放大。

**建议**：把该表按 legacy / 可靠两列拆分，或新增“可靠模式错误码见 007 第 9.6、9.7 节”的显式引用，并列出至少 `connection_superseded`、`agent_takeover_required`、`retry_exhausted`、`delivery_attempt_stale` 四条与本路径直接相关的分支。

### P1-4 需求与设计对“接管”的承诺不一致

**证据**：

- `docs/需求分析/圆桌会议网站需求.md` 第 48、72、223、225 行：需求承诺“新的 Agent 连接可以使用会议码和会议内显示名称接管原参与者”“连接中断后重新加入、恢复等待或接管原参与者的未完成任务”“同一参与者同时只保留一个有效连接会话”。
- `006/design.md` 第 475 行（本次未修改）：“验收时必须明确告知：主题五阶段一次传输级重连若无法恢复原 MCP 协议会话，可能留下 Disconnected 的同名 Agent，新的同名 join 会返回 `agent_recovery_not_supported`”。
- 007 第 360 行：`agent_recovery_not_supported` 仅 legacy；可靠 create 返回 `agent_takeover_required`。

**问题**：需求第 5.8 节在本次变更中只被改写了“接入方式 + 统一发布”两段，**接管的可用性承诺没有同步**。同一份需求里既说“可以接管”，又（经由设计）说“当前返回不支持”，而 DSH 实测正是踩在这个坑上（P7：凭证丢失后连接“活跃但不可寻址”，只能靠 `takeover` 自愈）。这不只是措辞问题：接入规范与邀请提示词会据此向 Agent 承诺一种当前不可靠获得的恢复路径。

**建议**：在需求 5.8 明确“接管/恢复的当前可用范围与交付主题（007/008）”，或在需求中把接管能力标注为对应主题完成后生效，避免需求—设计—规范三处语义漂移。

### P1-5 邀请提示词缺少设计要求的“CLI/Runtime 默认路径”

**证据**：

- `006/design.md` 第 460 行：提示词“固定包含同源 `SKILL.md`、`manifest.json`、MCP 地址（`origin + /mcp`）、**CLI/Runtime 默认路径**、Agent 显示名称、持续监听与回复约定和会议代码”。
- `004/.../InviteMeetingDialog/design.md` 第 18 行与 `impl.md` 第 5 行：只定义 `SKILL.md`、`manifest.json`、`/mcp` 三处地址与 `agentPackageUrl` 覆盖参数，**未定义 CLI/Runtime 默认路径**。
- `README.md` 1.2 的模板（被 004 指定为权威模板）中也没有 CLI/Runtime 默认路径，只有“技能入口 / 版本清单 / MCP 地址”。

**后果**：AI-PKG012 的验收语句（“成员复制内容含同源 SKILL.md、manifest、MCP 地址和指定名称/会议码”）恰好绕开了这一项，导致设计条款无法被验收；同时 `impl.md` 第 5 节又要求覆盖 `agentPackageUrl`、由生成器从发布入口构造 SKILL 与 manifest 的 URL，两者对“提示词里到底有哪些地址”口径不一。

**建议**：二选一——要么在 004 与 README 模板中补上 CLI/Runtime 默认入口（例如 `<服务外部地址>/downloads/rtm-agent/latest/` 作为安装入口前缀），要么从 `006/design.md` 第 460 行删除“CLI/Runtime 默认路径”，并把 AI-PKG012 的断言与最终模板逐项对齐。

### P1-6 发布承载方（Server 子文档）未随本次变更更新

**证据（代码事实）**：`docs/plans/006-AI参会/` 下当前只有 `design.md`、`impl.md`、`testPlan.md`、`ReferenceAgent/README.md`（已用 glob 确认），`design.md` 第 519~523 行列出的 Server/Client 子文档目录在该主题下并不存在。本次新增的 `impl.md` 第 8 节第 5 步要求“检查 MIME、404、缓存、下载范围以及 SPA/API/MCP 路由隔离”，但唯一能承载这些实现细节的位置（Server 设计文档、`Program.cs` 静态映射、`publish.ps1` 改造）没有被指定。

**后果**：P0-3 的 fallback 冲突、P1-2 的目录布局、`downloads` 的物理位置、缓存与 MIME 策略都没有落点文档；实施者只能在代码里自行决定，验收再回头对文档，形成反复。

**建议**：明确新增 `docs/plans/006-AI参会/Server/`（或直接在 15.2 内）承载静态下载挂载、fallback 排除、缓存头、发布目录结构与 `publish.ps1` 改造的契约，并在 `impl.md` 第 5 步引用具体文件。

### P1-7 仓库内会留存恢复密钥的高风险路径未纳入本次约束

**证据（代码事实与实测证据）**：

- `.gitignore` 中没有 `.mcp/`、也没有状态文件相关的忽略规则（已检索，仅命中 `.vscode/extensions.json` 与 `project.lock.json`）。
- `DSH连接错误和修改建议.md` 第 288~290 行把本次运行状态文件位置记为 `.mcp/state/agent.log` 与 `.mcp/state/session.left.json`；第 220 行明确要求把 `recoveryKey` 落盘。
- 工作树中当前不存在 `.mcp/` 目录（实测 `Test-Path` 为 False），说明这些状态文件在会话中被清理或位于别处，但其“未忽略”的性质没有被任何条款约束。

**问题**：15.1 只要求“本地通信端点、恢复密钥和状态文件不公开给其他用户”，15.4 只要求它们“不进入公开技能、下载 URL、普通日志或安装脚本”。**唯一能保证“不进仓库”的机制（`.gitignore` 或状态目录必须位于仓库外）没有被写入任何设计条款**，而新增 `DSH连接错误和修改建议.md` 恰好把这类路径写进了文档并提交到仓库。

**建议**：在 15.1 增加一条硬约束：Runtime 状态目录默认位于用户私有目录，且不得位于仓库工作树内；若确需仓库内路径，必须显式加入 `.gitignore` 并在技能中说明。这条属低成本高收益。

### P1-8 未定义“同一 Agent 名称同时存在两个 Runtime 实例”的防护与验收

**证据（文档声明）**：`design.md` 15.1：“同一任务不得同时分配给两个处理器。”`testPlan.md` AI-PKG007：“不同 Agent、会议、本地用户不能串用任务与凭据。”AI-PKG008 覆盖“旧尝试回复被拒绝”。三条都没有覆盖“同一 Agent 身份、同一会议、在两个宿主/两个本地用户下各起一个 Runtime”的情形。

**问题**：本地 Runtime 方案把“同一身份只启用一种接入路径”写进了提示词（README 1.2），但提示词是软约束。若同一 Agent 名称在两台机器上各起一个 Runtime：

- 两者都用同一 `recoveryKey`/`operationId` 走 `resume_agent`，服务端会按 007 的所有权规则形成抢占与 `connection_superseded`；
- 若两者用同名 `join` + `takeover`，则后加入者会静默替换前者，而前端表现与重复回复的边界无人验收。

**建议**：明确 Runtime 的本地单实例约束（同一状态目录、同一 Agent 身份只允许一个 Runtime，通过文件锁或本地端点互斥实现），并把它并入 AI-PKG007；同时说明“同身份跨机器竞争”由服务端所有权语义处理，客户端不承诺去重。

### P1-9 回复正文一致性约束未进入 CLI/Runtime 设计

**证据（代码事实）**：`AgentCollaborationService.cs:372-397`：

- 提交时 `replyIdempotencyKey` 必须等于服务端按 TaskId 派生的值；
- 投递尝试必须属于当前连接且处于 `Delivered`；
- 任务已 `Replied` 时，只有**正文完全相等**才按幂等重放返回原消息，否则返回 `reply_idempotency_mismatch`。

**问题**：设计把提交动作拆成“Agent 生成正文 → CLI `reply` → Runtime 按保存的正文提交”，并声明“相同正文返回原消息”。但没有任何条款规定：

- Runtime 必须先持久化正文再提交（否则传输失败后 Agent 再次生成的正文可能与首次不同，触发 `reply_idempotency_mismatch` 而不是补齐成功）；
- 当第二次 `reply` 传入与首次不同的正文时，CLI 应返回什么（拒绝并保留首次正文？还是提示 Agent 正文已冻结？）；
- 旧尝试被替换时返回 `delivery_attempt_stale`，Agent 应如何用 `status` 重新取回当前尝试。

**后果**：这条约束在实现时极易被解释成“以最后一次传入的正文为准”，从而在重试路径上产生“正文被静默替换”或“重试永远失败”两种相反的错误；而 AI-PKG008 只断言“传输失败不重复发消息”，无法覆盖该分支。

**建议**：在 15.1 的任务处理句柄条款中明确“正文一旦保存即冻结，重复提交必须使用同一正文；不同正文返回明确的本地错误码”，并在 AI-PKG008 增加“同一任务两次不同正文”的断言。

---

## 3. 遗漏与不完整项（P2，不阻断但应在冻结前补齐或显式接受）

1. **可靠模式特有响应分支未进入技能设计**：`join_meeting` 在可靠模式下可能返回 `kind=recovery_required`（007 第 3.3 节），此时应转 `resume_agent` 而不是重新 `create`。技能与 README 均未描述该分支，Runtime 的实现者容易把它当成失败。
2. **失败上报与显式重试的语义边界未在技能中体现**：007 要求 `agent_transient_failure` / `agent_permanent_failure` 只允许两个受控码且不得携带任意正文；CLI 若以自由文本参数承载，会同时违反该约束与“命令行不泄露业务正文”的要求。建议明确“正文只通过 stdin/文件/本地端点传入，不走命令行参数”，这也顺带规避进程列表与命令历史泄露。
3. **`notifications/initialized` 的已知服务端偏差未纳入**：DSH 实测 P3 记录该通知返回 500。若 CLI/Runtime 把初始化通知失败判为连接失败，会重现“成功会话被判失败”。建议在技能或 README 中列为已知偏差并给出“忽略但记录”的处置。
4. **自我回复循环防护未在技能层明确**：DSH 实测 P8（5 秒内 6 条自触发消息）的根因是“回复正文回带 `@被回复者`”与“未识别提及来源是自己”。006 主题明确不实现循环保护（第 565 行），但 Runtime 会原样提交 Agent 生成的正文，006 第 15 节又新增了“Agent 不因看到 MCP 工具名称再次建立直连”这类技能层约定，却未包含该防护。建议在技能中把“回复正文剥离 `@提及`、不响应自身触发”写成强制条款，或明确声明该风险由 Agent 侧承担。
5. **`takeover` 的同身份校验仍未落地**：DSH 建议 S11（007 服务端对 takeover 增加 `recoveryKey` 匹配）未被本次修订吸收。007 设计第 3.2 节描述了 `connection_superseded` 与接管语义，但未要求密钥匹配。这里存在“凭会议码 + 显示名称即可接管他人同名 Agent”的越权面，值得作为 007 的独立待办记录（本次不必改 006）。
6. **发布包自身的信任与完整性**：设计采用 HTTPS + SHA-256 校验，但没有说明是否需要签名（运行时/安装脚本属于可执行内容）。建议至少在威胁模型小节显式说明“MVP 不做签名，依赖 HTTPS 与同源发布”。
7. **邀请提示词中的地址在反向代理场景可能不可达**：客户端用 `window.location.origin` 推导下载与 MCP 地址（`InviteMeetingDialog.tsx:10` 默认值、`api/client/apiClient.ts:33` 同源约定、`004/.../impl.md` 第 5 行）。002 已明确“生产由反向代理终止 TLS”（`002/.../Server/impl.md` 第 747 行）。若操作者通过内部地址访问站点，生成的提示词会把内部地址分发给外部 Agent。设计已说“具体 URL 由部署环境配置”，但 004 文档未说明该配置在何处、如何覆盖（调用方 `MeetingWorkspacePage.tsx:70` 目前也未传 `siteUrl`/`mcpUrl`），建议补一句落点。
8. **旧版本下载与技能的一致性**：`latest/SKILL.md` 属可变入口，`SKILL.md` 内容与版本绑定，而 `@latest` 的缓存策略只说“须重新验证或不缓存”。建议明确技能内部包含版本自检（`capabilities` 已含本地版本，可在技能中要求比对 manifest），避免“旧技能 + 新 CLI”组合无人负责。
9. **`maximumWaitTimeoutSeconds` 是名义字段而非独立配置**：实测该值没有独立配置项，直接取 `WaitTimeoutSeconds`（默认 300），且校验要求 `ReliableWaitTimeoutSeconds ≤ WaitTimeoutSeconds`（`AgentRecoveryOptions.cs:27-28,39`）。设计把两者并列写入 manifest 兼容范围与 `capabilities`，容易让人误以为最大等待可独立于服务端等待配置调整；建议注明二者的实际关系。
10. **服务端没有会话关闭回执**：服务端无 leave 工具、无自建 DELETE 处理，关闭依赖 MCP DELETE（SDK 提供）且只在校验中体现为连接转 `Disconnected`（`McpAgentContractTests.cs:531-549`）。因此 15.1“停止时…关闭远程会话并报告实际结果”只能实现为“DELETE 调用结果 + 本地状态推断”，技能不应向 Agent 承诺服务端回执式的关闭确认。

---

## 4. 实施风险

1. **路由与静态文件改造风险（高）**：静态目录映射、fallback 排除、`/mcp` 端点、`/api` 兜底 404 之间存在顺序耦合；改动 `Program.cs` 属于高风险区，必须补齐 002 已建立的回归（P-004 子路径刷新、API 404、发布冒烟）。
2. **版本与发布链路改造风险（高）**：本次要把“前端构建 + 后端发布 + 技能 + CLI + 安装器 + manifest + checksums”聚合为一个 Release，而现有 `publish.ps1` 只做前端构建与 `dotnet publish` + 复制 wwwroot，且无版本概念。该改造会触及 `.gitignore`、发布脚本、部署方式（现网为 IIS 进程内托管）与既有发布检查；且单实例约束在实现层只有 SQLite WAL + `busy_timeout=5000`，**没有任何启动锁或单写互斥**（`SqliteDatabaseInitializer.cs:40-58`），所以“不通过新旧实例并行写同一 SQLite”目前只是文档约束，切换流程必须写成停机步骤，不能假设有互斥保护。
3. **CLI/Runtime 是全新进程模型（中高）**：常驻进程 + 短命令客户端 + 本地 IPC + 状态文件 + 单实例锁 + 崩溃恢复，跨平台（`install.sh` 暗示 Linux/macOS 支持）时权限模型差异大；设计尚未承诺支持平台清单（“实施时结合构建环境确定”）。
4. **CP 与超时耦合风险（中）**：Runtime 常驻并独立续租，但外部模型可能长时间推理。007 已定义确认时限与租约；若 Runtime 把“已确认”与“已回复”混同，或未在推理期间持续 `renew_agent_lease`，会出现租约过期→`resume`→尝试失效的连锁。AI-PKG009 覆盖了这一点（P1），但需要有明确的实现契约支撑。
5. **磁盘与状态增长（中）**：Runtime 要“可靠保存上下文再确认”，但未定义上下文与状态的保留上限与清理策略；长驻留 + 大字符预算（12000 起）会持续增长。
6. **术语与结构风险（低）**：新增文档段落以“待完成/待执行”追加在既有完成门之后（见 P2-9），在被其他执行者或模型阅读时容易被误判为“尚未完成整体设计”，从而绕过既有结论。

---

## 5. 文档一致性与结构问题

1. **失效引用**：`006/impl.md` 第 201 行、`006/testPlan.md` 第 311 行仍写 `[MCP Agent 接入规范](ReferenceAgent/README.md)`，而该文件标题已改为“Agent 接入规范（技能、CLI 与 MCP）”。
2. **章节顺序**：`006/impl.md` 的 `## 7. 完成定义` 之后追加 `## 8. 统一发布包与 CLI/Runtime 实施（待完成）`；`006/testPlan.md` 的 `## 19. 完成门` 之后追加 `## 20. …（待执行）`。完成门/完成定义之后仍有“待完成”章节，语义上自相矛盾（完成门是否包含第 8/20 节未说明）。
3. **文档定位与内容不符**：`impl.md` 第 9 行声称“本文描述基础参会及统一发布的实现顺序和落点”，但第 1~7 节实际是基础 MCP 部分，统一发布只在第 8 节；`006/design.md` 第 567 行仍称“自动化测试矩阵、…参考 Agent 脚本…写入 …`ReferenceAgent/README.md`”，与新 README 已写好的 1.1~1.3 内容不对应。
4. **工具数量的语境问题**：`impl.md`、`testPlan.md` 头部“共 9 个工具（既有 4 个、可靠专属 5 个）”出现在 legacy 基础实现文档中，容易被读成“本主题需实现 9 个工具”。建议明确“其中 4 个属本主题，5 个由 007 提供”。
5. **需求层次倒置**：需求分析第 5.8 节写入了下载入口、安装脚本、manifest、校验文件等实现机制。按仓库约定，需求分析应确认“需要什么”，统一发布包属实现选择，宜留在总体设计与主题设计，需求侧最多保留“Agent 接入需要可自助安装、版本一致、可离线校验”的产品承诺。
6. **需求未吸收的既有承诺**：需求第 5.7 节“显式请求 retry 后重新开始重试周期”未与 006 第 15 节的 `retry` 建立引用；需求侧也未提及租约、显式确认、删除恢复等 007 交付物，`docs/需求分析/圆桌会议网站需求.md` 第 403 行仍称“首版暂不保留待确认的产品问题”。
7. **001 内部措辞不统一**：第 120 行使用“CLI/Runtime 联调路径”，第 206 行使用“MCP 和 CLI 联调流程”，第 256 行使用“技能、CLI/Runtime 和版本清单”。
8. **测试计划措辞**：`testPlan.md` AI-BLD004 写“可发现四个主题五工具”，新增第 20 节写“前后端、技能及引用资源”，未区分“技能引用资源”与 `skill.zip` 的关系（design 15.2 两者都列）。
9. **DSH 报告的落位问题（供用户决策）**：新增 `DSH连接错误和修改建议.md` 位于工作树根、且未被任何规则忽略，既是本次变更的实测证据，也属于仓库文档维护策略中未定义的类别（既非需求分析、也非计划、也非 UML）。建议在冻结时决定其去向：转化为 006/007 的条款后删除，或明确其归档位置。
10. **本报告的归档位置**：按用户指定写入 `docs/plans/DSH审查报告.md`。该文件不属“设计/实现/测试”三类，也不属 `NNN-业务主题` 目录命名；如后续需要长期维护，建议移入对应主题目录或在 AGENTS.md 中补充审查报告的归档约定。
11. **“统一外部基础地址规则”是空引用**：`004/.../impl.md` 第 5 行与 `design.md` 第 18 行都要求“部署子路径时复用统一外部基础地址规则”，但该规则在 `docs/` 中没有任何定义、在代码中也没有实现（无 `VITE_*`、无 `.env*`、无 `import.meta.env`/`BASE_URL`/`basePath`；`vite.config.ts:8` 固定 `base:'/'`，服务端无 `UsePathBase`；`window.location.origin` 仅在 `apiClient.ts:18,33` 与 `InviteMeetingDialog.tsx:10` 直接使用）。建议要么删掉该措辞并明确“首版不支持子路径部署”，要么先定义规则再引用。
12. **既有测试与模板改造的耦合未列入验收**：`meetingWorkspace.test.tsx:70-88` 用 `buildAgentInvitePrompt(...)` 的返回值反推页面文本，并断言 `地址：<mcpUrl>`、`你的会议显示名称是：\n<名称>`、`会议代码是：\n<码>` 三行结构。新模板（含 `SKILL.md`、`manifest.json`、固定版本指引、CLI 循环）会直接改变这些断言；`004/.../impl.md` 第 5 节只描述新增断言，未说明既有断言必须同步改写。建议在新版组件验收中显式列出“改造既有断言”的动作，避免实施时按“新测试通过、旧测试删除”处理。

---

## 6. 冻结与实施建议

### 6.1 是否可冻结

**当前状态：不建议冻结。** 依据是 P0-1~P0-3 三项：一项会让 CLI 路径在失败场景下不可用（P0-1），一项让验收标准无法判定（P0-2），一项与现有代码事实直接冲突（P0-3）。这三项都属于“文档内可解决、但必须先解决”的问题，不需要新增技术预研；同时 P1-2（版本号与发布布局）、P1-9（回复正文一致性）属于低成本、必须提前定稿的契约项，建议一并纳入冻结门槛。

### 6.2 最小冻结路径（建议按序完成，均只改文档）

1. **补齐命令族与失败路径**（对应 P0-1、P0-2 第 3 点）：在 `006/design.md` 15.1 与 `impl.md` 第 8 节加入 `report-failure` / `retry`，并给出 `nextAction` 全量映射表（含 `await_explicit_retry`、`stop`、`resume_agent`）；`testPlan.md` 第 20 节补 1 条失败路径 P1 用例。
2. **冻结本地契约**（对应 P0-2 第 1、2 点）：在 15.1 增加本地通信方式、状态目录与权限、任务处理句柄字段与有效期、Runtime 单实例约束的最小契约表；同时写入“状态目录不得位于仓库工作树内”（P1-7）。
3. **写清下载承载**（对应 P0-3、P1-2、P1-6）：在 15.2 增加静态挂载与 fallback 排除的实现级约束、`downloads` 相对内容根的准确层级、`latest` 实现机制、版本号唯一来源与旧版本清理策略；指定承载这些内容的 Server 子文档位置。
4. **同步接入规范**（对应 P1-1、P1-3、P1-5、P1-9）：按 legacy / 可靠两列重写 README 第 3 节等待时限与故障处理；补 `connection_superseded` 等关键错误码与 `recovery_required → resume_agent` 分支；统一提示词地址清单与 AI-PKG012 断言；在 15.1 写明“正文保存即冻结、重复提交必须同正文”，并在 AI-PKG008 增加对应断言。

第 5 节的文档一致性问题（失效引用、章节顺序、标题、空引用“统一外部基础地址规则”、既有测试断言改写）可与上述 4 项一并机械修正，不必单独冻结。

### 6.3 可以立即并行启动、不受上述阻塞影响的工作

- AI-PKG001/003/005 中与契约无关的打包、目录边界、安装器工程骨架；
- 006 既有基础 MCP 与 007 可靠能力的回归确认（用于后续 AI-PKG010 的基线）；
- `.gitignore` 与发布脚本的版本化改造（属于 P1-2 的落地准备，不依赖文档冻结）；
- 邀请提示词纯函数的测试改造准备（`invitePrompts.ts` 的签名扩展方向已明确，但最终地址清单需等 6.2 第 4 项定稿）。

### 6.4 与 DSH 实测反馈的关系

`DSH连接错误和修改建议.md` 的问题清单在本次设计中已被覆盖的部分：P2（宿主无 MCP 客户端）→ 15.1 技能 + CLI；P4（可靠模式三件套）→ 15.1 引用 007；P5（业务连接绑定协议会话）→ 常驻 Runtime 单会话；P7（凭证丢失后的僵死状态）→ 15.1 `resume` / 显式 `takeover`。

尚未被覆盖的部分：P3（初始化通知 500 的处置）、P8（自回复循环防护）、P9（进程清理误杀）以及 S5/S6/S7 的工程约定（见第 3 节第 2、3、4 条）。建议在完成 6.2 的同时，把 P3/P8 明确写入技能或接入规范的已知偏差与强制条款，使“实测发现的问题”与“设计冻结的条款”一一对应，避免同一问题在实施阶段再次出现。

---

## 7. 审查证据索引

| 结论 | 主要证据 |
| --- | --- |
| O-1 静态路由冲突 | `Server/RoundTableMeeting/Program.cs:88-89,106-131`（排除清单 :111-115 不含 `/downloads`） |
| O-1b 静态文件仅认 wwwroot | `Program.cs:122`；全仓无 `PhysicalFileProvider`/`StaticFileOptions`；发布产物无 `downloads/`（`.gitnexus/008-publish-final/web.config`） |
| O-2 无统一版本号 | `Server/Directory.Build.props`、`Server/RoundTableMeeting/RoundTableMeeting.csproj`、`Client/roundTableMeeting/package.json:4`、`publish.ps1:6`、`.gitignore:91`；协议标识硬编码为 `agent-messaging-v1`（`Hosting/ServiceCollectionExtensions.cs:68-69`） |
| O-3 9 个工具清单 | `Server/RoundTableMeeting/AgentCollaboration/McpAgentTools.cs:16,30,39,47,56,65,74,82,90` |
| O-4 等待时限契约 | `McpAgentTools.cs:30-37`、`AgentRecoveryOptions.cs:8-15,27-28,39`、`Server/Tests/.../TestApplicationFactory.cs:113-116`、`007/design.md:165,191,274,300,316` |
| O-5 错误码全集与 nextAction | `007-AI任务连续性与接管/design.md:287,347-403` |
| O-6 接管承诺不一致 | `docs/需求分析/圆桌会议网站需求.md:48,72,223,225`、`006-AI参会/design.md:475`、`007/design.md:360`、`AgentRecoveryContracts.cs:10-11`（`joinMode` 无 `resume`） |
| O-7 提示词地址清单不一致 | `006/design.md:460`、`004/.../InviteMeetingDialog/design.md:18`、`004/.../InviteMeetingDialog/impl.md:5`、`006/ReferenceAgent/README.md:21-37` |
| O-8 客户端现状 | `invitePrompts.ts:1-13`、`components/InviteMeetingDialog.tsx:10`、`vite.config.ts:8,14`、`api/client/apiClient.ts:33` |
| O-9 幂等与正文一致性 | `AgentReplyIdempotencyKeyFactory.cs:7`、`AgentCollaborationService.cs:328-414`、`AgentReliableTaskService.cs:80-116` |
| O-10 会话关闭无服务端回执 | `McpAgentTools.cs`（无 leave）、`Tests/.../McpAgentContractTests.cs:531-549` |
| O-11 单实例无实现级互斥 | `SqliteDatabaseInitializer.cs:40-58`、`006/testPlan.md:384`、`008/design.md:165` |
| O-12 空引用与既有测试耦合 | `004/.../impl.md:5`、`004/.../design.md:18`、`vite.config.ts:8`、`apiClient.ts:18,33`、`meetingWorkspace.test.tsx:70-88`、`MeetingWorkspacePage.tsx:70` |
| O-13 实测证据 | `DSH连接错误和修改建议.md:225,288-290` |

---

**审查结论**：变更方向正确、覆盖了 DSH 实测暴露的主要能力缺口；但存在 3 项 P0 与 12 项 P1 问题，其中 P0-1~P0-3、P1-1、P1-2、P1-9 均已由仓库现有代码、测试或脚本证实，属于必须在冻结前修订的契约冲突或覆盖缺口。建议按第 6.2 节的 4 项文档修订完成后再冻结并启动实施；修订完成前，仅启动第 6.3 节列出的不依赖冻结的工作。
