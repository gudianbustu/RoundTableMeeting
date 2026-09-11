# 账号访问与用户管理测试方案

## 1. 文档定位

本文定义 003 账号访问与用户管理主题的自动化测试、契约测试、构建回归和浏览器验收。实施顺序见[总体实现方案](impl.md)，服务端见[Server/impl.md](Server/impl.md)，客户端见[Client/impl.md](Client/impl.md)。

测试验证登录基线、管理员专属用户管理和个人设置。新增范围包括 IsAdmin、初始化、授权撤销、最后管理员保护、本人昵称与密码修改。本文是待执行验收计划，不表示新增功能或测试已完成；自助找回、复杂角色体系、审计和高级会话能力仍延期。

## 2. 测试目标

测试需要证明：

1. 账号规范化、持久化和唯一性稳定。
2. 配置种子幂等，不覆盖已有资料，失败时阻止启动。
3. 登录、当前用户和退出使用真实 Cookie 完成闭环。
4. API 未认证、无效凭证、字段验证和 Origin 拒绝使用稳定 ProblemDetails。
5. Application 不访问 Web、Options、Cookie 或 EF Core。
6. 页面刷新通过 me 恢复资料，不重新提交凭证。
7. 客户端竞态、全局 401、退出网络失败和安全返回路径行为正确。
8. 仅管理员可使用用户管理 API 和页面，所有用户可用个人设置；匿名和越权请求被拒绝。
9. 用户管理写操作遵循方法级 Origin metadata、JSON 正文策略、统一 ProblemDetails 和禁缓存契约。
10. 002 的系统状态、Kiota、构建、发布和 SPA 回归继续通过。

## 3. 环境与隔离

### 3.1 账号和秘密

- 测试账号由测试配置注入，不使用开发或生产账号。默认管理员 A、普通用户 B；并发场景显式增加管理员 C，InitialAdminUsername 明确指向 A。
- 密码仅存在于测试输入和内存配置，不写入快照、日志或测试输出。
- IntegrationTests 每个工厂使用独立临时 SQLite 文件和独立 CookieContainer。
- 不依赖测试执行顺序；每个测试可独立创建宿主。

### 3.2 HTTP 和浏览器

- WebApplicationFactory 用于大部分 HTTP 契约测试。
- 所有 Cookie 写请求测试显式添加 Origin；分别覆盖 http://localhost:5173、外部 Origin、null Origin 和缺失 Origin。
- 5173/5043 仅为仓库默认值，执行前确认端口进程来自本工作树；若被其他会话占用，按第 20 节隔离配置，不连接或终止他人服务。发布验收使用本工作树的同源宿主。
- Chrome 使用独立 Profile，CDP 只绑定 127.0.0.1。

## 4. 优先级和完成规则

- P0：身份错误、凭证泄露、未认证放行、种子破坏、登录状态错误或构建阻断，必须通过。
- P1：核心失败路径、契约和客户端状态，主题完成前必须通过。
- P2：视觉、辅助诊断和维护性检查，完成前应通过或得到明确延期批准。

所有 P0/P1 失败都阻止本轮完成。既有“普通用户可管理他人”的正向断言必须改为拒绝断言；不得仅追加测试而保留相互矛盾的开放授权预期。

## 5. Domain 测试

项目：RoundTableMeeting.Domain.Tests。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AD-001 | P0 | 创建合法 UserAccount | Guid 非空；字段保存规范化值 |
| AD-002 | P0 | 用户名前后空格 | Username 去除空格，NormalizedUsername 一致 |
| AD-003 | P0 | Unicode 组合字符等价 | NFC 后得到相同 NormalizedUsername |
| AD-004 | P0 | 用户名大小写变化 | ToUpperInvariant 后比较值相同 |
| AD-005 | P0 | 用户名规范化后为空 | 创建失败，不产生实体 |
| AD-006 | P1 | 昵称前后空格与 NFC | 保存规范化昵称，不要求平台唯一 |
| AD-007 | P0 | 实体公开 API 扫描 | 不含 Cookie、Claim、复杂角色对象、禁用或会话字段 |
| AD-008 | P1 | 用户名、昵称和规范化值超长 | 按约定长度上限拒绝，不生成可持久化实体 |
| AD-009 | P1 | 显示用户名上限与规范化长度防御 | 显示用户名最多 64；规范化值最多 128。当前 .NET ToUpperInvariant 没有稳定的多字符扩展样例可触发后者溢出，防御性分支保留但不伪造不可执行样例 |
| AD-010 | P0 | UserAccount 主键契约 | 只使用继承的 ID；实体不额外声明 UserId 属性 |

## 6. Application 测试

项目：RoundTableMeeting.Application.Tests。使用 fake 仓储、fake 当前用户上下文、fake UoW 和真实 PasswordHasher<UserAccount>；真实实现无法稳定产生的 SuccessRehashNeeded 分支允许用受控 fake 驱动。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AA-001 | P0 | 正确用户名密码 | 返回 CurrentUserDto |
| AA-002 | P0 | 用户名不存在 | invalid_credentials |
| AA-003 | P0 | 密码错误 | 与 AA-002 完全相同的外部结果 |
| AA-004 | P1 | 用户名大小写/空格/NFC 变化 | 使用统一规范化查询并成功登录 |
| AA-005 | P0 | 空用户名或密码 | validation_failed，Errors 键为 username/password，不查询仓储或哈希器 |
| AA-006 | P0 | 当前上下文匿名 | unauthenticated |
| AA-007 | P0 | 当前 UserId 对应账号存在 | 返回 userId、username、nickname、isAdmin，不含 hash |
| AA-008 | P0 | 当前 UserId 账号不存在 | unauthenticated |
| AA-009 | P0 | 首次种子两个账号 | 在同一事务创建账号并将指定 A 初始化为管理员，B 为普通用户 |
| AA-010 | P0 | 已有管理员时重复执行种子 | 不覆盖资料与管理员身份，不产生写入 |
| AA-011 | P0 | 配置内规范化用户名重复 | 初始化失败，未写数据库 |
| AA-012 | P1 | 已有管理员，一项账号存在一项缺失 | 只新增缺失的普通用户，不覆盖已有账号 |
| AA-013 | P1 | 种子取消 | OperationCanceledException 上抛 |
| AA-014 | P1 | 工作单元提交失败 | 异常上抛，不报告种子成功 |
| AA-015 | P1 | PasswordHasher 返回 SuccessRehashNeeded | 登录成功，但本主题不产生密码写回 |
| AA-016 | P0 | 用户名不存在 | PasswordVerificationService 仍执行 dummy hash 验证 |
| AA-017 | P1 | 密码包含首尾空格 | 按原字符串成功验证，不 Trim 或 NFC |
| AA-018 | P1 | 原始用户名超过 64、NFC 后不超过 64 | 通过规范化长度校验并正常查询 |

