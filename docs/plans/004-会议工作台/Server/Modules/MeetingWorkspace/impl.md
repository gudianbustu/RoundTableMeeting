# 服务端会议工作台模块实现方案

## 1. 实现范围

本文把[模块设计](design.md)落实为领域实体、仓储、应用服务和结果映射。HTTP 管道、Controller、会议码生成/解析和删除 HostedService 见[服务端实现方案](../../impl.md)。

模块只负责会议、静态参与者、人类加入、创建回执、会议列表、详情、邀请和创建者删除；不实现消息、实时连接、MCP 或 AI。

## 2. 实体实施顺序

1. 创建 `MeetingLifecycleState { Active, Deleting }` 和 `ParticipantType { Human, Agent }`。
2. 实现 `Meeting` 工厂：名称 Trim + NFC，1 至 128；说明 Trim + NFC，空转 null，最多 2000；创建后 Active；MeetingCode 必须为非空 Guid；只允许 `BeginDeleting()`。
3. 实现 `MeetingParticipant` 工厂：Human 的 UserId 非空，显示名称 Trim + NFC 后 1 至 64，比较值不超过 128；加入后不提供改名方法。
4. 实现 `MeetingCreationReceipt : BaseEntity`：使用 Guid ID 持久化，CreatorUserId + CreationRequestId 作为业务唯一键；包含 MeetingId、State、CreatedAtUtc、ExpiresAtUtc，Deleted 转换清空 MeetingId。
5. 为三类实体增加 Domain.Tests，验证无参 ORM 构造不能形成可提交的非法实体。

所有时间由注入 `TimeProvider` 提供；Application 负责取得 UTC 时间，Domain 不调用系统时钟。

## 3. 仓储契约

`IMeetingRepository` 固定提供 Active 详情、按 Guid MeetingCode 查找、创建幂等回执查询与状态更新、过期 Deleted 回执查询与删除、列表投影、创建者 Active -> Deleting 条件更新和 Deleting 扫描。

`IMeetingParticipantRepository` 固定提供按 MeetingId/UserId 查询、名称占用检查、参与者投影和显式删除。

仓储默认 NoTracking，不返回 IQueryable，不隐式 Commit。

## 4. DTO 和结果

固定 DTO：

- `CreateMeetingRequest(creationRequestId, name, description?)`。
- `CreateMeetingResultDto(meeting, invitation)`。
- `MeetingListItemDto(meetingId, name, myDisplayName, isCreator, joinedAt)`。
- `MeetingDetailsDto(meetingId, name, description, createdAt, currentParticipant, canDelete, canManageInvitation, participants)`，不含 meetingCode。
- `MeetingInvitationDto(meetingId, meetingCode)`，只对创建者返回。
- `JoinPreviewDto(meetingId, meetingName, description, defaultDisplayName, alreadyJoined, existingDisplayName?)`。
- `ParticipantDto(participantId, displayName, type, isCurrentUser, isCreator, joinedAt)`。

`ParticipantDto.type` 使用 Abstraction `ParticipantKind`，由 Application 从 Domain `ParticipantType` 映射；Controller 不直接序列化领域枚举。

`ParticipantType` 与 `ParticipantKind` 必须一一对应：`Human` -> `human`、`Agent` -> `agent`。新增任一枚举值时，必须在同一变更中补齐 Application 映射、OpenAPI/Kiota 契约和对应测试，禁止静默回退为整数或未知字符串。

创建结果状态包括 Created、Replayed、CreationRequestMismatch、CreationRequestConsumed；加入结果包括 Success、AlreadyMember、DisplayNameTaken、JoinConflict、MeetingNotFound；删除结果包括 Accepted、MeetingNotFound、CreatorRequired、ValidationFailed。`MeetingOperationStatus` 必须包含 Accepted，Controller 只按状态枚举映射 202，不根据结果文案判断。

## 5. 应用用例

### 5.1 所有用例共同前置

读取 `ICurrentUserContext`；`IsAuthenticated=false`、UserId null 或 Guid.Empty 返回 Unauthenticated。不要从请求 UserId、昵称、Cookie Claim 或 URL 恢复身份。

### 5.2 创建

1. 规范化请求和 CreationRequestId。
2. 通过 `IUserAccountRepository.FindByIdAsync` 读取账号昵称。
3. 在 `IMeetingTransactionRunner` 内按 CreatorUserId + CreationRequestId 查回执。
4. Active 回执只有关联 Meeting 存在且 Active 才能重放；内容一致返回 Replayed，内容不一致返回 CreationRequestMismatch。
5. Deleted、Deleting、缺失 Meeting 的回执返回 CreationRequestConsumed。
6. 无回执时调用 `Guid.NewGuid()` 生成 MeetingCode，创建 Meeting、Human 创建者参与者和 Active 回执，单次 Commit。
7. 返回 Created 的详情和一次邀请信息。

### 5.3 列表和详情

列表只查询当前 UserId 的 Human 参与关系和 Active 会议，按 JoinedAtUtc DESC、MeetingId ASC。详情先要求 Active，再要求成员；返回不含会议码的详情，参与者按 JoinedAtUtc ASC、ParticipantId ASC。

### 5.4 邀请信息

先要求 Active 且当前用户为创建者；非创建者统一 MeetingNotFound。直接读取持久化 Guid MeetingCode 并格式化为小写 `D` 字符串返回。

### 5.5 预检和加入

预检先 Trim 并严格解析 `D` 格式 Guid MeetingCode，再查 Active 会议，返回最小预览；已有成员返回 alreadyJoined 和原显示名称。

加入在事务内重新查 Active Meeting：已有 `MeetingId + UserId` 返回原身份；否则规范化显示名称，预检查名称占用，注册新 Human 参与者。参与者唯一约束冲突由 Repository.Sqlite 转换为 Domain `MeetingJoinConflictException`，当前 scope 废弃；Application 映射为 JoinConflict。客户端至多重试一次，第二次稳定得到已有身份或 DisplayNameTaken。

### 5.6 删除

1. 要求当前账号为成员；非成员返回 MeetingNotFound。
2. 要求为创建者；普通成员返回 CreatorRequired。
3. Trim + NFC 后以 `StringComparison.Ordinal` 比较 confirmationName，错误返回字段 validation_failed。
4. 在 runner 内原子 Active -> Deleting；提交成功即返回 Accepted。
5. 清理由 HostedService 继续执行，不能在失败后恢复 Active。

## 6. 访问守卫和事务边界

`IMeetingAccessGuard` 固定提供 `RequireMemberAsync`、`RequireCreatorForInvitationAsync`、`RequireCreatorForDeletionAsync` 和 `RequireParticipantAsync`，分别保持成员 404、邀请非创建者 404、删除成员非创建者 403 的披露语义。

`IMeetingTransactionRunner` 委托内禁止嵌套；Application 不能注入 DbContext，也不能在委托内调用 Commit。Runner 在委托成功后统一调用一次 Commit。所有取消令牌传递到仓储、runner 和 Commit。

## 7. 测试映射

- Domain：`MW-D001`–`MW-D012`。
- Application：`MW-A001`–`MW-A023`。
- Repository/事务：`MW-R001`–`MW-R017`。
- HTTP/OpenAPI：`MW-H*`、`MW-K*`。

模块实现不得通过模拟消息或模拟会议数据让页面看起来完成；所有成功结果必须来自真实持久化。
