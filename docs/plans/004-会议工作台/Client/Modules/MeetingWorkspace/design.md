# 工作台客户端模块设计

## 1. 责任与出口

模块对页面提供useMeetingWorkspace、meeting列表/详情视图模型、创建/预检/加入/编辑/删除/退出/邀请回调。API适配复用Kiota，状态与权限定义见[客户端设计](../../design.md)及[服务端设计](../../../Server/design.md)。

## 2. 状态协调

active/history列表分别缓存，选中会议来自路由。详情含permissions、revision、membershipState、lifecycleState、visibleThroughSequence；canDelete控制创建者删除入口，deleting禁止任何内容读取，left仍可读取有权历史。mutation成功更新对应列表和详情；旧revision不覆盖新值，旧generation不更新当前会议。

邀请凭证仅当前弹窗内存；取消、切会、权限改变、注销清空。操作失败保留表单并输出可理解错误。发送/历史缓存和阅读锚点由005模块拥有，模块只传权限及会议标识。

## 3. 恢复与导航

无选择为空工作台；无权限404回到无选择并刷新列表；left保持当前路由转只读历史；deleting/deleted移出当前视图及两类列表并清理本会议缓存。明确rejoin才调用Join，不把详情请求失败或网络恢复映射成join。

document.title由当前页面状态统一更新：会议信息成功变更后同步，不随消息计数改变；卸载转回所属页面标题。初始加载避免短暂显示上个会议标题。