## 7. Repository.Sqlite 测试

项目：RoundTableMeeting.Repository.Sqlite.Tests，使用真实临时 SQLite。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AR-001 | P0 | 空库执行 Migration | 创建账号表和唯一索引 |
| AR-002 | P0 | 保存并按 NormalizedUsername 查询 | 返回正确账号且查询 NoTracking |
| AR-003 | P0 | 保存两个大小写等价用户名 | 唯一索引拒绝第二条 |
| AR-004 | P0 | 保存两个 NFC 等价用户名 | 唯一索引拒绝第二条 |
| AR-005 | P0 | 按 UserId 查询 | 返回 NoTracking 的 UserAccount；Repository 不生成 DTO |
| AR-006 | P1 | 重复种子后数据库行数 | 行数不增加，原昵称和 hash 不变 |
| AR-007 | P1 | Migration Snapshot | 包含字段长度和 NormalizedUsername 唯一索引 |
| AR-008 | P0 | 测试数据库隔离 | 不读取或修改开发数据库 |

## 8. 架构和 DI 测试

项目：RoundTableMeeting.ArchitectureTests 与 IntegrationTests。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AC-001 | P0 | Application 引用扫描 | 不引用 Web、Repository.Sqlite、EF Core |
| AC-002 | P0 | Application 类型扫描 | 不出现 HttpContext、ClaimsPrincipal、Cookie、IConfiguration、IOptions<AuthenticationOptions> |
| AC-003 | P0 | ICurrentUserContext 实现位置 | 实现位于 Web，Application 只有消费方 |
| AC-004 | P0 | Repository 类型扫描 | 不引用密码哈希器、Options、Cookie 或 HTTP 类型 |
| AC-005 | P0 | 服务解析 | IAuthenticationService、IUserManagementService、IAccountSeeder、IUserAccountRepository、ICurrentUserContext 可按预期生命周期解析 |
| AC-006 | P1 | 同 scope Repository/UoW/DbContext | 共享同一个 DbContext |
| AC-007 | P1 | Identity 依赖扫描 | 不注册完整 Identity Store、角色或 Identity UI |
| AC-008 | P1 | Client 依赖扫描 | 页面和组件不直接 import api/generated |
| AC-009 | P0 | 默认授权策略 | 独立 DefaultAuthorizationTestController 无授权标记时返回 401；现有 AllowAnonymous TestController 正常访问 |

## 9. HTTP IntegrationTests

TestApplicationFactory 注入临时数据库、两个测试账号、AllowedOrigins 和真实 Cookie 认证。CreateClient 时使用 HandleCookies=true；需要检查 Set-Cookie 时禁用自动重定向。测试项目实现 `ManualTimeProvider : TimeProvider` 并通过 PostConfigure 当前 Cookie scheme 的 TimeProvider 注入，可手工 Advance，无需新增时间测试包或等待真实 8 小时。现有 002 IntegrationTests 和真实进程启动测试均注入有效认证配置，只有 AI-021 故意省略。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AI-001 | P0 | 启动测试宿主 | Migration 后种入两个账号，随后开始处理请求 |
| AI-002 | P0 | 正确凭证登录 | 200，返回用户 DTO；Set-Cookie 名为 RoundTableMeeting.Auth，Path=/，HttpOnly、SameSite=Lax，无持久化 Expires |
| AI-003 | P0 | 用户名不存在 | 401 invalid_credentials，不泄露账号不存在 |
| AI-004 | P0 | 密码错误 | 与 AI-003 相同状态、code 和安全 detail |
| AI-005 | P1 | 登录字段为空 | 400 validation_failed，JSON 字段为 errors，不是 fieldErrors |
| AI-006 | P0 | 未登录 GET /api/auth/me | 401 unauthenticated，application/problem+json，无 HTML Location |
| AI-007 | P0 | 登录后 GET /api/auth/me | 200，返回同一 userId、username、nickname、isAdmin |
| AI-008 | P0 | 使用同一 Cookie 再发起 me API 请求 | 仍可通过 me，证明刷新无需重新提交凭证 |
| AI-009 | P0 | POST logout 后 me | logout 成功；之后 me 为 401 |
| AI-010 | P1 | 无会话 logout，携带允许 Origin | 204 幂等成功 |
| AI-011 | P0 | 外部 Origin 登录 | 403 origin_not_allowed，不执行凭证查询 |
| AI-012 | P0 | null Origin | 403 origin_not_allowed |
| AI-013 | P0 | 配置的 Vite Origin 登录 | 进入凭证验证并按正确凭证返回 200 |
| AI-014 | P0 | 缺失 Origin 登录 | 403 origin_not_allowed，不执行凭证查询 |
| AI-015 | P1 | 登录 Content-Type 非 JSON | 415 unsupported_media_type，不建立 Cookie |
| AI-016 | P0 | Cookie Challenge | 401 ProblemDetails，不 302 重定向 |
| AI-017 | P1 | 发送随机或签名无效的认证 Cookie | me 返回 401 unauthenticated，不抛出 500 |
| AI-018 | P1 | 账号已不存在但 Cookie 有效 | me 返回 401 unauthenticated，并返回清除 Cookie |
| AI-019 | P0 | OpenAPI 认证路径 | 包含三条 auth path、成功 DTO、400/401/403/415/500 错误响应映射 |
| AI-020 | P0 | 日志敏感信息扫描 | 不包含实际测试密码、PasswordHash 值、Cookie 头、Claim 值或完整登录请求体 |
| AI-021 | P0 | 配置为空启动真实进程 | 非零退出，端口未监听，日志不泄露秘密 |
| AI-022 | P1 | 重复启动同一数据库 | 不重复账号、不重置 hash |
| AI-023 | P1 | 系统状态匿名访问 | 仍返回框架约定状态，不泄露账号信息 |
| AI-024 | P1 | 未知 API | 仍返回 002 的 JSON route_not_found，不进入 SPA fallback |
| AI-025 | P1 | 真实登录后请求 ForbiddenTestController | 独立测试 Controller 使用始终失败的授权策略，返回 403 forbidden ProblemDetails，不 302 重定向；该端点仅用于冻结后续契约 |
| AI-026 | P1 | ManualTimeProvider 推进超过 8 小时 | me 返回 401 unauthenticated |
| AI-027 | P1 | login/me/logout 全部最终响应缓存头 | 成功、401、403、415 等最终响应均包含 Cache-Control: no-store 和 Pragma: no-cache；me 账号不存在时同时清除 Cookie |
| AI-028 | P0 | 002 独立启动失败回归 | 注入有效认证配置后，仍由目标数据库故障导致非零退出；ExceptionType 为 Microsoft.Data.Sqlite.SqliteException |
| AI-029 | P1 | 未标记未知 API | Origin 中间件不拦截未知 `/api/*`，仍返回 route_not_found |
| AI-030 | P1 | 显式 AllowAnonymous 回归 | login、logout、system/status、OpenAPI、Scalar、API 404 fallback 和现有 `/__test/*` 不受 FallbackPolicy 阻断 |
| AI-032 | P0 | 独立默认拒绝控制器 | DefaultAuthorizationTestController 无 AllowAnonymous/Authorize 标记，在 FallbackPolicy 下返回 401 |
| AI-033 | P1 | Origin 元数据顺序、正文策略和标准写方法 | 显式 UseRouting 后按方法级 RequireBrowserOrigin 元数据读取 Origin；login 元数据要求 JSON，logout 元数据不要求 JSON；后续用户管理的 PATCH 以及框架测试的 PUT/DELETE 不能被中间件硬编码拒绝；移动到 UseRouting 前的探针不能作为生产实现 |
| AI-034 | P0 | 认证管道加入后的 002 回归 | 现有 TestController 显式 AllowAnonymous，`/__test/application-part`、`/__test/throw`、`/__test/validation` 保持 200/500/400；静态文件、OpenAPI、Scalar、未知 API、SPA fallback 和非 HTML/非 GET 边界保持 002 语义 |
| AI-035 | P1 | AllowedOrigins 配置错误 | 启动失败并指出安全配置键，不打印 Origin 值或其他秘密 |

