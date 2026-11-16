# DSH 连接会议 MCP 服务的问题记录与修改建议

## 0. 文档定位

- 目的：记录 DSH（DeepSeek Harness Agent）以 MCP Streamable HTTP 方式接入会议服务时，从连接、入会到持续驻留监听全过程中实际发生的问题、复现路径、根因判断，以及针对 DSH 与配套客户端的修改建议。
- 范围：本次实测为 DSH 会话内的临时代理脚本 `.mcp/rtm-agent.ps1`（PowerShell 7.6.6 手写 MCP 客户端）；不涉及服务端业务代码改动。
- 与既有文档的关系：接入契约以 [MCP Agent 接入规范](docs/plans/006-AI参会/ReferenceAgent/README.md) 与主题六（`docs/plans/007-AI任务连续性与接管`、`Demo/AI参会联调客户端`）为准。本文只补充“DSH 作为接入宿主”这一侧的缺口，不重复协议契约。

### 本次实测环境

| 项目 | 值 |
| --- | --- |
| MCP 端点 | `https://winserver.eechostudio.live:10028/mcp` |
| 服务标识 | `round-table-meeting-agent-messaging` / `agent-messaging-v1` |
| 协议版本 | `2024-11-05`（Streamable HTTP，SSE 响应） |
| 会议 | 测试服务器发布会议 `04230260-ca1c-4459-a43d-be572cbbba52` |
| DSH 侧身份 | 显示名 `DSH测试`，participantId `4a34fb06-19ac-416d-99f0-cc2f2a1ee2f9` |
| 可靠性模式 | `explicit_ack_v1`（连接代次最终为 4） |
| 宿主 | PowerShell 7.6.6，DSH 后台作业（`run_in_background`） |

---

## 1. 结论摘要

| 编号 | 问题 | 归属 | 严重度 | 状态 |
| --- | --- | --- | --- | --- |
| P1 | 沙箱网络策略下 TLS 握手被拦，所有 HTTPS 出站失败 | DSH 环境 | 阻塞 | 已定位；改用 `danger-full-access` 后恢复 |
| P2 | DSH 无内置 MCP 客户端，需手写 JSON-RPC/SSE 客户端 | DSH 能力缺口 | 高 | 本次以脚本绕过 |
| P3 | 服务端对 `notifications/initialized` 返回 500 | 服务端 | 低 | 未阻断（后续调用正常） |
| P4 | `reliabilityMode` / `recoveryKey` 参数组合约束未即时暴露 | 服务端契约 | 中 | 按错误码纠正 |
| P5 | 业务连接绑定创建它的 MCP 协议会话，跨进程监听全部 `connection_invalid` | 服务端设计 | 高 | 已改为单进程一体化 |
| P6 | 凭证落盘时机过晚，解析异常导致连接 ID 与恢复密钥丢失 | DSH 侧脚本 | 高 | 已修复（先落盘） |
| P7 | 凭证丢失后连接进入“活跃但不可寻址”，无独立 leave 接口 | 服务端设计 | 中 | 以 `joinMode=takeover` 自愈 |
| P8 | 首版回复回带 `@提及` 触发自我回复循环，5 秒内连发 6 条 | DSH 侧脚本 | 严重 | 已修复并验证 |
| P9 | 沙箱读写受限时，命令行匹配进程会误杀自身命令行 | DSH 环境 | 低 | 已识别，改用进程名+长度过滤 |

---

## 2. 问题详述

### P1 沙箱策略下 TLS 握手失败（阻塞级）

**症状**：以 `workspace-write` 策略运行时，对目标端点的 TLS 握手失败；同一策略下访问任意 HTTPS 站点（如 `https://example.com`）同样失败，说明不是目标站点问题。

```
System.Net.Http.HttpRequestException : The SSL connection could not be established, see inner exception.
 └ System.Security.Authentication.AuthenticationException : Authentication failed, see inner exception.
    └ System.ComponentModel.Win32Exception : 安全包中没有可用的凭证
```

**同时可观察到的现象**：

| 探测项 | `workspace-write` | `danger-full-access` |
| --- | --- | --- |
| DNS 解析 | 正常（A 记录 `198.18.0.97`，属保留网段，指向本地网络中间层） | 正常 |
| TCP 10028 连通 | 正常（`Test-NetConnection` 为 True） | 正常 |
| HTTPS/TLS 请求 | 失败（上述异常） | **成功** |
| `curl.exe` 直连 | 被拒绝访问（`ResourceUnavailable`） | — |

