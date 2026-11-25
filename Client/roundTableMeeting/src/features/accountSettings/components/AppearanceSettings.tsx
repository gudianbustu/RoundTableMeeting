import { Card, Radio } from 'antd'
import { palettes, saveAppearance, useAppearance, type Palette } from '@/app/theme/appearance'

import { colors } from '@/app/theme/appearanceColors'

export function AppearanceSettings() {
  const { palette, preference, mode } = useAppearance()
  return <Card title="外观">
    <div className="appearance-options">
      <fieldset><legend>配色主题</legend>
        <Radio.Group className="palette-options" aria-label="配色主题" value={palette} onChange={event => saveAppearance(event.target.value, preference)}>
          {(Object.keys(palettes) as Palette[]).map(key => <Radio key={key} value={key}>
            <i aria-hidden className="palette-swatch" style={{ backgroundImage: `linear-gradient(135deg, ${colors[key][mode].primary} 50%, ${colors[key][mode].accent} 50%)` }} />{palettes[key]}
          </Radio>)}
        </Radio.Group>
      </fieldset>
      <fieldset><legend>明暗模式</legend>
        <Radio.Group className="mode-options" aria-label="明暗模式" value={preference} onChange={event => saveAppearance(palette, event.target.value)}>
          <Radio.Button value="system">跟随系统</Radio.Button><Radio.Button value="light">浅色</Radio.Button><Radio.Button value="dark">深色</Radio.Button>
        </Radio.Group>
      </fieldset>
      <p>外观偏好仅保存在当前设备。</p>
    </div>
  </Card>
}
