# 账号访问与用户管理客户端设计

## 1. 文档定位

本文定义账号访问主题的客户端工程接入、路由、认证状态、API 适配和应用布局。主题总体边界以[主题总体设计](../design.md)为准；认证业务状态和公共出口以[认证模块设计](Modules/Authentication/design.md)为准；页面和组件交互以对应子文档为准。

本主题提供最小认证外壳和内部测试所需的用户管理页面，不建设完整用户后台或账号安全中心。

本轮新增内容属于设计增量，尚未声明现有客户端已经实现 `/users` 路由。

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

用户管理 API 适配放在 `features/userManagement/api/`，提供 listUsers、createUser、updateNickname 和 resetPassword；认证模块只在当前用户昵称被修改时提供 `refreshCurrentUser()`，不接管用户管理列表状态。

## 4. 路由结构

最小路由结构：

| 路径 | 页面 | 认证 |
| --- | --- | --- |
| `/login` | `LoginPage` | 匿名 |
| `/` | `AuthGate -> AuthenticatedLayout -> AuthenticatedHomePage` | 必须认证 |
| `/system/status` | 既有系统状态页 | 按框架约定保持公开 |
| `/users` | `UserManagementPage` | 必须认证，无额外权限 |
| `*` | 统一未找到页面 | 按路由上下文处理 |

认证状态尚未确定时，只有受保护路由通过 AuthGate 显示加载状态；公开 `/system/status` 始终直接渲染，不因 me 请求失败而被阻断。认证失败后受保护路由跳转 `/login`；登录页已登录时跳转安全 returnTo 或 `/`。

认证布局中的用户管理入口对所有 `authenticated` 用户显示，不做菜单权限判断。未来引入菜单认证时只收紧入口可见性和服务端 API policy，不把权限状态塞入当前用户管理 feature。

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
- 提供用户管理入口；
- 不承载会议业务和完整账号设置。

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
- 用户自行修改密码、账号找回和首次登录改密；
- 角色、权限管理和账号后台；
- 记住我、长期免登录、设备管理和多设备会话；
- 会议列表、会议创建、聊天消息和 AI 业务。
