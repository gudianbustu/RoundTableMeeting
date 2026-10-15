# 官方 CLI 协议与下载承载设计

## 1. 内部协议适配

ASP.NET Core宿主提供[006设计](../design.md)的/api/agent/v1 HTTP JSON及有界长轮询适配，具体操作与代次约束复用[007](../../007-AI任务连续性与接管/design.md)。Agent凭据不使用浏览器Cookie，不接受外部伪造participantId；活跃连接与历史访问资格分开验证。

命令入口不实现第二套消息/附件存储，调用005应用服务与004权限。协议能力与releaseVersion在握手返回，无法兼容明确失败，不回退宿主MCP。本轮不维护原公开/mcp教程；旧端点停用与客户端兼容切换须在实施阶段验证。

## 2. 静态发布

publish/RoundTableMeeting作为ContentRoot，发布聚合器生成downloads/rtm-agent/<releaseVersion>/和latest/。在UseRouting后、SPA fallback前映射/downloads，仅GET/HEAD，不开放目录浏览、上传或覆盖。固定版本长期缓存，latest使用no-cache；缺失文件即使Accept为text/html仍404，不回index.html。

包提供正确MIME、附件下载和受控Range。manifest含版本、协议范围、平台架构/最低系统、依赖、文件名、大小及SHA-256；只登记实际验证支持的构建。latest切换前完整校验版本目录，安装器先固定版本，防止混装。

服务端、Web、CLI、技能、指南、安装脚本与校验文件同版发布。向量模型可独立版本化包，模型清单引用模型版本/许可/校验/运行要求；未准备好时仍交付原文和关键词能力，但完整FR-016验收须补足本机向量验证。

## 3. 运行约束

公开基础地址按实际HTTP/HTTPS及部署子路径统一构造，不能由客户端自行拼出不同源的协议和下载地址。开发仅代理/api及/downloads，邀请不再依赖/mcp代理。日志不记录会议码、令牌、完整正文或私有文件路径。后台每次请求使用独立DI scope，等待不长期持有DbContext。

## 4. 待执行验证

真实发布包检查固定版本下载、latest切换、文件缺失404、清单校验及三系统安装；内部协议检查版本不匹配、取消等待、历史授权和代次隔离。这里是设计，不表示下载文件已生成或服务已部署。
