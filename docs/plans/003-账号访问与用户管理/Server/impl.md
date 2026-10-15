# 账号访问与用户管理服务端实现方案

## 1. 文档定位

本文将[服务端设计](design.md)具体化为项目文件、包引用、配置、数据库 Migration、DI 注册、Cookie 管道、Controller 和运行命令。主题阶段顺序见[主题实现方案](../impl.md)，模块内部实现见[账号访问模块实现方案](Modules/AccountAccess/impl.md)，自动化验证见[测试方案](../testPlan.md)。

本文在既有登录和用户管理实现上增加管理员权限、初始管理员和个人设置；以下新增文件均为待实施落点，不表示已经生成。

## 2. 依赖和项目变更

沿用现有项目引用、包版本和依赖注入约定，不新增横向项目，不升级技术栈。Microsoft.Extensions.Identity.Core 与 PasswordHasher 已存在，继续复用，不重复添加依赖。账号服务不引用 Web 或 EF 类型。

| 项目 | 本轮增量 |
| --- | --- |
| Domain | UserAccount.IsAdmin、明确设置行为、账号查询与按列更新契约 |
| Abstraction | IsAdmin DTO、管理员设置与本人改密契约、管理员查询和账号事务接口 |
| Application | 扩展 AccountSeeder/UserManagementService，新增管理员查询与本人设置服务 |
| Repository.Sqlite | IsAdmin 迁移、按列更新与账号事务执行器 |
| Web | AdminOnly 策略、本人 Controller、启动配置传参与错误契约 |

## 3. 文件落点

下列路径相对 `Server/`；“新增”仅表示实施计划，当前文档阶段不创建源文件。

| 操作 | 路径 | 修改内容 |
| --- | --- | --- |
| 修改 | RoundTableMeeting.Domain/Entities/UserAccount.cs | IsAdmin 与设置行为 |
| 修改 | RoundTableMeeting.Domain/Repositories/IUserAccountRepository.cs | 管理员计数、按列更新契约 |
| 修改 | RoundTableMeeting.Repository.Sqlite/Repositories/UserAccountRepository.cs | 查询与只更新目标列 |
| 修改 | RoundTableMeeting.Repository.Sqlite/Database/Configurations/UserAccountConfiguration.cs | IsAdmin 非空默认 false |
| 新增/修改 | RoundTableMeeting.Repository.Sqlite/Migrations/ | AddUserAccountAdmin 迁移、Designer 和现有 Snapshot |
| 新增 | RoundTableMeeting.Abstraction/Services/IAccountTransactionRunner.cs | 账号写事务回调契约 |
| 新增 | RoundTableMeeting.Repository.Sqlite/Transactions/AccountTransactionRunner.cs | 取得写锁、提交与回滚 |
| 修改 | RoundTableMeeting.Abstraction/Contracts/Authentication/CurrentUserDto.cs | 增加 IsAdmin |
| 修改 | RoundTableMeeting.Abstraction/Contracts/UserManagement/UserSummaryDto.cs | 增加 IsAdmin |
| 新增 | RoundTableMeeting.Abstraction/Contracts/UserManagement/UpdateAdminRequest.cs | 必填 nullable bool IsAdmin |
| 修改 | RoundTableMeeting.Abstraction/Contracts/UserManagement/UserManagementResults.cs | Forbidden、LastAdminRequired |
| 修改 | RoundTableMeeting.Abstraction/Services/IUserManagementService.cs | UpdateAdminAsync |
| 修改 | RoundTableMeeting.Application/UserManagement/UserManagementService.cs | 事务内授权及管理员设置 |
| 新增 | RoundTableMeeting.Abstraction/Services/IAdministratorAccessService.cs | 当前操作者管理员查询 |
| 新增 | RoundTableMeeting.Application/AccountAccess/AdministratorAccessService.cs | 查询数据库当前身份 |
| 新增 | RoundTableMeeting.Abstraction/Services/IAccountSettingsService.cs | 本人昵称与密码用例 |
| 新增 | RoundTableMeeting.Abstraction/Contracts/AccountSettings/ChangePasswordRequest.cs | 当前密码、新密码 |
| 新增 | RoundTableMeeting.Abstraction/Contracts/AccountSettings/AccountSettingsResults.cs | Success/Unauthenticated/ValidationFailed |
| 新增 | RoundTableMeeting.Application/AccountAccess/AccountSettingsService.cs | 本人更新及密码校验 |
| 修改 | RoundTableMeeting.Abstraction/Services/IAccountSeeder.cs | 增加 initialAdminUsername 参数 |
| 修改 | RoundTableMeeting.Application/AccountAccess/AccountSeeder.cs | 原子初始化管理员 |
| 修改 | RoundTableMeeting.Application/AccountAccess/AuthenticationService.cs | DTO 映射 IsAdmin |
| 修改 | RoundTableMeeting/Authentication/AuthenticationOptions.cs | 可选 InitialAdminUsername |
| 新增 | RoundTableMeeting/Authentication/AdminRequirement.cs、AdminAuthorizationHandler.cs | AdminOnly 授权 |
| 修改 | RoundTableMeeting/Hosting/ServiceCollectionExtensions.cs | 注册策略与 handler，复用既有服务扫描 |
| 修改 | RoundTableMeeting/Program.cs | 启动时传入 InitialAdminUsername |
| 修改 | RoundTableMeeting/Controllers/UsersController.cs | 应用策略、增加 admin PATCH 与 403/409 |
| 新增 | RoundTableMeeting/Controllers/AccountController.cs | 两个本人写接口 |
| 修改 | RoundTableMeeting/Errors/ProblemDetailsFactory.cs | last_admin_required 固定安全错误 |

