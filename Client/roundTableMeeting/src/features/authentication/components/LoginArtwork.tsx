import type { CSSProperties } from 'react'

const orbits = [
  { transform: 'translate(192 410) rotate(-24)', rx: 225, ry: 445 },
  { transform: 'translate(196 424) rotate(32)', rx: 176, ry: 465 },
  { transform: 'translate(205 444) rotate(53)', rx: 252, ry: 345 },
]

export function LoginArtwork({ variant }: { variant: 'mobile' | 'panel' }) {
  if (variant === 'panel') {
    return <aside className="signin-art" aria-label="围一张桌，一起讨论。邀请人与 Agent 加入同一场会议。">
      <span>围一张桌，一起讨论。</span>
    </aside>
  }
  return <svg className="signin-orbits" viewBox="0 0 390 844" preserveAspectRatio="xMidYMid slice" aria-hidden="true">
    {orbits.map(({ transform, rx, ry }, index) => {
      const path = `M ${rx},0 A ${rx} ${ry} 0 1 1 ${-rx},0 A ${rx} ${ry} 0 1 1 ${rx},0`
      return <g key={transform} transform={transform} style={{ '--orbit-path': `path('${path}')` } as CSSProperties}>
        <path d={path} fill="none" stroke="currentColor" strokeOpacity=".16" />
        {[0, 1, 2].map(node => <circle key={node} r={node === 0 ? 2.5 : 1.5}
          className="signin-orbit-node" style={{
            animationDelay: `-${index * 1.26 + node * 3 + 1.375}s`,
            animationDirection: index === 1 ? 'reverse' : 'normal',
          }} />)}
      </g>
    })}
  </svg>
}
