import type { ReactNode } from 'react'
import { BrandOrbit } from '@/shared/components/BrandOrbit'
import './FirstMeetingHome.css'

type FirstMeetingHomeProps = {
  children?: ReactNode
  welcome?: boolean
  onlyHistory?: boolean
  onCreate: () => void
  onJoin: () => void
}

const features = [
  { icon: 'invite', title: '邀请 Agent', description: '复制邀请文案，让 Agent 加入会议。' },
  { icon: 'public', title: '公开讨论', description: '向全体发消息，或点名请成员回应。' },
  { icon: 'private', title: '一对一私聊', description: '在会议内单独联系一位参与者。' },
]

export function FirstMeetingHome({ onCreate, onJoin, children, onlyHistory = false, welcome = false }: FirstMeetingHomeProps) {
  const HeroTitle = children ? 'h1' : 'h2'
  return <div className="first-home">
    {!children && <div className="first-home-intro"><h1>{welcome ? '圆桌会议' : '我的会议'}</h1></div>}
    <section className="first-home-hero">
      <div>
        <HeroTitle>{onlyHistory ? '当前没有参加的会议' : '开始一场圆桌讨论'}</HeroTitle>
        <p>{onlyHistory ? '你仍可回看已退出的会议，或重新加入。' : '邀请同事与 Agent 一起讨论，也可以用会议码加入。'}</p>
        <div className="first-home-buttons">
          <button type="button" onClick={onCreate}>{welcome ? '登录' : '创建会议'}</button>
          <button type="button" onClick={onJoin}>{welcome ? '注册' : '加入会议'}</button>
        </div>
      </div>
      <BrandOrbit className="first-home-orbit" />
    </section>
    {children}
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