NoStoreResponse、Origin 校验与 Cookie 事件复用现有实现；只补端点元数据与响应声明，避免重建中间件。若实际命名冲突，先同步本表，保持职责不变。

## 4. 配置和秘密

### 4.1 配置结构

Authentication 配置节包含 AllowedOrigins、SeedAccounts 和可选 InitialAdminUsername。`appsettings.json` 只保存空数组或非秘密结构，不保存 `<secret>` 占位账号；`appsettings.Development.json` 固定保存 `AllowedOrigins = ["http://localhost:5173"]`，Development user-secrets 保存两个 SeedAccounts，并在首次部署或无管理员的旧库升级时显式提供 InitialAdminUsername；Testing 使用 WebApplicationFactory 的 InMemoryCollection 同时提供 Origin 和账号，生产使用环境变量或部署 Secret Provider。开发和验收至少提供两个账号，真实密码不提交仓库。

先执行以下命令初始化 Web 项目的 UserSecretsId；生成的 UserSecretsId 保留在 csproj 中。随后设置 Development 账号 secrets：

~~~powershell
dotnet user-secrets init --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:0:Username" "alice" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:0:Nickname" "Alice" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:0:Password" "<local-password>" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:1:Username" "bob" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:1:Nickname" "Bob" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:1:Password" "<local-password>" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:InitialAdminUsername" "alice" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
~~~

Options 使用 ValidateOnStart，至少验证：

- AllowedOrigins 是不带路径的绝对 HTTP/HTTPS Origin，不接受通配符、null 或空值。
- SeedAccounts 非空；用户名和昵称规范化显示值最长 64 个 .NET 字符，NormalizedUsername 最长 128 个字符，初始密码长度为 1 至 256 个字符。
- 用户名和昵称 Trim + NFC 后非空，NormalizedUsername 规范化后最长 128 且不重复。密码不 Trim、不做 Unicode 规范化。
- 校验异常、日志和响应不包含密码或完整配置对象；启动安全摘要可以指出 `Authentication:AllowedOrigins` 或 `Authentication:SeedAccounts` 配置键无效，但不得打印配置值。

Web 绑定并验证 Options，再映射为 SeedAccountInput 传给 Application。Application 不注入 IConfiguration 或 Web Options。

## 5. Domain 和 Repository

### 5.1 UserAccount

UserAccount 继承 BaseEntity，包含 Username、NormalizedUsername、Nickname、PasswordHash 和默认 false 的 IsAdmin。用户名显示值使用 Trim + Unicode NFC，比较值再执行 ToUpperInvariant。昵称使用 Trim + NFC，不要求唯一。Username 和 Nickname 最大 64，NormalizedUsername 最大 128，PasswordHash 最大 512；请求密码最大 256。

