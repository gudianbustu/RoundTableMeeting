# 系统框架客户端设计

## 1. 文档定位

本文定义系统框架主题的客户端工程结构、目录边界、路由、API Client、错误适配、系统状态页面、测试和构建方式。跨端运行方式和总体完成标准以[系统框架设计](../design.md)为准。

## 2. 技术基线

- 使用 React、TypeScript 和 Vite。
- 使用 Ant Design 作为基础 UI 组件库。
- 使用 React Router 管理多页面路由。
- 使用 pnpm 管理依赖并提交 `pnpm-lock.yaml`。
- Node.js 项目基线使用 Node 24 LTS。
- 使用 Vitest、jsdom、`@testing-library/react`、`@testing-library/user-event` 和 `@testing-library/jest-dom` 进行单元测试和组件测试。
- 使用 Kiota 根据服务端 OpenAPI JSON 生成 TypeScript API Client。
- Kiota CLI 和生成代码依赖的运行时 npm 包使用精确版本，并与 Tool Manifest 中的 Kiota 版本配套。
- 不使用 Axios 编写手工 API Client。
- 框架阶段不引入 TanStack Query、Zustand 或其他额外全局状态管理库。

## 3. 项目结构

客户端项目固定放在 `Client/roundTableMeeting/`。项目目录和业务目录使用小驼峰，React 组件和页面文件使用帕斯卡命名。

```text
Client/
  roundTableMeeting/
    public/
    src/
      main.tsx
      app/
        App.tsx
        router/
        providers/
        layouts/
        theme/
      api/
        generated/
        client/
        errors/
      features/
        systemStatus/
          api/
          components/
          hooks/
          pages/
          types/
          index.ts
      shared/
        components/
        hooks/
        types/
        utils/
        styles/
      assets/
      test/
        setup.ts
    index.html
    package.json
    pnpm-lock.yaml
    vite.config.ts
    vitest.config.ts
    tsconfig.json
    tsconfig.app.json
```

以上是目录边界，不要求初始化时创建全部空目录。某个 feature 暂时不需要 `hooks/` 或 `types/` 时，不创建对应目录。

## 4. 总体组织规则

客户端采用“业务功能优先、功能内部技术分组”的组织方式：

- 页面、业务组件、业务 Hook、业务类型和业务 API 适配归属于具体 feature。
- 不在 `src/` 下建立统一的全局 `pages/`、`components/` 或 `hooks/` 业务目录。
- 通用且无具体业务语义的组件和工具放在 `shared/`。
- `app/` 负责组合应用，不承载具体业务组件。
- `api/generated/` 只保存 Kiota 生成代码。

客户端依赖方向保持为：

```text
app -> features -> api/shared
```

`shared` 不依赖具体 feature。feature 之间不直接穿透依赖彼此的内部目录。需要跨 feature 组合的流程由 `app`、路由或后续明确的上层业务 feature 负责。

## 5. app 目录

`app/` 是客户端组合根：

- `App.tsx`：应用根组件。
- `router/`：React Router 配置、路由对象和路由级错误边界。
- `providers/`：Ant Design、路由相关和后续全局 Context Provider。
- `layouts/`：依赖路由或应用级上下文的应用壳、认证布局和工作台布局，不放单一业务 feature 的页面组件。
- `theme/`：Ant Design Theme Token 和全局主题配置。

框架主题只提供最小应用外壳。账号主题开始后，可以在不改变 feature 边界的前提下扩展认证布局和工作台布局。

## 6. feature 目录

每个业务 feature 使用小驼峰目录名，例如：

```text
features/
  systemStatus/
  authentication/
  userManagement/
  meetingWorkspace/
  chat/
  agentCollaboration/
```

首版框架主题只创建实际使用的 `systemStatus`。后续目录由对应业务主题创建。

feature 内部使用文件夹约束职责：

- `api/`：该 feature 对公共 Kiota Client 的业务适配、请求参数组装和响应模型转换。
- `components/`：只服务于该 feature 的表单、列表、状态展示和交互组件。
- `hooks/`：只服务于该 feature 的 Hook 和状态组合逻辑。
- `pages/`：该 feature 对外暴露的路由页面。页面负责组合组件，不直接承载底层请求细节。
- `types/`：该 feature 的前端类型、视图模型和状态类型。
- `index.ts`：feature 的受控公共出口。

跨目录使用 feature 时优先通过 `index.ts`，不直接穿透到 feature 内部实现文件。feature 内部文件可以使用相对路径或 feature 内部约定，但不能形成循环依赖。

## 7. shared 目录

`shared/` 为通用客户端能力提供固定位置：

