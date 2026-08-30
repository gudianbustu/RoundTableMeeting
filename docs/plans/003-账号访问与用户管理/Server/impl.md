# 账号访问与用户管理服务端实现方案

## 1. 文档定位

本文将[服务端设计](design.md)具体化为项目文件、包引用、配置、数据库 Migration、DI 注册、Cookie 管道、Controller 和运行命令。主题阶段顺序见[主题实现方案](../impl.md)，模块内部实现见[账号访问模块实现方案](Modules/AccountAccess/impl.md)，自动化验证见[测试方案](../testPlan.md)。

本文只实现配置预置账号、登录、当前用户查询、退出和统一认证边界，不实现用户管理后台或高级账号安全。

## 2. 依赖和项目变更

沿用 002 已建立的项目引用，不新增横向项目：

| 项目 | 本主题新增内容 |
| --- | --- |
| RoundTableMeeting.Domain | UserAccount、用户名规范化、IUserAccountRepository |
| RoundTableMeeting.Abstraction | 认证 DTO、服务接口、当前用户上下文、种子契约 |
| RoundTableMeeting.Application | AuthenticationService、AccountSeeder |
| RoundTableMeeting.Repository.Sqlite | DbSet、实体配置、仓储、Migration |
| RoundTableMeeting | Cookie、Authorization、Origin 校验、Controller、启动种子 |

Application 直接引用提供 IPasswordHasher<TUser> 的 Microsoft.Extensions.Identity.Core `10.0.11`，与当前 ASP.NET Core/EF Core 基线保持一致；在 `Server/Directory.Packages.props` 增加 `<PackageVersion Include="Microsoft.Extensions.Identity.Core" Version="10.0.11" />`，Application 项目只声明 PackageReference。不引入完整 ASP.NET Core Identity UI、角色系统或 EF Identity Store。

## 3. 文件落点

~~~text
Server/
  RoundTableMeeting.Domain/
    Entities/UserAccount.cs
    Repositories/IUserAccountRepository.cs
  RoundTableMeeting.Abstraction/
    Contracts/Authentication/LoginRequest.cs
    Contracts/Authentication/CurrentUserDto.cs
    Contracts/Authentication/SeedAccountInput.cs
    Contracts/Authentication/AuthenticateResult.cs
    Contracts/Authentication/CurrentUserResult.cs
    Services/IAuthenticationService.cs
    Services/ICurrentUserContext.cs
    Services/IAccountSeeder.cs
  RoundTableMeeting.Application/
    AccountAccess/AuthenticationService.cs
    AccountAccess/AccountSeeder.cs
    AccountAccess/PasswordVerificationService.cs
  RoundTableMeeting.Repository.Sqlite/
    Database/Configurations/UserAccountConfiguration.cs
    Repositories/UserAccountRepository.cs
    Migrations/<timestamp>_AddUserAccounts.cs
  RoundTableMeeting/
    Authentication/AuthenticationOptions.cs
    Authentication/AuthenticationConstants.cs
    Authentication/HttpCurrentUserContext.cs
    Authentication/OriginValidationMiddleware.cs
    Authentication/AuthenticationResponseHeadersMiddleware.cs
    Authentication/RequireBrowserOriginAttribute.cs
    Authentication/AuthenticationProblemDetailsExtensions.cs
    Controllers/AuthenticationController.cs
~~~

以上文件名和目录作为本主题实施基线；只有发现与已存在同名类型冲突时才允许在 `impl.md` 先更新后调整，职责不能跨层移动。

## 4. 配置和秘密

### 4.1 配置结构

Authentication 配置节包含 AllowedOrigins 和 SeedAccounts。`appsettings.json` 只保存空数组或非秘密结构，不保存 `<secret>` 占位账号；`appsettings.Development.json` 固定保存 `AllowedOrigins = ["http://localhost:5173"]`，Development user-secrets 只保存两个 SeedAccounts；Testing 使用 WebApplicationFactory 的 InMemoryCollection 同时提供 Origin 和账号，生产使用环境变量或部署 Secret Provider。开发和验收至少提供两个账号，真实密码不提交仓库。

