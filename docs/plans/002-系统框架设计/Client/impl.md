# 系统框架客户端实现方案

## 1. 文档定位

本文把[客户端设计](design.md)具体化为 Vite 项目初始化、依赖版本、目录创建、路由、公共 Kiota Client、系统状态 feature、Vitest、开发代理和生产构建步骤。总体阶段顺序以[系统框架实现方案](../impl.md)为准，验证编号和浏览器验收以[测试方案](../testPlan.md)为准。

本文描述的客户端基线只实现系统状态页面和框架外壳，不创建登录、会议、消息或 AI 的模拟页面。

## 2. 工具链和依赖版本

### 2.1 Node.js 和 pnpm

客户端根目录 `Client/roundTableMeeting/` 建立 `.node-version`，内容固定为 `24.20.0`；`package.json` 同时固定 Volta 的 Node 与 pnpm 版本。仓库采用 Volta 作为本机版本切换方式，首次进入项目或执行客户端命令前运行 `volta install node@24.20.0` 和 `volta install pnpm@11.24.0`；CI 镜像必须使用同一 Node/pnpm 版本。`package.json` 固定：

~~~json
{
  "engines": {
    "node": ">=24.0.0 <25.0.0",
    "pnpm": ">=11.24.0 <12.0.0"
  },
  "packageManager": "pnpm@11.24.0",
  "volta": {
    "node": "24.20.0",
    "pnpm": "11.24.0"
  }
}
~~~

同时建立 `.npmrc`：

~~~ini
save-exact=true
engine-strict=true
~~~

Node 版本文件和 Volta 配置共同约束 Node 版本；文档不能把当前机器的 Node 26 当作项目运行基线。`engine-strict=true` 下，未切换到 Node 24 的环境应快速失败。安装依赖必须使用：

~~~powershell
corepack enable
corepack prepare pnpm@11.24.0 --activate
pnpm install --frozen-lockfile
~~~

客户端提供 `pnpm run check:toolchain`，直接检查实际执行脚本的 Node 为 `24.20.0`、pnpm 为 `11.24.0`。发布 Target 在安装依赖前先执行该检查；版本不匹配时 publish 必须返回非零，不能只依赖 `engine-strict` 是否把版本偏差提升为错误。

### 2.2 运行依赖

首版直接依赖固定为：

| 包 | 版本 | 用途 |
| --- | --- | --- |
| `react` | `19.2.8` | React 运行时 |
| `react-dom` | `19.2.8` | 浏览器渲染 |
| `antd` | `6.6.1` | 基础 UI 组件 |
| `@ant-design/icons` | `6.3.2` | Ant Design 图标 |
| `react-router-dom` | `7.18.2` | 页面路由 |
| `@microsoft/kiota-bundle` | `1.0.0-preview.103` | Kiota TypeScript 运行时组合包 |

`@microsoft/kiota-bundle` 的 `1.0.0-preview.103` 是 Kiota CLI `1.34.1` 执行 `kiota info -l TypeScript` 得到的配套 runtime。不要把 `latest`、范围版本或 CLI `1.34.1` 与另一套 preview runtime 混用。Kiota 生成的实际 import 如发生变化，以生成结果和 `kiota info -l TypeScript` 为准；若配套版本变更，必须同步包清单、锁文件和本段文档。

### 2.3 开发和测试依赖

| 包 | 版本 | 用途 |
| --- | --- | --- |
| `vite` | `8.2.2` | 开发服务器和生产构建 |
| `@vitejs/plugin-react` | `6.1.0` | React Vite 插件 |
| `typescript` | `5.9.3` | TypeScript 编译 |
| `@types/react` | `19.2.18` | React 类型 |
| `@types/react-dom` | `19.2.5` | React DOM 类型 |
| `@types/node` | `24.13.3` | Vite/Node 配置类型 |
| `vitest` | `4.1.11` | 测试运行器 |
| `@vitest/coverage-v8` | `4.1.11` | Vitest 覆盖率 Provider |
| `jsdom` | `30.0.1` | DOM 测试环境 |
| `@testing-library/react` | `16.3.3` | React 组件测试 |
| `@testing-library/user-event` | `14.6.6` | 用户交互测试 |
| `@testing-library/jest-dom` | `7.0.1` | DOM 断言 |

