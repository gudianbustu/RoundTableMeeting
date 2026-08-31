import { RouterProvider } from 'react-router-dom'
import { AppProviders } from './providers/AppProviders'
import { router } from './router'

/** 组合各 Provider 与路由器的应用根组件 */
export function App() {
  return <AppProviders><RouterProvider router={router} /></AppProviders>
}
