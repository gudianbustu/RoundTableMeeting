# 003 账号访问与用户管理 实现审查报告（第三轮：代码 vs 设计）

## 0. 本轮审查范围与方法

- **审查对象**：`docs/plans/003-账号访问与用户管理/` 已经落地的源码实现，覆盖 `Server/`（Domain、Abstraction、Application、Repository.Sqlite、Web 宿主）与 `Client/roundTableMeeting/src/`（认证 feature、路由、Provider、公共 Client、测试），以及对应测试工程。
- **审查依据**：003 目录下的 `design.md`/`impl.md`/`testPlan.md` 及 `Server/**`、`Client/**` 子文档；本轮以**代码是否忠实实现设计、是否存在新缺陷/契约偏差**为主线。
- **审查方式**：逐文件阅读实现与测试，并与设计文档、跨端契约（OpenAPI/Kiota、ProblemDetails、Cookie、Origin、FallbackPolicy）交叉核对。未修改任何源码或文档（仅覆盖写入本审查报告）。
- **总体结论**：实现**高质量、忠实于设计**，且测试覆盖非常充分。上一轮（第二轮）的主要遗留问题——FallbackPolicy 与既有 `TestController` 的冲突——已被正确解决（`TestController` 加 `[AllowAnonymous]`，默认拒绝/403 用独立测试控制器，并用架构测试锁定管道顺序）。本轮未发现阻断性缺陷；仅若干轻微瑕疵与设计-代码偏差，均为非阻塞项。

严重程度分级：**高 / 中 / 低**；**（新）** 表示本轮实现审查新发现。

---

## 1. 已核实：设计契约在实现中的落地（正向）

以下均逐文件核对，实现与设计一致：

### 1.1 身份与持久化
- `UserAccount`（`Domain/Entities/UserAccount.cs`）：继承 `BaseEntity`，主键为继承的 `ID`，**未另设 `UserId` 属性**；显示用户名 Trim+NFC≤64，`NormalizedUsername`=ToUpperInvariant≤128；昵称 Trim+NFC 且非空≤64；`Create` 工厂 + 一次性 `InitializePasswordHash`；public 无参构造仅供 EF/TTA 物化。
- `UserAccountConfiguration`：表 `UserAccounts`，列长度 64/128/64/512，`NormalizedUsername` 唯一索引。
- Migration `AddUserAccounts`：只建表建唯一索引，不写入账号（运行时种子负责）。
- `RoundTableMeetingDbContext`：新增 `UserAccounts` DbSet，并在 `SaveChanges` 前置 `ValidateUserAccounts`（非空、长度兜底）。
- `IUserAccountRepository`：按 `NormalizedUsername` 与 `ID` 查询，默认 NoTracking，不暴露写方法、不返回 `IQueryable`。

### 1.2 认证应用层
- `IAuthenticationService`：`AuthenticateAsync(LoginRequest, CancellationToken)` + `GetCurrentUserAsync(...)`，与设计签名一致；Web Controller 用 `using AppAuthenticationService = ...` 规避与 `Microsoft.AspNetCore.Authentication.IAuthenticationService` 的同名冲突。
- `AuthenticateResult`/`CurrentUserResult`：状态枚举 + `Errors`（键固定 `username/password`）+ `User`（不含 hash）。
- `AuthenticationService`：校验（含显示名≤64、密码≤256）→ 规范化用户名查询 → `PasswordVerificationService.Verify`；账号不存在时也执行等价 dummy hash 验证再统一 `invalid_credentials`；`SuccessRehashNeeded` 视为成功且不写回；公开 `userId/username/nickname`。
- `AccountSeeder`：一次校验全部输入（含密码长度）、输入内去重、查缺、哈希、统一 `CommitAsync`；已存在账号不覆盖。
- `PasswordVerificationService`：Singleton，构造时生成 dummy hash；null 账号时执行同等工作量 PBKDF2，缓解用户名时序枚举。

