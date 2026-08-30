# 账号访问与用户管理测试方案

## 1. 文档定位

本文定义 003 账号访问主题的自动化测试、契约测试、构建回归和浏览器验收。实施顺序见[总体实现方案](impl.md)，服务端见[Server/impl.md](Server/impl.md)，客户端见[Client/impl.md](Client/impl.md)。

测试只验证最小账号接入，不把添加用户、修改密码、密码重置、角色、审计或高级会话能力当作遗漏测试。

## 2. 测试目标

测试需要证明：

1. 账号规范化、持久化和唯一性稳定。
2. 配置种子幂等，不覆盖已有资料，失败时阻止启动。
3. 登录、当前用户和退出使用真实 Cookie 完成闭环。
4. API 未认证、无效凭证、字段验证和 Origin 拒绝使用稳定 ProblemDetails。
5. Application 不访问 Web、Options、Cookie 或 EF Core。
6. 页面刷新通过 me 恢复资料，不重新提交凭证。
7. 客户端竞态、全局 401、退出网络失败和安全返回路径行为正确。
8. 002 的系统状态、Kiota、构建、发布和 SPA 回归继续通过。

## 3. 环境与隔离

### 3.1 账号和秘密

- 测试账号由测试配置注入，不使用开发或生产账号。
- 密码仅存在于测试输入和内存配置，不写入快照、日志或测试输出。
- IntegrationTests 每个工厂使用独立临时 SQLite 文件和独立 CookieContainer。
- 不依赖测试执行顺序；每个测试可独立创建宿主。

### 3.2 HTTP 和浏览器

- WebApplicationFactory 用于大部分 HTTP 契约测试。
- 所有 Cookie 写请求测试显式添加 Origin；分别覆盖 http://localhost:5173、外部 Origin、null Origin 和缺失 Origin。
- 浏览器验收使用 Vite 5173 代理到服务端 5043；发布验收使用 ASP.NET Core 同源地址。
- Chrome 使用独立 Profile，CDP 只绑定 127.0.0.1。

## 4. 优先级和完成规则

- P0：身份错误、凭证泄露、未认证放行、种子破坏、登录状态错误或构建阻断，必须通过。
- P1：核心失败路径、契约和客户端状态，主题完成前必须通过。
- P2：视觉、辅助诊断和维护性检查，完成前应通过或得到明确延期批准。

所有 P0/P1 失败都阻止 003 完成。

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
| AD-007 | P0 | 实体公开 API 扫描 | 不含 Cookie、Claim、角色、禁用或会话字段 |
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
| AA-007 | P0 | 当前 UserId 对应账号存在 | 返回 userId、username、nickname，不含 hash |
| AA-008 | P0 | 当前 UserId 账号不存在 | unauthenticated |
| AA-009 | P0 | 首次种子两个账号 | 注册两个新增并只 Commit 一次 |
| AA-010 | P0 | 重复执行种子 | 不覆盖资料，不产生写入 |
| AA-011 | P0 | 配置内规范化用户名重复 | 初始化失败，未写数据库 |
| AA-012 | P1 | 一项已存在一项缺失 | 只新增缺失账号，不覆盖已有账号 |
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
| AC-005 | P0 | 服务解析 | IAuthenticationService、IAccountSeeder、IUserAccountRepository、ICurrentUserContext 可按预期生命周期解析 |
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
| AI-007 | P0 | 登录后 GET /api/auth/me | 200，返回同一 userId、username、nickname |
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
| AI-033 | P1 | Origin 元数据顺序和正文策略 | 显式 UseRouting 后按 RequireBrowserOrigin 元数据读取 Origin；login 元数据要求 JSON，logout 元数据不要求 JSON；移动到 UseRouting 前的探针不能作为生产实现 |
| AI-034 | P0 | 认证管道加入后的 002 回归 | 现有 TestController 显式 AllowAnonymous，`/__test/application-part`、`/__test/throw`、`/__test/validation` 保持 200/500/400；静态文件、OpenAPI、Scalar、未知 API、SPA fallback 和非 HTML/非 GET 边界保持 002 语义 |
| AI-035 | P1 | AllowedOrigins 配置错误 | 启动失败并指出安全配置键，不打印 Origin 值或其他秘密 |

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
| AP-013 | P0 | AuthenticatedHomePage | 显示 UserId、用户名和昵称，不重复用户栏、不显示模拟会议 |
| AP-014 | P0 | CurrentUserBar logout 成功 | 进入退出态并禁用重复点击 |
| AP-015 | P0 | CurrentUserBar logout 失败 | 仍显示当前用户和可重试错误 |
| AP-016 | P2 | 登录页窄屏 | 文本、输入和按钮不溢出或遮挡 |
| AP-018 | P1 | LoginPage unsupported_media_type | 显示请求格式不受支持，不显示网络错误文案 |
| AP-019 | P1 | returnTo 包含 query/hash | 校验后保留 pathname、search、hash；非法 pathname、search 或 hash 分别回退 `/` |
| AP-020 | P1 | 用户名原始长度 65 至 128 | 客户端允许输入，最终由服务端规范化长度决定是否接受 |

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

## 14. CDP 浏览器验收

### 14.1 开发环境

| 编号 | 优先级 | 检查 | 预期 |
| --- | --- | --- | --- |
| AU-001 | P0 | 打开 http://localhost:5173/login | 登录页非空，表单可见 |
| AU-002 | P0 | 错误密码登录 | 网络 401 invalid_credentials，页面不泄露账号存在性 |
| AU-003 | P0 | 账号 A 登录 | 网络 200 + Set-Cookie，进入首页显示 A 的 UserId、用户名和昵称 |
| AU-004 | P0 | 刷新首页 | 只调用 me，不重新调用 login，仍显示 A |
| AU-005 | P0 | 退出 | 携带允许 Origin 的 POST logout；之后 me 为 401 并回登录页 |
| AU-006 | P0 | 账号 B 登录 | 显示 B 的 UserId、用户名和昵称，不残留 A 的资料 |
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

## 16. 003 完成门

- 所有 P0/P1 测试通过。
- dotnet build/test、pnpm test/build、generate:api 和 dotnet publish 全部成功。
- 两个账号的登录、刷新保持、退出和切换均通过浏览器验收。
- 认证日志、响应、OpenAPI 示例和客户端状态不含密码、hash 或 Cookie。
- 002 系统状态、ProblemDetails、Kiota、未知 API 和发布测试保持通过。
- 没有新增延期的用户管理能力。
