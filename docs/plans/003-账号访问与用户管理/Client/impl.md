# 账号访问与用户管理客户端实现方案

## 1. 文档定位

本文将[客户端设计](design.md)具体化为目录、公共 API Client、认证 Provider、路由和构建步骤。认证状态机见[认证模块实现方案](Modules/Authentication/impl.md)，页面和组件的实现见对应子目录 impl.md，验证编号见[测试方案](../testPlan.md)。

本主题不新增前端状态管理库，建设内部测试所需的最小用户管理页，不建设完整用户后台或会议工作台。

## 2. 依赖与工具链

继续使用当前精确版本：React 19.2.8、React Router 7.18.2、Ant Design 6.6.1、TypeScript 5.9.3、Vite 8.2.2、Vitest 4.1.11 和 Kiota preview.103。认证状态使用 React Context、useReducer/useState 和自定义 Hook，不引入 TanStack Query、Zustand 或 Redux。

## 3. 目录和文件

~~~text
src/
  app/
    layouts/AuthenticatedLayout.tsx
    providers/AppProviders.tsx
    router/index.tsx
  api/
    client/apiClient.ts
    client/authenticationEvents.ts
  features/
    authentication/
      api/login.ts
      api/getCurrentUser.ts
      api/logout.ts
      components/LoginForm.tsx
      components/CurrentUserBar.tsx
      components/AuthGate.tsx
      context/AuthenticationContext.ts
      hooks/useAuthentication.ts
      pages/LoginPage.tsx
      pages/AuthenticatedHomePage.tsx
      providers/AuthenticationProvider.tsx
      types/authentication.ts
      index.ts
    userManagement/
      api/listUsers.ts
      api/createUser.ts
      api/updateNickname.ts
      api/resetPassword.ts
      components/UserListTable.tsx
      components/CreateUserForm.tsx
      components/EditNicknameForm.tsx
      components/ResetPasswordDialog.tsx
      pages/UserManagementPage.tsx
      types/userManagement.ts
      index.ts
  test/
    authenticationTestUtils.tsx
~~~

测试与被测文件 colocate。页面和组件不直接 import api/generated；authentication feature 通过自己的 api 适配调用公共 getApiClient()。

## 4. 公共 Kiota Client 调整

### 4.1 Cookie 请求

浏览器同源 fetch 默认 credentials 为 same-origin，公共 HttpClient 封装仍显式设置 credentials: 'same-origin'，避免未来改动造成 Cookie 不发送。客户端不读取、保存或打印 Cookie。

### 4.2 全局 401 通知

在 api/client 增加轻量 authenticationEvents 模块：

- subscribeUnauthorized(listener) 返回取消订阅函数。
- notifyUnauthorized() 只通知内存订阅者。
- 在现有 RequestOptionsMiddleware 中 await next.execute；使用 `new URL(url, window.location.origin).pathname` 取得 pathname，响应 status 为 401 且 pathname 不以 `/api/auth/` 开头时调用 notifyUnauthorized，然后原样返回 Response，不读取或消费响应体。
- login、me 和 logout 三个认证接口由认证模块直接处理，不触发全局登出事件，避免初始化或退出流程自我打断。
- 不使用 window 自定义事件、localStorage 或跨标签页同步。

AuthenticationProvider 订阅事件并把状态转为 anonymous。feature 页面不重复捕获所有业务 API 的 401。Provider 的初始化错误只影响读取认证状态的登录页和 AuthGate；公开 `/system/status` 不读取认证 Context，因此仍可正常渲染和重试系统状态。

### 4.3 重新生成

启动服务端后执行 pnpm generate:api，生成认证路径和 DTO。生成失败时保留旧目录。完成后执行 pnpm test 和 pnpm build，检查请求路径只有一个 /api 前缀且生成结果不含 localhost 绝对地址。

## 5. AuthenticationProvider 组装

AppProviders 顺序固定为：

1. authentication feature 的 `index.ts` 导出 AuthenticationProvider；AppProviders 通过 feature 公共出口导入，并由 Ant Design ConfigProvider 包裹 AuthenticationProvider。
2. App 继续渲染 `<AppProviders><RouterProvider router={router} /></AppProviders>`。

AuthenticationProvider 不依赖 Router Hook，避免 Provider 位于 Router 外时无法使用；登录后的导航由页面完成，匿名后的路由响应由 AuthGate 完成。

Provider 首次挂载调用 getCurrentUser。React StrictMode 开发环境可能执行重复 effect，实现使用 AbortController 和递增 operationId：旧请求完成时若 operationId 已过期，不更新状态；清理 effect 时取消请求。

## 6. 路由实现

路由树固定为：

