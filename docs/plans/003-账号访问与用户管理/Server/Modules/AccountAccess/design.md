# 服务端账号访问模块设计

## 1. 模块职责

账号访问模块是服务端登录与当前身份用例的业务边界，负责预置账号查询、凭证验证和当前用户资料读取。浏览器退出由 Web 层清除 Cookie，不属于 Application 用例。用户列表、添加、昵称修改和密码重置由独立的用户管理模块负责。

模块对外提供稳定的应用服务契约，对内组合账号仓储、密码哈希器和请求级当前用户上下文。HTTP Controller、当前用户上下文实现、Cookie 配置和 Origin 校验属于 Web 边界；数据库实现属于 Repository 边界。

## 2. 目录与依赖

模块源码职责固定如下，准确文件清单以实现文档为准：

```text
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
  AccountAccess/
    AuthenticationService.cs
    AccountSeeder.cs
    PasswordVerificationService.cs
RoundTableMeeting.Repository.Sqlite/
  Database/Configurations/UserAccountConfiguration.cs
  Repositories/UserAccountRepository.cs
RoundTableMeeting/
  Controllers/AuthenticationController.cs
  Authentication/
    HttpCurrentUserContext.cs
    RequireBrowserOriginAttribute.cs
```

依赖方向保持 `Web -> Application -> Abstraction/Domain`，`Repository -> Domain/Abstraction`。Application 不引用 Web、EF Core 或具体 Cookie 类型。

## 3. 模块用例

### 3.1 登录

输入用户名和密码，输出登录结果及当前用户最小资料。应用服务执行：

1. 对输入做非空和长度上限校验。
2. 按用户名查询账号。
3. 使用 `IPasswordHasher<UserAccount>` 验证密码。
4. 凭证有效时返回 `UserId`、用户名和昵称。
5. 凭证无效时返回统一的无效凭证结果。

应用服务不生成 Cookie；Controller 根据成功结果建立 Cookie。

### 3.2 当前用户

输入由统一当前用户上下文提供的 `UserId`，输出当前账号资料。若当前身份匿名、Claim 无法解析或账号不存在，返回未认证结果；不能根据用户名、昵称或请求参数恢复身份。

### 3.3 退出边界

退出由 Web 层直接清除 Cookie，账号访问模块不定义 logout 应用服务，也不保存或撤销持久化会话。

### 3.4 初始账号种子

初始化器读取配置账号集合，通过仓储查询并新增不存在的账号。种子逻辑允许重复执行，不覆盖已经存在的账号资料。

## 4. 最小契约

### 4.1 当前用户上下文

请求级当前用户上下文只提供：

- `IsAuthenticated`；
- `UserId`；

未认证时不返回伪造的空 `Guid` 作为用户身份；调用方必须先判断 `IsAuthenticated`。用户名和昵称通过 `GetCurrentUserAsync` 按 `UserId` 查询，不进入 Cookie Claim 或请求级上下文。

### 4.2 认证服务

认证服务至少表达以下操作：

- `AuthenticateAsync(LoginRequest request, CancellationToken cancellationToken)`；
- `GetCurrentUserAsync(CancellationToken cancellationToken)`；

`AuthenticateResult` 状态固定为 Success、InvalidCredentials、ValidationFailed；ValidationFailed 携带 `Errors: IReadOnlyDictionary<string, string[]>`，字段键固定为 `username` 和 `password`。`CurrentUserResult` 状态固定为 Success、Unauthenticated。返回类型不暴露 `UserAccount`、密码哈希、EF 实体状态或 `ClaimsPrincipal`。

### 4.3 仓储

`IUserAccountRepository` 至少提供按规范化用户名查询、按 `UserId` 查询和新增账号所需的能力。写入继续通过现有 `IUnitOfWork` 完成；仓储不隐式提交，不返回 `IQueryable` 给 Application。配置查重、登录查询和数据库唯一索引统一使用去除首尾空格、Unicode NFC 和不区分大小写的规范化用户名。

## 5. 账号查询语义

- 用户名查询只用于登录和种子初始化。
- 登录和种子初始化在查询前使用同一用户名规范化函数。
- 查询结果不得把密码哈希映射到跨端 DTO。
- 用户名唯一约束由数据库保证，应用层重复检查只用于产生可理解的错误。
- 账号不存在和密码错误在认证服务对外统一为同一个无效凭证结果。
- 账号记录被删除或不可用时，已有 Cookie 在当前用户查询中按未认证处理；本主题不实现账号禁用状态。

