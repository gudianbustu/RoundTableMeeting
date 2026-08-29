import { createBrowserRouter, Navigate } from 'react-router-dom'
import { AppLayout } from '../layouts/AppLayout'
import { RouteErrorElement } from './RouteErrorElement'
import { NotFoundPage, SystemStatusPage } from '@/features/systemStatus'

export const router = createBrowserRouter([
  {
    element: <AppLayout />,
    errorElement: <RouteErrorElement />,
    children: [
      { path: '/', element: <Navigate to="/system/status" replace /> },
      { path: '/system/status', element: <SystemStatusPage /> },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
])
