import { Button, Empty, Spin } from 'antd'
import type { ReactNode } from 'react'
import type { useAvatarCatalog } from './useAvatarCatalog'

export function AvatarCatalogContent({ catalog, children }: { catalog: ReturnType<typeof useAvatarCatalog>; children: ReactNode }) {
  if (catalog.status === 'loading') return <Spin aria-label="正在加载头像" />
  if (catalog.status === 'error') return <span role="alert">头像库加载失败。<Button type="link" onClick={catalog.reload}>重试</Button></span>
  if (!catalog.items.length) return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description="暂无可用头像" />
  return <>{children}</>
}