由于现有 `SqliteRepository<TEntity, TPrimaryKeyType>` 具有 `new()` 约束，UserAccount 保留 public 无参构造，仅供 EF/TTA 物化；属性 setter 保持 private/protected。业务创建固定使用 `UserAccount.Create(username, nickname)`，随后调用一次性 `InitializePasswordHash(hash)`；该方法拒绝空 hash 和第二次调用。只有 PasswordHash 已初始化的实体才能交给种子用例注册新增，测试验证无参构造不能产生可提交的合法账号。

规范化由 Domain 中单一纯函数完成，配置、登录、实体构造和仓储查询必须复用。实体不包含 Cookie、Claim、复杂角色对象、禁用状态或会话字段。

### 5.2 SQLite 映射和 Migration

UserAccountConfiguration 配置 Guid 主键、字段长度和 NormalizedUsername 普通精确唯一索引。数据库不使用 SQLite NOCASE 代替项目规范化，也不持久化明文密码。

现有 AddUserAccounts Migration 保留不变；本轮新增 AddUserAccountAdmin Migration 增加 IsAdmin 并更新 Snapshot，不在 Migration 中写入账号。运行时种子负责初始管理员。

### 5.3 仓储

IUserAccountRepository 提供按 NormalizedUsername 和按 UserId 查询的异步方法。查询默认 NoTracking，不返回 IQueryable；新增继续使用现有 IUnitOfWork.RegisterAdd 和 CommitAsync。实体配置文件固定放在 `RoundTableMeeting.Repository.Sqlite/Database/Configurations/UserAccountConfiguration.cs`，避免与技术 Options 的 `Configuration/` 目录混用。

## 6. Application

### 6.1 AuthenticationService

登录实现顺序：

1. 校验输入和长度上限。
2. 生成规范化用户名。
3. 查询账号。
4. 使用 IPasswordHasher<UserAccount> 验证密码。
5. 账号不存在时通过 PasswordVerificationService 对启动时生成的 dummy hash 执行等价 VerifyHashedPassword，再返回 invalid_credentials；测试不对绝对耗时设阈值，但禁止跳过哈希工作。
6. Success 和 SuccessRehashNeeded 都视为登录成功；本主题不因为重哈希提示写回密码。
7. 成功返回 CurrentUserDto；用户名不存在和密码错误统一返回 invalid_credentials。

当前用户查询只从 ICurrentUserContext 读取 IsAuthenticated 和 UserId，再按 UserId 查询资料。匿名、无效 UserId 或账号不存在统一返回 unauthenticated。

Application 不创建 Cookie，不访问 HttpContext，不返回 PasswordHash 或 ClaimsPrincipal。

由于项目接口名 `IAuthenticationService` 与 `Microsoft.AspNetCore.Authentication.IAuthenticationService` 同名，Web Controller 和组合根固定使用 using 别名，例如 `using AccountAuthenticationService = RoundTableMeeting.Abstraction.Services.IAuthenticationService;`，避免 CS0104 和误注入框架服务。

### 6.2 AccountSeeder

AccountSeeder 实现 IAccountSeeder：

1. 接收 Web 已验证的种子输入集合与可选 initialAdminUsername，再次规范化并检查输入内重复。
2. 开启写事务并取得写锁，再查询已有账号；缺失账号生成哈希并新增为普通用户。
3. 重新检查管理员数量；无管理员时必须将显式指定的已有或本次新增账号提升为管理员，配置缺失或目标不存在则失败回滚。
4. 已有管理员时忽略 initialAdminUsername，不覆盖已有账号的用户名、昵称、密码或身份。
5. 新增账号或管理员初始化任一发生变化，都提交同一事务；即使没有新增账号，也必须保存对已有账号的管理员提升。两者均无变化时不执行数据更新，结束事务。
6. 并行启动先等待写锁，再读取最新账号和管理员状态；不能基于等待前的结果重复插入或提升。唯一索引仍兜底，无法完成的数据库错误或取消回滚并向宿主上抛。

初始化顺序及启动失败条件以 [服务端设计](design.md)第 5.3 节为准，不保留另一套按新增数量判断是否提交的规则。

## 7. Web 认证

### 7.1 服务注册