版本来源为实施阶段可访问的官方 npm registry；写入 `package.json` 时使用精确版本，安装后提交 `pnpm-lock.yaml`。Kiota CLI `1.34.1` 的 TypeScript `preview.103` 生成代码与 TypeScript `7.0.2` 在 `Partial<...>.additionalData` 类型上出现阻断性兼容问题，已按实施约定回退到精确的 TypeScript `5.9.3`；必须同步 `package.json` 和锁文件，不得使用浮动版本。

Kiota 生成代码实际 import 的运行时包全部作为直接依赖固定为 `1.0.0-preview.103`：`@microsoft/kiota-abstractions`、`@microsoft/kiota-http-fetchlibrary`、`@microsoft/kiota-serialization-form`、`@microsoft/kiota-serialization-json`、`@microsoft/kiota-serialization-multipart` 和 `@microsoft/kiota-serialization-text`；`@microsoft/kiota-bundle` 同样固定为 `1.0.0-preview.103`，不只依赖传递依赖。

## 3. 项目初始化

从仓库根目录执行：

~~~powershell
pnpm dlx create-vite@9.2.0 Client/roundTableMeeting --template react-ts
Set-Location Client/roundTableMeeting
corepack enable
corepack prepare pnpm@11.24.0 --activate
pnpm add --save-exact react@19.2.8 react-dom@19.2.8 antd@6.6.1 @ant-design/icons@6.3.2 react-router-dom@7.18.2 @microsoft/kiota-bundle@1.0.0-preview.103
pnpm add -D --save-exact vite@8.2.2 @vitejs/plugin-react@6.1.0 typescript@5.9.3 @types/react@19.2.18 @types/react-dom@19.2.5 @types/node@24.13.3 vitest@4.1.11 @vitest/coverage-v8@4.1.11 jsdom@30.0.1 @testing-library/react@16.3.3 @testing-library/user-event@14.6.6 @testing-library/jest-dom@7.0.1
pnpm install --frozen-lockfile
~~~

`pnpm add` 负责首次生成 `pnpm-lock.yaml`，最后的 frozen install 验证锁文件可以从零还原。如果 Vite 初始化器已经生成其他版本，必须在 `package.json` 中校正为本文件版本后重新生成锁文件。删除模板的 `App.css`、演示图片、计数器和示例文案；保留 `index.html`、TypeScript 配置和必要的 Vite 入口。

首版 `package.json` 脚本固定为：

~~~json
{
  "scripts": {
    "dev": "vite",
    "build": "tsc -b && vite build",
    "preview": "vite preview",
    "test": "vitest run",
    "test:watch": "vitest",
    "test:coverage": "vitest run --coverage",
    "generate:api": "node scripts/generate-api.mjs",
    "check:format": "node scripts/check-text-format.mjs"
  }
}
~~~

脚本名称是跨文档约定。`generate:api` 必须等待服务端 OpenAPI HTTP 地址，`check:format` 检查生成目录的 UTF-8 无 BOM、CRLF 和禁止尾随空白。

## 4. 客户端目录和文件

首版实际目录：

~~~text
Client/roundTableMeeting/
  public/
  scripts/
    generate-api.mjs
    check-text-format.mjs
  src/
    main.tsx
    app/
      App.tsx
      router/
        index.tsx
        RouteErrorElement.tsx
      providers/
        AppProviders.tsx
      layouts/
        AppLayout.tsx
      theme/
        index.ts
    api/
      generated/
      client/
        apiClient.ts
        requestAdapter.ts
      errors/
        ApiError.ts
        toApiError.ts
    features/
      systemStatus/
        api/
          getSystemStatus.ts
        components/
          DatabaseStatus.tsx
          ServiceStatus.tsx
        pages/
          SystemStatusPage.tsx
          SystemStatusPage.test.tsx
        types/
          systemStatus.ts
        index.ts
    shared/
      components/
        PageState.tsx
      styles/
        global.css
    test/
      setup.ts
      renderWithProviders.tsx
  index.html
  package.json
  pnpm-lock.yaml
  tsconfig.json
  tsconfig.app.json
  tsconfig.node.json
  vite.config.ts
  vitest.config.ts
