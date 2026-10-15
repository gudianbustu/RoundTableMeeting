# 会议头部设计

## 1. 职责与行为

展示会议名称、主题/背景及可用操作；创建者可编辑和移除，非创建者可退出，已加入人类可邀请。历史模式说明只读原因。移除确认解释关闭全体交流且保留历史，输入会议名；退出确认说明仅影响本人。创建者退出错误不能转换为移除。

## 2. 输入与协作

name、description、permissions、lifecycleState、membershipState及onEdit/onClose/onLeave/onInvite。仅消费权限不推断管理员额外能力；编辑表单由模块带expectedRevision提交。

## 3. 边界

名称长文本换行；操作均有aria-label，确认取消保留当前状态。更名成功通过模块同步标题，不在组件直接修改document.title。

状态与权限以[客户端设计](../../design.md)及[服务端契约](../../../Server/design.md)为准；组件不建立第二套生命周期规则。
