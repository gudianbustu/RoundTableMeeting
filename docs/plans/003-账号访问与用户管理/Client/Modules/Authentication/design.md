# 客户端认证模块设计

## 1. 模块职责

认证模块负责客户端登录、当前用户初始化、会话状态展示、退出和受保护路由所需的状态协调。它不解析 Cookie、不保存 Token、不决定服务端权限，也不承载用户列表与管理请求。

模块通过 feature 公共出口向 `app` 暴露 Provider、认证 Hook、用户视图模型和页面需要的最小操作。

## 2. 固定目录

```text
features/authentication/
  api/
    login.ts
    getCurrentUser.ts
    logout.ts
  components/
    LoginForm.tsx
    CurrentUserBar.tsx
    AuthGate.tsx
  context/
    AuthenticationContext.ts
  hooks/
    useAuthentication.ts
  pages/
    LoginPage.tsx
  providers/
    AuthenticationProvider.tsx
  types/
    authentication.ts
  index.ts
```

文件名和目录按本节冻结；实现文档补充具体类型和函数签名。

## 3. 状态模型

认证状态使用以下互斥状态：

- `checking` 可以进入 `authenticated`、`anonymous` 或 `error`。
- `authenticated` 可以因退出或会话失效进入 `anonymous`。
- `anonymous` 提交登录时保持 `anonymous`，通过独立 `isLoggingIn` 表达进行中；登录成功进入 `authenticated`，失败仍为 `anonymous`。
- `error` 可以在用户重试时进入 `checking`。
- 重新初始化时，当前状态可以暂时进入 `checking`，但旧请求不能覆盖较新的登录或退出结果。

`authenticated` 状态必须包含由服务端确认的当前用户资料；`anonymous` 不包含旧用户资料；`error` 不得误显示为已登录。

## 4. Provider 与 Hook

`AuthenticationProvider` 固定放在 `features/authentication/providers/`，通过 feature 的 `index.ts` 公开，并由 `app/providers/AppProviders.tsx` 组合。它负责：

- 首次渲染时调用 `getCurrentUser()`；
- 将结果转换为认证状态；
- 暴露 `login(username, password)`；
- 暴露 `logout()`；
- 暴露不改变页面路由的 `refreshCurrentUser()`，供用户管理等资料修改功能在当前账号昵称变化后刷新认证资料；
- 暴露 `isLoggingIn`、`isLoggingOut` 和 `operationError`；
- 在收到业务 API 的 `401` 时清理当前用户并转为匿名；
- 防止并发初始化请求覆盖较新的登录或退出结果。

`useAuthentication()` 是页面和组件读取认证状态的唯一入口。组件不自行调用 `getCurrentUser()`，也不直接操作 Router 进行重复的认证判断。

## 5. 初始化与刷新

应用加载和页面刷新时：

1. 状态设为 `checking`。
2. 调用 `GET /api/auth/me`。
3. 成功时保存服务端返回的用户视图模型并设为 `authenticated`。
4. `401` 时清理内存状态并设为 `anonymous`。
5. 网络或未知错误时设为 `error`，提供重试。

刷新丢失的是 React 内存状态，不是浏览器 Cookie 会话。客户端重新获取资料属于初始化，不属于重新登录。

## 6. 登录与退出

登录流程：

1. `LoginForm` 做本地必填校验。
2. 调用认证模块 `login` 操作。
3. 成功后 Provider 保存服务端返回用户，LoginPage 导航到原目标受保护路径或 `/`。
4. 失败时保持登录页和用户名输入，密码字段清空。

退出流程：

1. `CurrentUserBar` 调用 Provider 的 `logout`。
2. Provider 调用退出 API。
3. 服务端返回 `204` 时客户端清理当前用户状态；`403` 或网络/未知错误时保留当前认证状态并显示退出失败。
4. 确认退出后导航到 `/login`。

资料刷新流程：

