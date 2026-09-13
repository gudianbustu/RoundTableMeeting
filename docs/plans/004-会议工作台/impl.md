# 会议工作台主题实现方案

## 1. 文档定位

本文把[会议工作台主题设计](design.md)拆解为可执行的服务端、客户端、数据库、生成代码和联调步骤。服务端具体文件与管道见[服务端实现方案](Server/impl.md)，会议工作台应用模块见[服务端模块实现方案](Server/Modules/MeetingWorkspace/impl.md)，客户端工程见[客户端实现方案](Client/impl.md)，客户端模块、页面和组件见各目录下的 `impl.md`，完整验证矩阵见[测试方案](testPlan.md)。

本文不记录实施日志，不提前实现人类消息、实时连接、MCP Agent 或 AI 任务。

## 2. 实施前提

### 2.1 已有基线

直接扩展 002 系统框架和 003 账号访问主题已经建立的正式工程：

- 服务端：`.NET 10`、ASP.NET Core Controller、Application/Domain/Abstraction/Repository.Sqlite 分层、EF Core SQLite、显式 `IUnitOfWork`、ProblemDetails、Serilog、OpenAPI 和 Scalar。
- 客户端：React、TypeScript、Vite、Ant Design、React Router、Kiota、公共 API Client、统一 `ApiError` 和 Vitest。
- 开发联调：Vite 以 `http://localhost:5173` 提供页面并代理 `/api` 到 ASP.NET Core；服务端 HTTP 端口沿用 002 配置。
- 生产形态：ASP.NET Core 同源托管 Vite 构建产物。

本主题不新建横向 Infrastructure 项目，不改写认证 Cookie 解析，不在组件中直接调用生成 Client。若 003 用户管理增量尚未落地，实施阶段需先按 003 已冻结文档补齐通用敏感响应头能力，再接入会议 Controller；这属于共享前置能力，不单独建立新主题。

### 2.2 本主题交付物

- `Meeting`、`MeetingParticipant`、`MeetingCreationReceipt` 领域实体和仓储契约。
- 会议、参与者、创建回执 Migration 和 SQLite 映射。
- Guid 会议码生成、标准 `D` 格式解析、唯一索引和邀请信息权限边界。
- `IMeetingTransactionRunner` 同连接事务实现、Active/Deleting 删除屏障和可恢复删除 HostedService。
- 创建、列表、详情、邀请码、加入预检、加入确认和删除 HTTP API。
- 认证后加入限流 filter、逐端点 `RequireBrowserOrigin`/JSON 正文策略和敏感响应禁缓存中间件。
- OpenAPI 明确 DTO、错误响应和 `Retry-After`，以及可生成的 Kiota Client。
- `meetingWorkspace` 客户端 feature、工作台路由、会议列表、创建/加入/删除流程、参与者静态列表和消息区空插槽。
- Domain/Application/Repository/Integration/Architecture/Vitest/浏览器验收测试。

## 3. 阶段总览

| 阶段 | 目标 | 主要交付物 | 完成门 |
| --- | --- | --- | --- |
| 0 | 冻结实现契约 | DTO、错误码、文件清单、配置键、迁移策略 | 与 design/testPlan 一致，无消息/AI 功能混入 |
| 1 | 领域和持久化 | Meeting、Participant、CreationReceipt、映射、Migration | 空库迁移和唯一索引通过 |
| 2 | 会议码和事务基础 | Guid 规则、TransactionRunner、TimeProvider、Options | 会议码生成/解析、同连接事务、取消和回滚测试通过 |
| 3 | 会议应用用例 | 创建、列表、详情、预检、加入、邀请、删除 | Application 测试通过，未认证和成员边界明确 |
| 4 | Web API 和删除恢复 | Controller、授权、Origin、限流、敏感响应头、HostedService | HTTP ProblemDetails、并发、删除恢复通过 |
| 5 | OpenAPI 与 Kiota | 生成契约、错误映射、Retry-After | `generate:api` 成功，字符串枚举和双成功状态可用 |
| 6 | 客户端模块与工作台 | feature、路由、Hook、页面和组件 | Vitest 和真实 API 页面流程通过 |
| 7 | 集成、浏览器与回归 | 构建、发布、CDP、全主题回归 | P0/P1 全通过，主题可冻结验收 |

## 4. 阶段实施步骤

### 4.1 阶段 0：实现契约冻结

