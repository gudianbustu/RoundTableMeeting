# 原生 MCP 服务与脚本资源设计

## 1. 宿主入口与资源

ASP.NET Core 提供 Streamable HTTP 的 /mcp 入口。多会议工具按 room 校验服务站点和参与者身份，消息、历史和附件沿用 004、005 的应用服务。等待与传输均支持取消；长等待不占用数据库事务。部署路径前缀用于构造邀请和同源资源地址。

官方回退脚本与同版脚本指南放在 Web 静态资源目录。原生指南由服务端嵌入资源提供，对外路径为 /agent/0.1.0/native-guide.txt。用户明确选择脚本时使用这组资源。对真实发布包验证脚本和指南可读取、内容和版本匹配、缺失资源不被当作有效脚本。服务端无独立 CLI 安装包或本地索引模型分发任务。

## 2. 私聊、附件与生命周期

服务端按参与者可见投影查询历史，校验 room 与消息范围后签发限时附件凭据。鉴权失败、网络错误与会议删除分别处理。已退出成员受历史上界限制，已删除会议拒绝原身份访问消息和附件。日志隐去会议码、令牌、正文和私有路径。

## 3. 兼容协议与状态查询

CurrentMcpTools只暴露当前消息工具，输出采用具体泛型DTO及字符串枚举；文件通过prepare_attachment_transfer签发五分钟凭据，在/mcp-files以Bearer头进行原始字节上传和Range下载。权限、未绑定限制、文件大小和存储读取复用005服务。传输凭据绑定MCP transport、connectionSessionId、方法和下载附件ID，每连接最多16个有效凭据，进程总量4096；失效连接不能复用。下载后由Agent自行读取文件，不提供服务端内容解读工具。

可信MeetingAgentContext仅在连接、代次、租约及成员校验后设置；应用服务借用适配器已取得的共享会议门，避免重复获取造成退出死锁。统一按会议门、参与者锁、数据库事务顺序进入；退出独占会议门。通知在释放门和参与者锁之后发布。新连接在既有Transport字段标为mcp-streamable-http-participant-v2，连接租约沿用现有存储约束；活动投影不读取旧任务为新协议生成工作状态，旧任务事实保持隔离。

## 4. MCP参与者连接状态查询

get_meeting_info的participants在既有participantId/displayName/type/membershipState外增加connectionState，取online/offline。人类沿用MeetingPresenceRegistry多连接合并及断开宽限；Agent要求当前进程注册有效、数据库连接Connected、当前代次及绑定一致、游标指向该连接且租约未过期。已退出成员始终offline，保留membershipState=left与暂时离线区分。不返回最后活跃、阅读或模型工作推断。结果是查询时连接快照，不新增主动探测或保证模型响应；get_meeting_status继续只表示会议生命周期。

复用现有网页presence与MCP GetOnlineAsync，不增加存储或新工具。测试覆盖人类连接/断开、Agent加入/断开/恢复/租约过期、退出与离线的区分。