- `shared/components/`：不依赖路由、认证状态或应用壳上下文的通用 UI 组件，例如空状态、加载状态和确认对话框。
- `shared/hooks/`：通用浏览器或 React Hook。
- `shared/types/`：真正跨 feature 的基础类型。
- `shared/utils/`：通用纯函数、格式化和校验辅助。
- `shared/styles/`：全局样式、基础样式变量和 Ant Design 样式补充。

路由级和应用壳级布局固定放在 `app/layouts/`；单一 feature 的业务布局放在该 feature 的 `components/`；只有不依赖路由和应用上下文、确实被多个 feature 使用且不携带单一业务语义的组件才移动到 `shared/components/`。不能为了可能的复用提前抽取，也不能把业务组件放进 shared 后通过大量参数维护多个业务分支。

## 8. API 与 Kiota

### 8.1 生成代码

Kiota 生成代码固定放在：

```text
src/api/generated/
```

生成目录提交到仓库，但禁止手工修改。Kiota 版本通过仓库内 .NET Tool Manifest 固定；生成目录依赖的 Kiota runtime npm 包也必须使用精确版本并保持配套。重新生成时必须先启动 ASP.NET Core 服务端，统一脚本等待 OpenAPI HTTP 地址就绪，再通过 HTTP 获取当前 OpenAPI JSON 并运行 Kiota；仓库不额外提交一份需要人工同步的 OpenAPI JSON。服务未就绪或生成失败时脚本必须以非零结果退出。OpenAPI 变化后，需要通过重新生成产生可审查差异。

生成脚本在写入 `src/api/generated/` 后统一转换为 CRLF，遵守项目文件行尾约定；生成目录不得通过 `.gitattributes` 豁免为 LF。

### 8.2 公共 API 层

- `api/client/`：以模块级单例创建和配置 Kiota Client，处理基础地址、公共请求选项和后续认证接入。开发和生产统一使用相对基础地址 `/api`；开发由 Vite 代理，生产由同源 ASP.NET Core 提供。
- `api/errors/`：识别并转换 `ProblemDetails`、网络错误和无法解析的服务端错误。

页面和组件不能直接依赖 `api/generated/`。每个 feature 通过自己的 `api/` 调用公共 Client，并将生成模型转换为适合该 feature 的视图模型或状态。公共 Client 不由页面或组件各自 new；后续认证通过该公共层的 RequestAdapter 配置接入。

### 8.3 错误模型

服务端 `ProblemDetails` 的跨端字段固定为标准字段 `type`、`title`、`status`、`detail`、`instance`，以及扩展字段 `code`、`traceId`；字段校验错误额外包含 `errors: Record<string, string[]>`。客户端公共错误对象至少能够表达：

- HTTP 状态。
- 稳定业务错误标识。
- 用户可理解的错误消息。
- 字段级校验信息。
- 网络不可用或服务端不可访问。

公共错误对象的准确 TypeScript 类型在实施设计中固定，并优先由 OpenAPI/Kiota 生成的 ProblemDetails 模型承载传输字段。页面不解析任意服务端 JSON，也不直接依赖 `ProblemDetails` 的传输细节；页面分支判断使用稳定的 `code` 或 HTTP 状态，不使用本地化错误文案。

## 9. 路由与页面

React Router 配置放在 `app/router/`。路由项引用 feature 通过公共出口暴露的页面。

框架主题至少提供系统状态页面路由。后续账号主题可以增加登录、账号设置和用户管理路由，会议主题再增加工作台路由。

路由必须具备：

- 未匹配路径的明确页面或回退行为。
- 路由级加载状态。
- 路由渲染异常的错误边界。
- 生产环境刷新子路径时由 ASP.NET Core SPA 回退正确返回应用。

## 10. 系统状态 feature

`features/systemStatus/` 是框架主题的首个可见 feature，至少包含：

- 调用系统状态 API 的适配函数。
- 请求中、成功和失败状态。
- 展示客户端、服务端、数据库和运行环境状态的组件。
- `SystemStatusPage` 路由页面。

页面使用真实服务端数据，不包含模拟账号、模拟会议或其他业务占位数据。服务端不可访问时必须停止加载并显示明确错误；服务端返回 `degraded` 或数据库 `unavailable` 时，页面展示降级状态而不是把它当作网络失败。

系统状态页面是临时框架验收入口。后续账号主题可以替换默认首页，但删除或替换前需要保留等价的框架集成验证路径。

## 11. 状态管理

框架阶段使用 React 自带状态、Context 和自定义 Hook。

- 页面本地状态保留在页面或 feature Hook。
- 跨应用但稳定的状态通过 `app/providers/` 中的 Context 提供。
- 不为了系统状态页面提前引入全局状态库。
- 后续出现明确的复杂跨页面状态需求时，再由最早需要它的业务主题评估 TanStack Query、Zustand 或其他方案。

