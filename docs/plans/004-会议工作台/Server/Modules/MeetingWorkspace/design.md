# 会议工作台应用模块设计

## 1. 契约与职责

[服务端设计](../../design.md)唯一维护模型、字段、HTTP和授权规则。模块在Application实现Create、ListMine、GetDetails、GetInvitation、PreviewJoin、Join、Update、Delete、Leave；Abstraction提供请求/结果及访问授权投影，Repository不返回Web DTO或认证Cookie。

## 2. 用例编排

Create从003当前账号取得UserId与昵称，校验后进入短事务；按creationRequestId查询回执，相同意图返回原会议，异参拒绝，不存在则原子创建会议、创建者成员及回执。缺少当前账号不回退客户端提交身份。

PreviewJoin只返回会议名/背景、默认或既有显示名及当前参与状态，不创建成员，不泄露参与者列表；Join重新验证有效码、开放状态与统一名称唯一性。已有joined直接返回原身份；已有left只有此明确命令可改joined并恢复完整历史。

GetDetails/ListMine使用NoTracking投影及统一访问上下文；列表分活跃/历史。GetInvitation要求active+joined且human，不仅创建者可邀请。Update要求创建者及expectedRevision条件更新；冲突不覆盖旧值。

Delete按Server唯一契约提交删除屏障，协调取消活动IO、幂等清理文件与业务数据、删除状态和最小回执；Leave在短事务读取本人可见高水位并固定退出上界，创建者Leave先拒绝。状态事件随事务保存，提交后通知。删除与退出不能共享“转只读历史”的完成逻辑。

## 3. 授权服务

IMeetingAccessGuard分别提供历史与写入授权结果，返回服务端设计中的权限和visibleThroughSequence。消息/附件/CLI必须使用受信身份调用；不得接受外部participantId直接替代认证。不通过重复加入“修复”退出权限，deleting/deleted拒绝全部内容，删除状态专用证明不赋予历史访问权。

## 4. 事务和错误

使用现有IMeetingTransactionRunner共享Scoped DbContext/连接；事务内重查、登记变更、一次提交，不嵌套，不跨网络调用。唯一冲突回滚并释放作用域后最多一次新作用域重查；正常业务冲突映射稳定结果，原始SQLite错误不出边界。

任务和测试归[模块实现计划](impl.md)及[主题测试计划](../../../testPlan.md)。本模块不建立业务任务或审计日志；复用现有删除协调及恢复服务并按Server契约扩展，不在008复制清理流程。