编号说明：历史 `AI-031` 的 `/__test/*` AllowAnonymous 回归已合并到 `AI-030`，不再保留重复用例；后续编号保持不重排，避免既有实现和审查引用失效。

## 10. OpenAPI 和 Kiota 测试

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AK-001 | P0 | 服务端启动后 pnpm generate:api | 生成 login、me、logout 请求器和 DTO |
| AK-002 | P0 | 生成代码 TypeScript 构建 | pnpm build 成功 |
| AK-003 | P1 | 认证错误映射 | generated operation 识别 400/401/403/415/500 ProblemDetails |
| AK-004 | P1 | URL 检查 | 最终请求只有一个 /api，不含开发绝对地址 |
| AK-005 | P1 | 服务端停止生成 | 非零退出，原 generated 目录不变 |
| AK-006 | P1 | generated 格式 | UTF-8 无 BOM、CRLF、无诊断文件 |

## 11. 客户端认证模块 Vitest

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AV-001 | P0 | Provider 初始渲染 | checking；StrictMode 下允许创建两次请求，但只有最后一个未取消请求可生效 |
| AV-002 | P0 | me 成功 | authenticated，保存用户资料 |
| AV-003 | P0 | me 401 | anonymous，不显示旧用户 |
| AV-004 | P1 | me 网络失败 | error，提供 retry |
| AV-005 | P1 | retry 成功 | 重新请求并进入 authenticated |
| AV-006 | P0 | StrictMode 重复 effect | 第一次请求被取消，旧结果不覆盖新结果，不显示错误 |
| AV-007 | P0 | Provider 登录成功 | 保存用户并返回 CurrentUserViewModel；Provider 不负责导航 |
| AV-008 | P0 | invalid_credentials | 保持 anonymous，不触发全局 401 登出事件 |
| AV-009 | P1 | 登录网络失败 | 保持 anonymous，显示安全错误 |
| AV-010 | P0 | logout 204 | 清理用户并进入 anonymous |
| AV-011 | P1 | logout 返回 403 origin_not_allowed | 保持 authenticated，显示错误；不设置匿名状态 |
| AV-012 | P0 | logout 网络失败 | 保持 authenticated，允许重试 |
| AV-013 | P0 | 全局业务 401 | 清理用户，AuthGate 导航登录 |
| AV-014 | P1 | Provider 卸载 | 取消请求和订阅，不更新卸载组件 |
| AV-015 | P1 | useAuthentication 无 Provider | 抛出明确开发错误 |
| AV-016 | P0 | me 返回 unauthenticated | 由初始化操作处理，不重复触发全局 401 事件 |
| AV-017 | P1 | me 网络失败时访问公开状态页 | system/status 仍渲染自身状态，不被认证 error 阻断 |

## 12. 页面和组件 Vitest

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| AP-001 | P0 | LoginPage checking | 不闪现登录表单 |
| AP-002 | P0 | LoginPage anonymous | 显示用户名、密码和登录按钮 |
| AP-003 | P0 | LoginPage 已认证 | 通过唯一 Navigate 导航安全 returnTo 或 / |
| AP-004 | P0 | 空字段提交 | 显示字段错误，不调用 onSubmit |
| AP-005 | P1 | 请求中重复点击 | 只提交一次 |
| AP-006 | P0 | 登录失败 | 用户名保留、密码清空、显示统一错误 |
| AP-007 | P0 | AuthGate checking | 不渲染受保护内容 |
| AP-008 | P0 | AuthGate anonymous | 导航 /login，保存安全 returnTo |
| AP-009 | P0 | AuthGate 非法外部 returnTo | 回退 /，不开放重定向 |
| AP-010 | P0 | AuthGate authenticated | 渲染 Router Outlet 子路由 |
| AP-011 | P1 | AuthGate error/retry | 显示 PageState 重试并调用 retry |
| AP-017 | P1 | LoginPage 初始化 error | 显示 PageState 重试，不显示登录表单 |
| AP-012 | P0 | AuthenticatedLayout | 只显示一个 CurrentUserBar 和一个 Outlet 区域 |
| AP-013 | P0 | 当前会议首页认证接入 | `/` 与 `/meetings/:meetingId` 保留 MeetingWorkspacePage；不恢复身份占位页，账号入口按权限显示 |
| AP-014 | P0 | CurrentUserBar logout 成功 | 进入退出态并禁用重复点击 |
| AP-015 | P0 | CurrentUserBar logout 失败 | 仍显示当前用户和可重试错误 |
| AP-016 | P2 | 登录页窄屏 | 文本、输入和按钮不溢出或遮挡 |
| AP-018 | P1 | LoginPage unsupported_media_type | 显示请求格式不受支持，不显示网络错误文案 |
| AP-019 | P1 | returnTo 包含 query/hash | 校验后保留 pathname、search、hash；非法 pathname、search 或 hash 分别回退 `/` |
| AP-020 | P1 | 用户名原始长度 65 至 128 | 客户端允许输入，最终由服务端规范化长度决定是否接受 |

