import './SystemStatusPage.css'
import { Alert, Button, Typography } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { LoadingState } from '@/shared/components/PageState'
import { getSystemStatus } from '../api/getSystemStatus'
import { DatabaseStatus } from '../components/DatabaseStatus'
import { ServiceStatus } from '../components/ServiceStatus'
import type { SystemStatusViewModel } from '../types/systemStatus'
import type { ApiError } from '@/api/errors/ApiError'

/** 将 API 错误映射为状态页面面向用户的文案 */
export function getSystemStatusErrorCopy(error: ApiError) {
  if (error.kind === 'network') {
    return { title: '服务暂时不可访问', description: '请检查服务端是否已启动，然后重试。' }
  }
  if (error.kind === 'problemDetails') {
    switch (error.code) {
      case 'validation_failed':
        return { title: '请求数据未通过校验', description: '请检查请求内容后重试。' }
      case 'concurrency_conflict':
        return { title: '数据保存发生冲突', description: '数据可能已被其他操作更新，请稍后重试。' }
      case 'internal_error':
        return { title: '服务端发生错误', description: '服务端暂时无法完成请求，请稍后重试。' }
      default:
        return { title: error.status === 404 ? '请求的资源不存在' : '请求未能完成', description: '请稍后重试。' }
    }
  }
  return { title: '请求未能完成', description: '请稍后重试。' }
}

/** 加载并显示匿名服务健康摘要 */
export function SystemStatusPage() {
  const [state, setState] = useState<'loading' | 'ready' | 'error'>('loading')
  const [status, setStatus] = useState<SystemStatusViewModel>()
  const [error, setError] = useState<ApiError>()
  const controllerRef = useRef<AbortController | undefined>(undefined)
  const load = useCallback(() => {
    controllerRef.current?.abort()
    const controller = new AbortController()
    controllerRef.current = controller
    setState('loading')
    void getSystemStatus(controller.signal).then((value) => { if (!controller.signal.aborted) { setStatus(value); setError(undefined); setState('ready') } }).catch((value: unknown) => {
      if (controller.signal.aborted || (value instanceof Error && value.name === 'AbortError')) return
      if (!controller.signal.aborted) { setError(value as ApiError); setState('error') }
    })
  }, [])
  useEffect(() => { load(); return () => controllerRef.current?.abort() }, [load])

  if (state === 'loading') return <LoadingState />
  if (state === 'error' || !status) {
    const errorCopy = getSystemStatusErrorCopy(error ?? { kind: 'unknown', message: '请求未能完成，请稍后重试。' })
    return <section className="system-status"><h1>框架运行状态</h1>
      <div className="system-status-panel status-problem" role="alert">
        <span className="status-problem-icon" aria-hidden="true">!</span>
        <h2>{errorCopy.title}</h2><p>{errorCopy.description}</p>
        {error?.traceId && <p>关联标识：{error.traceId}</p>}
        <Button type="primary" onClick={load}>重试</Button>
      </div>
    </section>
  }
  const degraded = status.status === 'degraded'
  const rows = [
    ['客户端', <span className="status-loaded">已加载</span>],
    ['服务端', <ServiceStatus status={status.status} />],
    ['数据库', <DatabaseStatus status={status.databaseStatus} />],
    ['运行环境', status.environment],
  ] as const
  return <section className="system-status">
    <h1>框架运行状态</h1>
    {degraded && <Alert type="warning" showIcon title="服务端已连接，但数据库处于不可用状态。" />}
    <div className="system-status-panel">
      <table aria-label="框架运行状态详情"><tbody>{rows.map(([label, value]) =>
        <tr key={label}><th scope="row">{label}</th><td>{value}</td></tr>)}</tbody></table>
    </div>
  </section>
}

/** 为未知路由渲染兜底页面 */
export function NotFoundPage() { return <section className="page-state"><Typography.Title level={2}>页面不存在</Typography.Title><Typography.Text>请返回系统状态页面。</Typography.Text><Link to="/system/status">返回系统状态</Link></section> }
