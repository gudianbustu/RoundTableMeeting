# 会议导航设计

## 1. 职责与行为

展示活跃与历史分组；每项使用稳定meetingId，退出后移至有权历史；删除后从全部入口移除。无选择时提供创建/加入，历史会议可打开只读页。

## 2. 输入与协作

activeItems、historyItems、selectedMeetingId、onSelect/onCreate/onJoin。排序遵循服务端joinedAt与稳定ID。

## 3. 边界

不显示未读数、最后消息时间或工作状态。桌面侧栏与窄屏抽屉复用选择回调，键盘可访问。

状态与权限以[客户端设计](../../design.md)及[服务端契约](../../../Server/design.md)为准；组件不建立第二套生命周期规则。