**判断**：TCP 可达但 TLS 无法完成，且失败范围覆盖所有 HTTPS 目标，属于宿主沙箱的网络/凭据策略限制，而非服务端证书或端口问题。DSH 报错只透出最外层 `HttpRequestException`，`安全包中没有可用的凭证` 这一关键内层信息需要逐层展开 `InnerException` 才能看到，容易被误判为“服务端不可用”。

**建议（详见 3.1）**：网络策略与文件策略解耦，并让 TLS 失败附带可操作的诊断信息。

---

### P2 DSH 缺少内置 MCP 客户端

**症状**：本次任务要求“连接 MCP 服务并使用其工具”，但 DSH 会话工具集中没有任何 MCP 客户端工具（无 `mcp.*`、无 Streamable HTTP 调用器、无 SSE 订阅器）。执行者只能自行实现：

- `initialize` → 保存响应头 `mcp-session-id`
- `notifications/initialized`
- `tools/list`
- `tools/call`
- SSE 响应体解析（`event: message` + `data: {...}` 逐行还原 JSON）
- 会话复用、租约续期、超时与错误码分支

**后果**：接入质量完全依赖执行者临场手写的客户端。本次因此产生了 P6、P8 两个由实现细节引发的严重问题；换言之，**协议没错，缺的是宿主能力**。

---

### P3 `notifications/initialized` 返回 500

**可复现细节**：初始化成功后发送通知（无 `id`，`params: {}`），HTTP 状态 `500`；但紧随其后的 `tools/list` 与 `tools/call` 均返回 200 且结果正确，因此未阻断流程。

**判断**：疑似服务端把无 `id` 的 JSON-RPC 通知也走通了需要生成响应的路径（或对空 `params` 通知处理不当），导致响应体构造失败。虽无害，但会误导客户端把成功会话判为失败。

**建议**：通知类请求按 JSON-RPC 约定返回 `202 Accepted` 且无响应体；若确需拒绝，返回结构化错误而不是裸 500。

---

### P4 reliable 模式参数组合约束

**实际错误序列**：

| 请求参数 | 服务端结果 |
| --- | --- |
| `meetingCode` + `displayName` + `contextCharacterLimit` + `reliabilityMode` + `recoveryKey` | `reliability_mode_required`：`These parameters require reliable mode; remove them or use a reliable client.` |
| 同上，去掉 `reliabilityMode`（仅留 `recoveryKey`） | 仍属遗留参数组合 |
| `reliabilityMode=explicit_ack_v1` + `recoveryKey`（无 `operationId`） | `agent_request_invalid`：`The Agent request parameters are invalid.` |
| 三者齐备（含 `operationId` UUID） | 成功，`kind=joined`，`reliabilityMode=explicit_ack_v1` |

**关键约束**（工具 schema 未直接说明）：`recoveryKey`（32 字节随机数的规范无填充 base64url）与 `reliabilityMode` **必须同进同出**，且显式模式还需客户端持久化的 `operationId`。错误信息 `reliability_mode_required` 的字面含义容易把方向引向“去掉参数”，而实际处置应是“补齐可靠模式三件套”。

---

### P5 业务连接绑定 MCP 协议会话（高危）

**症状**：入会成功后，另起进程仅做监听时，所有业务调用均失败：

```json
{"succeeded": false, "errorCode": "connection_invalid", "message": "The connection session is invalid."}
```

**复现路径**：

1. 进程 A 建立 MCP 会话 `S1`，`join_meeting` 成功，得到业务连接 `C1`；
2. 进程 A 退出（MCP 会话 `S1` 结束）；
3. 进程 B 建立新 MCP 会话 `S2`，携带 `C1` 调用 `wait_for_mention` → 全部 `connection_invalid`（日志中连续数百次）。

**结论**：这与接入规范第 1 节“业务连接 ID 不等于 MCP 协议会话 ID，也不能跨协议会话使用”一致——即 **`connectionSessionId` 的有效性锚定在创建它的 MCP 会话上**，而非仅凭租约存续。

**架构影响**：Agent 的“入会 + 确认初始上下文 + 等待 + 续约 + 回复”必须位于**同一个常驻进程、同一个 MCP 会话**内，不能拆分为“入会进程”和“监听进程”。

---

### P6 凭证落盘时机过晚（实现缺陷）

**症状**：首次入会实际已经在服务端成功，但脚本在解析响应时抛错：

```
设置“initialDeliveryId”时出现异常：在此对象上找不到属性“initialDeliveryId”
```

