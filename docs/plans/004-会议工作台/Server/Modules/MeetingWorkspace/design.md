# 服务端会议工作台模块设计

## 1. 模块职责

会议工作台模块负责会议创建、人类首次加入、参与身份恢复、当前用户会议列表、成员可见详情、静态参与者列表和创建者删除。它是后续消息与 AI 主题共同依赖的会议身份边界。

模块不负责消息、实时在线状态、MCP Agent、未读信息或账号认证。当前账号由主题二的 `ICurrentUserContext` 提供；所有应用用例的首个前置条件固定为 `IsAuthenticated = true` 且 `UserId` 非空、非空 Guid，否则返回 Unauthenticated。创建与加入预检通过既有 `IUserAccountRepository.FindByIdAsync(UserId)` 读取当前保存昵称，不从客户端或 Cookie Claim 取得默认名称。

## 2. 目录与依赖

源码职责固定如下；允许实施文档只补充测试和组合根辅助文件，不改变这些核心边界：

```text
RoundTableMeeting.Domain/
  Entities/Meeting.cs
  Entities/MeetingParticipant.cs
  Entities/MeetingCreationReceipt.cs
  Enums/MeetingLifecycleState.cs
  Enums/ParticipantType.cs
  Repositories/IMeetingRepository.cs
  Repositories/IMeetingParticipantRepository.cs
RoundTableMeeting.Abstraction/
  Contracts/Meetings/
  Enums/MeetingOperationStatus.cs
  Enums/ParticipantKind.cs
  Services/IMeetingWorkspaceService.cs
  Services/IMeetingAccessGuard.cs
  Services/IMeetingTransactionRunner.cs
RoundTableMeeting.Application/
  Meetings/MeetingWorkspaceService.cs
  Meetings/MeetingAccessGuard.cs
RoundTableMeeting.Repository.Sqlite/
  Database/Configurations/MeetingConfiguration.cs
  Database/Configurations/MeetingParticipantConfiguration.cs
  Database/Configurations/MeetingCreationReceiptConfiguration.cs
  Repositories/MeetingRepository.cs
  Repositories/MeetingParticipantRepository.cs
  Transactions/MeetingTransactionRunner.cs
RoundTableMeeting/
  Controllers/MeetingsController.cs
  Meetings/MeetingJoinRateLimitOptions.cs
  Meetings/MeetingJoinRateLimitFilter.cs
  Authentication/NoStoreResponseAttribute.cs
  Authentication/SensitiveResponseHeadersMiddleware.cs
  Meetings/MeetingDeletionSignal.cs
  OpenApi/MeetingOpenApiOperationTransformer.cs
  Meetings/MeetingDeletionRecoveryService.cs
```

Application 只依赖 Abstraction、Domain、领域仓储和工作单元。会议码是可分享的 Guid 邀请凭证，Application 只依赖 Guid 生成和解析规则，不引入密钥、加密或外部保护服务。

本主题新增 `IMeetingTransactionRunner.ExecuteAsync<TResult>(Func<CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken)`，不修改既有 `IUnitOfWork`。创建、加入和删除用例在 runner 委托中使用同一作用域仓储重新读取状态，并通过既有工作单元注册实体变化；委托不得调用 `CommitAsync`。Runner 在委托成功后统一执行本次事务唯一一次 `CommitAsync`，Repository.Sqlite 负责在同一 Scoped DbContext 和连接上绑定 `BEGIN IMMEDIATE` 等价事务、最终提交和失败回滚。Runner 禁止嵌套调用。

## 3. 领域不变量

### 3.1 Meeting

- 创建时名称有效、创建者有效、MeetingCode 为非空 Guid。
- 创建后状态为 Active。
- 没有正式开始或结束操作。
- 只能从 Active 进入 Deleting，不能恢复。
- 会议码不允许修改。

### 3.2 MeetingParticipant

- 本主题只创建 Human 类型。
- `UserId` 在数据库中允许为空，但 Human 类型必须存在，未来 Agent 类型必须为空。
- 显示名称规范化后非空且不超过 64 个字符，`NormalizedDisplayName` 不超过 128 个字符。
- 加入后显示名称不可修改。
- 同一 Meeting 与 User 只建立一个人类参与身份。
- 同一 Meeting 内规范化名称对所有参与者类型唯一。

### 3.3 MeetingCreationReceipt

- 继承 `BaseEntity`，使用 Guid `ID` 作为持久化主键。
- `CreatorUserId + CreationRequestId` 是业务幂等键，由数据库唯一索引保证。
- Active 必须关联 Meeting，Deleted 必须清空 MeetingId 并设置 ExpiresAtUtc。

## 4. 仓储契约

### 4.1 IMeetingRepository

至少提供：