## 12.1 用户管理增量测试

本节验证基础用户管理，保留既有编号并将权限预期调整为管理员。所有管理成功场景使用真实管理员 Cookie；创建和昵称 PATCH 携带允许的 Origin 与 JSON 正文，密码重置只携带允许的 Origin，不发送请求正文。

### 12.1.1 Domain

项目：RoundTableMeeting.Domain.Tests。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| UM-D01 | P0 | ChangeNickname 合法输入 | Trim + NFC 后只更新 Nickname，UserId、Username、NormalizedUsername 和 PasswordHash 不变 |
| UM-D02 | P0 | ChangeNickname 空白、NFC 后为空或超长 | 拒绝修改，实体保持原值 |
| UM-D03 | P0 | ReplacePasswordHash | 合法哈希替换成功；空白或超过 512 被拒绝；账号其他字段不变 |

### 12.1.2 Application

项目：RoundTableMeeting.Application.Tests，继续使用 fake 仓储、fake UoW 和真实 PasswordHasher<UserAccount>。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| UM-A01 | P0 | 获取用户列表 | 保持仓储稳定顺序并映射 UserSummaryDto，不暴露 PasswordHash |
| UM-A02 | P0 | 添加合法账号 | 规范化用户名和昵称、按原字符串生成初始密码哈希、RegisterAdd 并只 Commit 一次，返回新用户摘要；合法密码保留首尾空格，全空白密码拒绝 |
| UM-A03 | P0 | 添加规范化后重复用户名 | 返回 UsernameConflict，不注册新增、不提交 |
| UM-A04 | P0 | 添加账号字段无效 | 返回 ValidationFailed，Errors 键为 username、nickname 或 initialPassword；不查询目标账号或写入仓储（前置操作者授权可查询） |
| UM-A05 | P0 | 预检查后发生唯一索引并发冲突 | UsernameConflictException 映射为 UsernameConflict；其他数据库异常继续上抛 |
| UM-A06 | P0 | 修改存在账号昵称 | 事务内验证管理员后只更新 Nickname，返回更新摘要，其他列不覆盖 |
| UM-A07 | P1 | 修改不存在账号昵称 | 返回 UserNotFound，不注册编辑、不提交 |
| UM-A08 | P0 | 重置存在账号密码 | 只使用固定值 123456 生成新哈希并提交；事务内验证操作者管理员身份，只更新密码哈希，不撤销会话 |
| UM-A09 | P1 | 重置不存在账号密码 | 返回 UserNotFound，不调用哈希器、不注册编辑、不提交 |

### 12.1.3 Repository.Sqlite

项目：RoundTableMeeting.Repository.Sqlite.Tests，使用真实临时 SQLite。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| UM-R01 | P0 | ListAllAsync 多账号 | 按 NormalizedUsername、ID 稳定排序，查询 NoTracking，实体包含持久化资料但不在仓储层生成 DTO |
| UM-R02 | P0 | 账号昵称和哈希按列更新 | 写事务只持久化目标列，其他并发更新的字段保持最新值 |
| UM-R03 | P0 | 并发写入等价用户名 | 目标唯一索引冲突转换为 UsernameConflictException；非目标约束和普通 DbUpdateException 不误报 |

### 12.1.4 HTTP、OpenAPI 和日志

项目：RoundTableMeeting.IntegrationTests，使用 TestApplicationFactory、真实 CookieContainer 和临时 SQLite。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| UM-H01 | P0 | 未登录访问五条用户管理 API | 全部返回 401 unauthenticated ProblemDetails，不产生写入 |
| UM-H02 | P0 | 管理员 GET /api/users | 200 稳定数组，只含 userId、username、nickname、isAdmin；普通用户 403 forbidden |
| UM-H03 | P0 | POST /api/users 合法请求 | 201 返回摘要，不声明指向不存在单用户 GET 的 Location；新账号可立即登录 |
| UM-H04 | P0 | POST 重复用户名 | 预检查冲突与数据库并发冲突都返回 409 username_conflict |
| UM-H05 | P0 | POST 字段无效 | 400 validation_failed，JSON 字段名为 errors，不建立账号 |
| UM-H06 | P0 | PATCH 存在账号昵称 | 200 返回更新摘要；随后列表和目标账号 me 显示新昵称 |
| UM-H07 | P1 | PATCH 不存在 UserId | 404 user_not_found，不修改其他账号 |
| UM-H08 | P0 | POST 重置存在账号密码 | 204；旧密码新登录失败，123456 新登录成功，重置前已有 Cookie 仍可调用 me |
| UM-H09 | P1 | POST 重置不存在 UserId | 404 user_not_found，不修改任何密码哈希 |
| UM-H10 | P0 | 三条写 API 的 Origin 缺失、null、外部或多值 | 403 origin_not_allowed，Application 用例不执行 |
| UM-H11 | P0 | 创建与昵称 PATCH 的媒体类型 | application/json 及带 charset 被接受；缺失或其他 media type 返回 415 unsupported_media_type |
| UM-H12 | P1 | 密码重置无正文策略 | 不要求 Content-Type；空正文正常进入用例，意外方法或路径仍由 routing 决定 |
| UM-H13 | P0 | 五条 API 的全部最终响应 | 200/201/204/400/401/403/404/409/415/500 均含 Cache-Control: no-store 和 Pragma: no-cache |
| UM-H14 | P0 | 用户管理错误响应 | 统一 application/problem+json、稳定 code、status、title、detail、traceId；不返回 MVC 默认 HTML |
| UM-H15 | P0 | OpenAPI 用户管理契约 | 包含五条 operation、DTO、PATCH、201 响应体及 400/401/403/404/409/415/500 映射；不生成不存在的单用户 GET |
| UM-H16 | P0 | 错误 HTTP 方法和相邻未知路径 | routing 保持 404/405；Origin 中间件不将其改写为 403 或 415 |
| UM-H17 | P0 | 日志与响应扫描 | 不包含初始密码、123456、PasswordHash、Cookie、完整请求正文或操作者 Claim |

### 12.1.5 客户端 Vitest

