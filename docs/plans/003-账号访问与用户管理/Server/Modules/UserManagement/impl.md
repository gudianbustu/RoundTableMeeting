# 服务端用户管理模块实现方案

## 1. 实施边界

在已有用户管理服务、仓储和 Controller 上增加管理员授权与身份修改。领域字段和数据库 Migration 参照 [服务端实现方案](../../impl.md)第 13 节；授权与事务规则以 [模块设计](design.md)和 [服务端设计](../../design.md)为准。

## 2. 契约

UserSummaryDto 增加 bool IsAdmin。CreateUserRequest 仍只有 Username、Nickname、InitialPassword，新账号固定普通用户；UpdateNicknameRequest 不变。新增 UpdateAdminRequest，IsAdmin 必填，采用 nullable bool 加 Required 或等价绑定校验，禁止缺失值被解释为取消管理员。

IUserManagementService 增加 UpdateAdminAsync(Guid userId, UpdateAdminRequest, CancellationToken)。沿用当前统一 UserManagementStatus/Result，增加 Forbidden、LastAdminRequired，不另造互相矛盾的各用例状态枚举。列表仍返回 IReadOnlyList<UserSummaryDto>。

## 3. 用例实现

1. UsersController 的 AdminOnly 策略进行入口授权，未知或普通操作者不执行目标查询。
2. 所有写用例从 ICurrentUserContext 取得操作者 ID，开启数据库写事务后再次读取并确认管理员身份；失败返回 Forbidden，不注册任何写入。
3. 添加用户沿用规范化与唯一索引处理，新账号 false；昵称只更新 Nickname；密码只更新 PasswordHash，使用固定常量 `123456`。
4. UpdateAdminAsync 在同一事务中读取目标。不存在返回 UserNotFound；相同状态返回摘要；取消管理员前确认仍有其他管理员，否则 LastAdminRequired；成功只更新 IsAdmin 并返回最新摘要。
5. 同一事务提交或回滚；返回 DTO 使用实际提交的目标状态。取消和数据库异常沿现有边界传播。

## 4. 持久化实现

仓储提供按 ID 读取、稳定列表、管理员存在/数量查询和按列更新；写事务抽象不得暴露 EF 或 SQLite 类型。SQLite 使用取得写锁后读取再更新的事务，例如等价 BEGIN IMMEDIATE 的提供器能力；不能以 Deferred 事务外 Count 或进程内锁保证最后管理员。

停止在昵称、密码和管理员写入中使用旧 NoTracking 实体的整行 RegisterEdit。Repository 按意图只更新一个列；新增仍可沿用 RegisterAdd。任何相邻昵称/密码请求都不能恢复刚撤销的管理员身份或覆盖刚更新的密码。

## 5. Controller、错误与生成

UsersController 使用 Authorize(Policy = "AdminOnly") 与 NoStoreResponse。保留四条原接口并增加 PATCH /api/users/{userId}/admin，标记 RequireBrowserOrigin(true)。GET 增加 403 响应声明。Forbidden -> 403 forbidden；LastAdminRequired -> 409 last_admin_required；其余沿用现有统一错误映射。

在 ProblemDetailsFactory 注册 last_admin_required 的固定安全提示，不拼接数据库或用户数据。全部五条 operation 声明实际响应并重新生成 Kiota；检查 isAdmin 必填布尔、admin PATCH builder 和 403/409 映射。重置接口继续无正文，不生成可传入自定义密码的参数。

## 6. 验证

覆盖管理员成功、普通用户和匿名拒绝、本人降级、最后管理员保护、并发互相降级、状态幂等、按列写入、撤销后旧 Cookie 的下一次调用被拒绝，以及固定密码重置旧登录继续有效。使用真实临时 SQLite 与独立请求作用域验证并发，不仅依赖 fake 仓储。

## 7. 与增量实施计划对齐

阶段 A 先完成 AccountTransactionRunner 和按列更新；阶段 B 再把所有管理写用例接入。统一由执行器提交工作单元与数据库事务，服务回调不重复 CommitAsync，参见 [服务端实现](../../impl.md)第 15 节。

新增 UpdateAdminRequest 和结果枚举分支时同步 Controller switch、OpenAPI、fake 仓储和服务构造；不能只修改页面或仅给 UsersController 添加策略而遗漏事务内重新授权。验证文件映射见 [testPlan.md](../../../testPlan.md)第 19 节。