在组合根中：

1. 绑定 AuthenticationOptions 并 ValidateOnStart。
2. 注册 IPasswordHasher<UserAccount> 为 Singleton；PasswordHasher 无请求状态，供认证和种子复用。
3. 注册 PasswordVerificationService 为 Singleton，并在创建时生成 dummy hash；它只依赖 Singleton PasswordHasher，不捕获 Scoped 服务。
4. AddAuthentication().AddCookie()。
5. AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())。
6. 手工注册 Web 实现 ICurrentUserContext -> HttpCurrentUserContext 为 Scoped。
7. 调用 AddHttpContextAccessor，为 HttpCurrentUserContext 提供请求访问器。
8. 注册 OriginValidationMiddleware。
9. 继续只扫描 Application 和 Repository.Sqlite 程序集。

不调用完整 Identity 注册扩展，不创建角色表或登录 UI。

### 7.2 Cookie

- Cookie.Name = `RoundTableMeeting.Auth`，Path = `/`。
- HttpOnly = true，SameSite = Lax。
- 各环境 SecurePolicy = SameAsRequest，HTTP 不写 Secure，HTTPS 写 Secure；可信代理转发协议在认证前正确还原，不接受不可信来源伪造协议。
- 会话级 Cookie，不设置持久化 Expires；认证票据 ExpireTimeSpan 固定 8 小时，SlidingExpiration = false。登录 SignInAsync 固定传入 `AuthenticationProperties { IsPersistent = false }`，8 小时只控制加密票据有效期，不写 Cookie Expires。票据过期后 me 返回 unauthenticated。
- Claim 只保存 UserId，Claim 类型固定为 `round_table_meeting_user_id`，由 Web 常量维护。

ICurrentUserContext 的接口属性固定为 `bool IsAuthenticated` 和 `Guid? UserId`；匿名或 Claim 无法解析时 UserId 为 null，不使用 Guid.Empty。

Cookie Events 覆盖 API 默认重定向：Challenge 返回 401 unauthenticated ProblemDetails；Forbidden 返回 403 forbidden ProblemDetails。forbidden 同时用于普通用户访问用户管理的正式场景。API 不返回 HTML 登录页。Cookie Events 和 OriginValidationMiddleware 统一调用 Web 现有 ProblemDetailsFactory/TraceIdResolver 的扩展方法生成 ApiProblemDetails，不能各自手写另一套 JSON。

`NoStoreResponseAttribute` 作为 Controller/action metadata 标记敏感端点。`SensitiveResponseHeadersMiddleware` 在 UseRouting 后读取 metadata，并通过 Response.OnStarting 统一追加 `Cache-Control: no-store` 和 `Pragma: no-cache`；AuthenticationController 与 UsersController 均标记该 metadata。中间件放在 Origin、Authentication 和模型绑定之前，确保成功、Challenge、Origin 拒绝、415、自动 400 和内部错误都携带同一缓存头。旧的 auth-path 专用响应头中间件由此替换，避免用户管理出现第二套规则。

### 7.3 Origin 校验

固定类型 `RequireBrowserOriginAttribute(bool requireJsonBody = false)` 作为方法级 endpoint metadata；OriginValidationMiddleware 位于显式 `UseRouting()` 之后，只处理当前 endpoint 带该元数据的 Cookie 写操作。login 使用 `RequireBrowserOrigin(true)` 要求 JSON，logout 使用默认参数只要求 Origin，不要求请求体 Content-Type；后续业务写 action 引入时必须同步标记并显式选择正文策略：

- `RequireJsonBody=true` 的标记端点要求解析后的 media type 精确为 `application/json`；不匹配返回 415 `unsupported_media_type`。
- `RequireJsonBody=false` 的无正文端点不强制 Content-Type；POST、PATCH、PUT、DELETE 的方法合法性由 routing/action metadata 决定。
- 方法与路由不匹配时 Origin 中间件直接放行，由 routing 保留 404/405，不能改写为 403/415。
- Origin 必须存在且精确匹配 AllowedOrigins；null、缺失和外部源返回 403 origin_not_allowed。
- 缺失 Origin 同样返回 403 origin_not_allowed；IntegrationTests 和联调工具必须显式提供允许的 Origin。
- 不开放携带凭证的跨源 CORS。