测试与 userManagement feature、AuthenticationProvider 和路由文件 colocate。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| UM-V01 | P0 | /users 首次加载成功 | 显示稳定用户表格与添加入口，行资料包含 isAdmin，不包含密码 |
| UM-V02 | P1 | 列表网络失败后重试 | 显示 ErrorState；重试成功替换为最新完整列表 |
| UM-V03 | P1 | StrictMode、重复加载与页面卸载 | 旧请求被取消，晚返回结果不覆盖新状态且无卸载后更新 |
| UM-V04 | P0 | 添加账号成功 | 只提交一次，关闭弹窗、提示成功并刷新列表 |
| UM-V05 | P0 | 添加 validation_failed | 字段错误绑定 username、nickname、initialPassword，表单保持可修正 |
| UM-V06 | P1 | 添加 username_conflict | 显示用户名冲突，不关闭弹窗，不误报网络错误 |
| UM-V07 | P0 | 添加请求失败 | 初始密码被清空，用户名和昵称保留，不把密码写入日志或持久化状态 |
| UM-V08 | P0 | 修改其他账号昵称成功 | 刷新列表，不调用 refreshCurrentUser |
| UM-V09 | P0 | 修改当前账号昵称成功 | 刷新列表并调用 refreshCurrentUser，CurrentUserBar 更新昵称 |
| UM-V10 | P1 | 当前账号昵称成功后的两个刷新 | 列表刷新与 refreshCurrentUser 独立执行；任一失败不阻止另一个、不回滚服务端成功结果，并显示对应同步错误 |
| UM-V11 | P1 | 修改或重置返回 user_not_found | 关闭失效目标操作、提示账号不存在并刷新列表 |
| UM-V12 | P0 | 打开密码重置确认框 | 明确目标账号、固定值 123456 和已有会话不立即失效；不提供密码输入框 |
| UM-V13 | P0 | 密码重置成功 | 只提交一次，关闭确认框并显示“密码已重置为 123456” |
| UM-V14 | P0 | 任一用户管理 API 返回 401 | 公共客户端通知 AuthenticationProvider，清理认证状态并离开受保护页面 |
| UM-V15 | P1 | 三类操作并发与旧请求晚返回 | 各自 submitting 独立；旧请求不能关闭新弹窗、覆盖目标或错误 |

### 12.1.6 Kiota 与生成

使用现有 generate:api 临时目录替换流程和客户端构建命令。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| UM-K01 | P0 | pnpm generate:api | 生成 users 集合、按 userId、nickname PATCH、reset-password 请求器和三个 DTO |
| UM-K02 | P0 | generated TypeScript 构建 | 类型检查和 Vite 构建成功；URL 只有一个 /api，不含开发绝对地址 |
| UM-K03 | P1 | 生成错误映射和文件规范 | 客户端可识别 400/401/403/404/409/415/500 ProblemDetails；文件 UTF-8 无 BOM、CRLF，无临时诊断文件 |

### 12.1.7 浏览器验收

开发环境使用独立 Chrome CDP Profile；发布环境重复同一业务闭环并检查 Secure Cookie 与同源请求。

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| UM-B01 | P0 | 已登录后从布局进入 /users | 菜单仅管理员可见，页面列表可读；普通用户直达无权限；未登录转到登录页 |
| UM-B02 | P0 | 账号 A 添加账号 B | B 出现在列表并可用输入的初始密码登录 |
| UM-B03 | P0 | A 修改 B 昵称 | 列表立即显示新昵称；B 刷新或重新登录后的当前用户栏显示新昵称 |
| UM-B04 | P0 | A 重置 B 密码 | B 的旧密码新登录失败，123456 成功；重置前已打开的 B 会话仍保持登录 |
| UM-B05 | P0 | 普通用户 B 管理 A 或新增账号 C | 403 forbidden，不查询目标或写库，不清除 B 的登录 |
| UM-B06 | P1 | Network、Console、宽窄屏截图 | 方法、Origin、Content-Type 和状态码符合契约；无敏感日志、未处理异常、重叠或溢出 |

## 13. 构建和发布回归

~~~powershell
dotnet restore Server/RoundTableMeeting.slnx
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug --no-build

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
| AB-001 | P0 | 服务端全部测试 | 002 和 003 测试均通过 |
| AB-002 | P0 | 客户端测试和构建 | Vitest、tsc、Vite 全部成功 |
| AB-003 | P0 | dotnet publish | 自动构建客户端，发布目录包含登录页资源 |
| AB-004 | P0 | 发布环境刷新 /login | SPA fallback 返回应用并渲染登录页 |
| AB-005 | P0 | 发布环境未认证 /api/auth/me | JSON 401，不返回 index.html |
| AB-006 | P1 | 发布 HTTPS 登录 | Cookie 名称、Path、Secure、HttpOnly、SameSite 和会话级属性符合契约 |
| AB-007 | P1 | /system/status 回归 | 匿名页面和 API 继续可用 |
| AB-008 | P1 | 未知 API/Scalar/OpenAPI 回归 | 不被认证布局或 SPA fallback 覆盖 |
| AB-009 | P0 | 发布环境刷新 /users | SPA fallback 返回应用；管理员身份确认后加载，普通用户无权限且不请求目录 |
| AB-010 | P0 | 发布环境用户管理闭环 | 同源完成添加、昵称修改和固定密码重置，成功与错误响应均满足禁缓存契约 |

## 14. CDP 浏览器验收

### 14.1 开发环境

| 编号 | 优先级 | 检查 | 预期 |
| --- | --- | --- | --- |
| AU-001 | P0 | 打开 http://localhost:5173/login | 登录页非空，表单可见 |
| AU-002 | P0 | 错误密码登录 | 网络 401 invalid_credentials，页面不泄露账号存在性 |
| AU-003 | P0 | 账号 A 登录 | 网络 200 + Set-Cookie，进入现有会议工作台，顶栏显示 A，个人设置可查看用户名和身份 |
| AU-004 | P0 | 刷新首页 | 只调用 me，不重新调用 login，仍显示 A |
| AU-005 | P0 | 退出 | 携带允许 Origin 的 POST logout；之后 me 为 401 并回登录页 |
| AU-006 | P0 | 账号 B 登录 | 顶栏和个人设置显示 B 的资料，不残留 A；会议首页保持现有布局 |
| AU-007 | P1 | Console | 无未处理异常、Cookie 或密码日志 |
| AU-008 | P1 | /system/status | 仍可匿名打开 |
| AU-009 | P2 | 宽屏截图 | 登录和首页布局无重叠 |
| AU-010 | P2 | 窄屏截图 | 表单、错误和按钮不溢出 |

### 14.2 发布环境

重复 AU-001 至 AU-008，地址改为发布 HTTPS 地址，额外检查 Secure Cookie、/login 子路径刷新和同源 API。截图输出到忽略目录 artifacts/cdp/003/，不回写实施日志。

## 15. 失败场景矩阵

