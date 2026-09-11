# 服务端用户管理模块设计

## 1. 职责与授权

负责管理员使用的用户列表、添加账号、昵称修改、固定密码重置和管理员身份设置。UsersController 使用 AdminOnly 策略，依据数据库当前账号判断权限。普通用户返回 403 forbidden，未认证返回 401；先检查操作者，再查询目标。

Cookie 与当前用户身份仍由账号访问模块负责。管理员权限不扩展会议访问或删除能力；个人设置使用独立 IAccountSettingsService，不复用允许传入任意目标 ID 的管理接口。

## 2. 用例

- 列表：按 NormalizedUsername、ID 稳定排序，返回 UserId、Username、Nickname、IsAdmin，不搜索、不分页、不缓存。
- 添加：输入用户名、昵称、初始密码。沿用 Trim/NFC 用户名规则与唯一索引；初始密码包含至少一个非空白字符、最长 256，原字符串哈希。新用户一律 IsAdmin=false，创建契约不接受管理员标志。
- 昵称：输入目标 UserId 与 nickname，Trim/NFC 后非空且最长 64。只修改账号昵称，不覆盖已有会议显示名称。
- 重置密码：输入目标 UserId，无请求正文；固定将 `123456` 生成新哈希，成功 204。允许管理员重置本人，不撤销已有 Cookie，不要求首次登录强制改密。
- 管理员设置：输入目标 UserId 与必填 isAdmin，支持授权、撤销和本人降级；相同目标状态幂等返回摘要。最后一名管理员不得被取消，返回 409 last_admin_required。

## 3. 应用契约与事务

IUserManagementService 保留 ListUsersAsync、CreateUserAsync、UpdateNicknameAsync、ResetPasswordAsync，增加 UpdateAdminAsync；方法均支持 CancellationToken。操作者由 ICurrentUserContext 提供，不由请求参数指定。

管理结果表达 Success、ValidationFailed、UsernameConflict、UserNotFound、Forbidden 和 LastAdminRequired；列表可继续直接返回摘要集合，前置 AdminOnly 负责拒绝。未知异常沿统一边界传播，不能用异常文案判断业务分支。

每个管理写事务重新读取操作者并验证 IsAdmin。管理员变更在取得 SQLite 写锁后读取目标和管理员数量，并发保证至少一名管理员。昵称、密码、身份分别只更新对应列，不能用旧实体整行覆盖其他字段。事务契约及撤销生效边界以 [服务端设计](../../design.md)第 12 节为准。

新增账号沿用数据库规范化用户名唯一索引；应用预检查与并发唯一冲突均返回 username_conflict，非目标数据库错误不得误报。取消原样传播，失败回滚。

## 4. HTTP 契约

| 方法 | 路径 | 成功 | 错误状态 |
| --- | --- | --- | --- |
| GET | `/api/users` | 200 UserSummaryDto[] | 401/403/500 |
| POST | `/api/users` | 201 UserSummaryDto | 400/401/403/409/415/500 |
| PATCH | `/api/users/{userId}/nickname` | 200 UserSummaryDto | 400/401/403/404/415/500 |
| POST | `/api/users/{userId}/reset-password` | 204 | 401/403/404/500 |
| PATCH | `/api/users/{userId}/admin` | 200 UserSummaryDto | 400/401/403/404/409/415/500 |

有 JSON 的写接口使用 RequireBrowserOrigin(true)；重置无正文使用 RequireBrowserOrigin；GET 不要求 Origin。所有响应均 no-store/no-cache，包括授权拒绝。错误统一输出 ApiProblemDetails，区分 forbidden 与 origin_not_allowed。409 根据场景为 username_conflict 或 last_admin_required，404 为 user_not_found。

## 5. 日志与非目标

不返回密码哈希、固定重置密码、会话数据或配置来源。密码仅由客户端根据固定契约展示；不将输入或密码写入日志、trace、生成示例。不增加操作审计、历史记录、会话撤销、用户删除禁用、搜索分页、复杂角色组织体系。
