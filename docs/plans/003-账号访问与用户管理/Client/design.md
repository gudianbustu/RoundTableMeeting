# 账号访问与用户管理客户端设计

## 1. 文档定位

本文定义账号访问主题的客户端工程接入、路由、认证状态、API 适配和应用布局。主题总体边界以[主题总体设计](../design.md)为准；认证业务状态和公共出口以[认证模块设计](Modules/Authentication/design.md)为准；页面和组件交互以对应子文档为准。

本主题提供认证外壳、管理员用户管理和所有用户可用的个人设置。

当前已有基础用户管理；本设计中的管理员限制和个人设置尚待实现与验收。

## 2. 客户端目标

客户端完成后应能够：

1. 未认证时显示登录页。
2. 提交用户名和密码并处理登录结果。
3. 登录成功后显示当前用户昵称和最小账号资料。
4. 页面刷新时使用浏览器已有 Cookie 会话重新读取当前用户，不要求重新登录。
5. 用户退出后回到登录页，受保护路由不可继续展示。
6. 将登录失败、未认证、会话失效和网络错误转换为可理解的页面状态。
7. 查看用户列表并添加测试账号。
8. 修改账号昵称或将密码重置为固定值 `123456`。

## 3. 工程接入

沿用系统框架客户端设计：React、TypeScript、Vite、Ant Design、React Router、Kiota、Vitest 和 Testing Library。本主题新增目录固定如下：

```text
src/
  app/
    layouts/
      AuthenticatedLayout.tsx
    router/
    providers/
      AppProviders.tsx
  features/
    authentication/
      api/
      components/
      context/
      hooks/
      pages/
      providers/
      types/
      index.ts
    userManagement/
      api/
      components/
      pages/
      types/
      index.ts
```

页面、组件和 Hook 不直接依赖 `api/generated/`。认证 API 适配放在 `features/authentication/api/`，公共请求客户端继续由 `src/api/client/` 提供。

用户管理 API 适配放在 `features/userManagement/api/`，提供 listUsers、createUser、updateNickname 和 resetPassword；认证模块通过 `refreshCurrentUser()` 同步资料并供 AdminGate 确认当前身份，不接管用户管理列表状态。

## 4. 路由结构

最小路由结构：

| 路径 | 页面 | 认证 |
| --- | --- | --- |
| `/login` | `LoginPage` | 匿名 |
| `/` | `AuthGate -> AuthenticatedLayout -> MeetingWorkspacePage` | 必须认证 |
| `/meetings/:meetingId` | `MeetingWorkspacePage` | 必须认证，沿用会议权限 |
| `/system/status` | 既有系统状态页 | 按框架约定保持公开 |
| `/users` | `AdminGate -> UserManagementPage` | 必须认证且为管理员 |
| `/settings/profile` | `PersonalSettingsPage` | 必须认证 |
| `*` | 统一未找到页面 | 按路由上下文处理 |

认证状态尚未确定时，只有受保护路由通过 AuthGate 显示加载状态；公开 `/system/status` 始终直接渲染，不因 me 请求失败而被阻断。认证失败后受保护路由跳转 `/login`；登录页已登录时跳转安全 returnTo 或 `/`。

认证布局仅在当前用户 isAdmin=true 时显示用户管理入口，所有 authenticated 用户都可见个人设置入口；服务端独立检查管理权限。

## 5. API 适配

认证 feature 通过公共 Kiota Client 提供三个业务适配函数：

- `login(input)`：调用登录接口，返回当前用户视图模型。浏览器通过同源请求自动携带和接收 Cookie。
- `getCurrentUser()`：调用当前用户接口。`401` 转换为未认证状态，网络异常保留为可重试错误。
- `logout()`：调用退出接口并清除客户端状态。

请求适配层不把密码写入日志、不把 Cookie 暴露给业务代码，也不在客户端保存长期令牌。

## 6. 认证状态

认证状态由认证模块和应用 Provider 维护，至少包括：

- `checking`：应用正在用现有 Cookie 查询当前用户；
- `authenticated`：当前用户资料已确认；
- `anonymous`：没有有效会话；
- `error`：服务不可访问或初始化失败，可重试。

状态只保存当前页面生命周期内的用户资料；刷新后从服务端重新获取。客户端不能把“曾经登录过”当作认证事实。

## 7. 应用布局

认证完成后使用 `AuthenticatedLayout`：

