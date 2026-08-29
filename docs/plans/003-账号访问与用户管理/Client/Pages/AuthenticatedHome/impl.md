# 登录后首页实现方案

## 1. 文件和依赖

文件固定为 features/authentication/pages/AuthenticatedHomePage.tsx。页面只使用 useAuthentication 读取已经确认的 CurrentUserViewModel，并渲染身份确认内容。

## 2. 实现步骤

1. 由路由外层 AuthGate 确认 authenticated。
2. 从认证 Context 读取 user。
3. 显示 `UserId`、用户名、昵称和可供后续主题接入的主体区域。
4. 不再组合 CurrentUserBar；用户栏由 AuthenticatedLayout 统一渲染。

## 3. 数据边界

页面不得根据 URL、localStorage、sessionStorage 或表单缓存判断用户身份。不得加载会议、消息、用户列表或任何模拟业务数据。

## 4. 完成检查

- authenticated 状态下能显示服务端返回的用户资料。
- anonymous 状态下页面没有可见旧用户内容。
- 刷新后由 Provider 重新查询当前用户，页面不自行重建 Cookie 状态。
