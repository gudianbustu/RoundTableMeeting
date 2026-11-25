import { isUntypedNode } from '@microsoft/kiota-abstractions'

export type AvatarChoice = { id: string; name: string; imageUrl: string; category?: string }

export function avatarUrl(value: unknown): string | undefined {
  if (typeof value !== 'string' || !value) return
  try {
    const url = new URL(value, window.location.origin)
    if (url.origin === window.location.origin && !url.pathname.startsWith('//') && ['http:', 'https:'].includes(url.protocol)) return url.pathname + url.search
  } catch { /* An unavailable image uses the existing initials or icon. */ }
}

export function avatarColorSlot(value: unknown): number | undefined {
  const slot = isUntypedNode(value) ? value.getValue() : value
  return typeof slot === 'number' && Number.isSafeInteger(slot) && slot >= 0 ? slot : undefined
}
