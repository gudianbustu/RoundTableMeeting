import type { CSSProperties } from 'react'
import './BrandOrbit.css'

// Cosine and sine vectors reproduce the nine dots from the original 4.8-second artwork.
const nodes = [
  [-226.73, -19.96, -99.84, -124.50, 6.3],
  [-168.96, 51.38, -25.03, -60.31, 6.3],
  [-158.85, -40.56, 222.03, -113.62, 5],
  [-112.88, 118.58, -248.63, 21.67, 6.3],
  [26.89, -97.87, 246.43, 79.44, 5],
  [62.84, -77.95, 158.95, -14.25, 5],
  [106.23, 26.63, -133.90, 74.63, 5],
  [199.97, 117.73, -146.50, 45.04, 5],
  [271.71, -78.12, 26.55, 91.94, 5],
] as const

export function BrandOrbit({ className }: { className: string }) {
  return <svg className={`brand-orbit ${className}`} viewBox="-284 -139 568 278" aria-hidden="true">
    <g fill="none" stroke="var(--rt-accent)" opacity=".52">
      {[nodes[0], nodes[1], nodes[3]].map(([cosX, cosY, sinX, sinY]) =>
        <circle key={cosX} r="1" vectorEffect="non-scaling-stroke"
          transform={`matrix(${cosX} ${cosY} ${sinX} ${sinY} 0 0)`} />)}
      <circle r="38" opacity=".45" />
      <circle r="28" opacity=".65" />
      <circle r="18" strokeWidth="2" />
    </g>
    <circle r="5.5" fill="var(--rt-accent)" />
    {nodes.map(([cosX, cosY, sinX, sinY, radius]) =>
      <circle className="brand-orbit-node" key={cosX} r={radius} style={{
        '--cos-x': `${cosX}px`, '--cos-y': `${cosY}px`,
        '--sin-x': `${sinX}px`, '--sin-y': `${sinY}px`,
      } as CSSProperties} />)}
  </svg>
}
