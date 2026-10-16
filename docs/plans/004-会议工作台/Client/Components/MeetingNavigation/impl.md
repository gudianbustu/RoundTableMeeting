# 会议导航实现计划

按[组件设计](design.md)更新现有features/meetingWorkspace/components/MeetingNavigation.tsx及其模块调用方；复用既有Ant Design组件、受控输入和错误适配。

1. 先更新视图类型与props，再修改渲染及操作回调，不让组件直接访问Kiota生成Client。
2. 实现设计中的权限、加载/失败/取消与成功状态；切会时旧回调不得影响新会议。
3. 覆盖实际用户操作、拒绝边界和键盘可访问性，接入[主题测试计划](../../../testPlan.md)；不把静态字符串断言当作完整验收。

当前/历史会议切换已接入并通过退出、重入浏览器验证。