~~~

只有存在实际文件时才创建目录。`api/generated/` 是 Kiota 输出目录，生成后提交但禁止手工编辑；`features/systemStatus/` 通过 `index.ts` 对外暴露页面，不把底层生成模型暴露给 app。

## 5. TypeScript、Vite 和测试配置

### 5.1 路径别名

TypeScript 的 `compilerOptions.paths`、Vite 的 `resolve.alias` 和 Vitest 的 alias 必须一致：

~~~text
@/* -> ./src/*
~~~

TypeScript 的应用配置需要包含 `src` 和测试文件；Node/Vite 配置使用独立的 `tsconfig.node.json`。构建不能因为只排除了测试类型而绕过类型错误。

### 5.2 Vite 配置

Vite 固定使用：

- 开发端口 `5173`。
- `strictPort: true`，端口被占用时明确失败，不静默改端口。
- `/api` 代理到 `http://localhost:5043`。
- 代理只覆盖 `/api`，不代理 OpenAPI 或 Scalar 页面。
- 生产 `base: '/'`，避免部署在子路径时产生未设计的 URL 变体。

代理配置使用 `changeOrigin: false`；如果 HTTPS 开发地址被选用，只在本机开发证书可接受时设置 `secure: false`，默认 HTTP 5043 不要求跳过证书验证。

### 5.3 Vitest 配置

`vitest.config.ts` 固定：

- `environment: 'jsdom'`。
- `globals: false`，测试文件显式从 Vitest 导入 API。
- `setupFiles: ['./src/test/setup.ts']`。
- `include: ['src/**/*.test.{ts,tsx}', 'scripts/**/*.test.mjs']`；脚本测试覆盖 Kiota 生成内容归一化、generated 目录替换与失败回滚，客户端源码测试覆盖页面、路由和 feature 行为。
- `resolve.alias` 与 Vite 一致。
- 覆盖率只统计 `src/` 中非 generated 的业务源码。

`src/test/setup.ts` 导入 `@testing-library/jest-dom/vitest`，并只补充 jsdom 缺失且组件实际使用的 `matchMedia`、`ResizeObserver` 等 API。不要在 setup 中模拟整个 Ant Design 或真实 API Client。

## 6. 应用组装和路由

### 6.1 入口和 Provider

`main.tsx` 负责挂载 `App`。`AppProviders` 负责组合 Ant Design `ConfigProvider`、Router 所需的上下文和未来真正需要的全局 Provider；它不创建 API 请求、不拥有 feature 状态。

主题 Token 放在 `app/theme/index.ts`，应用级全局 CSS 放在 `shared/styles/global.css`，由入口只加载一次。组件优先使用 Ant Design 组件，页面布局通过明确的 CSS grid/flex 约束，避免让动态状态改变固定区域尺寸。

### 6.2 路由

使用 React Router 的 browser router。首版路由固定包含：

| 路径 | 页面 | 说明 |
| --- | --- | --- |
| `/` | `SystemStatusPage` | 框架验收首页 |
| `/system/status` | `SystemStatusPage` | 显式状态页地址 |
| `*` | Not Found 页面 | 客户端未匹配路径 |

系统状态页面通过 `features/systemStatus/index.ts` 暴露给 `app/router`。路由级错误元素显示安全的页面错误状态，并提供返回状态页的链接；不把异常堆栈显示给用户。

后续账号主题可以把 `/` 替换为登录页或工作台，但必须保留 `/system/status` 或等价框架验证路径，直到新的业务主题重新覆盖相同集成检查。

## 7. 公共 Kiota Client

### 7.1 生成代码

生成目录固定为：

~~~text
Client/roundTableMeeting/src/api/generated/
~~~

从客户端根目录启动服务端后执行：

~~~powershell
pnpm generate:api
~~~

脚本的默认参数：

- OpenAPI 地址：`http://localhost:5043/openapi/v1.json`。
- 输出目录：`src/api/generated`。
- 语言：`TypeScript`。
- 类名：`RoundTableMeetingApiClient`。
- 生成选项：`--clean-output`、`--additional-data`。

