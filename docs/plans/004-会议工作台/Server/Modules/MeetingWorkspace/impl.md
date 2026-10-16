# 会议工作台应用模块实现计划

## 1. 前提与落点

依据[模块设计](design.md)及[服务端契约](../../design.md)。沿用Abstraction服务/DTO、Application会议用例和Repository.Sqlite仓储目录，手写扩展，不生成重复领域对象。

## 2. 待执行任务

| 编号 | 工作 | 前置与检查点 |
| --- | --- | --- |
| M01 | 扩展会议/成员状态及历史上界投影 | Server迁移映射确定；joined/left与online/offline不混用 |
| M02 | 拆分历史授权与写入授权 | M01；全部查询/消息/附件调用可取得相同上界 |
| M03 | 更新Create/List/Details/Invitation/Preview/Join | M02；普通已加入人类可邀请，重入复用身份 |
| M04 | 实现Update/Delete/Leave与operationId收据 | M02；revision、创建者退出拒绝、重复边界不移动 |
| M05 | 扩展Delete编排与后台清理恢复 | M04；保留/deletion；删除屏障、私聊/附件清理、最小墓碑及专用生命周期查询完整 |
| M06 | 真实事务与应用测试 | M03—M05；主题W01、W04—W09覆盖 |

禁止把业务实体改成直接Controller读写；失效作用域不重用，业务异常通过结果映射。第1批参与状态、退出/重入及删除生命周期已实现；完整模块中的元数据编辑、私聊可见投影仍待后续实施。当前验证及范围统一见[服务端实现计划](../../impl.md)。