先执行以下命令初始化 Web 项目的 UserSecretsId；生成的 UserSecretsId 保留在 csproj 中。随后设置 Development 账号 secrets：

~~~powershell
dotnet user-secrets init --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:0:Username" "alice" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:0:Nickname" "Alice" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:0:Password" "<local-password>" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:1:Username" "bob" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:1:Nickname" "Bob" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
dotnet user-secrets set "Authentication:SeedAccounts:1:Password" "<local-password>" --project Server/RoundTableMeeting/RoundTableMeeting.csproj
~~~

Options 使用 ValidateOnStart，至少验证：

- AllowedOrigins 是不带路径的绝对 HTTP/HTTPS Origin，不接受通配符、null 或空值。
- SeedAccounts 非空；用户名和昵称规范化显示值最长 64 个 .NET 字符，NormalizedUsername 最长 128 个字符，初始密码长度为 1 至 256 个字符。
- 用户名和昵称 Trim + NFC 后非空，NormalizedUsername 规范化后最长 128 且不重复。密码不 Trim、不做 Unicode 规范化。
- 校验异常、日志和响应不包含密码或完整配置对象；启动安全摘要可以指出 `Authentication:AllowedOrigins` 或 `Authentication:SeedAccounts` 配置键无效，但不得打印配置值。

Web 绑定并验证 Options，再映射为 SeedAccountInput 传给 Application。Application 不注入 IConfiguration 或 Web Options。

## 5. Domain 和 Repository

### 5.1 UserAccount

UserAccount 继承 BaseEntity，包含 Username、NormalizedUsername、Nickname 和 PasswordHash。用户名显示值使用 Trim + Unicode NFC，比较值再执行 ToUpperInvariant。昵称使用 Trim + NFC，不要求唯一。Username 和 Nickname 最大 64，NormalizedUsername 最大 128，PasswordHash 最大 512；请求密码最大 256。

由于现有 `SqliteRepository<TEntity, TPrimaryKeyType>` 具有 `new()` 约束，UserAccount 保留 public 无参构造，仅供 EF/TTA 物化；属性 setter 保持 private/protected。业务创建固定使用 `UserAccount.Create(username, nickname)`，随后调用一次性 `InitializePasswordHash(hash)`；该方法拒绝空 hash 和第二次调用。只有 PasswordHash 已初始化的实体才能交给种子用例注册新增，测试验证无参构造不能产生可提交的合法账号。

规范化由 Domain 中单一纯函数完成，配置、登录、实体构造和仓储查询必须复用。实体不包含 Cookie、Claim、角色、禁用状态或会话字段。

### 5.2 SQLite 映射和 Migration

UserAccountConfiguration 配置 Guid 主键、字段长度和 NormalizedUsername 普通精确唯一索引。数据库不使用 SQLite NOCASE 代替项目规范化，也不持久化明文密码。

新增 AddUserAccounts Migration，这是 002 初始空模型之后的首个真实业务实体迁移；只创建账号表和索引，不在 Migration 中写入固定账号。运行时种子负责创建账号。

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

1. 接收 Web 已验证的种子输入集合。
2. 再次规范化并检查输入内重复。
3. 查询已有账号。
4. 缺失账号使用密码哈希器生成摘要并注册新增。
5. 已存在账号不覆盖用户名、昵称或密码。
6. 有新增时统一 CommitAsync，无新增时不写数据库。
7. 异常和取消向启动宿主上抛。