| 场景 | 服务端 | 客户端 | 级别 |
| --- | --- | --- | --- |
| 无种子配置 | 启动失败，不监听 | 无可用服务 | P0 |
| 错误密码 | 401 invalid_credentials | 统一错误，保留用户名 | P0 |
| Cookie 无效 | 401 unauthenticated | 清理用户并登录导航 | P0 |
| 外部 Origin | 403 origin_not_allowed | 不建立会话 | P0 |
| me 网络失败 | 无响应 | error，可重试，不显示假登录 | P0 |
| logout 网络失败 | 无确认 | 保持当前用户，提示重试 | P0 |
| 数据库种子失败 | 启动失败，安全日志 | 无半初始化应用 | P0 |
| generated 失败 | 契约不可生成 | 旧生成目录保留 | P1 |
| 用户名重复 | 409 username_conflict | 表单保留并提示冲突 | P0 |
| 用户目标不存在 | 404 user_not_found | 关闭失效操作并刷新列表 | P1 |
| 用户管理写请求 Origin 无效 | 403 origin_not_allowed | 不改变列表或认证状态 | P0 |
| 创建或昵称请求非 JSON | 415 unsupported_media_type | 显示格式错误，不伪造成功 | P0 |
| 重置后的已有 Cookie | 会话继续有效 | 不强制退出，后续安全主题再处理 | P0 |

## 16. 003 完成门

- 所有 P0/P1 测试通过。
- dotnet build/test、pnpm test/build、generate:api 和 dotnet publish 全部成功。
- 两个账号的登录、刷新保持、退出、切换和用户管理闭环均通过浏览器验收。
- 认证日志、响应、OpenAPI 示例和客户端长期状态不含密码、hash 或 Cookie；表单只在输入与请求生命周期内短暂持有密码。
- 002 系统状态、ProblemDetails、Kiota、未知 API 和发布测试保持通过。
- 管理员用户管理和所有用户个人设置通过验收，普通用户不能操作他人；最后管理员和并发场景通过。
- 没有混入搜索、分页、删除、禁用、复杂角色体系或审计等延期能力。

## 17. 管理员权限与个人设置验收

以下全部为本轮待执行用例。真实 SQLite 并发测试使用不同连接和作用域，通过同步屏障形成竞争，不以顺序调用冒充并发。测试数据库、端口和浏览器 Profile 与其他会话隔离。

| 编号 | 优先级 | 层次与场景 | 预期 |
| --- | --- | --- | --- |
| UA-001 | P0 | Domain 新账号、数据库迁移 | IsAdmin 默认 false，旧数据 ID、昵称、密码和会议关联保留 |
| UA-002 | P0 | 空库与旧库首次初始化 | 显式 InitialAdminUsername 匹配账号并提升，其他账号普通 |
| UA-003 | P0 | 无管理员且初始管理员配置缺失/无效/目标不存在 | 启动失败，初始化事务回滚，不开放请求 |
| UA-004 | P0 | 已有管理员后重复启动 | 不覆盖密码、昵称或身份；已撤销的管理员不被配置恢复 |
| UA-005 | P0 | 并发启动初始化 | 重新检查管理员状态，最终至少一个管理员，不重复账号或覆盖资料 |
| UA-006 | P0 | 登录、me、列表 DTO 与客户端适配 | 均包含 isAdmin，false 合法，缺失/非布尔值拒绝；Cookie 不增加身份快照 |
| UA-007 | P0 | 普通用户访问五条管理 API | 允许 Origin 和合法请求下均 403 forbidden，先拒绝后查询，不泄露目标存在性 |
| UA-008 | P0 | 匿名访问管理与本人接口 | 合法 Origin 下均 401，不写库 |
| UA-009 | P0 | 管理员创建用户 | 201，默认 false；请求额外传入 isAdmin 不得造成提权 |
| UA-010 | P0 | 管理员授权、撤销、重复设置 | 200 返回实际摘要；同状态幂等；不存在 404 |
| UA-011 | P0 | 缺失或非布尔 isAdmin | 400 validation_failed，不将缺失值视为 false |
| UA-012 | P0 | 最后一名管理员取消自己 | 409 last_admin_required，身份保持 |
| UA-013 | P0 | 两个管理员并发互相取消/并发取消自己 | 至少一名保留；后执行请求重新验证操作者或最后管理员规则，无双降级 |
| UA-014 | P0 | 管理员撤销后使用原 Cookie | 提交撤销后的下一次管理调用 403；本人资料和会议原权限仍可用 |
| UA-015 | P0 | 昵称/密码修改与身份修改竞争 | 仅更新对应列，不能恢复被撤销权限或覆盖其他最新字段 |
| UA-016 | P0 | 两个本人改密请求竞争 | 按事务顺序验证最新哈希，旧密码不能重复覆盖后续密码 |
| UA-017 | P0 | 本人接口伪造 userId/isAdmin | 忽略或拒绝额外字段，始终只操作当前账号，不提权、不改他人 |
| UA-018 | P0 | 普通用户与管理员分别修改本人昵称 | 200；顶栏同步，用户名不变，已有会议显示名称不变，新入会采用新昵称 |
| UA-019 | P0 | 正确当前密码修改本人密码 | 204；旧密码新登录失败，新密码成功；当前与其他已有 Cookie 沿用原有效期 |
| UA-020 | P0 | 当前密码错误 | 400 validation_failed + errors.currentPassword，无写入，保持登录 |
| UA-021 | P1 | 新密码空白、超长、含首尾空格与 Unicode | 空白/超长拒绝；有效原字符串哈希，不 Trim/NFC |
| UA-022 | P1 | 确认密码不一致 | 客户端字段提示且不发请求；确认值不进入 HTTP 契约 |
| UA-023 | P0 | 管理员重置 B 为 123456，B 再自行改密 | 重置无正文 204；旧密码失败，123456 登录后能改为自己的密码；不强制首次改密 |
| UA-024 | P0 | 本人接口当前账号不存在 | 401 unauthenticated，不写库，不返回他人资料 |
| UA-025 | P0 | 新接口 Origin、媒体类型、错误与缓存 | 缺失/非法 Origin 为 403 origin_not_allowed；不合法媒体类型 415；成功与失败 no-store/no-cache |
| UA-026 | P0 | OpenAPI、Kiota 与 DI | 新服务可解析；路由、布尔必填、403/409 和错误字段一致，生成可构建 |
| UA-027 | P0 | 普通用户菜单、直接 URL 和登录 returnTo | 无管理入口，直达/返回 /users 无权限且不请求用户列表；个人设置可用 |
| UA-028 | P0 | 管理页进入资料刷新失败 | 不按旧管理员缓存放行，不加载目录，允许重试 |
| UA-029 | P0 | 管理 API forbidden 与资料刷新失败 | 立即清列表关弹窗，保持拒绝，无全局退出；origin_not_allowed 不误作降级 |
| UA-030 | P0 | 管理员本人降级 | 成功后离开管理页；资料刷新失败不恢复管理交互，个人设置仍可访问 |
| UA-031 | P1 | 个人两个表单、失败与卸载 | 独立状态；密码成功失败均清空，昵称保留；取消与旧响应不产生迟到更新 |
| UA-032 | P1 | 昵称写入成功但资料刷新失败 | 提示刷新失败，可单独重试；不回滚或重复保存 |
| UA-033 | P0 | 敏感信息与越权回归 | 不记录/返回密码、哈希、Cookie；管理员不能越过会议成员/创建者边界 |
| UA-034 | P1 | 浏览器宽窄屏与键盘 | 身份列、确认框、个人表单可读可操作，标签和输入自动填充语义正确 |

