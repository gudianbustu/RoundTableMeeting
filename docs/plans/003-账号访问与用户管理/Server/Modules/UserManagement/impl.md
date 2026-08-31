# 服务端用户管理模块实现方案

## 1. 实现范围

本文将[用户管理模块设计](design.md)落地为 Domain、Abstraction、Application、Repository.Sqlite 和 Web 的实施细节。认证、Cookie 与 Origin 框架继续复用现有 003 和提交 `4abd58e`，不在本模块重复实现。

## 2. 文件落点

```text
Server/
  RoundTableMeeting.Domain/
    Entities/UserAccount.cs
    Exceptions/UsernameConflictException.cs
    Repositories/IUserAccountRepository.cs
  RoundTableMeeting.Abstraction/
    Contracts/UserManagement/UserSummaryDto.cs
    Contracts/UserManagement/CreateUserRequest.cs
    Contracts/UserManagement/UpdateNicknameRequest.cs
    Contracts/UserManagement/UserManagementResults.cs
    Services/IUserManagementService.cs
  RoundTableMeeting.Application/
    UserManagement/UserManagementConstants.cs
    UserManagement/UserManagementService.cs
  RoundTableMeeting.Repository.Sqlite/
    UnitOfWork/UnitOfWorkImpl.cs
    Repositories/UserAccountRepository.cs
  RoundTableMeeting/
    Authentication/NoStoreResponseAttribute.cs
    Authentication/SensitiveResponseHeadersMiddleware.cs
    Controllers/UsersController.cs
```

不增加 Migration：现有 UserAccounts 表字段已经足够支持昵称修改和密码哈希替换。

## 3. Domain 修改

### 3.1 ChangeNickname

`UserAccount.ChangeNickname(string nickname)` 调用现有 `NormalizeNickname`，成功后只替换 Nickname。空白、NFC 后为空和超过 64 继续抛 ArgumentException。

### 3.2 ReplacePasswordHash

`ReplacePasswordHash(string passwordHash)` 要求非空白且不超过持久化列上限 512，替换已有 PasswordHash。它不能复用或放宽 `InitializePasswordHash` 的一次性创建语义。

### 3.3 用户名冲突异常

新增 Domain 级 `UsernameConflictException`，不引用 EF 或 SQLite。Repository.Sqlite 的 `UnitOfWorkImpl.CommitAsync` 包装现有 `EfUnitOfWorkCore.CommitAsync`，只在识别到唯一索引 `IX_UserAccounts_NormalizedUsername` 对应的 SQLite 约束异常时转换为该异常；Application 捕获并返回 UsernameConflict。其他 DbUpdateException 原样上抛，现有并发异常转换保持不变。

## 4. Abstraction 契约

### 4.1 DTO

- `UserSummaryDto(Guid UserId, string Username, string Nickname)`。
- `CreateUserRequest`：Username `[Required][StringLength(128)]`、Nickname `[Required][StringLength(64)]`、InitialPassword `[Required][StringLength(256)]`。默认 Required 拒绝空字符串和全空白字符串；Application 使用 IsNullOrWhiteSpace 做同一防御性校验。合法密码仍按原字符串传给哈希器，保留首尾空格，不做 Trim 或 Unicode 规范化。
- `UpdateNicknameRequest`：Nickname `[Required][StringLength(64)]`。

请求属性只承担 HTTP 原始长度与必填校验；Application 继续执行 Trim/NFC 后的防御性校验。

### 4.2 结果

- `CreateUserStatus`: Success、ValidationFailed、UsernameConflict。
- `UpdateNicknameStatus`: Success、ValidationFailed、UserNotFound。
- `ResetPasswordStatus`: Success、UserNotFound。

ValidationFailed 携带 `Errors: IReadOnlyDictionary<string,string[]>`，字段键固定为 username、nickname、initialPassword。列表直接返回 IReadOnlyList<UserSummaryDto>。

### 4.3 IUserManagementService

接口按设计固定四个异步方法，全部接受 CancellationToken。接口不接收操作者 UserId，不做权限判断；Web 的 `[Authorize]` 是当前唯一访问门槛。

## 5. Repository

`IUserAccountRepository.ListAllAsync` 返回 `IReadOnlyList<UserAccount>`，查询使用 `OrderBy(account => account.NormalizedUsername).ThenBy(account => account.ID)` 和 NoTracking。

修改流程继续使用按 ID 的 NoTracking 实体，再由 IUnitOfWork.RegisterEdit 作为 Detached -> Modified 提交。列表和写操作共享同一个 Scoped DbContext。