### 1.3 Web 认证与授权
- `ServiceCollectionExtensions`：注册 Cookie 认证（`HttpOnly`、`SameSite=Lax`、生产 `SecurePolicy=Always`、`ExpireTimeSpan=8h`、`SlidingExpiration=false`）、`FallbackPolicy = RequireAuthenticatedUser()`、`ICurrentUserContext→HttpCurrentUserContext`（Scoped）、`IPasswordHasher`/`PasswordVerificationService`（Singleton）、两个认证中间件、`Idemic Identity` 未引入。
- `Program.cs` 管道：异常→Serilog→HTTPS→`UseRouting()`→`AuthenticationResponseHeadersMiddleware`→`OriginValidationMiddleware`→`UseAuthentication()`→`UseAuthorization()`→静态文件→映射（OpenAPI/Scalar/Controllers/API fallback/SPA fallback 均 `.AllowAnonymous()`）。`Origin` 中间件严格位于 `UseRouting()` 之后（有架构测试锁定）。
- `AuthenticationController`：login（`[AllowAnonymous]`+`[RequireBrowserOrigin]`，SignInAsync `IsPersistent=false`，返回 `CurrentUserDto`）、me（`[Authorize]`，账号不存在时 `SignOutAsync`+401）、logout（`[AllowAnonymous]`+`[RequireBrowserOrigin]`，幂等 204）。显式声明 400/401/403/415/500 响应。
- `AuthenticationProblemDetailsExtensions`：复用共享 `ApiProblemDetails` 类型与 `TraceIdResolver`，生成稳定 `code`（`invalid_credentials`/`unauthenticated`/`origin_not_allowed`/`unsupported_media_type`/`validation_failed`/`forbidden`）。
- `AuthenticationOptions`：`Validate` 校验纯 origin（无通配符/路径/尾斜杠）、去重、SeedAccounts 非空且密码长度、用户名/昵称规范化 → `ValidateOnStart`；校验错误不打印配置值。

### 1.4 客户端
- `AuthenticationProvider`：`checking/authenticated/anonymous/error` 状态机 + `isLoggingIn/isLoggingOut/operationError`；`operationId`+`AbortController` 处理 StrictMode/竞态；订阅全局 `401`（排除 `/api/auth/`）；登录保持 anonymous、成功设 authenticated；退出 204→anonymous、403/网络→保持认证；`login/logout/retry` 语义正确。
- `AuthGate`：`authenticated` 渲染 `<Outlet />`（不接收 children）；`anonymous`→`<Navigate to="/login" state={{returnTo:结构化}}>`；`error`→`ErrorState`。`safeReturnTo` 校验 pathname/search/hash。
- `LoginPage`：`authenticated` 用**单一** `Navigate` 分支导航；`getReturnTo` 三段校验后拼接；`error` 用 `PageState`。**修复了“只导航一次”与 returnTo query/hash 保留**。
- `LoginForm`：`maxLength` username=128/password=256；`onSubmit` 捕获失败后 `resetFields(['password'])`。
- `CurrentUserBar`/`AuthenticatedLayout`：仅一个用户栏、`nickname||username` 兜底；`AuthenticatedHomePage` 显示 `UserId`、用户名、昵称。
- `apiClient`：`credentials:'same-origin'`，全局 401 通知按 pathname 排除 `/api/auth/*`。
- 路由：`/login` 无布局、`/system/status` 保留 `AppLayout`、根 `/` 经 `AuthGate→AuthenticatedLayout→AuthenticatedHomePage`、`*`→NotFound。生成 `auth` 客户端含 400/401/403/415/500 错误映射；`toApiError` 归一化。

### 1.5 测试
- 测试覆盖对应 testPlan 主要编号：Domain（`UserAccountTests`）、Application（`AuthenticationServiceTests`，含 dummy hash、密码原样、SuccessRehashNeeded、种子幂等/去重/取消/提交失败）、Repository（`UserAccountRepositoryTests`，含唯一索引、NFC 等价、迁移列/索引、空账号拒持久化）、Integration（`AuthenticationContractTests`，含 Cookie 会话属性、8 小时过期、缺失账号清 Cookie、403/415 不缓存、OpenAPI 声明、claims 仅含目标 Claim、403 专用端点、管道顺序回归）、Architecture（`DependencyTests`，含引用方向、管道顺序、鉴别 Identity 未引入、Options/HttpContext 留在 Web）。客户端 Vitest 覆盖 Provider 状态机、竞态、结构化 returnTo、单一导航、AuthGate Outlet、页面组件。

---

## 2. 发现的问题（均为非阻塞）

### 2.1 【低】客户端未按设计展示 `validation_failed` 的字段级错误

- 设计：`Client/Pages/Login/impl.md` §4 要求“`validation_failed` 显示字段错误”。
- 实现：`LoginForm.tsx` 第 11 行对 `validation_failed` 只显示通用 `error.message`（ProblemDetails detail，即“One or more validation errors occurred.”），未按 `error.fieldErrors` 逐字段呈现。
- 影响：用户名/密码长度等校验失败时用户只看到一句笼统提示，与“字段错误”的契约不完全一致。属 UX 细节。
- 建议：在 `LoginForm` 依据 `error.fieldErrors` 渲染用户名/密码字段错误，或明确降级为通用提示并同步设计。可留待后续优化，不阻塞。

### 2.2 【中低】Origin 中间件的 `application/json` 校验用 `/login` 路径启发式，无法推广到未来业务写端点

