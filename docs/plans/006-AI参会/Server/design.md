# 官方 CLI 协议与下载承载设计

## 1. 内部协议适配

ASP.NET Core宿主提供[006设计](../design.md)的/api/agent/v1 HTTP JSON及有界长轮询适配，具体操作与代次约束复用[007](../../007-AI任务连续性与接管/design.md)。Agent凭据不使用浏览器Cookie，不接受外部伪造participantId；活跃连接与历史访问资格分开验证。

命令入口不实现第二套消息/附件存储，调用005应用服务与004权限。CLI目标协议能力与releaseVersion在握手返回，无法兼容明确失败。当前先提供participant-sequence-v2的/mcp试用适配，不恢复旧任务工具；试用指南见../ReferenceAgent/README.md。

## 2. 静态发布

publish/RoundTableMeeting作为ContentRoot，发布聚合器生成downloads/rtm-agent/<releaseVersion>/和latest/。在UseRouting后、SPA fallback前映射/downloads，仅GET/HEAD，不开放目录浏览、上传或覆盖。固定版本长期缓存，latest使用no-cache；缺失文件即使Accept为text/html仍404，不回index.html。

包提供正确MIME、附件下载和受控Range。manifest含版本、协议范围、平台架构/最低系统、依赖、文件名、大小及SHA-256；只登记实际验证支持的构建。latest切换前完整校验版本目录，安装器先固定版本，防止混装。

服务端、Web、CLI、技能、指南、安装脚本与校验文件同版发布。向量模型可独立版本化包，模型清单引用模型版本/许可/校验/运行要求；未准备好时仍交付原文和关键词能力，但完整FR-016验收须补足本机向量验证。

## 3. 运行约束

公开基础地址按实际HTTP/HTTPS及部署子路径统一构造，不能由客户端自行拼出不同源的协议和下载地址。CLI目标开发代理/api及/downloads；当前MCP试用保留/mcp代理，并代理同前缀/mcp-files附件传输。日志不记录会议码、令牌、完整正文或私有文件路径。后台每次请求使用独立DI scope，等待不长期持有DbContext。

## 4. 待执行验证

真实发布包检查固定版本下载、latest切换、文件缺失404、清单校验及三系统安装；内部协议检查版本不匹配、取消等待、历史授权和代次隔离。上述CLI分发仍为设计；MCP试用服务包的本机验证结果见根测试计划。

## 5. 私聊与删除适配

005受信身份服务负责私聊收发、本人可见序号与过滤；不得在HTTP适配处广播后客户端过滤。004定义lifecycle-status与删除proof登记，仅返回删除状态；007控制信封分deleting/deleted，最终删除确认交006受管副本清理。无权404不能翻译成可信meeting_deleted。

## MCP试用适配实现边界

CurrentMcpTools只暴露当前消息工具，输出采用具体泛型DTO及字符串枚举；文件通过prepare_attachment_transfer签发五分钟凭据，在/mcp-files以Bearer头进行原始字节上传和Range下载。权限、未绑定限制、文件大小和存储读取复用005服务。传输凭据绑定MCP transport、connectionSessionId、方法和下载附件ID，每连接最多16个有效凭据，进程总量4096；失效连接不能复用。下载后由Agent自行读取文件，不提供服务端内容解读工具。

可信MeetingAgentContext仅在连接、代次、租约及成员校验后设置；应用服务借用适配器已取得的共享会议门，避免重复获取造成退出死锁。统一按会议门、参与者锁、数据库事务顺序进入；退出独占会议门。通知在释放门和参与者锁之后发布。新连接在既有Transport字段标为mcp-streamable-http-participant-v2，连接租约沿用现有存储约束；活动投影不读取旧任务为新协议生成工作状态，旧任务事实保持隔离。

## MCP参与者连接状态查询

get_meeting_info的participants在既有participantId/displayName/type/membershipState外增加connectionState，取online/offline。人类沿用MeetingPresenceRegistry多连接合并及断开宽限；Agent要求当前进程注册有效、数据库连接Connected、当前代次及绑定一致、游标指向该连接且租约未过期。已退出成员始终offline，保留membershipState=left与暂时离线区分。不返回最后活跃、阅读或模型工作推断。结果是查询时连接快照，不新增主动探测或保证模型响应；get_meeting_status继续只表示会议生命周期。

复用现有网页presence与MCP GetOnlineAsync，不增加存储或新工具。测试覆盖人类连接/断开、Agent加入/断开/恢复/租约过期、退出与离线的区分。