1. 从 design.md 固定 HTTP 路由、DTO 字段、错误 `code`、字段错误键和状态码。
2. 固定会议码由 `Guid.NewGuid()` 生成，数据库使用 Guid 列和唯一索引，接口按标准小写 GUID `D` 格式返回。
3. 固定 Domain/Application 负责长度验证；SQLite `HasMaxLength` 仅为映射声明。
4. 固定 `ParticipantDto.type` 的传输值为 `human | agent`，不让成功 DTO 输出内部生命周期枚举。
5. 固定创建请求 `CreationRequestId`、Active/Deleted 回执、30 天 Deleted 回执保留。
6. 固定删除成功为 `202 Accepted`，重复或不可见删除为 `404 meeting_not_found`。
7. 固定创建者会议内显示名称使用创建时账号昵称，创建表单不增加自定义字段。

验收：design.md、本文、Server/Client impl.md 和 testPlan.md 使用同一契约。

### 4.2 阶段 1：领域和 SQLite 持久化

1. 在 Domain 创建 `MeetingLifecycleState`、`ParticipantType`、`Meeting`、`MeetingParticipant`、`MeetingCreationReceipt`。
2. 集中实现会议名称、说明、显示名称和会议码输入规则；会议码只 Trim 首尾空白并严格按 GUID `D` 格式解析，Human 的 `UserId` 非空，未来 Agent 的 `UserId` 为空。
3. 在 Domain 创建 `IMeetingRepository`、`IMeetingParticipantRepository`、回执查询契约。
4. 在 Repository.Sqlite 增加 DbSet、配置和 Migration。
5. 建立 `Meetings.MeetingCode` Guid 唯一索引、创建回执 Guid 主键与 `(CreatorUserId, CreationRequestId)` 业务唯一索引、参与者过滤唯一索引和名称无过滤唯一索引。
6. 会议和参与者外键使用 Restrict；删除协调器显式幂等清理。
7. 为 Deleted 回执保存 `ExpiresAtUtc`，不保存用户内容。

验收：空临时 SQLite 可迁移；长度、外键、唯一索引、NoTracking 和 Migration Snapshot 测试通过。

### 4.3 阶段 2：会议码、时间和事务

1. 在 Web 组合根注册 `TimeProvider.System`，Application 只依赖框架 `TimeProvider` 抽象，测试使用可替换单例。
2. 创建事务内调用 `Guid.NewGuid()` 生成会议码，保存为 Guid；人类成员邀请信息按标准小写 `D` 格式返回。
3. 加入输入只 Trim 首尾空白并使用 `Guid.TryParseExact(value, "D")` 或等价规则解析；大小写不敏感，不自动修复内部空白、缺失连字符或其他 Guid 格式。
4. 新增 `IMeetingTransactionRunner.ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>>, CancellationToken)`。
5. Runner 与 Scoped DbContext、物理连接、`IUnitOfWork` 共用 scope；绑定同一 EF Core transaction，禁止嵌套，异常/取消回滚。
6. 不修改通用 `IUnitOfWork` 接口；Runner 只作为会议主题能力。

验收：Guid 生成、严格 `D` 格式解析、大小写不敏感、唯一索引碰撞失败，以及 runner 同连接提交/回滚/取消/嵌套错误通过。

### 4.4 阶段 3：Application 用例

1. 实现 `MeetingWorkspaceService`，所有用例首个条件检查 `IsAuthenticated` 和非空 `UserId`。
2. 创建：读取账号昵称，事务内先查询创建回执；一致重放、内容 mismatch、Consumed、首次创建分别返回固定结果。
3. 列表：按当前参与者 `JoinedAtUtc DESC, MeetingId ASC` 返回 Active 会议。
4. 详情：成员可见、不含会议码；参与者按 `JoinedAtUtc ASC, ParticipantId ASC`。
5. 邀请信息：已入会人类成员读取并返回标准 `D` 格式会议码。
6. 预检：规范化会议码，返回最小预览和 `meetingId`，不创建参与者。
7. 加入：事务内复查 Active、按 `MeetingId + UserId` 幂等、按名称唯一约束创建 Human 参与者。
8. 删除：校验成员、创建者和确认名称，事务内 Active -> Deleting，提交后排队清理。
9. 所有 Repository/Application 异常不携带会议码、名称全文或数据库细节。

验收：Application fake 测试覆盖正常、重复、并发冲突、权限、删除屏障和取消。

### 4.5 阶段 4：Web API 和删除恢复