脚本实现要求：

1. 读取 `OPENAPI_URL`、`OPENAPI_READY_TIMEOUT_MS` 等环境变量，提供合理默认值。
2. 使用 `fetch` 轮询 OpenAPI 地址，间隔 250ms，总超时 30s；只接受 HTTP 2xx。
3. 解析 JSON，确认 `openapi` 以 `3.0` 开头，且包含 `paths` 对象。
4. 在系统临时目录创建本次生成的 staging 目录；不把独立 OpenAPI JSON 作为仓库资产。
5. 调用仓库工具，把 Kiota 输出写入 staging：`dotnet tool run kiota generate --language TypeScript --openapi <url> --output <staging> --class-name RoundTableMeetingApiClient --clean-output --additional-data`。`--additional-data` 用于为生成模型保留未知字段，支持契约向前兼容；不能在未重新检查生成差异和错误适配前删除。Kiota 产生的 `.kiota.log` 只属于运行诊断，不进入 generated 交付目录。
6. 在 staging 中检查入口 Client、TypeScript 结构和绝对路径；将生成入口的默认 adapter base URL 归一化为当前页面 origin，将 `kiota-lock.json` 的描述地址归一化为相对 `/openapi/v1.json`；统一转换为 UTF-8 无 BOM，行尾使用 CRLF，并移除尾随空白。
7. staging 全部通过后才替换 `src/api/generated/`；先准备同父目录替换目录，再备份并重命名旧目录，最后切换新目录；任意失败都恢复原目录且不留下替换目录、备份目录或半生成结果。
8. 任意等待、HTTP、JSON、Kiota、文本检查或目录替换失败都返回非零，并输出不含秘密的诊断信息。

生成入口和锁文件中的 localhost 或 127.0.0.1 任意端口地址必须在 staging 阶段归一化；归一化后扫描全部 generated 文件，禁止残留任何开发绝对 URL。页面 feature 按 ApiError.kind、稳定 code 和 HTTP status 选择安全标题与说明，未知 code 使用通用文案；traceId 只作为可选关联标识展示。共享 PageState 不承载业务错误语义，AbortError、调用方取消和组件卸载不进入错误态。

脚本不启动或关闭服务端，不杀掉开发者已有进程。它只消费已经启动的服务；服务端生命周期由开发者或上层联调脚本负责。dotnet publish 使用发布环境提供的 pnpm 命令；本机验证时使用 Volta 将 Node 24.20.0 和 pnpm 11.24.0 放入当前命令 PATH，不把 Volta 作为发布机运行时依赖。

### 7.2 RequestAdapter 和单例

`api/client/apiClient.ts` 通过模块级惰性单例创建 Kiota Client：

- 客户端公开的 API 路径前缀固定为相对 `/api`，不配置环境专属后端地址。
- 使用 Kiota HTTP Fetch RequestAdapter 和 JSON serialization runtime。
- 首版使用匿名请求，不接入认证；后续认证只修改公共 RequestAdapter/RequestOption 配置，不让页面传 Token。
- 导出 `getApiClient()`，不导出让业务随意 `new` 的构造细节。
- 单例只保存无状态的 Client 和 adapter 配置，不保存当前会议、用户或页面数据。

服务端 OpenAPI path 已包含 `/api` 前缀，OpenAPI `servers` 省略或使用相对 `/`，不得写入 `http://localhost:5043` 等开发绝对地址。Kiota RequestAdapter 的技术 base URL 设置为当前浏览器 origin，不能设置为 `/api`；生成请求模板负责追加一次 `/api/...`。例如当前页面为 `http://localhost:5173/` 时，adapter base URL 为 `http://localhost:5173`，operation path 为 `/api/system/status`，最终浏览器请求为 `http://localhost:5173/api/system/status`，再由 Vite 代理到 `http://localhost:5043/api/system/status`；生产时同一路径由 ASP.NET Core 同源处理。最终网络请求必须只有一个 `/api` 前缀，不能产生 `/api/api`，也不能残留开发绝对地址。

### 7.3 API 错误适配

`api/errors/` 中，`ApiError.ts` 只定义公共类型，`toApiError.ts` 实现错误转换：

