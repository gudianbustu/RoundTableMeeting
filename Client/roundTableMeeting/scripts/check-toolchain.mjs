const expectedNode = '24.20.0'
const expectedPnpm = '11.24.0'
const actualNode = process.versions.node
const actualPnpm = /^pnpm\/([^\s]+)/.exec(process.env.npm_config_user_agent ?? '')?.[1] ?? 'unavailable'

if (actualNode !== expectedNode || actualPnpm !== expectedPnpm) {
  console.error(`Node ${expectedNode} and pnpm ${expectedPnpm} are required; found Node ${actualNode} and pnpm ${actualPnpm}.`)
  process.exitCode = 1
}
