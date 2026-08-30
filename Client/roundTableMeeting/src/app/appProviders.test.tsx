import { cleanup, render, screen, waitFor } from '@testing-library/react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { AppProviders } from './providers/AppProviders'
import { router } from './router'
import { RouterProvider } from 'react-router-dom'

const getCurrentUser = vi.hoisted(() => vi.fn())
const getSystemStatus = vi.hoisted(() => vi.fn())

vi.mock('@/features/authentication/api/getCurrentUser', () => ({ getCurrentUser }))
vi.mock('@/features/systemStatus/api/getSystemStatus', () => ({ getSystemStatus }))

afterEach(() => cleanup())

describe('AppProviders public routes', () => {
  it('keeps system status available when authentication initialization fails', async () => {
    getCurrentUser.mockRejectedValue({ kind: 'network', message: 'network' })
    getSystemStatus.mockResolvedValue({ status: 'available', databaseStatus: 'available', environment: 'Testing' })
    router.navigate('/system/status')

    render(<AppProviders><RouterProvider router={router} /></AppProviders>)

    await waitFor(() => expect(screen.getByText('框架运行状态')).toBeInTheDocument())
    expect(screen.queryByText('服务暂时不可访问')).not.toBeInTheDocument()
  })
})
