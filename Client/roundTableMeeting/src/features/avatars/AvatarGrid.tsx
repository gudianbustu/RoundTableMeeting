import { useId } from 'react'
import { Tooltip } from 'antd'
import { AvatarImage } from './AvatarImage'
import type { AvatarChoice } from './avatar'

const groupNames: Record<string, string> = { model: '模型', tool: '工具', character: 'Q 版伙伴', 'ai-girl': 'AI娘', uploaded: '自定义' }

export function AvatarGrid({ items, selected, onSelect, disabled = false, label = '选择头像' }: {
  items: AvatarChoice[]; selected?: string; onSelect?: (id: string) => void; disabled?: boolean; label?: string
}) {
  const name = useId()
  const groups = new Map<string, AvatarChoice[]>()
  for (const item of items) {
    const group = groupNames[item.category ?? ''] ?? '可用头像'
    const entries = groups.get(group)
    if (entries) entries.push(item)
    else groups.set(group, [item])
  }
  return <div role={onSelect ? 'radiogroup' : undefined} aria-label={label}>
    {Array.from(groups, ([group, entries]) => <section key={group} aria-label={group}>
      <h2>{group}</h2>
      <div className="rt-avatar-grid" role={onSelect ? undefined : 'list'} aria-label={group}>
        {entries.map(item => <Tooltip key={item.id} title={item.name} trigger={['hover', 'focus']}>
          {onSelect ? <label className="rt-avatar-cell">
            <input type="radio" disabled={disabled} name={name} aria-label={item.name} checked={item.id === selected} onChange={() => onSelect(item.id)} />
            <AvatarImage src={item.imageUrl} fallback={Array.from(item.name)[0]} />
            {item.id === selected && <i aria-hidden="true">✓</i>}
          </label> : <span className="rt-avatar-cell" role="listitem" tabIndex={0} aria-label={item.name}>
            <AvatarImage src={item.imageUrl} fallback={Array.from(item.name)[0]} />
          </span>}
        </Tooltip>)}
      </div>
    </section>)}
  </div>
}