## 6. Controller 责任

`AuthenticationController` 只拥有三条路由：

| 路由 | Controller 行为 |
| --- | --- |
| `POST /api/auth/login` | 绑定请求、调用认证服务、建立最小 Claim Cookie、返回当前用户 DTO |
| `GET /api/auth/me` | 读取当前用户上下文、调用当前用户查询、返回 DTO 或 `401` |
| `POST /api/auth/logout` | 清除 Cookie，保持幂等 |

Controller 不读取密码哈希、不直接查询 DbContext、不把异常文案当作业务分支、不返回认证对象。

## 7. 配置种子边界

种子配置使用服务端 Options 绑定并在启动阶段验证：

- 账号集合不能为空；
- 每个账号的用户名和昵称规范化后不能为空；初始密码按原字符串非空且长度为 1 至 256，不做 Trim 或 Unicode 规范化；
- 配置内用户名不得重复；
- 明文密码不得复制到账号实体、数据库、日志、错误或应用级长期状态；配置 Provider 可能在进程生命周期内持有部署秘密，这是配置系统边界，不宣称明文从进程内存完全消失；
- 种子用例完成后释放启动 scope 和种子 DTO，不在 Application 或 Repository 缓存明文密码；
- 种子过程不输出配置对象。

`IAccountSeeder` 和种子输入 DTO 位于 Abstraction，由 Application 实现；Web 负责绑定和验证 Options，在 `IDatabaseInitializer` 成功后把 DTO 集合传给种子用例。Application 不引用 Web Options 或配置 Provider，通过仓储和事务创建缺失账号，并在无管理员时初始化指定管理员；Repository 不读取配置或决定种子流程。任何需要人类登录的环境缺少有效账号都必须启动失败。

测试可以使用随机临时数据库和测试配置，不能依赖开发数据库中的账号。

## 8. 失败语义

| 场景 | 模块结果 | HTTP 结果 |
| --- | --- | --- |
| 请求字段为空 | 验证失败 | `400`，字段错误 |
| 用户名不存在 | 无效凭证 | `401`，`code = invalid_credentials` |
| 密码不匹配 | 无效凭证 | `401`，`code = invalid_credentials` |
| 当前会话不存在 | 未认证 | `401`，`code = unauthenticated` |
| 账号种子配置无效 | 初始化失败 | 启动失败，不开放业务请求 |
| 数据库查询失败 | 基础设施异常 | 统一 `ProblemDetails`，不泄露内部细节 |

字段校验固定使用 `400`、`code = validation_failed` 和 ProblemDetails 传输字段 `errors`。客户端公共错误层将 `errors` 转换为 `fieldErrors`。认证 API 必须在 OpenAPI operation 中声明这些错误响应，本模块不让客户端通过错误文案判断分支。

## 9. 模块非目标

模块本身不添加以下服务或接口：

- `CreateUser`、`ListUsers`、`UpdateNickname`、`ResetPassword`；这些操作属于同主题的用户管理模块；
- 角色和权限管理服务；
- Session 查询、设备列表和全局撤销服务；
- 登录审计、风险评分或通知服务。

这些能力要在核心会议、群聊和 AI 协作链路完成后，按新的需求和设计进入。

## 管理员资料与初始管理员

CurrentUserDto 增加必填 bool IsAdmin；登录与 me 都从数据库映射，不从 Cookie 读取管理员快照。ICurrentUserContext 仍只提供 IsAuthenticated 和 UserId；当前身份不存在时保持 401 语义。

种子契约增加可选 initialAdminUsername，由 Web 从 Authentication:InitialAdminUsername 映射。缺失账号默认普通用户；当数据库没有管理员时，必须通过显式配置找到一个账号并在事务中提升，否则启动失败。有管理员时忽略该配置，不覆盖已有昵称、密码和身份。完整初始化顺序以 [服务端设计](../../design.md)第 5.3 节为唯一规则。

管理员运行期查询通过 IAdministratorAccessService 读取当前数据库；不引入角色 Claim 或跨请求缓存。个人修改密码由独立 AccountSettingsService 执行，复用现有 PasswordHasher/密码验证规则，不混入登录返回类型或 Cookie 操作。