数据库唯一索引是最终并发兜底；并发初始化冲突使启动失败，不静默覆盖。

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
- 生产 SecurePolicy = Always，Development/Testing = SameAsRequest，支持现有 HTTP 联调。
- 会话级 Cookie，不设置持久化 Expires；认证票据 ExpireTimeSpan 固定 8 小时，SlidingExpiration = false。登录 SignInAsync 固定传入 `AuthenticationProperties { IsPersistent = false }`，8 小时只控制加密票据有效期，不写 Cookie Expires。票据过期后 me 返回 unauthenticated。
- Claim 只保存 UserId，Claim 类型固定为 `round_table_meeting_user_id`，由 Web 常量维护。

ICurrentUserContext 的接口属性固定为 `bool IsAuthenticated` 和 `Guid? UserId`；匿名或 Claim 无法解析时 UserId 为 null，不使用 Guid.Empty。

Cookie Events 覆盖 API 默认重定向：Challenge 返回 401 unauthenticated ProblemDetails；Forbidden 返回 403 forbidden ProblemDetails。forbidden 是为后续业务冻结的通用契约，本主题只通过测试专用拒绝策略端点验证形态，不代表当前存在正式业务权限场景。API 不返回 HTML 登录页。Cookie Events 和 OriginValidationMiddleware 统一调用 Web 现有 ProblemDetailsFactory/TraceIdResolver 的扩展方法生成 ApiProblemDetails，不能各自手写另一套 JSON。

AuthenticationResponseHeadersMiddleware 对 `/api/auth/*` 请求使用 Response.OnStarting 统一追加 `Cache-Control: no-store` 和 `Pragma: no-cache`，并放在 Origin、Authentication 和模型绑定之前，确保成功、Challenge、Origin 拒绝、415、自动 400 和内部错误都携带同一缓存头。

### 7.3 Origin 校验

新增固定类型 `RequireBrowserOriginAttribute(bool requireJsonBody = false)` 作为端点元数据；OriginValidationMiddleware 位于显式 `UseRouting()` 之后，只处理当前 Endpoint 带该元数据的 Cookie 写操作。login 使用 `RequireBrowserOrigin(true)` 要求 JSON，logout 使用默认参数只要求 Origin 和 POST，不要求请求体 Content-Type；后续业务写 action 引入时必须同步标记并显式选择正文策略：

- `RequireJsonBody=true` 的标记端点要求解析后的 media type 精确为 `application/json`；不匹配返回 415 `unsupported_media_type`。
- `RequireJsonBody=false` 的无正文端点只接受 POST，不强制 Content-Type。
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

login 和 logout action 标记 `RequireBrowserOrigin`。login、me、logout 的所有最终响应（成功、validation、invalid credentials、challenge、forbidden、unsupported media type、账号不存在）统一设置 `Cache-Control: no-store` 和 `Pragma: no-cache`。GetCurrentUserAsync 返回 Unauthenticated 时，me action 先执行 SignOutAsync 删除陈旧 Cookie，再返回 401。该缓存头逻辑由认证响应公共扩展或中间件统一添加，不在每个分支重复。

错误契约：

- 400 validation_failed，字段字典名为 errors。
- 401 invalid_credentials。
- 401 unauthenticated。
- 403 origin_not_allowed。
- 403 forbidden（已认证但后续业务权限不足）。
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

账号种子不塞入 SQLite 初始化器。请求管道固定为：异常处理、Serilog 请求日志、HTTPS、`UseRouting()`、AuthenticationResponseHeadersMiddleware、OriginValidationMiddleware、`UseAuthentication()`、`UseAuthorization()`、静态文件，随后映射 OpenAPI、Scalar、Controller、API fallback 和 SPA fallback。必须显式调用 `UseRouting()`，Origin 中间件必须严格位于其后，否则无法读取端点元数据。Origin 中间件只对带元数据端点生效，未知 API 继续得到 route_not_found。