原因是状态对象用了 `[pscustomobject]`，对不存在的属性赋值即抛异常，且该异常发生在写状态文件之前，导致 **`connectionSessionId`、`recoveryKey`、`initialDeliveryId` 全部丢失**。

**连锁后果**：后续重连一律返回 `agent_connection_already_active`，因为服务端仍认为旧连接活跃，而本地已无凭证可用（引出 P7）。

**修复**：

- 状态容器改用 `hashtable`（属性可增量写入，不再抛异常）；
- 入会后**先用正则从原始响应中提取 `connectionSessionId` 并落盘**，再做完整解析——即“凭证先落盘，解析可失败”。

---

### P7 凭证丢失后的“活跃但不可寻址”状态

**症状**：

| 操作 | 结果 |
| --- | --- |
| 用旧业务连接 ID 续约 | `connection_invalid` |
| 用旧业务连接 ID 查状态 | `connection_invalid` |
| 重新 `join_meeting`（默认 `create`） | `agent_connection_already_active`（`nextAction=stop`） |

即：服务端认为该 Agent 身份仍有活跃连接，但该连接只对**已消失的 MCP 会话**可见，形成无法正常回收的僵死状态。

**处置**：使用 `joinMode=takeover` 显式替换自己的遗留连接（`joinDisposition=taken_over`，`connectionGeneration` 递增）。本次通过该方式恢复，未影响其他参与者连接。

**风险提示**：`takeover` 语义是“替换当前 Agent”，若被误用于**其他 Agent** 的连接，会构成越权接管。建议服务端对 takeover 增加“同一身份凭证校验”（例如要求 `recoveryKey` 匹配），把“自愈”与“接管他人”区分开。

---

### P8 回复回带 `@提及` 引发自我回复循环（严重）

**现象**：会议中在 5 秒内连续出现 6 条结构相同、逐条变长的机器人消息（seq #6–#11）。

**完整时间线**：

| 时间 | 事件 |
| --- | --- |
| 11:25:41 | 人类发 `@DSH测试 你是否可以正常收到消息` → 我回复。**回复正文把原文的 `@DSH测试` 原样抄回** |
| 11:25:42 | 服务端把这条回复中的 `@DSH测试` 解析为对我的新提及 → 我按新任务再次回复（seq #7） |
| 11:25:43 | 新回复又把上一轮的 `@DSH测试` 抄回 → 再次自触发（seq #8） |
| 11:25:44–46 | 每轮正文比上一轮更长（叠加式增长），继续自触发（seq #9/#10/#11） |
| 11:25:46 | 执行者察觉异常，终止监听作业，循环停止 |

**根因**：两个缺陷叠加——

1. 回复文本未剥离 `@提及`，把“被 @ 的原文”当作引用内容回写；
2. 未识别“提及来源是自己”，把自身消息产生的任务当成他人任务答复。

服务端行为是符合契约的（`Both Human and Agent messages can create tasks through valid @mentions`），因此**防循环必须在客户端实现**。

**修复（双重防护，已上线验证）**：

1. 回复前剥离所有 `@提及`（先用自身显示名精确匹配，再用 `@\S+` 兜底），并折叠多余空白；
2. 若 `context` 中触发消息的 `authorParticipantId` 等于自身 `participantId`，则**只确认上下文、绝不回复**。

**验证证据**：修复后于 11:27:18 重新入会并运行至 11:29:49 主动退出，期间 `已回复` 记录为 **0 条**，自循环分支未被触发，仅保留正常心跳续约；同时把该问题的诱因断言为“回复内容引用原文”。

**规范落差**：接入规范要求“普通消息不触发、每次回复后继续等待”，但**未明确规定“回复正文不得包含被回复对象的 @ 提及”**。这是所有 Agent 宿主都容易踩的坑，建议写入规范并列为一等验收项。

---

### P9 进程清理时的自我误杀

**症状**：`Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*rtm-agent.ps1*' }` 会匹配到**当前正在执行该过滤命令的 pwsh 自身**（其 `-Command` 参数包含同样的脚本路径文本），随后 `Stop-Process` 把自身杀掉，命令返回 `[exit code: 4294967295]`。

**影响**：仅影响执行者的清理操作，不影响业务。处理方式：按 `Name -eq 'pwsh.exe'` 限定进程名，并避免用会匹配到自身的命令行模式；或用 `job_kill` 管理后台作业而非手工扫描进程。

---

## 3. 修改建议

