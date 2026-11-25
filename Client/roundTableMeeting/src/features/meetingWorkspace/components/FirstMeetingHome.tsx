import { BrandOrbit } from '@/shared/components/BrandOrbit'
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
      <BrandOrbit className="first-home-orbit" />
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