完成门：UA 的全部 P0/P1、受影响原用例、002 Origin 与发布基础回归通过。文档检查只验证契约一致性，不替代这些实现测试。

## 18. 初始化与身份刷新边界验收

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| UA-035 | P0 | 旧库全部种子账号已存在但没有管理员 | 无新增账号仍提升显式指定账号并提交；其密码、昵称与 ID 不变 |
| UA-036 | P0 | 同一旧库并发初始化管理员 | 取得锁后重新读取，后执行者看到已有管理员不再提升或覆盖，事务失败不残留部分数据 |
| UA-037 | P1 | 新迁移名称与旧迁移链 | 只新增 AddUserAccountAdmin，旧 AddUserAccounts 不重建；重复启动不重复迁移，已有数据库可升级 |
| UA-038 | P0 | 账号优化后的首页与会议直达 | `/`、`/meetings/:meetingId` 仍为真实工作台，保留错误边界和原有会议交互，不渲染身份占位页 |
| UA-039 | P0 | AdminGate 首次确认刷新被第二次刷新取代 | 首次返回 cancelled，即使旧资料 isAdmin=true 也不放行；仅当前版本 refreshed 可用于确认 |
| UA-040 | P0 | 首次确认结果返回后发生退出、换号或新刷新 | 退出/换号使 identityRevision 过期，新刷新使 refreshRevision 过期；旧结果不能用于首次放行或覆盖新身份 |
| UA-041 | P1 | 管理页卸载取消或 StrictMode 重复 effect | 旧结果不更新 gate；仍在页面的未确认状态可重试，不出现自动刷新循环 |
| UA-042 | P1 | 昵称保存后的资料刷新被取消 | 不把保存误报失败、不宣称刷新成功，已保存结果不回滚，允许重试资料同步 |

## 19. 测试文件与实施映射

既有测试继续维护，新增文件只承载新的职责；下表为计划落点，文档阶段不创建测试源码。路径中 Server 测试均相对 `Server/Tests/`，客户端测试均相对 `Client/roundTableMeeting/src/`。

| 测试文件 | 操作 | 主要覆盖 |
| --- | --- | --- |
| RoundTableMeeting.Domain.Tests/UserAccountTests.cs | 扩展 | UA-001 的 false 默认、管理员设置与其他字段不变 |
| RoundTableMeeting.Application.Tests/AuthenticationServiceTests.cs | 扩展 | UA-006 当前身份映射，保留登录/dummy hash 回归 |
| RoundTableMeeting.Application.Tests/AccountSeederTests.cs | 新增 | UA-002～004、035 的初始化决策、配置与错误传播；并发初始化由集成测试验证 |
| RoundTableMeeting.Application.Tests/UserManagementServiceTests.cs | 扩展 | UA-007、009～014、017 的操作者与目标规则、无写入拒绝 |
| RoundTableMeeting.Application.Tests/AccountSettingsServiceTests.cs | 新增 | UA-018～024 的本人目标、当前密码错误与新密码原字符串 |
| RoundTableMeeting.Repository.Sqlite.Tests/UserAccountRepositoryTests.cs | 扩展 | UA-001、037 的旧库迁移与按列更新基础；UA-015 的业务竞争由集成测试验证 |
| RoundTableMeeting.Repository.Sqlite.Tests/AccountTransactionTests.cs | 新增 | 真实写锁、统一提交、回滚、资源释放和按列更新基础，不重复实现业务授权或密码校验规则 |
| RoundTableMeeting.IntegrationTests/AccountConcurrencyTests.cs | 新增 | UA-005、013、015～016、036；真实 Application 服务与 SQLite，独立作用域和连接验证初始化、最后管理员、字段隔离及改密竞争 |
| RoundTableMeeting.IntegrationTests/HttpContractTests.cs、AuthenticationContractTests.cs、AuthenticationOptionsTests.cs | 扩展 | UA-006～011、014、023～026 的真实 Cookie/Origin/错误/OpenAPI；更新原开放管理断言 |
| RoundTableMeeting.IntegrationTests/AccountSettingsContractTests.cs | 新增 | UA-008、017～025 的本人接口与固定重置后自行改密闭环 |
| RoundTableMeeting.IntegrationTests/StartupFailureTests.cs、TestApplicationFactory.cs | 调整夹具 | UA-002～005、035 的显式初始管理员、启动失败原始故障及隔离 |
| RoundTableMeeting.ArchitectureTests/DependencyTests.cs | 扩展 | UA-026 的 DI、Scope、跨层依赖；新增 handler 不访问 DbContext |
| features/authentication/authentication.test.tsx、authenticationViews.test.tsx | 扩展 | UA-006、028～030、039～042 的 refreshed/cancelled、身份版本与入口 |
| app/router/AdminGate.test.tsx | 新增 | UA-027～030、038～041、043～044 的路由、两类版本和已确认页面稳定性 |
| features/userManagement/userManagement.test.tsx | 扩展 | UA-009～012、027、029～030、034、043～044 的身份列、确认与拒绝清理 |
| features/accountSettings/accountSettings.test.tsx | 新增 | UA-018～022、024、031～032、034、042 的表单与资料刷新 |
| 浏览器与发布验收 | 实际运行后检查 | UA-014、018～019、023、027、030、033～034、038，及 AB/AU |

UA-033 的会议权限由现有会议接口集成测试验证：使用不属于会议的管理员请求读取、发送或删除，仍按既有规则拒绝；不能仅断言 isAdmin 没有被读取。UA-016 同时覆盖管理员重置与本人改密的两种提交顺序。

旧用例调整要求：UM-H02、UM-B01/05 等正向管理前提必须改成管理员；本人设置不能借管理员夹具掩盖越权；AA-010/012 的“不产生身份变更”前提是数据库已有管理员。保留原测试编号，删除相矛盾的断言，不能用跳过测试替代修正。

