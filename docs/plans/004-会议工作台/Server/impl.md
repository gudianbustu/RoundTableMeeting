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

真实SQLite测试包括并发发送/删除/退出两种顺序，旧回执与邀请码重复加入，退出后授权历史仍在；删除后业务数据及全部文件清理且无法再读。部署HTTP/HTTPS配置依赖002/003修订。本批仅执行隔离测试，不迁移运行库、不重启共享服务。

## 5. 第1批当前交付边界

- 已实现：Joined/Left及成员revision、退出幂等回执与历史上界、明确重入复用身份、活跃/历史列表；退出后发送、邀请、订阅、Agent恢复/接管均受限制。
- 已实现：删除屏障、202处理中/204完成、创建者状态查询、不可冒充内容访问权的生命周期凭据；清理业务行后才确认deleted，旧创建回执永久阻止原请求复活会议。新增清理扩展接口；当前尚无附件存储实现，不能据此认定文件清理产品能力已交付。
- 迁移AddMeetingMembershipLifecycle已在隔离SQLite旧结构验证，存续历史与旧Deleting意图保留；存在退出或生命周期事实时拒绝降级。未操作运行库。
- 当前消息仍全部公开，LeftThroughSequence采用现有会议序号。第2批必须先引入参与者可见投影，再开放私聊；不能直接将本批公共序号用于私聊。
- 已按测试宿主真实OpenAPI生成Kiota客户端，沿用项目1.34.1及preview.103运行库；最小Web适配在删除通知/410时停用聊天并清理草稿。完整退出/历史页面、元数据编辑、会议Revision、私聊、附件和原生 MCP 不在本批完成声明内。

### 5.1 当前验证结果

服务端基线全套721项通过。本批Domain 61、Application 68、Repository 170项通过，新增降级保护在3项迁移定向测试中通过。Integration全套412项中407通过，5项失败定位为旧字段集合断言和旧库夹具写入新字段；修正后相关11项定向测试全部通过，Agent退出及删除锁顺序相关14项全部通过。该证据为全量运行加修复后定向复测，不表述为最终版本全套一次通过。

真实OpenAPI契约测试验证消息/退出410、删除202/状态查询与Agent生命周期查询的响应模型。重新生成客户端后，前端删除兼容及API基础相关18项测试已通过；Node 24.20.0下类型检查和生产构建通过，仍有主包大于500 kB的体积提示；Architecture 17项通过。GitNexus变更分析评为CRITICAL，影响现存连接、消息和删除流程；图分析用于确定回归范围，不替代运行测试。完整主题与部署验收仍待后续批次。
