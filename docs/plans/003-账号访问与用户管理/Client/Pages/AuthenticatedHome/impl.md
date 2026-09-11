# 登录后首页认证接入实现方案

## 1. 路由变更范围

在现有 app/router/index.tsx 中保留 `/` 与 `/meetings/:meetingId` 的 MeetingWorkspacePage、现有错误边界和 AuthGate/AuthenticatedLayout。只为 /users 增加 AdminGate，并增加 /settings/profile。

不新增、不挂载 AuthenticatedHomePage 占位路由。本轮不要求删除现有未使用组件或导出，避免引入与账号优化无关的源码清理。

## 2. 页面职责

会议数据继续由 meetingWorkspace feature 加载；认证资料来自 useAuthentication，CurrentUserBar 与个人设置负责账号显示和修改。不得把会议数据查询迁入认证 Provider，也不通过 URL 或 storage 推断登录身份。

## 3. 验证

登录默认进入真实会议工作台，合法会议 returnTo 能恢复指定会议；刷新保持登录，退出阻止受保护页面访问。普通用户直达 /users 被拒绝但仍可访问原有会议与个人设置。测试不再要求首页显示 UserId 或身份确认占位内容。
