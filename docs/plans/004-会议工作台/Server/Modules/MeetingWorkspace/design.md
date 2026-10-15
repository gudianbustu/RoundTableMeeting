# 会议工作台应用模块设计

## 1. 契约与职责

[服务端设计](../../design.md)唯一维护模型、字段、HTTP和授权规则。模块在Application实现Create、ListMine、GetDetails、GetInvitation、PreviewJoin、Join、Update、Close、Leave；Abstraction提供请求/结果及访问授权投影，Repository不返回Web DTO或认证Cookie。

## 2. 用例编排

Create从003当前账号取得UserId与昵称，校验后进入短事务；按creationRequestId查询回执，相同意图返回原会议，异参拒绝，不存在则原子创建会议、创建者成员及回执。缺少当前账号不回退客户端提交身份。

PreviewJoin只返回会议名/背景、默认或既有显示名及当前参与状态，不创建成员，不泄露参与者列表；Join重新验证有效码、开放状态与统一名称唯一性。已有joined直接返回原身份；已有left只有此明确命令可改joined并恢复完整历史。

GetDetails/ListMine使用NoTracking投影及统一访问上下文；列表分活跃/历史。GetInvitation要求active+joined且human，不仅创建者可邀请。Update要求创建者及expectedRevision条件更新；冲突不覆盖旧值。

Close/Leave在同类短事务中读取消息最大序号并记录边界；使用operationId收敛重复请求，不移动已完成边界。Close只改closed，Leave只改left；禁止物理清理。创建者Leave先拒绝。状态事件随事务保存，提交后再通知。

## 3. 授权服务

IMeetingAccessGuard分别提供历史与写入授权结果，返回服务端设计中的权限和visibleThroughSequence。消息/附件/CLI必须使用受信身份调用；不得接受外部participantId直接替代认证。不通过重复加入“修复”退出权限，不将closed等同于不存在。

## 4. 事务和错误

使用现有IMeetingTransactionRunner共享Scoped DbContext/连接；事务内重查、登记变更、一次提交，不嵌套，不跨网络调用。唯一冲突回滚并释放作用域后最多一次新作用域重查；正常业务冲突映射稳定结果，原始SQLite错误不出边界。

任务和测试归[模块实现计划](impl.md)及[主题测试计划](../../../testPlan.md)。本模块不建立新的任务、审计日志或清理流程。
