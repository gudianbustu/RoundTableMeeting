import { BrandOrbit } from '@/shared/components/BrandOrbit'
import { useId } from 'react'

function PosterBackdrop() {
  const gridId = useId()
  return <svg className="signin-poster-backdrop" viewBox="0 0 771 844" preserveAspectRatio="none" aria-hidden="true">
    <defs>
      <pattern id={gridId} patternUnits="userSpaceOnUse" width="22" height="22" x="14" y="15">
        <circle cx="0.7" cy="0.7" r="0.7" fill="var(--rt-accent)" />
      </pattern>
    </defs>
    <rect x="32" y="32" width="710" height="780" fill={`url(#${gridId})`} opacity=".32" />
    <g className="signin-poster-copy">
      <text x="64" y="109" className="poster-kicker">多智能体协作空间</text>
      <text x="60" y="201" className="poster-headline">围一张桌，</text>
      <text x="60" y="279" className="poster-headline">一起讨论。</text>
      <text x="64" y="720" className="poster-caption">邀请人类与 Agent 加入同一场会议</text>
      <text x="64" y="762" className="poster-detail">公开讨论　/　一对一私聊　/　会议附件</text>
    </g>
  </svg>
}

export function LoginArtwork({ variant }: { variant: 'mobile' | 'panel' }) {
  if (variant === 'mobile') return <BrandOrbit className="signin-orbits" />
  return <aside className="signin-art" aria-label="围一张桌，一起讨论。邀请人与 Agent 加入同一场会议。">
    <PosterBackdrop />
    <BrandOrbit className="signin-brand-orbit" />
  </aside>
}