~~~text
createBrowserRouter([
  { path: '/login', element: <LoginPage /> },
  {
    element: <AuthGate />,
    children: [
      {
        element: <AuthenticatedLayout />,
        children: [
          { index: true, element: <AuthenticatedHomePage /> },
          { path: '/users', element: <UserManagementPage /> }
        ]
      }
    ]
  },
  {
    element: <AppLayout />,
    children: [{ path: '/system/status', element: <SystemStatusPage /> }]
  },
  { path: '*', element: <NotFoundPage /> }
])
~~~

LoginPage 和 NotFoundPage 不套用认证布局；SystemStatusPage 保留既有 AppLayout；只有根 index 进入 AuthGate 和 AuthenticatedLayout。NotFoundPage 自身继续渲染指向 `/system/status` 的返回链接，保持 002 V-008 回归语义。

AuthGate 只包受保护路由。登录页已认证时 Navigate 到安全返回路径或 /。/system/status 保持公开并继续作为 002 回归入口。

AuthenticatedLayout 固定显示用户管理入口，所有 authenticated 用户可见，不读取权限状态。`/users` 与 `/` 共用 AuthGate 和布局。

生产 SPA fallback 已由 002 支持，/login 和 / 刷新时应返回 index.html；/api/auth/* 不进入 SPA fallback。

## 7. API 适配

authentication/api 包含：

- login：签名固定为 `login(input: LoginInput, signal?: AbortSignal): Promise<CurrentUserViewModel>`；调用生成登录 operation，映射用户模型，invalid_credentials 作为 ApiError 抛出。
- getCurrentUser：签名固定为 `getCurrentUser(signal?: AbortSignal): Promise<CurrentUserViewModel>`；unauthenticated 作为 ApiError 抛出给 Provider。
- logout：签名固定为 `logout(signal?: AbortSignal): Promise<void>`；服务端契约只返回 204、403 或网络/未知错误。204 视为退出成功；403、network、unknown 都作为 ApiError 继续抛出，由 Provider 保持 authenticated；不保留不可达的 401 分支。

LoginInput 只包含 username 和 password；CurrentUserViewModel 只包含 userId、username、nickname。

适配层不把 generated model 传给页面，不保存密码，不在日志输出输入。

userManagement/api 固定：

- `listUsers(signal?) -> Promise<UserSummaryViewModel[]>`；
- `createUser(input, signal?) -> Promise<UserSummaryViewModel>`；
- `updateNickname(userId, input, signal?) -> Promise<UserSummaryViewModel>`，调用生成 PATCH operation；
- `resetPassword(userId, signal?) -> Promise<void>`。

适配函数统一通过 toApiError 转换 ProblemDetails，不读取响应 detail 作为分支。用户名冲突和目标不存在依据 code 处理。初始密码只存在于请求输入生命周期，失败后由表单清空。

## 8. 样式和可访问性

- 登录页使用全宽页面带受约束内容区域，不创建营销 Hero。
- 表单宽度使用 max-width 和 width: 100%，窄屏不溢出。
- AuthGate 加载和错误占位固定使用现有 `PageState`。
- 退出按钮使用 Ant Design 图标和文字，提供可访问名称。
- 不显示实现说明、快捷键或功能宣传文案。

## 9. 测试工具调整

renderWithProviders 保持现有 ConfigProvider + MemoryRouter 包装，并增加可选 `authenticationValue` 参数；传入时由 `src/test/authenticationTestUtils.tsx` 中的测试 Provider 包装，未传入时不增加认证 Context。生产 AuthenticationProvider 测试使用独立 `renderAuthenticationProvider` 工具，避免初始化请求污染无关测试。

- 组件单元测试通过测试工具注入 AuthenticationContext 测试值，Context 本身不从 feature 公共出口导出。
- Provider 测试单独渲染真实 Provider 并 mock feature API 适配。
- Router 测试使用 MemoryRouter 或 createMemoryRouter。
- 不整体 mock Ant Design 或 Kiota runtime。

## 10. 开发和构建

~~~powershell
Set-Location Client/roundTableMeeting
pnpm install --frozen-lockfile
pnpm generate:api
pnpm test
pnpm build
pnpm dev
~~~

开发浏览器打开 http://localhost:5173/login。登录和退出请求通过 Vite /api 代理到 5043，浏览器 Origin 仍是 http://localhost:5173，必须存在于服务端 AllowedOrigins。直接访问服务端 HTTPS 7043 不属于账号登录联调路径。

## 11. 客户端完成检查

- 首次加载 checking 不闪现登录页。
- 无会话显示登录页，有会话刷新后进入首页。
- invalid_credentials 不触发全局匿名事件。
- 其他业务 401 可以让 Provider 转为 anonymous。
- 退出网络失败保留当前用户，确认退出后返回登录页。
- /system/status 回归可用。
- pnpm test、pnpm build 和 dotnet publish 通过。
- `/users` 在认证布局内可访问，未登录返回 `/login`。
- 添加账号、昵称修改、固定密码重置成功后刷新列表。
- 当前用户昵称修改后 CurrentUserBar 同步更新。
- 用户管理菜单不做权限判断，未来由菜单/API 权限统一收紧。