- 按 `MeetingId` 查询 Active 会议；
- 按 Guid MeetingCode 查询会议；
- 判断 Guid MeetingCode 是否存在；
- 按当前账号参与关系查询会议列表投影；
- 原子地把创建者拥有的 Active 会议标记为 Deleting；
- 查询待恢复清理的 Deleting 会议。
- 按 `CreatorUserId + CreationRequestId` 查询 Active/Deleted 创建回执；
- 将创建回执转为 Deleted、清空 MeetingId，并删除已到期的 Deleted 回执。

### 4.2 IMeetingParticipantRepository

至少提供：

- 按 `MeetingId + UserId` 查询人类参与者；
- 判断规范化显示名称是否已占用；
- 查询会议参与者列表投影；
- 查询当前用户会议列表所需关联数据。

仓储不返回 `IQueryable` 给 Application，不隐式提交，不判断 HTTP 权限。

## 5. 应用 DTO

### 5.1 请求

- `CreateMeetingRequest`：`creationRequestId`、`name`、`description?`。
- `PreviewJoinRequest`：`meetingCode`。
- `JoinMeetingRequest`：`meetingCode`、`displayName`。
- `DeleteMeetingRequest`：`confirmationName`。

字段错误键固定为 `creationRequestId`、`name`、`description`、`meetingCode`、`displayName` 和 `confirmationName`。

### 5.2 响应

- `MeetingListItemDto`：`meetingId`、`name`、`myDisplayName`、`isCreator`、`joinedAt`。
- `CreateMeetingResultDto`：`meeting`、`invitation`。
- `MeetingDetailsDto`：`meetingId`、`name`、`description`、`createdAt`、`currentParticipant`、`canDelete`、`canManageInvitation`、`participants`。
- `MeetingInvitationDto`：`meetingId`、`meetingCode`；只有创建者可取得。
- `JoinPreviewDto`：`meetingId`、`meetingName`、`description`、`defaultDisplayName`、`alreadyJoined`、`existingDisplayName?`。`meetingId` 用于 alreadyJoined 分支直接打开既有会议，并在加入成功后作为导航目标校验；客户端不能据此跳过最终加入授权。
- `ParticipantDto`：`participantId`、`displayName`、`type`、`isCurrentUser`、`isCreator`、`joinedAt`。`isCreator` 只用于参与者列表展示；当前用户是否可删除或管理邀请，以 `MeetingDetailsDto.canDelete` 和 `canManageInvitation` 为唯一权威来源，客户端不得从参与者数组自行推导命令权限。

创建成功返回 `CreateMeetingResultDto`，其中包含不含会议码的 `MeetingDetailsDto` 和一次 `MeetingInvitationDto`；同一 `creationRequestId` 的 Active 回执且内容一致时返回 `200` 与相同结果，内容不一致返回 `CreationRequestMismatch`；Deleted 回执返回 `CreationRequestConsumed`。加入成功只返回不含会议码的 `MeetingDetailsDto`。邀请信息也可由创建者专属接口再次取得。

## 6. 用例详细流程

### 6.1 CreateAsync

1. 验证认证状态。
2. 规范化名称和说明。
3. 按当前 UserId 读取账号昵称并规范化为创建者显示名称。
4. 在显式事务内按 CreatorUserId + CreationRequestId 查询既有会议。
5. Active 回执只有在关联 Meeting 仍存在且状态为 Active 时才参与重放；请求内容一致时返回原会议详情和邀请信息，内容不一致时返回 CreationRequestMismatch。关联 Meeting 缺失、Deleting 或回执为 Deleted 时返回 CreationRequestConsumed。
6. 不存在时生成新的 Guid MeetingCode。
7. 创建 Meeting 和创建者 MeetingParticipant。
8. 注册两个新增实体并一次提交。
9. 返回新会议详情和邀请信息。

任何失败都不能留下只有会议或只有参与者的半状态；同一账号使用相同 CreationRequestId 重试不能创建第二场会议。

### 6.2 ListMineAsync

只返回当前账号存在人类参与关系且会议为 Active 的列表项，按当前参与者 JoinedAtUtc 倒序、MeetingId 升序稳定返回。该查询不读取会议码，不加载全部参与者。

### 6.3 GetDetailsAsync

1. 查 Active 会议。
2. 查当前用户参与者。
3. 任一步不存在时返回 MeetingNotFound。
4. 按 JoinedAtUtc 升序、ParticipantId 升序查询静态参与者列表。
5. 映射不含会议码的详情。

`GetInvitationAsync` 先确认创建者身份，再读取持久化的 Guid MeetingCode 并格式化为小写 `D` 字符串；空 Guid 或数据库读取故障属于数据完整性故障，返回内部错误并只记录 meetingId，不得返回空会议码伪装成功。

### 6.4 PreviewJoinAsync

1. Trim 会议码输入并按标准 GUID `D` 格式解析。
2. 按 Guid MeetingCode 查找 Active 会议；无结果统一 MeetingNotFound。
3. 若当前账号已有参与者，返回 alreadyJoined 和原显示名称。
4. 否则读取当前账号昵称作为默认显示名称。
5. 返回最小预览，不返回参与者和会议码。

