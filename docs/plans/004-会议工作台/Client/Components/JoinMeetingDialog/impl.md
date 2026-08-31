# 加入会议对话框实现方案

## 1. 文件和状态

文件为 `features/meetingWorkspace/components/JoinMeetingDialog.tsx`。内部状态包括 step、meetingCode、preview、displayName、previewError、joinError 和 submitting。

## 2. 会议码输入

粘贴或输入时只 Trim 首尾空白，使用严格的 GUID `D` 格式解析，大小写不敏感，并按小写 `D` 格式展示。内部空白、缺失连字符、带大括号或其他 Guid 格式均不自动修复；解析失败时显示字段错误。会议码只存在内存，关闭对话框或完成加入后清理。

## 3. 预检和加入

预检成功显示 meetingName、description 和 defaultDisplayName；不显示会议码回显、参与者或邀请码。alreadyJoined 显示 existingDisplayName 和“打开会议”，使用 preview.meetingId 导航且不修改名称。未加入时允许修改 displayName，再提交最终加入。

JoinConflict 自动重试一次；DisplayNameTaken 定位显示名称字段；429 读取 Retry-After 提示时间；404 统一会议不可访问。

## 4. 测试

覆盖 `MW-V014`–`MW-V019`、`MW-B004`，并验证首尾空白、标准 GUID `D` 格式、大小写不敏感、非法格式、键盘步骤、Escape、字段错误、重试和敏感值清理。
