# 服务端账号访问模块实现方案

## 1. 实现范围

本文将[账号访问模块设计](design.md)落地为 Domain、Abstraction、Application 和 Repository 的实现顺序。HTTP Cookie、Origin 校验和 Controller 由[服务端实现方案](../../impl.md)负责。

模块只实现登录凭证验证、当前账号查询、配置种子和账号资料映射，不实现用户创建、密码修改、密码重置、角色、审计或会话管理。

## 2. 实施顺序

### 2.1 Domain

1. 新增 UserAccount : BaseEntity。
2. 新增用户名规范化纯函数：Trim、Unicode NFC、空值拒绝、ToUpperInvariant 比较值。
3. 新增 IUserAccountRepository，声明按规范化用户名、按 Guid 查询和必要的新增查询能力。
4. 增加 Domain.Tests 的实体、规范化和主键测试。

### 2.2 Abstraction

新增 LoginRequest、CurrentUserDto、SeedAccountInput、AuthenticateResult、CurrentUserResult、IAuthenticationService、ICurrentUserContext 和 IAccountSeeder。DTO 不包含 PasswordHash、Cookie、Claim 或 EF 类型。

接口形状固定为：AuthenticateAsync(LoginRequest, CancellationToken) 返回 AuthenticateResult，状态只含 Success、InvalidCredentials、ValidationFailed；ValidationFailed 携带 Errors 字典，键固定为 username/password。GetCurrentUserAsync(CancellationToken) 返回 CurrentUserResult，状态只含 Success、Unauthenticated；SeedAsync(IReadOnlyCollection<SeedAccountInput>, CancellationToken) 无成功业务返回值，失败直接抛出。Web 只根据状态枚举映射 HTTP，不根据错误文案判断。

ICurrentUserContext 固定为 `bool IsAuthenticated` 和 `Guid? UserId`。UserAccount 通过 Create 工厂建立用户名和昵称，再由 AccountSeeder 计算哈希并调用一次性 InitializePasswordHash；Repository 不接受 PasswordHash 为空的新增账号。

### 2.3 Application

1. 消费由 Web 组合根注册的 IPasswordHasher<UserAccount>。
2. 实现 PasswordVerificationService，进程启动时生成一次 dummy account/hash；账号不存在时仍执行等价 VerifyHashedPassword，降低用户名时序枚举风险。
3. 实现 AuthenticationService。
4. 实现 AccountSeeder。
5. 所有异步方法传递 CancellationToken。
6. 认证结果使用统一结果类型，普通无效凭证不使用异常表示。
7. 数据库、哈希器或工作单元异常向 Web 集中错误处理边界上抛。

### 2.4 Repository.Sqlite

1. 在 DbContext 增加 DbSet<UserAccount>。
2. 增加 UserAccountConfiguration。
3. 对 NormalizedUsername 建立精确唯一索引。
4. 实现按规范化值查询。
5. 生成并验证 AddUserAccounts Migration。
6. 使用真实 SQLite 测试 NoTracking、唯一约束、查询和种子持久化。

## 3. 用户名规范化

统一逻辑顺序：

1. null 或空白输入失败。
2. Trim 去除首尾空白。
3. Normalize(FormC) 执行 Unicode NFC。
4. 规范化后为空则失败。
5. 比较值为规范化显示值的 ToUpperInvariant()。

实体保存显示值和比较值。配置查重、登录查询和数据库索引全部使用同一比较值，不使用 SQLite NOCASE、当前区域文化或客户端 JavaScript 比较替代。

## 4. AuthenticationService

LoginRequest 使用 `[Required]` 和 `[StringLength]` 固定 HTTP 模型校验：username 原始输入最大 128 个 UTF-16 code unit，password 最大 256。Application 再执行防御性校验：用户名 Trim + NFC 后显示值最大 64，NormalizedUsername 最大 128；失败返回 ValidationFailed + Errors。登录时查询账号并通过 PasswordVerificationService 验证；账号不存在时使用 dummy hash 执行等价 PBKDF2 验证。Success 和 SuccessRehashNeeded 都视为成功，但本主题不写回重哈希；成功映射 CurrentUserDto，账号不存在和密码错误统一返回 invalid_credentials。密码按原字符串验证，不做 Trim、NFC 或大小写转换。

当前用户：读取 ICurrentUserContext 的 IsAuthenticated 和 UserId，匿名或 Guid 无效返回 unauthenticated，按 UserId 查询账号并映射资料；账号不存在同样返回 unauthenticated。

Application 不创建或清除 Cookie，不访问 HttpContext，不返回 PasswordHash 或 ClaimsPrincipal。

## 5. AccountSeeder

AccountSeeder 接收 Web 已验证的种子输入集合，重新规范化并检查输入内重复，查询已有账号，缺失账号生成哈希并注册新增，已有账号不覆盖资料，有新增时统一 CommitAsync。配置为空、用户名冲突、哈希失败、数据库失败和取消均使初始化失败。

## 6. 结果和错误映射

| 模块结果 | HTTP | code | 传输字段 |
| --- | --- | --- | --- |
| 输入字段无效 | 400 | validation_failed | errors |
| 用户名不存在或密码错误 | 401 | invalid_credentials | 无账号提示 |
| 当前会话缺失或账号不存在 | 401 | unauthenticated | 无 |
| Origin 不允许 | 403 | origin_not_allowed | 无 |
| 已认证但无业务权限 | 403 | forbidden | 无 |
| 请求媒体类型不支持 | 415 | unsupported_media_type | 无 |
| 未处理基础设施异常 | 500 | internal_error | 安全 detail、traceId |

客户端 toApiError 将 errors 映射为 fieldErrors；服务端代码、OpenAPI 和测试不得使用 fieldErrors 作为 JSON 字段。

## 7. 测试实现映射

Application.Tests 使用 fake 仓储、fake 当前用户上下文、真实 PasswordHasher<UserAccount> 和 fake UoW，覆盖有效登录、无效凭证、空用户名、当前用户匿名、账号不存在、种子新增、重复执行、配置内重复、已有账号不覆盖和取消传播。只有真实 PasswordHasher 无法稳定构造的 SuccessRehashNeeded 分支允许使用受控 fake 驱动，并断言本主题不写回密码哈希。

Domain.Tests 只测试纯规范化和实体约束，不引用 Web 或 EF Core。架构测试确保 Application 不引用 Repository.Sqlite、Web、EF Core 或 Options。

## 8. 集成点检查

- Repository 不比较密码、不读取配置、不决定 HTTP 状态。
- IAccountSeeder 由 Web 在 Migration 后调用。
- ICurrentUserContext 实现在 Web，不在 Application。
- AuthenticationController 只通过 Application 服务取得结果。
