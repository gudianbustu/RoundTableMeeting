import { useContext } from 'react'
import { AuthenticationContext } from '../context/AuthenticationContext'

export function useAuthentication() {
  const value = useContext(AuthenticationContext)
  if (!value) throw new Error('useAuthentication must be used within AuthenticationProvider')
  return value
}
