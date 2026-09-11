# 服务端账号访问模块实现方案

## 1. 实现范围

本文将[账号访问模块设计](design.md)落地为 Domain、Abstraction、Application 和 Repository 的实现顺序。HTTP Cookie、Origin 校验和 Controller 由[服务端实现方案](../../impl.md)负责。

模块只实现登录凭证验证、当前账号查询、配置种子和账号资料映射。用户列表、用户创建、昵称修改和密码重置由同主题的 UserManagement 模块负责；本模块不实现角色、审计或会话管理。

## 2. 实施顺序

### 2.1 已有实现的复用

保留 UserAccount 工厂、用户名规范化、PasswordVerificationService、LoginRequest、AuthenticateResult、CurrentUserResult 和当前用户上下文。仅给 UserAccount、CurrentUserDto 增加 IsAdmin，并修改 AuthenticationService 映射；不重新实现登录、dummy hash 或 Cookie。

### 2.2 初始化增量

1. IAccountSeeder.SeedAsync 增加 string? initialAdminUsername 参数，Program 从 Options 传入；同步所有调用与测试 fake。
2. AccountSeeder 注入账号事务执行器。取得写锁后创建缺失普通用户；无管理员时提升显式指定账号，新增与提升共同提交。
3. 已有管理员时不覆盖身份；无新增但有提升仍须提交。数据库异常和取消整笔回滚。
4. 仓储与迁移实施落点见 [服务端实现方案](../../impl.md)第 3、15 节；保留原迁移，仅新增 AddUserAccountAdmin。

### 2.3 结果与分层

AuthenticateAsync 与 GetCurrentUserAsync 的状态枚举保持现有定义，DTO 增加 bool IsAdmin。ICurrentUserContext 仍为 bool IsAuthenticated 与 Guid? UserId。新增管理员查询服务不把权限缓存到 Cookie。

Application 不引用 Options、HttpContext 或 EF 类型，Repository 不读取秘密或验证密码。对既有认证异常和取消语义做回归，不以重构扩大本轮范围。

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

AccountSeeder 接收种子集合与 initialAdminUsername，在写事务中创建缺失普通用户；无管理员时提升显式指定账号，已有管理员时不覆盖资料或身份。具体顺序与失败回滚按 Server/design.md 第 5.3 节执行。

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

## 管理员资料与初始管理员

CurrentUserDto 增加必填 bool IsAdmin；登录与 me 都从数据库映射，不从 Cookie 读取管理员快照。ICurrentUserContext 仍只提供 IsAuthenticated 和 UserId；当前身份不存在时保持 401 语义。

种子契约增加可选 initialAdminUsername，由 Web 从 Authentication:InitialAdminUsername 映射。缺失账号默认普通用户；当数据库没有管理员时，必须通过显式配置找到一个账号并在事务中提升，否则启动失败。有管理员时忽略该配置，不覆盖已有昵称、密码和身份。完整初始化顺序以 [服务端设计](../../design.md)第 5.3 节为唯一规则。

管理员运行期查询通过 IAdministratorAccessService 读取当前数据库；不引入角色 Claim 或跨请求缓存。个人修改密码由独立 AccountSettingsService 执行，复用现有 PasswordHasher/密码验证规则，不混入登录返回类型或 Cookie 操作。
