import { useRef, type ReactNode } from 'react'
import {
  closestCenter, DndContext, KeyboardSensor, MouseSensor, TouchSensor, useSensor, useSensors,
  type CollisionDetection,
} from '@dnd-kit/core'
import {
  rectSortingStrategy, SortableContext, sortableKeyboardCoordinates, useSortable, verticalListSortingStrategy,
} from '@dnd-kit/sortable'
import { CSS } from '@dnd-kit/utilities'
import { DeleteOutlined, EditOutlined, EllipsisOutlined, HolderOutlined, PlusOutlined } from '@ant-design/icons'
import { Button, Dropdown } from 'antd'
import { AvatarImage } from './AvatarImage'
import type { AvatarChoice } from './avatar'

export type AvatarGroup = { name: string; items: AvatarChoice[] }
export type LibraryDialog = { kind: 'createGroup'; group?: undefined; item?: undefined } | { kind: 'renameGroup' | 'deleteGroup' | 'upload' | 'renameAvatar' | 'deleteAvatar'; group: AvatarGroup; item?: AvatarChoice }
type Reorder = (from: string, target: string, after: boolean) => void

export function SortableList({ ids, groups = false, listClassName, onMove, children }: {
  ids: string[]; groups?: boolean; listClassName?: string; onMove: Reorder; children: ReactNode
}) {
  const container = useRef<HTMLDivElement>(null)
  const sensors = useSensors(
    useSensor(MouseSensor, { activationConstraint: { distance: 6 } }),
    useSensor(TouchSensor, { activationConstraint: { delay: 220, tolerance: 6 } }),
    useSensor(KeyboardSensor, {
      coordinateGetter: sortableKeyboardCoordinates,
      keyboardCodes: { start: ['Space'], end: ['Space', 'Enter'], cancel: ['Escape'] },
    }),
  )
  const collision: CollisionDetection = args => {
    const p = args.pointerCoordinates
    const viewport = container.current?.closest('[data-sort-viewport]')
    const bounds = (viewport ?? container.current)?.getBoundingClientRect()
    if (p && bounds && (p.x < bounds.left || p.x > bounds.right || p.y < bounds.top || p.y > bounds.bottom)) return []
    return closestCenter(args)
  }
  return <DndContext sensors={sensors} collisionDetection={collision}
    accessibility={{ screenReaderInstructions: { draggable: '按空格开始排序，用方向键移动，再按空格确认，按 Escape 取消。' } }}
    onDragEnd={({ active, over }) => {
      if (!over || active.id === over.id) return
      const from = ids.indexOf(String(active.id)), to = ids.indexOf(String(over.id))
      if (from >= 0 && to >= 0) onMove(ids[from], ids[to], from < to)
    }}>
    <SortableContext items={ids} strategy={groups ? verticalListSortingStrategy : rectSortingStrategy}>
      <div ref={container} className={listClassName ?? (groups ? 'library-groups' : 'library-avatars')}>{children}</div>
    </SortableContext>
  </DndContext>
}

type TileProps = { busy: boolean; selected: string; select: (id: string) => void; open: (dialog: LibraryDialog) => void }

export function LibraryGroupCard({ group, onMove, ...controls }: TileProps & { group: AvatarGroup; onMove: Reorder }) {
  const drag = useSortable({ id: group.name, disabled: controls.busy })
  const open = (kind: 'renameGroup' | 'deleteGroup' | 'upload') => controls.open({ kind, group })
  return <section className="library-group" aria-label={group.name} data-dragging={drag.isDragging || undefined}
    ref={drag.setNodeRef} style={{ transform: CSS.Translate.toString(drag.transform), transition: drag.transition }}>
    <div className="library-group-head">
      <h2><button ref={drag.setActivatorNodeRef} {...drag.attributes} {...drag.listeners} disabled={controls.busy}
        className="library-handle" aria-label={`拖动分组「${group.name}」排序`}><HolderOutlined />{group.name}</button></h2>
      <div className="library-group-actions">
        <Button size="small" icon={<PlusOutlined />} disabled={controls.busy} onClick={() => open('upload')}>上传头像</Button>
        <Dropdown trigger={['hover', 'click']} placement="bottomRight" disabled={controls.busy}
          menu={{ items: [
            { key: 'renameGroup', label: '编辑分组', icon: <EditOutlined />, onClick: () => open('renameGroup') },
            { key: 'deleteGroup', label: '删除分组', icon: <DeleteOutlined />, danger: true, onClick: () => open('deleteGroup') },
          ] }}>
          <Button size="small" type="text" icon={<EllipsisOutlined />} disabled={controls.busy}
            aria-label={`更多分组操作：${group.name}`} aria-haspopup="menu" />
        </Dropdown>
      </div>
    </div>
    {group.items.length ? <SortableList ids={group.items.map(item => item.id)} onMove={onMove}>
      {group.items.map(item => <AvatarTile key={item.id} group={group} item={item} {...controls} />)}
    </SortableList> : <p className="library-empty">暂无头像</p>}
  </section>
}

function AvatarTile({ group, item, busy, selected, select, open }: TileProps & { group: AvatarGroup; item: AvatarChoice }) {
  const drag = useSortable({ id: item.id, disabled: busy })
  const active = item.id === selected
  return <div ref={drag.setNodeRef} className="library-tile" data-dragging={drag.isDragging || undefined}
    style={{ transform: CSS.Translate.toString(drag.transform), transition: drag.transition }}>
    <button ref={drag.setActivatorNodeRef} {...drag.attributes} {...drag.listeners} type="button" disabled={busy}
      aria-label={item.name} aria-pressed={active} className="library-choice" onClick={() => select(item.id)}>
      <span className="library-face"><AvatarImage src={item.imageUrl} fallback={Array.from(item.name)[0]} /></span>
      <span className="library-filename" title={item.name}>{item.name}</span>
    </button>
    {active && <>
      <button type="button" className="library-edit" aria-label={`修改头像「${item.name}」文件名`} disabled={busy}
        onClick={() => open({ kind: 'renameAvatar', group, item })}><EditOutlined /></button>
      <button type="button" className="library-delete" aria-label={`删除头像「${item.name}」`} disabled={busy}
        onClick={() => open({ kind: 'deleteAvatar', group, item })}><DeleteOutlined /></button>
    </>}
  </div>
}
