import { useContext } from 'react'
import { AuthenticationContext } from '../context/AuthenticationContext'

/** 读取认证上下文，并在 Provider 外使用时快速失败 */
export function useAuthentication() {
  const value = useContext(AuthenticationContext)
  if (!value) throw new Error('useAuthentication must be used within AuthenticationProvider')
  return value
}
