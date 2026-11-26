import { AvatarLibraryPage } from '@/features/avatars/AvatarLibraryPage'
import { AdminGate } from './AdminGate'
import { PersonalSettingsPage } from '@/features/accountSettings'
import { FirstMeetingHome } from '@/features/meetingWorkspace/components/FirstMeetingHome'
import { createBrowserRouter, useNavigate } from 'react-router-dom'
import { AppLayout } from '../layouts/AppLayout'
import { RouteErrorElement } from './RouteErrorElement'
import { NotFoundPage, SystemStatusPage } from '@/features/systemStatus'
import { AuthGate, LoginPage } from '@/features/authentication'
import { AuthenticatedLayout } from '../layouts/AuthenticatedLayout'
import { UserManagementPage } from '@/features/userManagement'
import { MeetingWorkspacePage } from '@/features/meetingWorkspace'

function WelcomePage() {
  const navigate = useNavigate()
  return <main className="app-shell"><FirstMeetingHome welcome onCreate={() => navigate('/login')} onJoin={() => navigate('/register')} /></main>
}

/** 公共页面、认证页面和回退页面的浏览器路由树 */
export const router = createBrowserRouter([
  { path: '/welcome', element: <WelcomePage /> },
  { path: '/register', element: <LoginPage registration /> },
  { path: '/login', element: <LoginPage /> },
  {
    element: <AppLayout />,
    errorElement: <RouteErrorElement />,
    children: [
      { path: '/system/status', element: <SystemStatusPage /> },
    ],
  },
  {
    element: <AuthGate />,
    children: [{ element: <AuthenticatedLayout />, children: [{ path: '/', element: <MeetingWorkspacePage /> }, { path: '/meetings/:meetingId', element: <MeetingWorkspacePage /> }, { path: '/settings/profile', element: <PersonalSettingsPage /> }, { element: <AdminGate />, children: [{ path: '/users', element: <UserManagementPage /> }, { path: '/admin/avatars', element: <AvatarLibraryPage /> }] }] }],
  },
  { path: '*', element: <NotFoundPage /> },
])