### 6.5 JoinAsync

1. 重复执行会议码与 Active 校验。
2. 若当前账号已有参与者，返回原身份成功。
3. 规范化显示名称并预检名称占用。
4. 创建人类参与者并提交。
5. 并发唯一约束冲突返回 JoinConflict，当前作用域废弃，不在失败后的 DbContext 上查询。
6. 返回不含会议码的详情并允许客户端直接进入会议。

不得用会议码和显示名称接管另一个人类参与者；人类身份只按 UserId 恢复。

### 6.6 DeleteAsync

1. 验证当前账号是成员；非成员返回 MeetingNotFound。
2. 验证当前账号是创建者；成员但非创建者返回 CreatorRequired。
3. 校验确认名称。
4. 原子条件更新 Active -> Deleting，条件包含创建者 UserId。
5. 状态提交后立即返回删除已接受；HTTP 映射为 `202 Accepted`。
6. 删除协调器在独立作用域清理当前主题从属数据并最终删除 Meeting。
7. 清理失败保持 Deleting，交给启动恢复器和运行期重试。

重复删除 Deleting 或已物理删除的会议对外统一为不可访问；客户端不依赖恢复器内部状态。只有删除屏障没有成功提交时，首次删除请求才返回失败。

## 7. 统一访问守卫

`IMeetingAccessGuard` 固定表达：

- `RequireMemberAsync`：Active 且当前用户是成员，否则 MeetingNotFound；
- `RequireCreatorForInvitationAsync`：Active 且当前用户是创建者，任何非创建者均返回 MeetingNotFound；
- `RequireCreatorForDeletionAsync`：非成员返回 MeetingNotFound，成员但非创建者返回 CreatorRequired；
- `RequireParticipantAsync`：Active 且指定 Participant 属于 Meeting，否则 MeetingNotFound。

本主题的服务调用它，后续消息和连接入口必须复用它。守卫返回稳定业务结果，不抛出包含会议码或用户内容的异常。

## 8. 会议码契约

会议码规则固定为：创建时使用 `Guid.NewGuid()` 生成新值，数据库保存 Guid；接口以标准小写 `D` 格式字符串返回。输入只 Trim 首尾空白，使用大小写不敏感的 `Guid.TryParseExact(value, "D")` 严格解析；缺失或超过 64 个字符返回 `ValidationFailed`，通过基础校验后仍无法解析、查无会议或会议处于 Deleting 时返回 `MeetingNotFound`。该规则不移除内部空白、不自动补齐连字符、不接受大括号或其他 Guid 格式。

Domain `ParticipantType` 与 Abstraction `ParticipantKind` 必须保持一一对应：`Human` 映射为 `human`，`Agent` 映射为 `agent`；新增参与者类型时必须同步更新领域枚举、传输枚举、Application 映射、OpenAPI 和测试。

## 9. 删除恢复器

删除恢复器由 HostedService 承载，在服务启动后立即扫描一次 Deleting 会议，并按低频周期继续重试；它不阻塞其他 Active 会议开放流量。删除请求在屏障提交后发送一次内存唤醒信号，但该信号只用于降低延迟，持久化的 Deleting 状态才是崩溃恢复事实。每条清理使用独立 DI scope，失败记录 meetingId、traceId 和异常类型，不记录会议内容。该恢复器同时按周期扫描并删除已超过 `ExpiresAtUtc` 的 Deleted 创建回执；回执清理失败不能阻塞会议删除重试。

本主题只登记参与者清理器。每个清理步骤必须幂等，重复执行、部分数据已经删除或进程在步骤间退出时都能继续收敛。后续主题通过明确接口注册消息、实时状态、AI 连接和任务清理步骤；不得修改状态机含义或绕过访问屏障。

## 10. Controller 责任

`MeetingsController` 只绑定请求、调用应用服务和映射结果。加入端点额外挂载限流策略和 Browser Origin 元数据。Controller 不直接读取会议码，不查 DbContext，不根据前端传入 UserId 判断身份。删除命令绑定确认名称，邀请信息端点只对创建者开放。

OpenAPI 必须声明创建、详情、邀请信息和加入的明确成功 DTO，以及字段错误、MeetingNotFound、DisplayNameTaken、JoinConflict、CreationRequestMismatch、CreationRequestConsumed、CreatorRequired 和 Web 限流的 `429 meeting_join_rate_limited` 响应，使 Kiota 生成稳定模型；`429` 响应同时声明 `Retry-After` 响应头。`ParticipantDto.type` 必须生成 camelCase 字符串枚举 `human | agent`；Deleting 不作为公开错误状态。

## 11. 非目标

模块不提供 `LeaveMeeting`、`UpdateMeeting`、`RegenerateCode`、`RemoveParticipant`、`SetOnlineStatus`、`AppendMessage` 或 Agent 接入服务。
