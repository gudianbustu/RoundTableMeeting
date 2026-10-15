# 群聊客户端状态模块设计

模块拥有消息Map、序号索引、历史边界、连接generation、草稿/发送载荷、附件暂存与阅读锚点。页面只消费视图和回调，契约见[客户端设计](../../design.md)。

合并按messageId去重、sequence排序，重复事件不追加；更晚序号不能掩盖中间缺口。每个action携带meetingId/generation，旧请求丢弃。退出更新权限、上界与双方私聊发送资格；删除封锁入口、清空本会议数据并拒绝迟到回写。

发送载荷生成稳定clientSubmissionId，pending_confirmation期间重试不得换ID或正文；核对服务已保存则合并，未保存且可发才重发。编辑旧失败文本形成新意图，原pending记录仍需核对，不能一键把不确定旧发送当作未发生。

锚点与草稿按会议及交流范围隔离，history/show跳转不产生阅读状态。参与者输入辅助只建议已有名称，不提醒遗漏@。附件列表和定位共用当前会议访问权限。

状态键扩展为meetingId+scope+peerParticipantId；草稿、引用、发件箱不可因切页丢失visibility/recipient。输入字段与冲突规则见Server契约，私聊不可先接收全量他人数据再本地筛选。删除代次阻断请求、索引或附件晚到回写。
