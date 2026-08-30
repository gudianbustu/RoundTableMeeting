import { useCallback, useEffect, useMemo, useRef, useState, type PropsWithChildren } from 'react'
import { subscribeUnauthorized } from '@/api/client/authenticationEvents'
import type { ApiError } from '@/api/errors/ApiError'
import { getCurrentUser } from '../api/getCurrentUser'
import { login as loginRequest } from '../api/login'
import { logout as logoutRequest } from '../api/logout'
import { AuthenticationContext } from '../context/AuthenticationContext'
import type { AuthenticationState } from '../types/authentication'

export function AuthenticationProvider({ children }: PropsWithChildren) {
  const [state, setState] = useState<AuthenticationState>({ status: 'checking' })
  const [isLoggingIn, setIsLoggingIn] = useState(false)
  const [isLoggingOut, setIsLoggingOut] = useState(false)
  const [operationError, setOperationError] = useState<ApiError>()
  const operationId = useRef(0)
  const controllerRef = useRef<AbortController | undefined>(undefined)
  const beginOperation = useCallback(() => {
    controllerRef.current?.abort()
    setIsLoggingIn(false)
    setIsLoggingOut(false)
    setOperationError(undefined)
    const controller = new AbortController()
    controllerRef.current = controller
    operationId.current += 1
    return { id: operationId.current, controller }
  }, [])
  const cancelOperation = useCallback(() => {
    controllerRef.current?.abort()
    controllerRef.current = undefined
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
  const login = useCallback(async (username: string, password: string) => {
    const { id, controller } = beginOperation()
    setIsLoggingIn(true); setOperationError(undefined)
    try {
      const user = await loginRequest({ username, password }, controller.signal)
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
  const value = useMemo(() => ({ state, login, logout, retry, isLoggingIn, isLoggingOut, operationError }), [state, login, logout, retry, isLoggingIn, isLoggingOut, operationError])
  return <AuthenticationContext.Provider value={value}>{children}</AuthenticationContext.Provider>
}