## 12. 样式与组件

- 基础 UI 优先使用 Ant Design。
- 全局主题放在 `app/theme/`。
- 通用样式放在 `shared/styles/`。
- feature 特有样式与对应组件或页面 colocate。
- 静态资源放在 `assets/`；需要原样复制且不经 Vite 处理的资源放在 `public/`。

通用组件应保持无业务语义。业务组件即使视觉上可复用，也应先保留在 feature 内，确认跨 feature 的稳定共性后再抽取。

## 13. 命名与引用

- 客户端项目目录和普通目录使用小驼峰。
- React 组件和页面文件使用帕斯卡命名，例如 `SystemStatusPage.tsx`。
- Hook 使用 `useXxx` 命名。
- 测试文件使用 `.test.ts` 或 `.test.tsx`。
- 统一配置 `@/` 指向 `src/`，避免多层 `../../../`。
- 跨 feature 引用通过 feature 公共出口，不通过路径穿透内部文件。

## 14. 测试

客户端测试固定使用 Vitest、jsdom、`@testing-library/react`、`@testing-library/user-event` 和 `@testing-library/jest-dom`。其中 jsdom 提供 DOM 环境，React Testing Library 负责按用户视角渲染和查询，user-event 负责交互，jest-dom 提供 DOM 断言。

`src/test/setup.ts` 负责加载 jest-dom，并将 Vitest 组件测试环境固定为 jsdom；只补充组件真实需要但 jsdom 缺失的浏览器 API，例如 `matchMedia` 和 `ResizeObserver`。公共测试渲染工具负责包装 `MemoryRouter`、Ant Design `ConfigProvider` 和测试目标必需的应用 Provider；不整体模拟 Ant Design，也不把所有测试强制包入无关 Provider。

测试至少验证：

- 系统状态页面的请求中、成功和失败状态。
- `ProblemDetails` 和网络错误适配。
- 基础路由和未匹配路由。
- 公共 API Client 配置。
- 通用组件的独立行为。

测试文件默认与被测文件 colocate，例如：

```text
features/systemStatus/pages/
  SystemStatusPage.tsx
  SystemStatusPage.test.tsx
```

`src/test/` 只保存 Vitest 全局初始化和真正跨 feature 的测试工具。客户端生产构建必须纳入自动验证。

## 15. 开发与生产构建

### 15.1 开发环境

- Vite 独立启动。需要重新生成 Kiota Client 时，同时启动 ASP.NET Core，并由生成脚本通过 HTTP 获取 OpenAPI JSON。
- `/api` 代理到 ASP.NET Core；客户端公共 Client 的基础地址固定为相对 `/api`，不在组件中拼接环境专属绝对地址。
- API 基础地址由公共 Client 配置统一处理。
- 本地开发不要求 ASP.NET Core 托管静态文件。

### 15.2 生产环境

- Vite 输出生产静态文件。
- ASP.NET Core 托管输出结果。
- 页面和 API 使用同一源站。
- React Router 子路径刷新依赖服务端 SPA 回退。
- `dotnet publish` 自动包含客户端构建结果；发布环境提供 Node 24 LTS 和 pnpm，使用 `pnpm install --frozen-lockfile`，依赖安装或构建失败必须使发布失败。生产环境必须使用 HTTPS，localhost 开发环境允许 HTTP。

## 16. 浏览器冒烟调试

使用独立 Profile 的开发模式 Chrome，通过仅绑定 `127.0.0.1` 的 CDP endpoint 调试。验收时需要确认：

1. CDP endpoint 可访问。
2. 目标页面出现在页面列表中。
3. 调试客户端已连接正确页面。
4. 能够读取 DOM、控制台和网络结果。
5. 能够生成并检查页面截图。

调试实例不读取日常浏览器中与项目无关的登录信息、Cookie 或历史记录。

## 17. 待在实施文档中固定

- Vite、React、Ant Design、React Router、Vitest、jsdom 和 Testing Library 相关包的准确版本。
- Kiota runtime npm 包的精确版本及其与 Tool Manifest 中 Kiota CLI 的配套关系。
- `package.json` 脚本名称。
- `@/` 路径别名的 TypeScript、Vite 和 Vitest 配置。
- Kiota 生成命令、服务端 OpenAPI HTTP 地址、服务就绪等待方式和运行时 npm 依赖。
- 公共 Client 的模块级单例、相对 `/api` 基础地址和 RequestAdapter 配置方式。
- 公共 API Client 与已确定字段契约对应的客户端错误对象准确 TypeScript 类型。
- 系统状态页面的组件拆分和视觉设计。
- Vite 代理端口和 ASP.NET Core 开发地址。
- 客户端构建产物与 `dotnet publish` 的集成方式。