1. 创建 `MeetingsController`，所有既有会议入口使用 `[Authorize]` 或 FallbackPolicy。
2. 固定路由：`GET/POST /api/meetings`、`GET /api/meetings/{meetingId}`、`POST /api/meetings/{meetingId}/invitation`、`POST /api/meetings/join/preview`、`POST /api/meetings/join`、`POST /api/meetings/{meetingId}/deletion`。
3. 创建、预检、确认加入、删除使用 `RequireBrowserOrigin(true)`；邀请信息使用 `RequireBrowserOrigin(false)`。
4. 在 `UseRouting` 后、Origin/Authentication 前放置 `SensitiveResponseHeadersMiddleware`；覆盖 `/api/auth/*`、`/api/users*`、`/api/meetings*`。
5. 加入限流 filter 放在认证之后、Controller/Application 之前；未认证先 401，已认证才计数并可能 429。
6. 全部会议响应设置 `Cache-Control: no-store` 和 `Pragma: no-cache`；429 声明 `Retry-After`。
7. 注册删除恢复 HostedService：启动扫描 Deleting，运行期重试删除参与者和会议；同一服务负责清理过期 Deleted 回执。
8. 配置 JSON 字符串枚举转换，禁止整数枚举进入传输层。

验收：真实 Cookie、Origin、限流、ProblemDetails、OpenAPI、删除并发和服务重启恢复通过。

### 4.6 阶段 5：OpenAPI 与 Kiota

1. 为每个 operation 声明成功 DTO 和 400/401/403/404/409/415/429/500 响应。
2. 创建 operation 声明 201、200、`creation_request_mismatch`、`creation_request_consumed`。
3. 429 响应声明 `Retry-After` header。
4. 启动服务端，通过现有 `pnpm generate:api` staging 流程生成客户端。
5. 检查路径只有一个 `/api`，generated 文件无开发绝对 URL、无 BOM、为 CRLF。

验收：Kiota 生成成功，Client 适配可识别 200/201、ProblemDetails、字符串枚举和 Retry-After。

### 4.7 阶段 6：客户端工作台

1. 创建 `features/meetingWorkspace` 目录及公共出口。
2. 增加 `/meetings/:meetingId`，替换 `/` 的认证后占位页；保留 `/system/status`。
3. 实现 `useMeetingWorkspace`：列表/详情/邀请/创建/加入/删除状态，AbortController，按 meetingId 请求版本隔离。
4. 实现会议列表、创建对话框、加入预检和确认、会议头部、参与者列表。
5. 创建成功保存短暂邀请信息；详情切换、隐藏邀请码、删除、注销和卸载时清除。
6. 详情 404 或删除后回 `/` 无选择工作台；只有首次正常进入 `/` 且无恢复标记时自动选择第一场会议。
7. 消息区只渲染后续主题插槽空状态，不添加发送框或模拟消息。

验收：Vitest 覆盖路由、缓存、并发请求、200/201、mismatch/consumed、GUID 会议码输入、删除和移动端。

### 4.8 阶段 7：集成、浏览器和发布

1. 执行服务端全部测试、客户端全部测试、架构依赖检查和 Kiota 生成。
2. 执行 `pnpm build`、`dotnet build`、`dotnet test` 和 `dotnet publish`。
3. 使用两个账号浏览器完成创建、复制邀请码、预检、加入、切换、刷新、静态参与者刷新和删除。
4. 验证普通人类成员可见并复制邀请码，非成员无法通过深链接读取详情。
5. 验证删除后旧会议码失效、其他标签页回无选择工作台、服务重启后 Deleting 清理继续。
6. 检查 DOM、Network、Console、缓存头、Origin、状态码、无敏感日志和无布局溢出。

## 5. 命令基线

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

## 6. 完成定义

- 主题 design.md、Server/Client/模块/页面/组件 impl.md 与 testPlan.md 契约一致。
- P0/P1 测试全部通过；P2 无未批准失败。
- 会议创建、加入、列表切换、静态参与者查看、创建者邀请码和删除流程可从浏览器完成。
- 多会议数据隔离，刷新和深链接恢复正确。
- 会议码、密码、密钥、Cookie、完整用户内容不进入日志、URL、缓存或异常。
- 消息、实时通信、AI Agent 和 AI 任务没有提前实现。
- 旧 002/003 构建、测试、OpenAPI、SPA fallback 和认证回归继续通过。