唯一索引异常转换必须校验 SQLite error code 与目标索引/列，不能把其他唯一或数据库异常误报为用户名冲突。

## 6. UserManagementService

### 6.1 ListUsersAsync

调用 ListAllAsync 并映射 DTO，不把实体列表或 PasswordHash 暴露给调用方。

### 6.2 CreateUserAsync

1. 防御性校验 request 和三个字段。
2. 使用 UserAccount.NormalizeDisplayUsername/NormalizeUsername/NormalizeNickname。
3. 密码用 IsNullOrWhiteSpace 检查至少包含一个非空白字符，并限制最长 256；校验通过后把原字符串交给哈希器。
4. 按 NormalizedUsername 查询；存在则 UsernameConflict。
5. UserAccount.Create，PasswordHasher.HashPassword，InitializePasswordHash。
6. RegisterAdd + CommitAsync。
7. 捕获 UsernameConflictException 并返回同一冲突结果。

密码哈希失败、取消或其他数据库异常向上抛出。

### 6.3 UpdateNicknameAsync

1. 校验 nickname。
2. FindByIdAsync；不存在返回 UserNotFound。
3. ChangeNickname。
4. RegisterEdit + CommitAsync。
5. 返回更新 DTO。

昵称不变仍允许 Commit；Repository/EF 可返回 0 或 1，业务结果仍为成功，但测试必须确认资料没有其他字段变化。

### 6.4 ResetPasswordAsync

1. FindByIdAsync；不存在返回 UserNotFound。
2. PasswordHasher.HashPassword(account, UserManagementConstants.ResetPassword)，常量固定 `123456`。
3. ReplacePasswordHash。
4. RegisterEdit + CommitAsync。
5. 返回 Success。

常量位于 Application 内部，不进入 DTO、配置、OpenAPI schema 或响应。

## 7. Web Controller

UsersController 使用 `[ApiController]`、`[Route("api/users")]`、`[Authorize]` 和 `[NoStoreResponse]`。不添加角色或 Policy。

- GET `/api/users`: 200 数组；401/500。
- POST `/api/users`: `[RequireBrowserOrigin(true)]`；201 + UserSummary；400/401/403/409/415/500。当前不提供单用户 GET，因此不声明指向不存在端点的 Location。
- PATCH `/{userId}/nickname`: `[RequireBrowserOrigin(true)]`；200；400/401/403/404/415/500。
- POST `/{userId}/reset-password`: `[RequireBrowserOrigin]`；204；401/403/404/500。

Controller 将结果状态映射为统一 ApiProblemDetailsWriter：UsernameConflict -> 409 username_conflict；UserNotFound -> 404 user_not_found；ValidationFailed -> 400 validation_failed。

## 8. 敏感响应禁缓存

新增方法/类级 `NoStoreResponseAttribute` 和 `SensitiveResponseHeadersMiddleware`。具体管道位置以 [服务端实现方案 §9](../../impl.md#9-启动和管道顺序) 为唯一依据；该中间件在 UseRouting 后读取 endpoint metadata，并在 Origin/Authentication/Authorization 前通过 Response.OnStarting 写入 no-store/no-cache，不在本模块复制第二套顺序定义。

将 AuthenticationController 与 UsersController 都标记 `[NoStoreResponse]`，由该中间件替代 AuthenticationResponseHeadersMiddleware。删除旧中间件及注册，避免两套缓存逻辑。OpenAPI、静态文件和系统状态不标记。

## 9. ProblemDetails 和 OpenAPI

ProblemDetailsFactory 增加：

- username_conflict: title `Username conflict`，安全 detail `The username is already in use.`。
- user_not_found: title `User not found`，安全 detail `The requested user does not exist.`。

四个 operation 全部显式声明 ApiProblemDetails。生成后检查 PATCH request builder、UserSummaryDto、CreateUserRequest、UpdateNicknameRequest 和 404/409 错误映射。

## 10. 依赖与生命周期

UserManagementService 使用 Scoped 标记自动注册。PasswordHasher 保持 Singleton；Repository、UoW、DbContext 保持 Scoped。UsersController 只注入 IUserManagementService。

## 11. 完成检查

- 不新增数据库字段或 Migration。
- 用户名冲突的预检查和并发唯一索引冲突都返回 409。
- 密码重置不接受请求正文。
- 所有用户管理响应 no-store/no-cache。
- 任何已登录用户均可调用，未登录统一 401。
- 不记录密码、哈希、操作人或审计信息。
