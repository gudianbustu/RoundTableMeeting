import { useCallback, useEffect, useMemo, useRef, useState, type PropsWithChildren } from 'react'
import { subscribeUnauthorized } from '@/api/client/authenticationEvents'
import type { ApiError } from '@/api/errors/ApiError'
import { getCurrentUser } from '../api/getCurrentUser'
import { login as loginRequest, register } from '../api/login'
import { logout as logoutRequest } from '../api/logout'
import { AuthenticationContext } from '../context/AuthenticationContext'
import type { AuthenticationState, RefreshCurrentUserResult } from '../types/authentication'

/** 管理认证初始化、登录退出操作和取消 */
export function AuthenticationProvider({ children }: PropsWithChildren) {
  const [state, storeState] = useState<AuthenticationState>({ status: 'checking' })
  const stateRef = useRef<AuthenticationState>({ status: 'checking' })
  const identity = useRef(0)
  const refresh = useRef(0)
  const [identityRevision, setIdentityRevision] = useState(0)
  const [refreshRevision, setRefreshRevision] = useState(0)
  const invalidateAdminAccess = useCallback(() => { setIdentityRevision(++identity.current) }, [])
  const setState = useCallback((next: AuthenticationState) => {
    const previous = stateRef.current
    if (previous.status !== next.status || (previous.status === 'authenticated' && next.status === 'authenticated'
      && (previous.user.userId !== next.user.userId || previous.user.isAdmin !== next.user.isAdmin))) {
      setIdentityRevision(++identity.current)
    }
    stateRef.current = next
    storeState(next)
  }, [])
  const [isLoggingIn, setIsLoggingIn] = useState(false)
  const [isLoggingOut, setIsLoggingOut] = useState(false)
  const [operationError, setOperationError] = useState<ApiError>()
  const operationId = useRef(0)
  const controllerRef = useRef<AbortController | undefined>(undefined)
  const refreshControllerRef = useRef<AbortController | undefined>(undefined)
  // 开始新操作前取消上一个操作，避免过期状态写入
  const beginOperation = useCallback(() => {
    controllerRef.current?.abort()
    refreshControllerRef.current?.abort()
    setIsLoggingIn(false)
    setIsLoggingOut(false)
    setOperationError(undefined)
    const controller = new AbortController()
    controllerRef.current = controller
    setIdentityRevision(++identity.current)
    operationId.current += 1
    return { id: operationId.current, controller }
  }, [])
  const cancelOperation = useCallback(() => {
    controllerRef.current?.abort()
    refreshControllerRef.current?.abort()
    controllerRef.current = undefined
    setIdentityRevision(++identity.current)
    operationId.current += 1
    setIsLoggingIn(false)
    setIsLoggingOut(false)
    setOperationError(undefined)
  }, [])
  const initialization = useCallback(async (id: number, signal: AbortSignal) => {
    try {
      const user = await getCurrentUser(signal)
      if (id === operationId.current && !signal.aborted) setState({ status: 'authenticated', user })
    } catch (error) {
      if (signal.aborted || (error instanceof Error && error.name === 'AbortError') || id !== operationId.current) return
      const apiError = error as ApiError
      setState(apiError.status === 401 || apiError.code === 'unauthenticated' ? { status: 'anonymous' } : { status: 'error', error: apiError })
    }
  }, [])
  const retry = useCallback(async () => {
    const { id, controller } = beginOperation()
    setState({ status: 'checking' })
    await initialization(id, controller.signal)
  }, [beginOperation, initialization])
  useEffect(() => {
    const { id, controller } = beginOperation()
    void initialization(id, controller.signal)
    const unsubscribe = subscribeUnauthorized(() => {
      cancelOperation()
      setState({ status: 'anonymous' })
    })
    return () => { cancelOperation(); unsubscribe() }
  }, [beginOperation, cancelOperation, initialization])
  const login = useCallback(async (username: string, password: string, registration = false) => {
    const { id, controller } = beginOperation()
    setIsLoggingIn(true); setOperationError(undefined)
    try {
      const user = registration
        ? await register({ username, password }, controller.signal)
        : await loginRequest({ username, password }, controller.signal)
      if (id === operationId.current) setState({ status: 'authenticated', user })
      return user
    } catch (error) {
      if (id === operationId.current && !(error instanceof Error && error.name === 'AbortError')) setOperationError(error as ApiError)
      throw error
    } finally {
      if (id === operationId.current) {
        controllerRef.current = undefined
        setIsLoggingIn(false)
      }
    }
  }, [beginOperation])
  const logout = useCallback(async () => {
    const { id, controller } = beginOperation()
    setIsLoggingOut(true); setOperationError(undefined)
    try {
      await logoutRequest(controller.signal)
      if (id === operationId.current) setState({ status: 'anonymous' })
    } catch (error) {
      if (id === operationId.current) setOperationError(error as ApiError)
      throw error
    } finally {
      if (id === operationId.current) {
        controllerRef.current = undefined
        setIsLoggingOut(false)
      }
    }
  }, [beginOperation])
  const refreshCurrentUser = useCallback(async (): Promise<RefreshCurrentUserResult> => {
    if (stateRef.current.status !== 'authenticated' || controllerRef.current && (isLoggingIn || isLoggingOut)) return { status: 'cancelled' }
    refreshControllerRef.current?.abort()
    const controller = new AbortController()
    refreshControllerRef.current = controller
    const id = ++refresh.current
    setRefreshRevision(id)
    const identityAtStart = identity.current
    try {
      const user = await getCurrentUser(controller.signal)
      if (controller.signal.aborted || id !== refresh.current || identityAtStart !== identity.current) return { status: 'cancelled' }
      setState({ status: 'authenticated', user })
      return { status: 'refreshed', user, identityRevision: identity.current, refreshRevision: id }
    } catch (error) {
      if (controller.signal.aborted || id !== refresh.current || identityAtStart !== identity.current) return { status: 'cancelled' }
      const apiError = error as ApiError
      if (apiError.status === 401 || apiError.code === 'unauthenticated') {
        cancelOperation()
        setState({ status: 'anonymous' })
      }
      throw error
    } finally {
      if (refreshControllerRef.current === controller) refreshControllerRef.current = undefined
    }
  }, [cancelOperation, setState, isLoggingIn, isLoggingOut])
  const value = useMemo(() => ({ state, login, logout, retry, refreshCurrentUser, isLoggingIn, isLoggingOut, operationError, identityRevision, refreshRevision, invalidateAdminAccess }), [state, login, logout, retry, refreshCurrentUser, isLoggingIn, isLoggingOut, operationError, identityRevision, refreshRevision, invalidateAdminAccess])
  return <AuthenticationContext.Provider value={value}>{children}</AuthenticationContext.Provider>
}