登录和退出 action、SystemController、OpenAPI、Scalar、专用 API 404 fallback 与 SPA fallback 显式使用 `[AllowAnonymous]` 或 endpoint `.AllowAnonymous()`。现有 IntegrationTests `Controllers/TestController.cs` 的 `/__test/application-part`、`/__test/throw`、`/__test/validation` 继续承担 002 ApplicationPart、ProblemDetails、字段校验、traceId 和请求日志回归，Controller 类必须显式标记 `[AllowAnonymous]`，保持原 200/400/500 语义。另新增独立测试控制器 `DefaultAuthorizationTestController`（无授权元数据）验证 FallbackPolicy 401，以及独立 `ForbiddenTestController`（始终失败策略）验证 403 forbidden；二者不得复用现有 TestController，也不得改变 002 夹具的预期。

TestApplicationFactory 的公共配置必须增加有效 AllowedOrigins 和两个 SeedAccounts，并把上述三个测试 Controller 作为测试 ApplicationPart 注入。现有 StartupFailureTests 及任何启动真实进程的 002 回归测试，除专门验证认证配置缺失的 AI-021 外，也必须注入有效认证配置，确保测试仍由原目标故障触发。

StartupFailureTests 除退出码、端口和脱敏断言外，还必须断言安全启动日志中的 `ExceptionType` 为 `Microsoft.Data.Sqlite.SqliteException`，且不是 OptionsValidationException；这样才能证明回归测试没有因认证配置错误假绿。

生产发布前必须把浏览器公开 Origin 完整值（scheme、host、port）写入 `Authentication__AllowedOrigins__0`；启动时缺失或格式非法直接失败，并在安全启动摘要中指出配置键，不打印允许源值。应用不把允许源列表写入日志，但运维检查命令必须在启动前对比发布 URL 与该配置。登录联调统一走 Vite 5173；直接访问 Development HTTPS 7043 不属于 003 登录验收路径。

ASP.NET Core Data Protection 密钥不在本主题持久化；服务端重启或更换实例后 Cookie 可以失效，这与主题总体设计“不承诺服务端重启后的会话恢复”一致。容器、多实例或跨部署保留会话时必须另行设计密钥存储。运行期业务不得再次解析 SeedAccounts；账号种子完成后只通过数据库账号工作。验收账号密码必须保存在本地受保护秘密中，当前主题没有找回或重置手段。

## 10. 命令

~~~powershell
dotnet tool restore
dotnet restore Server/RoundTableMeeting.slnx
dotnet tool run dotnet-ef migrations add AddUserAccounts --project Server/RoundTableMeeting.Repository.Sqlite/RoundTableMeeting.Repository.Sqlite.csproj --startup-project Server/RoundTableMeeting/RoundTableMeeting.csproj --context RoundTableMeetingDbContext --output-dir Migrations
dotnet build Server/RoundTableMeeting.slnx --configuration Debug
dotnet test Server/RoundTableMeeting.slnx --configuration Debug
dotnet run --project Server/RoundTableMeeting/RoundTableMeeting.csproj --launch-profile http
dotnet publish Server/RoundTableMeeting/RoundTableMeeting.csproj --configuration Release --output artifacts/publish
~~~

发布验收前通过部署环境注入：`Authentication__AllowedOrigins__0=https://<publish-host>`、`Authentication__SeedAccounts__0__Username`、`Authentication__SeedAccounts__0__Nickname`、`Authentication__SeedAccounts__0__Password`，以及索引 1 的同类三个键。浏览器实际访问 Origin 必须与 AllowedOrigins 完全一致；测试密码不能写入命令历史或提交文件，实际执行时由受保护脚本或 Secret Provider 注入。

## 11. 完成检查

- 空库自动迁移并创建账号唯一索引。
- 两个种子账号可重复初始化，不覆盖已有资料。
- Login、me、logout 通过真实 Cookie 闭环。
- API 401/403 返回 JSON ProblemDetails，不重定向 HTML。
- Vite Origin 允许，外部 Origin 拒绝。
- 密码、哈希、Cookie、Claim 和秘密不进入日志或响应。
- 002 框架测试和发布链路继续通过。
