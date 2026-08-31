import { createContext } from 'react'
import type { AuthenticationContextValue } from '../types/authentication'

/** 向功能树提供认证状态和操作 */
export const AuthenticationContext = createContext<AuthenticationContextValue | undefined>(undefined)
