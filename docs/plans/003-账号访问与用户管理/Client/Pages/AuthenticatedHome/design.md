# 登录后首页认证接入设计

## 1. 页面归属

当前 `/` 与 `/meetings/:meetingId` 已由 MeetingWorkspacePage 承载。会议页面的结构、数据和权限由会议工作台主题维护；本轮只调整共享账号入口，不替换首页、不将 AuthenticatedHomePage 接回路由。

## 2. 认证与导航

路由沿用 AuthGate -> AuthenticatedLayout -> MeetingWorkspacePage。未登录由 AuthGate 跳转登录，登录成功回到安全站内目标或 `/`；刷新通过 me 恢复身份。会议成员和创建者权限仍由会议用例检查。

CurrentUserBar 显示当前资料并提供个人设置和退出。用户管理入口仅管理员可见，/users 由 AdminGate 单独保护；个人设置位于 /settings/profile。会议页不需要显示 UserId 或身份确认占位内容。

## 3. 验收边界

管理员与普通用户登录、刷新和退出均沿用当前会议工作台；本轮账号变更不删除、替换会议路由，不影响已有会议选择与聊天。个人设置保存昵称后顶栏同步，已有会议显示名称保持原值。