开发环境按浏览器实际 Origin http://localhost:5173 判断，不使用代理后的 API Host 代替。

## 8. Controller 和 OpenAPI

AuthenticationController 固定：

| 方法 | 路径 | 行为 |
| --- | --- | --- |
| POST | /api/auth/login | 匿名；验证凭证，SignInAsync，返回 CurrentUserDto |
| GET | /api/auth/me | Authorize；查询当前用户，返回 DTO |
| POST | /api/auth/logout | 可匿名；SignOutAsync，幂等返回 204 |

login 和 logout action 标记 `RequireBrowserOrigin`。AuthenticationController 与 UsersController 标记 `NoStoreResponse`；认证和用户管理的所有最终响应统一设置 `Cache-Control: no-store` 和 `Pragma: no-cache`。GetCurrentUserAsync 返回 Unauthenticated 时，me action 先执行 SignOutAsync 删除陈旧 Cookie，再返回 401。缓存头由 metadata 中间件统一添加，不在每个分支重复。

错误契约：

- 400 validation_failed，字段字典名为 errors。
- 401 invalid_credentials。
- 401 unauthenticated。
- 403 origin_not_allowed。
- 403 forbidden（已认证但不具备管理员权限）。
- 415 unsupported_media_type。
- 500 internal_error。

每个 operation 显式声明成功和错误响应，使 Kiota 生成类型化错误映射。Controller 不查询 DbContext、不比较密码、不记录请求体。

LoginRequest 的 DataAnnotations 字段名通过现有模型键归一化输出为 `username` 和 `password`。Application 的 ValidationFailed 使用同样键名，Controller 把 Errors 交给统一 ProblemDetails 工厂，保证自动 400 与应用级 400 结构一致。

## 9. 启动和管道顺序

构建 WebApplication 后创建启动 scope：

1. IDatabaseInitializer.InitializeAsync，完成 Migration。
2. 将已验证 Options 映射为 SeedAccountInput。
3. IAccountSeeder.SeedAsync。
4. 任一步失败，记录安全摘要并终止，不监听端口。

账号种子不塞入 SQLite 初始化器。请求管道固定为：异常处理、Serilog 请求日志、受信转发协议与按配置启用的 HTTPS 重定向、`UseRouting()`、默认文件与静态文件、SensitiveResponseHeadersMiddleware、OriginValidationMiddleware、`UseAuthentication()`、`UseAuthorization()`，随后映射 OpenAPI、Scalar、Controller、API fallback 和 SPA fallback。必须显式调用 `UseRouting()`，两个 metadata 中间件必须严格位于其后，否则无法读取 endpoint metadata。静态资源在认证与 Origin 之前匿名提供；Origin 中间件只对带元数据端点生效，未知 API 继续得到 route_not_found。

登录和退出 action、SystemController、OpenAPI、Scalar、专用 API 404 fallback 与 SPA fallback 显式使用 `[AllowAnonymous]` 或 endpoint `.AllowAnonymous()`。用户管理增量的写 action 必须在具体 action 上声明方法级 `RequireBrowserOrigin`，不能通过 URL、Controller 名称或 HTTP 方法启发式判断。现有 IntegrationTests `Controllers/TestController.cs` 的 `/__test/application-part`、`/__test/throw`、`/__test/validation` 继续承担 002 ApplicationPart、ProblemDetails、字段校验、traceId 和请求日志回归，Controller 类必须显式标记 `[AllowAnonymous]`，保持原 200/400/500 语义。另新增独立测试控制器 `DefaultAuthorizationTestController`（无授权元数据）验证 FallbackPolicy 401，以及独立 `ForbiddenTestController`（始终失败策略）验证 403 forbidden；二者不得复用现有 TestController，也不得改变 002 夹具的预期。

TestApplicationFactory 的公共配置必须增加有效 AllowedOrigins、两个 SeedAccounts，以及显式指定管理员的 InitialAdminUsername，并把上述三个测试 Controller 作为测试 ApplicationPart 注入。现有 StartupFailureTests 及任何启动真实进程的 002 回归测试，除专门验证认证配置缺失的 AI-021 外，也必须注入有效认证配置，确保测试仍由原目标故障触发。

