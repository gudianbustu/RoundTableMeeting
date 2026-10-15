# 会议工作台客户端实现计划

## 1. 依赖

先完成004服务端真实OpenAPI与Kiota更新，按[客户端设计](design.md)及[模块计划](Modules/MeetingWorkspace/impl.md)实现；不得直接手改生成目录。

## 2. 组合顺序

C01：扩展feature API适配和模块视图模型，分离活跃/历史列表与权限；维护request generation和revision，保证慢响应不覆盖新状态。

C02：按[页面计划](Pages/MeetingWorkspace/impl.md)接入现有AuthenticatedLayout与/meetings/:meetingId路由。history模式读取同会议授权历史，不另建平行会议。

C03：更新MeetingNavigation、MeetingHeader、ParticipantList、CreateMeetingDialog、JoinMeetingDialog及InviteMeetingDialog的既有组件文档。优先保留桌面/移动布局及可访问交互，增加编辑、删除/退出、分组和统一CLI邀请。

C04：接入005消息/附件区域及007连接状态，统一document.title更新位置；加入/离开/更名和切会均测试，新消息不触发标题提醒。

## 3. 验证

组件测试只验证实际交互/权限与旧响应问题；最终使用真实浏览器检查桌面/窄屏、焦点、网络与标题。主题W01—W09为验收入口。当前仅计划，未构建或运行。
