type UnauthorizedListener = () => void

const listeners = new Set<UnauthorizedListener>()

/** 订阅未授权响应并返回取消订阅回调 */
export function subscribeUnauthorized(listener: UnauthorizedListener): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

/** 通知所有监听器某个已认证 API 请求被拒绝 */
export function notifyUnauthorized(): void {
  for (const listener of listeners) listener()
}