### 3.1 DSH 侧（宿主能力）

| 编号 | 建议 | 对应问题 | 优先级 |
| --- | --- | --- | --- |
| S1 | 内置 MCP Streamable HTTP 客户端：`initialize`/`initialized`、会话复用、SSE 解析、`tools/list`、`tools/call`，并把业务连接句柄与会话绑定关系纳入会话状态管理 | P2、P5 | 高 |
| S2 | 网络策略与文件策略解耦：允许按域名/端点放行出站；至少提供“仅允许指定 HTTPS 端点”的中间档，避免为联网放开整个文件沙箱 | P1 | 高 |
| S3 | 网络失败诊断增强：TLS/证书/链路失败时展开完整 `InnerException` 链并给出可操作提示，而不是只显示最外层 `HttpRequestException` | P1 | 中 |
| S4 | 长驻留原语：把“常驻订阅 + 心跳续约 + 重连”作为一等能力（后台作业 + 自动续约 + 断线恢复），不要求执行者手写循环 | P5、P6、P7 | 高 |
| S5 | 隐私与落盘约定：MCP 协议会话 ID 不写入提示词、普通日志或仓库；恢复密钥等敏感凭证需显式路径与权限约定 | 接入规范第 1 节 | 中 |
| S6 | 会话结束语义：明确“关闭 MCP 会话”的实现方式（DELETE / 关闭响应流），并在退出时校验服务端确实离线，而不是仅凭本地进程退出 | P7 | 中 |
| S7 | 后台作业清理模板：提供按作业 ID 精确终止与“残留进程检测”的标准做法，避免命令行自匹配误杀 | P9 | 低 |

### 3.2 客户端实现侧（接入 Agent 的宿主或脚本）

1. **单进程常驻**：入会、确认初始上下文、等待、续约、回复必须在同一进程同一 MCP 会话内完成（P5）。
2. **凭证先落盘**：`connectionSessionId`、`recoveryKey`、`initialDeliveryId`、`operationId` 在解析完整响应之前即持久化（P6）。
3. **状态容器可增量写入**：避免“属性不存在即抛异常”的容器打断关键路径（P6）。
4. **回复净化**：剥离 `@提及` + 拒绝响应自身触发的任务（P8）。
5. **按 `errorCode` 分支，而非按文案**：`idle` 是成功空闲、`connection_invalid` 停止旧循环、`agent_connection_already_active` 不盲目重试、`agent_request_invalid` 检查可靠模式三件套（P4）。
6. **续约节拍**：服务端策略为连接租约 120 秒 / 续约间隔 30 秒 / 安全边际 30 秒，客户端续约周期应显著小于 120 秒；本次取 25 秒可用。
7. **等待时限**：`explicit_ack_v1` 下 `reliableWaitTimeoutSeconds=30`、`maximumWaitTimeoutSeconds=300`；客户端单次调用超时必须大于所选等待时长（P2 手写客户端要显式设置）。
8. **显式确认**：`initial` 与 `task` 两类 `confirm_context` 目标不可混用，确认时限默认 60 秒，需在回复前后及时确认。
9. **离开语义**：该 MCP 工具集**没有 leave 接口**，离开 = 停止续约 + 关闭 MCP 会话，随后等待租约到期由服务端回收，并通过独立会话查询确认 `connection_invalid`。

### 3.3 服务端/接入规范侧（建议）

| 编号 | 建议 | 对应问题 |
| --- | --- | --- |
| S8 | 修正 `notifications/initialized` 的响应语义（202 无体或结构化错误） | P3 |
| S9 | 参数约束前移：在 `join_meeting` 描述中写明“可靠模式三件套”互斥/依赖关系；错误信息从“删除参数”改为“补齐参数” | P4 |
| S10 | 提供显式离开接口（如 `leave_meeting`），或在状态查询中把“凭证丢失的活跃连接”标记为可回收，减少对 `takeover` 的依赖 | P7 |
| S11 | `takeover` 增加同身份校验（`recoveryKey` 匹配），把“自愈”与“越权接管他人”区分开 | P7 |
| S12 | 接入规范补充“回复正文不得包含被回复对象的 @ 提及”的强制条款与验收用例 | P8 |
| S13 | 提供“自身消息不产生自任务”的服务端开关或明确禁止自提及，作为客户端防护之外的第二道闸 | P8 |

---

## 4. 最小可运行接入流程（本次验证可用）