~~~typescript
export type ApiError = {
  kind: 'problemDetails' | 'network' | 'unknown';
  status?: number;
  code?: string;
  message: string;
  traceId?: string;
  fieldErrors?: Record<string, string[]>;
};
~~~

Kiota `1.34.1` 配套 runtime 的错误对象遵循 `ApiError`，提供 `responseStatusCode` 和 `responseHeaders`；当 operation 存在 OpenAPI error mapping 时，生成的 `ApiProblemDetails` 模型本身作为错误对象抛出，并附带上述响应元数据。公共适配层优先读取生成模型的 `code`、`traceId`、`errors`，再读取 `responseStatusCode`，不从错误文案反向解析 JSON。只有未注册错误映射、响应体无法按 schema 反序列化或 Fetch 本身失败时，才进入 unknown/network 分支。页面只能依赖 `kind`、`status`、`code`、`message` 和 `fieldErrors`，不解析任意 JSON，不以错误文案判断业务分支。

处理规则：

- 能解析标准 ProblemDetails 且含 `code`：`kind = 'problemDetails'`。
- Fetch 无响应：`kind = 'network'`，显示服务不可访问状态。
- 调用方取消或组件卸载导致的 `AbortError` 或 runtime 取消异常：不转换为 `network` 或 `unknown`，页面不显示失败状态。
- 有 HTTP 响应但未产生可用的类型化 ProblemDetails（例如 body 为空、Content-Type 非 JSON、JSON 无法按 schema 解析或模型缺少 `code`）：`kind = 'unknown'`，使用安全通用文案，并从 `responseStatusCode` 保留 HTTP 状态（如果存在）。
- `code = 'origin_not_allowed'`：保留 `kind = 'problemDetails'` 和 403，页面显示“当前请求来源不受允许，请刷新页面后重试”或等价安全文案，不展示 Origin 值。
- `code = 'unsupported_media_type'`：保留 `kind = 'problemDetails'` 和 415，页面显示“请求格式不受支持，请刷新页面后重试”或等价安全文案。
- 未知异常：`kind = 'unknown'`，显示安全通用文案。
- 保存 `traceId` 供页面显示或用户反馈，但不把内部路径、堆栈或响应原文展示出来。

## 8. systemStatus Feature

### 8.1 API 适配

`features/systemStatus/api/getSystemStatus.ts` 只负责：

1. 从公共 `getApiClient()` 获取生成 Client。
2. 调用 `GET /api/system/status` 对应的 Kiota operation。
3. 把 generated model 转换为 feature 的 `SystemStatusViewModel`。
4. 将 Kiota/网络异常转换为公共 `ApiError` 后抛给页面 Hook 或组件。

feature 不把 generated model 继续向页面传递，避免 OpenAPI 生成细节成为 UI 契约。

### 8.2 页面状态

`SystemStatusPage` 使用本地 React state 或 feature Hook 管理：

- `loading`：首次加载和手动重试。
- `available`：服务端和数据库可用。
- `degraded`：服务端可访问但数据库不可用。
- `error`：网络失败、服务端未启动或不可解析错误。

页面至少展示客户端已加载、服务端状态、数据库状态和环境名。加载状态使用固定布局的 loading indicator；错误状态提供重试按钮；数据库降级使用警告状态而不是网络错误状态。页面不显示连接字符串、绝对路径、堆栈或完整异常文本。

建议组件边界：

- `SystemStatusPage`：请求编排、页面整体布局和状态分支。
- `ServiceStatus`：展示服务端可访问及总体状态。
- `DatabaseStatus`：展示数据库 available/unavailable。
- `PageState`：只承载无业务语义的加载、错误和空状态视觉。

页面首次加载通过真实 API 触发，不提供默认的“成功”假数据。刷新按钮重新发起请求，不重复创建公共 Client。

## 9. 开发运行和 Vite Proxy

启动服务端：

~~~powershell
dotnet run --project Server/RoundTableMeeting/RoundTableMeeting.csproj --launch-profile http
~~~

另一个终端启动客户端：

~~~powershell
Set-Location Client/roundTableMeeting
pnpm install --frozen-lockfile
pnpm dev
~~~

