# 测试说明

项目有三层测试，按验证目标选择框架：

| 层 | 运行器 | 位置 | 适用场景 |
|---|---|---|---|
| **组件逻辑单测** | vitest（jsdom） | `src/`（与被测代码同目录）+ `scripts/*.test.mjs` | 纯逻辑、状态流转、数据→DOM 映射、交互回调。快，日常频繁跑。看不到真实布局。 |
| **浏览器组件测试** | vitest browser mode（真实 Chromium） | `tests/browser/` | 组件界面行为：布局、滚动、样式几何等 jsdom 覆盖不了的。**测试完浏览器保持渲染，可继续手动交互。** |
| **端到端（e2e）** | @playwright/test | `tests/e2e/` | 全链路验证：真实登录（Cookie）、vite proxy、SignalR、多页面跳转、路由守卫。需要真实后端。 |

选用原则：**验证"全链路"用 Playwright；验证"单个组件在真实浏览器里的表现"用 browser mode；验证"逻辑"用 jsdom 单测。** 不确定时先写单测，涉及布局再上 browser mode，涉及登录态/跨页面再上 Playwright。

## 运行

```bash
pnpm test             # jsdom 单测（vitest projects: unit）
pnpm test:browser     # 浏览器组件测试（vitest projects: browser，无头 Chromium）
pnpm test:browser:ui  # 浏览器组件测试 + Vitest UI（有头浏览器，中栏有实时页面预览，可交互）
pnpm test:e2e           # Playwright e2e（自动拉起后端 5043 + 前端 5173，已在运行则复用）
pnpm test:e2e:ui        # Playwright UI 可视化调试（时间旅行、watch 重跑）
pnpm test:e2e:codegen   # 打开浏览器交互，自动把点击操作生成 e2e 代码
```

vitest browser mode 的纯 watch 体验（改代码自动重跑、浏览器常驻可交互）：`pnpm vitest --project browser`。

## 前置条件

- .NET SDK 10（见仓库根 `global.json`）——仅 Playwright e2e 需要
- Node 24 + pnpm 11（见 `package.json` `engines`）
- Chromium 浏览器（browser mode 与 Playwright 共用）：`pnpm exec playwright install chromium`

## 测试账号（仅 Playwright e2e）

使用 `Server/RoundTableMeeting/appsettings.Development.json` 中的 `SeedAccounts`（alice / bob，密码 123456）。browser mode 与 jsdom 单测不依赖登录态，数据直接在测试内构造。

## 产物（均已被仓库根 `.gitignore` 忽略）

- Playwright HTML 报告：`playwright-report/`（`pnpm exec playwright show-report` 查看）
- Playwright 失败 trace：`test-results/`