- 设计：`Server/design.md` §6.5“带请求体的登记端点只接受 ... `application/json`”。
- 实现：`OriginValidationMiddleware.cs` 第 19–21 行用 `context.Request.Path?.EndsWith("/login")` 判断是否要求 JSON；`logout`（无正文）不强制。若未来业务写端点标记 `RequireBrowserOrigin` 且带正文，因 `isLogin=false` 将不会被强制 JSON。
- 影响：当前只有 login/logout 两个端点，无实际缺陷；但把 content-type 规则耦合到 `/login` 路径，未来难以复用，属设计-实现偏差与潜在坑。
- 建议：为需要强制 JSON 的能力引入显式元数据（如 `RequireJsonBody`）或让 `RequireBrowserOriginAttribute` 带参数，而不是依赖路径字符串。

### 2.3 【低】`Program.cs` 静态文件位置与 `Server/impl.md` §9 描述不一致

- 文档（`Server/impl.md` §9，第 220 行）：“HTTPS/静态文件、`UseRouting()`、...”。
- 实现（`Program.cs` 第 75–76 行）：`UseDefaultFiles()`/`UseStaticFiles()` 放在 `UseAuthorization()` **之后**、映射之前。
- 影响：因 SPA fallback `MapFallback` 为 `AllowAnonymous`，未认证用户仍能加载静态资源与 SPA，**实际可用**；但文档-代码顺序不一致，易误导后续维护者（若将来把 `MapFallback` 改为需认证，静态资源会被误伤）。
- 建议：要么按文档把静态文件移到 `UseRouting()` 之前，要么更新文档描述，使两者一致。

### 2.4 【低】testPlan `AD-009` 与实际 Domain 测试不符

- testPlan（第 60 行）`AD-009`：“64 长度显示用户名大写后超过 128 | 实体按 NormalizedUsername 上限拒绝”。
- 实现：`UserAccountTests`（第 76–84 行）`NormalizedUsernameExpansionBoundaryIsUnreachableForBoundedInvariantCasing` 注释承认该溢出场景**不可复现**（.NET invariant casing 无稳定的多码点膨胀样本），实际只断言 64 个小写 `a` 规范化后为 64 且在 1–128 区间。
- 影响：`AD-009` 的抗溢出防御未被真正测试，属于测试覆盖缺口；实体层 `NormalizeUsername` 的 `>128` 拒绝仍存在（防御性）。
- 建议：要么补充一个真正触发溢出的构造样本（若存在稳定字符），要么将 testPlan `AD-009` 描述改为“规范化长度上限拒绝”并注明不可复现边界。

### 2.5 【低】若干实现细节可优化（轻微，非缺陷）

- 中间件注册冗余：`AddSingleton<AuthenticationResponseHeadersMiddleware>()`/`AddSingleton<OriginValidationMiddleware>()`，但 `UseMiddleware<T>()` 对非 `IMiddleware` 类型实际用 `ActivatorUtilities` 实例化，DI 注册未被使用（无害）。
- `AuthenticationProblemDetailsExtensions` 直接 `new ApiProblemDetails`，未复用现有 `ProblemDetailsFactory` 的构造方法；但复用了共享 `ApiProblemDetails` 类型 + `TraceIdResolver`，契约一致。
- 路由器：`AuthenticatedHomePage` 用 `{ path: '/' }`（设计文档为 `index: true`），功能等价。
- `UserAccount.Create` 在构造器与对象初始化器重复计算 `NormalizedUsername`，冗余但结果一致（幂等）。
- `PasswordVerificationService` 的 dummy hash 在**首次解析**时生成（懒加载），与设计“启动时生成”略有出入；功能上等价。

---

## 3. 完成结论

**结论：003 账号访问与用户管理的设计与实现已闭环，可以进入下一阶段（会议创建、加入与工作台），无需为当前主题返工。** 阶段性唯一遗留的高风险项（FallbackPolicy 与既有 `TestController` 的回归冲突）已在实现中正确解决并附加架构测试锁定；服务端/客户端/测试三者与设计契约一致，未发现阻断性问题。

剩余项（第 2.1–2.5 节）均为低/中低、非阻塞的打磨项，可在后续迭代或代码走查时顺手处理：

- 优先：2.2（Origin content-type 规则去 `isLogin` 路径耦合）与 2.3（管道顺序文档-代码对齐）。
- 其次：2.1（`validation_failed` 字段错误展示）、2.4（`AD-009` 测试语义对齐）、2.5（轻微冗余）。

实现建议以 `testPlan.md` 的 `P0/P1` 为验收门；`P2` 项可作为后续优化。当前代码已通过设计文档所述的分层职责、安全目标与跨端契约，具备继续推进的业务与技术条件。

> 说明：本报告仅针对 003 目录所述设计与已落地实现；未修改任何源码或文档，仅覆盖写入本审查报告。审查依据为代码走读与测试核对，未实际执行构建/测试命令，运行结果请以 CI/本地构建为准。
