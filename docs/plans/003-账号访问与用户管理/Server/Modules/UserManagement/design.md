# 服务端用户管理模块设计

## 1. 模块定位

用户管理模块是在已完成登录身份之上的最小内部测试能力，负责用户列表、添加账号、修改昵称和固定密码重置。它不负责登录、Cookie、当前用户上下文或会议权限；这些能力继续由账号访问模块和 Web 认证边界提供。

当前不区分普通用户和管理员。模块只要求调用者已经通过平台认证，不读取角色、权限 Claim 或环境开关。后续增加 API 权限时，在 Web 授权策略上收紧访问，不改变本模块的业务输入输出。

## 2. 模块用例

### 2.1 用户列表

返回平台中的全部账号，按 `NormalizedUsername` 升序稳定排列。每项只包含：

- `UserId`；
- `Username`；
- `Nickname`。

列表不返回密码哈希、账号来源、会话状态、角色或审计字段。当前只服务内部少量账号，不分页、不搜索、不缓存。

### 2.2 添加账号

输入用户名、昵称和初始密码。模块执行：

1. 复用 `UserAccount` 的用户名、规范化用户名和昵称规则。
2. 校验密码原字符串包含至少一个非空白字符且不超过 256 个字符；只用 IsNullOrWhiteSpace 判断有效性，不改变原字符串，不做 Trim、NFC 或大小写转换。
3. 按规范化用户名检查冲突。
4. 创建账号并使用 `IPasswordHasher<UserAccount>` 生成不可逆哈希。
5. 通过现有 `IUnitOfWork` 注册新增并提交。
6. 返回新账号的最小 DTO。

用户名冲突不回显已有账号资料，固定返回 `UsernameConflict`。数据库唯一索引继续作为最终兜底；正常冲突通过应用层预检查转换为稳定 `409 username_conflict`。并发创建导致的唯一索引冲突必须由基础设施异常转换边界映射为同一业务冲突，不能退化为 `500`。

### 2.3 修改昵称

输入目标 `UserId` 和新昵称。昵称执行 Trim + NFC，规范化后非空且最长 64。成功时更新账号并返回最小 DTO。

用户名、`UserId` 和密码不随昵称修改而变化。昵称修改只改变账号的默认昵称；已经加入会议的参与者显示名称由会议参与关系保存，不被追溯更新。

### 2.4 重置密码

输入目标 `UserId`，不接受新密码参数。Application 固定使用字符串 `123456` 生成新哈希并替换原密码哈希，提交成功后只返回成功状态。

模块不返回固定密码、不返回哈希，也不区分目标是否为当前用户。现有 Cookie 只包含 `UserId`，重置密码后已有会话继续有效；后续会话撤销与强制重新登录不属于当前范围。

## 3. 领域与仓储契约

`UserAccount` 增加两个明确行为：

- `ChangeNickname(nickname)`：复用现有昵称规范化规则。
- `ReplacePasswordHash(passwordHash)`：只接受非空哈希，用于已经存在账号的密码替换。

`InitializePasswordHash` 继续只负责新账号首次初始化，不能借密码重置放宽其一次性约束。

`IUserAccountRepository` 增加按规范化用户名稳定列出全部账号的异步查询；按 `UserId` 和规范化用户名查询继续复用现有方法。仓储保持 NoTracking，不返回 `IQueryable`，写入仍由 `IUnitOfWork` 承担。

## 4. 应用服务契约

新增独立 `IUserManagementService`，至少表达：

- `ListUsersAsync(CancellationToken)`；
- `CreateUserAsync(CreateUserRequest, CancellationToken)`；
- `UpdateNicknameAsync(Guid userId, UpdateNicknameRequest, CancellationToken)`；
- `ResetPasswordAsync(Guid userId, CancellationToken)`。

结果类型使用明确状态，不通过异常文案判断业务分支：

- `Success`；
- `ValidationFailed`；
- `UsernameConflict`；
- `UserNotFound`。

基础设施异常继续进入统一异常边界。取消必须原样传播。Application 不访问 `HttpContext`，也不检查操作者权限。

## 5. HTTP 契约

| 方法 | 路径 | 成功 | 失败 |
| --- | --- | --- | --- |
| `GET` | `/api/users` | `200 UserSummary[]` | `401`、`500` |
| `POST` | `/api/users` | `201 UserSummary` | `400 validation_failed`、`401`、`403 origin_not_allowed`、`409 username_conflict`、`415`、`500` |
| `PATCH` | `/api/users/{userId}/nickname` | `200 UserSummary` | `400 validation_failed`、`401`、`403 origin_not_allowed`、`404 user_not_found`、`415 unsupported_media_type`、`500` |
| `POST` | `/api/users/{userId}/reset-password` | `204` | `401`、`403 origin_not_allowed`、`404 user_not_found`、`500` |

Controller 级别显式 `[Authorize]`，但不添加角色或 Policy。添加、昵称修改和密码重置都在具体 action 上标记 method-level `RequireBrowserOrigin`；添加使用 POST，昵称修改使用 PATCH，密码重置使用 POST。带 JSON 正文的添加和昵称修改设置 `RequireJsonBody=true`。系统框架提交 `4abd58e` 已证明 POST、PATCH、PUT、DELETE 均可由路由正常承接。

所有失败响应复用统一 `ProblemDetailsFactory` 和 `ApiProblemDetailsWriter`。实现时新增 `username_conflict` 与 `user_not_found` 的安全 title/detail 映射，并在每个 OpenAPI operation 显式声明实际可能返回的错误状态，使 Kiota 生成 `400/401/403/404/409/415/500` 的对应错误映射。

用户列表、添加、昵称修改和密码重置的全部最终响应必须包含 `Cache-Control: no-store` 和 `Pragma: no-cache`；该规则覆盖 200、201、204 和所有 4xx/5xx 分支。

## 6. 状态与并发语义

- 用户列表是请求时快照，不提供版本号。
- 昵称修改采用最后一次成功提交为准，不增加乐观并发字段。
- 同一个账号连续重置密码的结果均为固定密码 `123456`，操作可重复。
- 添加账号依赖规范化用户名唯一索引，重复用户名不能产生第二条记录。
- 账号在操作前已不存在时返回 `404 user_not_found`。

## 7. 安全和日志边界

- 所有接口必须认证，但当前没有额外权限检查。
- 用户列表和管理结果不得进入浏览器或代理缓存。
- 初始密码和固定重置密码不得进入响应、日志、异常、trace 属性或生成示例。
- 不记录谁创建了账号、谁修改了昵称或谁执行了重置。
- 不创建审计表、重置历史、首次登录改密标志或会话撤销记录。
- 当前方案只适合少量可信内部测试人员；未来权限主题必须同时收紧菜单和 API。

## 8. 模块非目标

- 用户搜索、分页和批量导入。
- 用户名修改、删除、禁用、恢复和合并。
- 用户自行修改密码、找回密码和首次登录改密。
- 角色、权限、组织、菜单认证和 API 权限。
- 管理审计、通知和会话撤销。