- 顶部或固定区域显示当前用户昵称；
- 提供退出按钮；
- 中央区域渲染受保护页面；
- 仅管理员可见用户管理入口，所有用户可见个人设置入口；
- 不承载会议业务；个人设置表单位于独立页面。

登录页不复用需要认证的应用布局。系统状态页可以继续使用现有框架布局，后续主题再决定是否统一外壳。

## 8. 错误与交互约束

- 登录凭证错误显示统一的无效凭证提示，不说明用户名是否存在。
- `401` 只表示需要登录或会话失效，不显示内部认证细节。
- 网络错误显示服务暂时不可访问和重试入口。
- 页面卸载或调用取消不显示失败消息。
- API 错误分支使用稳定错误代码或 HTTP 状态，不使用本地化文案判断。
- 密码输入使用密码类型控件，不在 React 状态以外的持久化介质保存。

## 9. 客户端非目标

本主题客户端不实现：

- 用户搜索、分页、删除、禁用、批量管理和公开注册；
- 账号自助找回和首次登录强制改密；
- 复杂角色体系、权限树和完整账号后台；
- 记住我、长期免登录、设备管理和多设备会话；
- 会议列表、会议创建、聊天消息和 AI 业务。

## 10. 个人设置页

路由 `/settings/profile` 放在 AuthGate -> AuthenticatedLayout 内。页面位于 features/accountSettings/pages/PersonalSettingsPage.tsx，由 accountSettings feature 负责 API、视图类型和表单状态，不把表单或全部用户资料放进 AuthenticationProvider。本页设计与实现维护在本文件和 Client/impl.md，不另建文档目录。

页面显示只读用户名和身份（管理员/普通用户），提供两个独立区域：

- 昵称表单：当前昵称预填，必填、最长 64；保存成功后刷新当前用户资料，同步顶栏。说明昵称只影响之后加入会议的默认值，不覆盖已有会议名称。
- 密码表单：当前密码、新密码、确认新密码；当前密码必填，新密码至少一个非空白字符且最长 256，两次新密码一致。不 Trim 或规范化密码。提交只发送 currentPassword、newPassword，成功清空全部密码并显示成功；失败也清空密码并显示字段或安全错误，昵称输入不受影响。

两个表单各自提交、错误与取消状态，提交中防止重复操作。用户名与 isAdmin 不可编辑，本人请求不能传目标 userId 或管理员标志。当前密码错误使用 validation_failed，不视为登录失效；只有 401 清理身份。昵称保存已成功但资料刷新失败时，提示资料刷新失败并提供重试，不能把保存误报为失败。

## 11. 管理员页面边界

CurrentUserViewModel 和 UserSummaryViewModel 增加 isAdmin，API 适配必须验证其类型为 boolean，false 是有效值。AdminGate 在 AuthGate 内检查当前资料；普通用户直接访问 /users 时展示无权限状态与返回首页入口，不加载用户列表。登录 returnTo 指向 /users 时也必须经过同一检查。

进入管理路由时通过 refreshCurrentUser 重新确认身份；返回契约为 refreshed（携带 user、identityRevision、refreshRevision）或 cancelled，HTTP/网络错误仍抛出。AdminGate 只接受本次仍有效的 refreshed 结果，且其 identityRevision 与 refreshRevision 必须匹配 Provider 当前版本、用户仍 authenticated 且 isAdmin=true；Promise 正常结束、cancelled 或旧版本都不能放行。确认期间不加载管理数据，失败允许重试，不能用旧 isAdmin 放行。收到管理 API 的 403 forbidden 后立即清空列表、关闭弹窗并停止管理交互，再刷新当前用户资料；刷新失败也保持无权限状态，不恢复旧管理数据。不把 forbidden 当作全局退出。origin_not_allowed 属于独立请求错误，不自动撤销身份。

管理员身份变化不要求 WebSocket 广播或轮询。本人降级成功立即离开 /users 并刷新当前资料；其他管理员撤销权限时，后端从撤销提交后的下一次管理请求拒绝，前端在重新进入管理页、刷新或收到 forbidden 后同步菜单。

首次放行后 AdminGate 只绑定 identityRevision；正常资料刷新只改变 refreshRevision，保持页面挂载。昵称刷新失败或取消不撤销已确认页面；401、身份变化或 forbidden 立即撤销。具体状态规则以 [认证模块设计](Modules/Authentication/design.md)为准。