1. 用户管理模块成功修改当前账号昵称后调用 `refreshCurrentUser()`。
2. Provider 重新请求 `/api/auth/me`，成功且请求版本仍有效时更新资料并返回 refreshed（user、identityRevision、refreshRevision）；取消或被新操作替代返回 cancelled，不把页面切换到整页 checking。
3. 如果返回 `401`，按认证失效流程清理状态；网络或未知错误保留当前已确认资料并返回可重试错误。

## 7. AuthGate

`AuthGate` 只根据 Provider 状态做路由级渲染：

- `checking`：显示稳定的加载占位；
- `authenticated`：渲染 Router `Outlet` 子路由；
- `anonymous`：导航到 `/login` 并保留安全的返回路径；
- `error`：显示服务不可访问和重试入口。

返回路径只能来自站内相对路径，不能接受外部 URL，避免形成开放重定向。

## 8. 组件通信

- `LoginPage` 负责页面布局和成功后的导航，`LoginForm` 负责输入和提交。
- `AuthenticatedLayout` 读取认证状态并统一组合 `CurrentUserBar`；会议首页由 MeetingWorkspacePage 承载，不恢复 AuthenticatedHomePage 占位路由。
- `AuthGate` 由路由配置使用，不被普通业务组件嵌套多次。
- 认证模块不向页面暴露生成 Kiota 模型；页面使用本 feature 的用户视图模型。

## 9. 模块非目标

认证模块不实现用户创建、用户列表、用户资料编辑、密码重置、权限判断、会议成员判断、Token 刷新、设备管理或登录审计；这些用户管理操作由独立 feature 实现。`refreshCurrentUser()` 只是认证资料同步入口，不承担用户管理业务。

## 管理员资料同步

CurrentUserViewModel 增加必填 boolean isAdmin，登录和 me 适配必须接受 false。refreshCurrentUser 同步昵称和身份，不存储角色 Claim，不使用 localStorage 持久化权限。AdminGate 在管理页进入时调用此操作确认身份；401 清理登录，forbidden 不等于退出。

个人设置保存昵称、本人管理员身份变更后使用同一刷新入口。Provider 的请求版本与取消机制必须避免旧响应覆盖退出、切换账号或最新身份；管理页收到 forbidden 后即使刷新失败也保持拒绝访问，不能依赖 Provider 保留的旧资料重新放行。

## 刷新结果与版本

refreshCurrentUser 返回 RefreshCurrentUserResult：`{ status: 'refreshed', user: CurrentUserViewModel, identityRevision: number, refreshRevision: number } | { status: 'cancelled' }`。Provider 暴露当前两个版本，它们仅用于客户端请求协调，不是服务端权限凭证。

- refreshRevision：每次资料刷新开始时递增，用于丢弃被后续刷新取代的响应。普通资料刷新不递增 identityRevision，也不取消已确认页面的权限。
- identityRevision：登录、退出、重新初始化、全局 401 开始时递增；最新资料确认 UserId 或 IsAdmin 改变时也递增。此版本变化才使既有管理员放行失效。改变身份时同时取消在途资料请求，旧响应不能覆盖新身份。

AdminGate 在进入或显式重试时发起确认；首次放行要求 refreshed 的两个版本均匹配 Provider 当前值，当前仍 authenticated 且 isAdmin=true。cancelled、失败或过期结果不能用于首次放行。成功后记录 identityRevision；之后普通资料刷新只更新 refreshRevision，已确认的管理页面继续挂载，不进入整页 checking。

普通资料刷新成功且身份不变时只同步昵称等资料；取消或网络失败时保留此前已确认页面并提供资料重试。若返回 401、确认身份改变或收到管理 forbidden，则立即取消放行并清理管理数据。forbidden 的本地拒绝状态只能通过重新进入或显式重试后的有效确认解除，不能被一次后台资料刷新自动解除。

不在每次版本变化时自动发起刷新，避免循环。首次确认被取消且仍在页面时提供重试。资料同步调用方收到 cancelled 不提示保存失败，也不宣称资料已刷新。管理员修改本人昵称后的资料同步必须保持管理页面与操作反馈，不要求重新确认管理员身份。
