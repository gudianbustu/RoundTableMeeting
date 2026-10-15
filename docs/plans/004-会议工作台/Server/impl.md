# 会议工作台服务端实现计划

## 1. 依据

按[服务端设计](design.md)和[模块计划](Modules/MeetingWorkspace/impl.md)实施，根计划负责跨端次序。现有Application/Domain/Abstraction/Repository.Sqlite/Web分层保持不变。

## 2. 数据与迁移

S01：在隔离旧库副本核查Meetings、MeetingParticipants、创建回执和旧Deleting记录；输出缺失数据诊断，不补造历史。复用并扩展MeetingDeletionRecoveryService及/deletion入口，设计包含私聊、附件、投影与删除墓碑的迁移。新增state、权限边界及revision约束，保留Guid、唯一索引和Restrict外键，执行foreign_key_check。

S02：扩展仓储NoTracking投影和条件写，接入已有事务Runner；验证同scope、单次工作单元提交、失败回滚、禁止嵌套。退出读本人可见高水位；删除写不可访问屏障，文件IO在事务外恢复处理。

## 3. 应用、Controller与客户端契约

S03：完成模块M01—M06；实现deletion/leave/update与历史列表、删除状态及lifecycle-status；既有deletion保留名称确认，完成204或处理中202。所有写端点保持Origin元数据，邀请为无正文请求，响应no-store。

S04：更新真实OpenAPI并在003既有认证下集成测试。枚举使用camelCase字符串；错误覆盖非成员404、非创建者403、创建者退出409、revision冲突、deleting/deleted/left。之后才更新Kiota和Web适配，不能用手工schema冒充真实接口。

## 4. 验证与交付

真实SQLite测试包括并发发送/删除/退出两种顺序，旧回执与邀请码重复加入，退出后授权历史仍在；删除后业务数据及全部文件清理且无法再读。部署HTTP/HTTPS配置依赖002/003修订。此计划未执行，不自动迁移运行库或重启。