StartupFailureTests 除退出码、端口和脱敏断言外，还必须断言安全启动日志中的 `ExceptionType` 为 `Microsoft.Data.Sqlite.SqliteException`，且不是 OptionsValidationException；这样才能证明回归测试没有因认证配置错误假绿。

生产发布前必须把浏览器公开 Origin 完整值（scheme、host、port）写入 `Authentication__AllowedOrigins__0`；启动时缺失或格式非法直接失败，并在安全启动摘要中指出配置键，不打印允许源值。应用不把允许源列表写入日志，但运维检查命令必须在启动前对比发布 URL 与该配置。登录联调统一走 Vite 5173；直接访问 Development HTTPS 7043 不属于 003 登录验收路径。

ASP.NET Core Data Protection 密钥不在本主题持久化；服务端重启或更换实例后 Cookie 可以失效，这与主题总体设计“不承诺服务端重启后的会话恢复”一致。容器、多实例或跨部署保留会话时必须另行设计密钥存储。运行期业务不得再次解析 SeedAccounts；账号种子完成后只通过数据库账号工作。验收账号密码必须保存在本地受保护秘密中；当前提供管理员执行的固定密码重置和所有用户自行改密，不提供自助找回、自定义重置或安全恢复流程。

## 10. 命令

迁移命令在实体与映射变更完成后执行一次；若本工作树已存在 AddUserAccountAdmin，则复用并检查该迁移，不重复创建。保留已有 AddUserAccounts 迁移和历史迁移链。

~~~powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet tool run dotnet-ef migrations add AddUserAccountAdmin --project Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj --startup-project Server/RoundTableMeeting/RoundTableMeeting.csproj --context RoundTableMeetingDbContext --output-dir Migrations
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug
dotnet run --project Server/RoundTableMeeting/RoundTableMeeting.csproj --launch-profile http
dotnet publish Server/RoundTableMeeting/RoundTableMeeting.csproj --configuration Release --output artifacts/publish
~~~

发布验收前通过部署环境注入：`Authentication__AllowedOrigins__0=<实际公开 HTTP 或 HTTPS Origin>`、`Authentication__SeedAccounts__0__Username`、`Authentication__SeedAccounts__0__Nickname`、`Authentication__SeedAccounts__0__Password`，以及索引 1 的同类三个键；首次部署或无管理员的旧库升级还须显式注入 `Authentication__InitialAdminUsername`。浏览器实际访问 Origin 必须与 AllowedOrigins 完全一致；测试密码不能写入命令历史或提交文件，实际执行时由受保护脚本或 Secret Provider 注入。

## 11. 完成检查

- 空库自动迁移并创建账号唯一索引。
- 两个种子账号可重复初始化，不覆盖已有资料。
- Login、me、logout 通过真实 Cookie 闭环。
- API 401/403 返回 JSON ProblemDetails，不重定向 HTML。
- Vite Origin 允许，外部 Origin 拒绝。
- 密码、哈希、Cookie、Claim 和秘密不进入日志或响应。
- 002 框架测试和发布链路继续通过。
- 仅管理员可访问五条用户管理接口；所有用户可使用两条本人写接口。
- 用户列表只返回 UserId、Username、Nickname、IsAdmin 并稳定排序；false 是合法字段值，不能省略。
- 用户名冲突统一 409 username_conflict，目标不存在统一 404 user_not_found。
- POST/PATCH 写请求的 Origin 和 JSON metadata 正确，方法错误保留 routing 404/405。
- 用户管理全部响应 no-store/no-cache。
- 固定密码、初始密码和哈希不进入日志、响应或 OpenAPI 示例。

## 13. 管理员与个人设置实施落点