浏览器打开 `http://localhost:5173/`。客户端请求 `/api/system/status`，Vite 代理到 `http://localhost:5043/api/system/status`。客户端代码不读取环境变量拼接后端绝对 URL；环境变量只允许控制脚本的 OpenAPI 地址和开发工具参数。

后续 Cookie 写请求仍由浏览器自动携带 Origin；Vite 代理透传页面 Origin `http://localhost:5173`，服务端允许列表必须配置页面源而不是代理目标 `http://localhost:5043`。客户端不得通过 JavaScript 手工设置或伪造 Origin Header。

服务端停止时，页面显示网络不可访问错误；服务端恢复后点击重试能够回到真实状态。不要以 Vite 能打开空白页面作为联调通过标准。

## 10. 生产构建和 ASP.NET Core 托管

### 10.1 单独构建客户端

~~~powershell
Set-Location Client/roundTableMeeting
pnpm install --frozen-lockfile
pnpm test
pnpm build
pnpm preview
~~~

`pnpm build` 必须先通过 `tsc -b`，再生成 `dist/`。构建结果不能包含源代码、测试文件、`node_modules`、OpenAPI 密钥或后端绝对地址。

### 10.2 随 dotnet publish 构建

Web 项目按[服务端实现方案](../Server/impl.md)中的 MSBuild Target 在计算发布文件前完成安装和构建，再把 `dist/` 纳入发布文件列表；具体 target 时序必须通过干净目录、已有旧 dist、重复 publish 和失败回滚探针后冻结：

~~~xml
<Exec WorkingDirectory="$(SpaRoot)" Command="pnpm run check:toolchain" />
<Exec WorkingDirectory="$(SpaRoot)" Command="pnpm install --frozen-lockfile" />
<Exec WorkingDirectory="$(SpaRoot)" Command="pnpm build" />
~~~

构建前必须检查 `node_modules/.bin/vite`、`pnpm-lock.yaml` 和 `dist/` 生成结果。前端安装或构建失败直接使 `dotnet publish` 失败；不设置 `ContinueOnError`，不在失败时复制旧的 dist。

发布时将 `dist/` 复制为 `wwwroot/`。ASP.NET Core 的静态文件和 SPA fallback 配置必须满足：

- `/`、`/system/status` 等页面导航返回 `index.html`。
- `/assets/*` 返回实际静态资源。
- `/api/*` 不进入 SPA fallback；未知 API 返回 JSON ProblemDetails 404。
- `/openapi/*`、`/scalar/*` 不进入 SPA fallback。
- 非 GET/HEAD 请求不返回 `index.html`。

发布验证使用 `dotnet publish` 生成目录直接启动服务端，不启动 Vite。生产配置使用 HTTPS；本地 Development profile 可以使用 HTTP。生产冒烟使用测试 HTTPS 证书和独立 HTTPS 端口，或在反向代理终止 TLS 的部署边界验证 HTTPS-only、转发协议头和重定向行为；不依赖开发证书信任。

## 11. 前端客户端完成标准

1. Node `24.20.0` 和 pnpm `11.24.0` 的精确约束与 `check:toolchain` 已写入项目，错误工具链会使 publish 失败，锁文件存在且可用 `--frozen-lockfile` 安装。
2. Vite、React、Ant Design、React Router、Kiota runtime、Vitest 和 Testing Library 依赖为精确版本。
3. 目录遵守 `app`、`features`、`shared`、`api/generated` 的边界；不存在模板演示内容。
4. `/` 和 `/system/status` 能渲染系统状态页面，页面请求真实 API。
5. 公共 Kiota Client 是模块级单例，feature 页面不直接依赖 generated 目录。
6. ProblemDetails、网络失败和未知响应能转换为统一 `ApiError`。
7. Vitest 使用 jsdom 和 React Testing Library，覆盖页面加载、成功、降级、失败和重试状态。
8. Vite `/api` 代理能够访问 HTTP 5043，客户端不拼接绝对 API 地址。
9. `pnpm build` 成功，`dotnet publish` 能自动构建并托管 dist。
10. 发布后的未知 `/api/*` 不会被 SPA fallback 返回 HTML，Router 子路径刷新可以正常打开。
