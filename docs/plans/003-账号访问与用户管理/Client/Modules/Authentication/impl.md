# 客户端认证模块实现方案

## 1. 状态类型

types/authentication.ts 定义 CurrentUserViewModel 和判别联合状态：checking、authenticated、anonymous、error。authenticated 必须携带 user；其他状态不保留过期用户。

Context 对生产代码不公开，只通过 useAuthentication 使用；测试通过 `src/test/authenticationTestUtils.tsx` 中的专用 Provider 注入值。Context 值包含 state、login(username,password)、logout()、retry()、refreshCurrentUser()、isLoggingIn、isLoggingOut 和 operationError。login 返回 Promise<CurrentUserViewModel>，logout/retry 返回 Promise<void>，refreshCurrentUser 返回 Promise<RefreshCurrentUserResult>，Context 额外暴露 identityRevision 和 refreshRevision；页面负责登录成功导航，Provider 负责状态真实性和操作错误。

## 2. 初始化

首次挂载：

1. operationId 增加并设 checking。
2. 创建 AbortController 调用 getCurrentUser。
3. 成功且 operationId 仍有效时设 authenticated。
4. 401 unauthenticated 设 anonymous。
5. network/unknown 设 error，并保留 retry。
6. effect 清理取消请求；取消不进入 error。

StrictMode 的旧请求不得覆盖较新的结果。

## 3. 登录

1. 为登录分配新的 operationId。
2. 设置 isLoggingIn=true 并清空 operationError，调用 login API；不把全局状态改为 checking，以免整个登录页替换。
3. 成功设 authenticated 并返回 user。
4. invalid_credentials 保持 anonymous 并向页面抛出安全 ApiError。
5. network/unknown 保持 anonymous，把 ApiError 写入 operationError，不伪造登录成功。
6. finally 设置 isLoggingIn=false；旧 operationId 不得改写新操作状态。

登录成功导航由 LoginPage 完成。返回路径必须通过安全相对路径函数校验。

## 4. 退出

1. 设置 isLoggingOut=true 并清空 operationError，调用 logout API。
2. 204 视为确认退出，设 anonymous。
3. 403、network/unknown 保持 authenticated，并向 CurrentUserBar 返回错误。
4. 错误写入 operationError。
5. finally 设置 isLoggingOut=false；旧 operationId 不得改写新操作状态。

不能在请求发出前预先清理用户，否则网络失败会造成客户端与服务端状态不一致。

## 5. 全局 401

Provider 挂载时订阅 unauthorized 事件，卸载时取消订阅。事件到达时：

- 取消当前初始化/登录/退出操作；
- 增加 operationId；
- 清理 operationError 和用户并设 anonymous。

所有 `/api/auth/*` 请求都不触发全局事件：login 的 invalid_credentials 和 me 的 unauthenticated 由对应认证操作直接处理；logout 按服务端 204/403 契约处理。只有后续非认证业务请求的 401 unauthenticated 才触发全局清理。

## 6. useAuthentication

### 6.1 refreshCurrentUser

资料同步和 AdminGate 共用 refreshCurrentUser，结果类型及版本规则以 [认证模块设计](design.md)为准：

1. 递增 refreshRevision 并创建 AbortController，使上一资料请求失效；捕获本次 identityRevision，不因普通资料刷新改变身份版本或全局认证 operationId，不把 state 改为 checking。
2. 调用 getCurrentUser。
3. 响应仅在 refreshRevision 和捕获的 identityRevision 均有效、当前仍 authenticated 时处理；比较当前与返回的 UserId/IsAdmin，有变化则递增 identityRevision，再同步 user 并返回 refreshed、user 和两个最新版本。只有昵称变化时身份版本不变；首次路由确认检查两个版本，已放行页面只绑定身份版本。
4. 401 时清理状态并转 anonymous，向调用方抛出 ApiError，不返回 refreshed；当前已非 authenticated 的过期响应返回 cancelled。
5. network/unknown 保留原 authenticated 用户并向调用方抛出错误；首次确认不得放行，已确认页面的普通资料同步失败不卸载页面。
6. 新刷新只使旧 refreshRevision 失效；登录、退出、重新初始化或全局 401 使 identityRevision 失效并取消资料请求。旧请求与取消请求返回 { status: cancelled }，不能以 void 或空值表示成功。取消不进入全局错误。

### 6.2 Hook 约束

Hook 只能在 Provider 内使用，缺少 Provider 时抛出明确开发错误。页面和组件不直接访问 Context 对象，不自行调用 getCurrentUser；用户管理只能调用 refreshCurrentUser。

## 7. 公共出口

features/authentication/index.ts 只导出 AuthenticationProvider、LoginPage、AuthenticatedHomePage、AuthGate、CurrentUserBar、useAuthentication 和必要类型；refreshCurrentUser 通过 Hook 暴露，不单独导出底层函数。不导出 generated model、底层 API 适配或 Context 实例。

## 8. 测试重点

- 初始化成功、401、网络错误和重试。
- StrictMode 重复初始化和旧请求晚返回。
- 登录成功、无效凭证、网络失败。
- 退出成功、已经未认证、网络失败。
- 全局 401 清理状态，invalid_credentials 不清理。
- Provider 卸载后不更新状态。
- refreshCurrentUser 的 refreshed/cancelled 区分、401、网络错误、并发刷新替代，以及旧成功结果晚于身份版本变化时不放行。

## 管理员资料同步

CurrentUserViewModel 增加必填 boolean isAdmin，登录和 me 适配必须接受 false。refreshCurrentUser 同步昵称和身份，不存储角色 Claim，不使用 localStorage 持久化权限。AdminGate 在管理页进入时调用此操作确认身份；401 清理登录，forbidden 不等于退出。

个人设置保存昵称、本人管理员身份变更后使用同一刷新入口。Provider 的请求版本与取消机制必须避免旧响应覆盖退出、切换账号或最新身份；管理页收到 forbidden 后即使刷新失败也保持拒绝访问，不能依赖 Provider 保留的旧资料重新放行。