- Domain：UserAccount 增加 private setter 的 bool IsAdmin 和明确设置行为；默认 false。最后管理员与操作者授权规则由应用服务在事务中编排。
- Repository.Sqlite：新增 AddUserAccountAdmin Migration 和更新 Snapshot，只新增非空 IsAdmin 列且默认 false，不修改旧迁移，不在迁移中写账号。增加写事务、管理员数量读取及按列更新能力，避免 RegisterEdit 的整实体覆盖；事务锁定方式参照 Server/design.md 第 12 节。
- Abstraction：CurrentUserDto、UserSummaryDto 增加 bool IsAdmin；新增 UpdateAdminRequest（必填 bool，避免缺失值被当作 false）、ChangePasswordRequest；种子契约增加可选 initialAdminUsername；新增 IAccountSettingsService 和 IAdministratorAccessService。不在 ICurrentUserContext 固化管理员快照。
- Application：UserManagementService 增加管理员变更及全部写入的事务内授权；AccountSettingsService 按当前 UserId 修改本人昵称或密码；AdministratorAccessService 提供当前管理员查询；AccountSeeder 按 Server/design.md 第 5.3 节执行初始化。
- Web：注册 AdminOnly requirement/handler，UsersController 应用策略；新增 AccountController 两条本人写接口。所有新增写接口标注 Origin、NoStore 和完整错误响应。沿用现有中间件顺序。
- OpenAPI/Kiota：包含 isAdmin、新请求与路由、last_admin_required 和管理 GET 的 403。布尔 false 必须按合法值解析，不得用 truthy 校验误判缺失。

配置示例增加 `Authentication:InitialAdminUsername`，值由部署者指定；单元/集成夹具显式指定管理员 A 和普通用户 B。不能让测试账号全部自动升为管理员，否则无法验证边界。正常重启不覆盖身份；启动失败不残留部分初始化结果。

新增功能完成前，以本节和对应模块为实施要求，不将现有基础登录测试通过视为全部范围完成。

## 14. 本人设置服务契约与改密并发

IAccountSettingsService 提供 UpdateMyNicknameAsync(UpdateNicknameRequest, CancellationToken) 和 ChangeMyPasswordAsync(ChangePasswordRequest, CancellationToken)。结果分别携带 CurrentUserDto 或无成功正文，状态使用 Success、Unauthenticated、ValidationFailed；字段错误用 Errors 字典。目标仅来自 ICurrentUserContext，AccountController 不读取目标请求参数。

ChangePasswordRequest 定义 currentPassword 和 newPassword，两者最长 256。当前密码按登录相同的原字符串语义验证，新密码拒绝全空白；确认密码留在客户端。AccountSettingsService 在写事务取得锁后读取当前账号，检查存在性并验证最新密码；错误当前密码返回 ValidationFailed，键 currentPassword，不调用写入。更新昵称只写 Nickname，更新密码只写 PasswordHash，返回成功不签发新 Cookie，也不撤销旧 Cookie。

管理员重置与个人改密并发时按数据库提交顺序生效：重置先提交则个人改密必须验证重置后的当前密码；个人改密先提交则后续管理员重置最终将密码设为 123456。数据库锁冲突处理沿现有基础设施规则；失败必须回滚，不能报告成功或部分更新。

## 15. 账号事务与按列更新的实施约束

IAccountTransactionRunner 使用 RunAsync<T>(Func<CancellationToken, Task<T>>, CancellationToken) 形状，位于 Abstraction；AccountTransactionRunner、IUnitOfWork、账号仓储共用同一 Scoped DbContext。

参考现有 MeetingTransactionRunner 的 BeginTransaction(deferred: false)、UseTransactionAsync 与清理方式实现账号执行器，但不改造会议执行器或引入其会议/Agent 异常转换。回调中只登记新增、执行按列更新和返回结果，不自行 CommitAsync；执行器统一调用工作单元保存，再提交数据库事务。新增账号写入尚未 flush 时，初始化流程必须同时考虑本批新增记录，不能仅靠数据库查询遗漏指定新账号；同一事务内保存后再查询也是允许的实现，不能提前提交数据库事务。

昵称、密码、身份的更新应仅影响目标列，支持直接按列 SQL/EF 更新或明确属性标记，禁止 RegisterEdit 整实体更新。业务拒绝必须发生在写入之前；异常和取消回滚全部操作，finally 解绑事务、清理跟踪并释放资源，不重用失败实体。最后管理员检查在取得写锁后进行，不能依赖进程锁。

此处执行器是本轮待新增源码，避免把账号事务范围耦合到其他会话正在维护的会议/Agent 事务实现。实施时通过架构测试与真实 SQLite 验证提交责任、回滚和同作用域共享。
