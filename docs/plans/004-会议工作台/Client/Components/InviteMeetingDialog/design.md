# 邀请入会对话框设计

## 1. 职责与行为

已加入人类成员可邀请。选择human/agent后分别展示网站地址与会议码，或输入Agent显示名生成完整邀请。名称为空时不生成提示词。

## 2. 输入与协作

open、invitation?、loading、onCancel与统一公开基础地址。打开时由模块读取邀请信息，组件不重复请求、不长期缓存；切会或关闭重置。

## 3. 边界

AI提示词引用006通用指南：服务地址、名称、会议码、同源latest/SKILL.md和manifest.json，选择系统架构、校验、安装、join/read/send/reconnect/disconnect。只使用官方CLI，不含/mcp或宿主直连替代路径。使用完整复制按钮及成功/失败反馈；部署子路径按统一外部地址构造。

状态与权限以[客户端设计](../../design.md)及[服务端契约](../../../Server/design.md)为准；组件不建立第二套生命周期规则。