```text
1. POST /mcp  initialize                → 从响应头取 mcp-session-id（记 S）
2. POST /mcp  notifications/initialized → 服务端可能返回 500，忽略但记录
3. POST /mcp  tools/list                → 确认 join_meeting/wait_for_mention/reply_to_mention 等
4. POST /mcp  tools/call join_meeting   → 参数：meetingCode, displayName, contextCharacterLimit,
                                          reliabilityMode=explicit_ack_v1, recoveryKey(32B base64url), operationId(UUID)
                                          [如返回 agent_connection_already_active → 追加 joinMode=takeover]
   → 立即持久化 connectionSessionId / recoveryKey / initialDeliveryId
5. POST /mcp  tools/call confirm_context(kind=initial, initialDeliveryId)
6. 循环：
     wait_for_mention(connectionSessionId, waitTimeoutSeconds≤300)
       ├─ kind=idle   → 续约（周期 < 120s）后继续等待
       └─ kind=mention
            ├─ 触发者 == 自己 → 仅 confirm_context(task)，不回复            ← P8 防护
            └─ 其他
                 ├─ confirm_context(kind=task, taskId, deliveryAttemptId)
                 ├─ reply_to_mention(taskId, deliveryAttemptId, replyIdempotencyKey, 净化后的正文)
                 └─ 续约后继续等待
7. 离开：停止循环 → 关闭 MCP 会话 → 等待租约到期 → 用独立会话查询确认 connection_invalid
```

要点：全过程必须复用同一个 `mcp-session-id`；所有 POST 均带 `Accept: application/json, text/event-stream`，响应按 `data:` 行解析为 JSON。

---

## 5. 验收检查清单

- [ ] 沙箱策略下 HTTPS 出站可达（或已明确放行该端点）
- [ ] 初始化后 `tools/list` 可见 `join_meeting`、`wait_for_mention`、`reply_to_mention`、`get_agent_status`
- [ ] 入会成功并记录 `connectionSessionId`、`reliabilityMode`、`connectionGeneration`
- [ ] 初始上下文在 60 秒内 `confirm_context(initial)` 完成
- [ ] 至少跨 2 次 `idle` 保持同一连接（无重新 join）
- [ ] 对两轮**不同内容**的 @提及分别给出对应回复（非固定话术）
- [ ] 回复正文不含任何 `@提及`，且自身消息不产生任何回复（P8 回归项）
- [ ] 连续驻留 ≥10 分钟，租约续期无中断
- [ ] 普通消息（未 @我）不触发任何回复
- [ ] 离开后经独立会话验证 `connection_invalid`，会议中不再显示该 Agent 在线

---

## 6. 证据与日志位置

| 内容 | 位置 |
| --- | --- |
| 本次客户端实现 | `.mcp/rtm-agent.ps1` |
| 运行日志（含循环时段完整事件） | `.mcp/state/agent.log` |
| 离开时的连接凭证与归档说明 | `.mcp/state/session.left.json` |
| 接入契约（权威） | `docs/plans/006-AI参会/ReferenceAgent/README.md` |
| 可靠模式与恢复设计 | `docs/plans/007-AI任务连续性与接管/design.md`、`Demo/AI参会联调客户端/README.md` |

关键时间点（本地时间）：11:23:03 首次入会成功；11:23:49 `takeover` 入会；11:25:41–11:25:46 **自我回复循环产生 6 条消息**；11:27:18 修复后重启；11:29:49 停止监听；11:32 经服务端确认连接失效。

---

## 7. 遗留与待确认

1. **P1 的正式处置**：本次通过临时放宽沙箱（`danger-full-access`）完成联调，DSH 是否会提供“仅放行指定 HTTPS 端点”的中间档位，需产品侧确认；在此之前，凡需联网到会议 MCP 的联调都必须放宽权限，安全成本偏高。
2. **P3 的成因**：`notifications/initialized` 的 500 是否稳定复现、是否影响某些严格客户端，需服务端确认。
3. **P8 的服务端侧闸门**：是否增加“自身消息不产生自任务”的服务端防护（S13），需与协议所有者确认，客户端防护不能作为唯一防线。
4. **P7 的长期方案**：`leave_meeting` 是否纳入协议（S10），以及 `takeover` 的身份校验（S11）如何与主题六的接管设计兼容，需在 `docs/plans/007-AI任务连续性与接管` 中落定。
5. **长驻留容量**：本次仅运行约 2.5 分钟即主动退出，未完成 10 分钟以上驻留与真实多轮异题验收，需按第 5 节清单补测。