## 20. 环境准备与执行批次

所有命令从本工作树执行。测试运行前读取当前项目的运行时和包管理器约束，依赖缺失时只在工作树恢复。测试库使用临时文件，开发库与主目录数据库不得作为迁移/并发测试输入。

浏览器和 OpenAPI 生成前确认监听进程的工作目录与本工作树一致。若默认端口被占用，选择空闲端口，在本工作树 Vite 配置中同步前端端口、/api、/hubs、/mcp 代理；服务端以 --no-launch-profile 与 --urls 使用对应后端端口，Authentication:AllowedOrigins 必须等于浏览器源。此类联调配置保持本地，不提交不必要的地址变更。以 Database:ConnectionString 指定本工作树临时数据库；账号密码由测试内存配置或受保护环境注入，避免多个工作树共享项目 user-secrets 时互相覆盖。

显式设置 OPENAPI_URL 为已确认的本工作树后端 `/openapi/v1.json`，再运行 pnpm generate:api；不能因为默认 5043 可访问就认为它是本轮服务。生成之前核对新路由和 isAdmin schema，生成后检查差异范围与类型构建。

| 批次 | 时间点 | 执行范围 | 通过条件 |
| --- | --- | --- | --- |
| T1 | 阶段 A 完成 | Domain、Application 初始化、SQLite 基础与初始化集成场景 | 默认值、旧库提升、迁移、并发初始化、事务和按列更新基础通过；不要求尚未实现的管理或改密用例 |
| T2 | 阶段 B 完成 | Application、Architecture、HTTP/Options/启动、本人接口与业务并发集成 | 管理员授权、最后管理员、字段隔离、改密竞争（UA-013/015/016）、错误和 OpenAPI 通过 |
| T3 | 阶段 C 完成 | 生成 Kiota、Vitest、客户端 build | false 解析、刷新取消/版本、页面与路由通过 |
| T4 | 交付前 | 服务端 solution、客户端全量、publish、浏览器 | UA 与受影响既有回归全部通过；会议工作台可用 |

服务端定向示例（从工作树根目录执行；新增测试类尚未创建时不能把零测试视为通过）：

~~~powershell
dotnet test Server/Tests/RoundTableMeeting.Domain.Tests/RoundTableMeeting.Domain.Tests.csproj --filter FullyQualifiedName~UserAccountTests
dotnet test Server/Tests/RoundTableMeeting.Application.Tests/RoundTableMeeting.Application.Tests.csproj --filter "FullyQualifiedName~AuthenticationServiceTests|FullyQualifiedName~AccountSeederTests|FullyQualifiedName~UserManagementServiceTests|FullyQualifiedName~AccountSettingsServiceTests"
dotnet test Server/Tests/RoundTableMeeting.Repository.Sqlite.Tests/RoundTableMeeting.Repository.Sqlite.Tests.csproj --filter "FullyQualifiedName~UserAccountRepositoryTests|FullyQualifiedName~AccountTransactionTests"
dotnet test Server/Tests/RoundTableMeeting.IntegrationTests/RoundTableMeeting.IntegrationTests.csproj --filter "FullyQualifiedName~HttpContractTests|FullyQualifiedName~AuthenticationContractTests|FullyQualifiedName~AuthenticationOptionsTests|FullyQualifiedName~AccountSettingsContractTests|FullyQualifiedName~AccountConcurrencyTests|FullyQualifiedName~StartupFailureTests"
dotnet test Server/Tests/RoundTableMeeting.ArchitectureTests/RoundTableMeeting.ArchitectureTests.csproj
~~~

客户端阶段在 Client/roundTableMeeting 中执行 pnpm generate:api、pnpm test 和 pnpm build。交付前从工作树根目录执行 dotnet test Server/RoundTableMeeting.slnx 和 dotnet publish；发布输出保留在本工作树 artifacts/publish。第 13 节命令是全量验证，必须在上述隔离准备完成后运行。

## 21. 并发与刷新测试的可重复构造

- 初始化旧库：先迁移到 IsAdmin 之前的已提交迁移并插入账号，再应用最新迁移；所有种子已存在时仍必须观察到管理员提升。不要仅用 EnsureCreated 模拟升级。
- 最后管理员：两个独立连接从屏障同时请求降级；在第一个持锁期间让第二个等待，第一个提交后第二个必须读取新状态。终态断言管理员数量至少一，不只统计成功数。
- 字段隔离：预读旧账号快照后由另一个事务改变管理员或密码，再执行昵称更新；验证更新目标列且保留另一事务结果。
- 改密竞争：第一个成功后，第二个仍提交旧当前密码应失败；重置先提交时，改密须验证重置后的值。异常或取消后重新用独立作用域读取数据库证明没有部分写入。
- AdminGate：用可控 deferred Promise 按相反顺序完成两个刷新，首次确认分别断言 cancelled、当前 refreshed 和过期结果；已确认页面普通资料刷新保持挂载，401、注销、换号、IsAdmin 改变或 forbidden 才撤销放行。测试不依赖真实网络延时或固定 sleep。
- forbidden：保留一个在途列表响应，在权限拒绝并清理后再令其成功；确认不能补回目录、打开弹窗或恢复 ready。该场景并入 UA-029。

上述竞态使用测试同步屏障、受控 Promise 或测试替身控制时序，不在生产代码加入固定延时。单元 fake 用于业务分支，真实 SQLite 与真实 Cookie 用于最终边界，二者不可互相替代。

## 22. 资料同步与已确认页面验收

| 编号 | 优先级 | 场景 | 预期 |
| --- | --- | --- | --- |
| UA-043 | P1 | 管理员在用户管理页修改本人昵称并刷新资料 | refreshRevision 改变，identityRevision 不变；页面持续挂载、顶栏更新、成功反馈保留，可继续管理，无需手动确认 |
| UA-044 | P1 | 已确认页面的昵称刷新被取消或网络失败 | 保持页面和既有权限，独立提示资料同步状态；若最新资料为普通用户或返回 401/管理 forbidden，立即撤销放行并清理目录 |

阶段 C/T3 覆盖 UA-043～044；阶段 B/T2 才执行最后管理员、业务字段竞争和改密竞争。AccountConcurrencyTests 使用真实 UserManagementService、AccountSettingsService 或 AccountSeeder，经同一宿主的不同作用域调用，连接到同一临时 SQLite 文件；阶段 A 仅执行其中初始化用例，阶段 B 执行全部。Repository 测试项目不新增对 Application 的引用，也不在测试内复制业务逻辑。测试映射中的全部 UA 用例仍需在交付前通过。
