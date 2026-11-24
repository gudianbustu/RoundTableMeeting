import type { CSSProperties } from 'react'
import './FirstMeetingHome.css'

type FirstMeetingHomeProps = {
  onCreate: () => void
  onJoin: () => void
}

const features = [
  { icon: 'invite', title: '邀请 Agent', description: '复制邀请文案，让 Agent 加入会议。' },
  { icon: 'public', title: '公开讨论', description: '向全体发消息，或点名请成员回应。' },
  { icon: 'private', title: '一对一私聊', description: '在会议内单独联系一位参与者。' },
]

const orbits = [
  { rx: 216, ry: 68, angle: -17, phases: [1, 9, 19] },
  { rx: 197, ry: 70, angle: 18, phases: [3, 13, 23] },
  { rx: 134, ry: 49, angle: -17, phases: [6, 18] },
]

function HomeOrbit() {
  return <svg className="first-home-orbit" viewBox="0 0 432 220" aria-hidden="true">
    {orbits.map(({ rx, ry, angle, phases }) => {
      const path = `M ${rx} 0 a ${rx} ${ry} 0 1 1 ${-2 * rx} 0 a ${rx} ${ry} 0 1 1 ${2 * rx} 0`
      return <g key={rx} transform={`translate(214 110) rotate(${angle})`} style={{ '--home-orbit': `path('${path}')` } as CSSProperties}>
        <path d={path} />
        {phases.map(phase => <circle key={phase} r="4" className="first-home-node" style={{ animationDelay: `-${phase}s` }} />)}
      </g>
    })}
    {[29, 21, 13].map(radius => <circle key={radius} cx="214" cy="110" r={radius} className="first-home-ring" />)}
    <circle cx="214" cy="110" r="5" />
  </svg>
}

export function FirstMeetingHome({ onCreate, onJoin }: FirstMeetingHomeProps) {
  return <div className="first-home">
    <div className="first-home-intro">
      <h1>我的会议</h1>
      <p>你创建和加入的会议会显示在这里。</p>
    </div>
    <section className="first-home-hero">
      <div>
        <h2>开始一场圆桌讨论</h2>
        <p>邀请同事与 Agent 一起讨论，也可以用会议码加入。</p>
        <div className="first-home-buttons">
          <button type="button" onClick={onCreate}>创建会议</button>
          <button type="button" onClick={onJoin}>加入会议</button>
        </div>
      </div>
      <HomeOrbit />
    </section>
    <h2 className="first-home-bandtitle">在圆桌里，你可以</h2>
    <div className="first-home-band">
      {features.map(({ icon, title, description }) => <div className="first-home-feature" key={icon}>
        <span className={`first-home-icon first-home-icon-${icon}`} aria-hidden="true" />
        <h3>{title}</h3>
        <p>{description}</p>
      </div>)}
    </div>
  </div>
}
