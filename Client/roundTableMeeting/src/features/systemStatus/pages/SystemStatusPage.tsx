import { Alert, Descriptions, Typography } from 'antd'
import { useCallback, useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { ErrorState, LoadingState } from '@/shared/components/PageState'
import { getSystemStatus } from '../api/getSystemStatus'
import { DatabaseStatus } from '../components/DatabaseStatus'
import { ServiceStatus } from '../components/ServiceStatus'
import type { SystemStatusViewModel } from '../types/systemStatus'
import type { ApiError } from '@/api/errors/ApiError'

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
    return <section className="status-content"><ErrorState title={errorCopy.title} description={errorCopy.description} traceId={error?.traceId} onRetry={load} /></section>
  }
  const degraded = status.status === 'degraded'
  return <section className="status-content"><Typography.Title level={2}>框架运行状态</Typography.Title>{degraded && <Alert type="warning" showIcon title="服务端已连接，但数据库处于不可用状态。" />}<Descriptions bordered column={{ xs: 1, sm: 2 }}><Descriptions.Item label="客户端">已加载</Descriptions.Item><Descriptions.Item label="服务端"><ServiceStatus status={status.status} /></Descriptions.Item><Descriptions.Item label="数据库"><DatabaseStatus status={status.databaseStatus} /></Descriptions.Item><Descriptions.Item label="运行环境">{status.environment}</Descriptions.Item></Descriptions>{error?.traceId && <Typography.Text type="secondary">关联标识：{error.traceId}</Typography.Text>}</section>
}

export function NotFoundPage() { return <section className="page-state"><Typography.Title level={2}>页面不存在</Typography.Title><Typography.Text>请返回系统状态页面。</Typography.Text><Link to="/system/status">返回系统状态</Link></section> }
