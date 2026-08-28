# RoundTableMeeting 项目
# 项目目录结构

```text
RoundTableMeeting\
├─ docs\
│  ├─ plans\
│  │  ├─ 001-总体计划\
│  │  │  └─ design.md
│  │  └─ NNN-业务主题\
│  │     ├─ design.md
│  │     ├─ impl.md
│  │     ├─ testPlan.md
│  │     ├─ Client\
│  │     │  ├─ design.md
│  │     │  ├─ impl.md
│  │     │  ├─ Pages\
│  │     │  │  └─ <页面>\
│  │     │  │     ├─ design.md
│  │     │  │     └─ impl.md
│  │     │  └─ Components\
│  │     │     └─ <组件>\
│  │     │        ├─ design.md
│  │     │        └─ impl.md
│  │     └─ Server\
│  │        ├─ design.md
│  │        └─ impl.md
│  ├─ 需求分析\
│  │  ├─ <业务主题>.md
│  │  └─ <流程图>.mmd
│  └─ uml\
│     ├─ <模型或业务主题>.puml
│     ├─ <模型或业务主题>.wsd
│     └─ <补充图示>.mmd
├─ Server\
│  └─ <后端项目或服务>\
├─ Client\
│  └─ <客户端项目>\
└─ Demo\
   ├─ <技术主题或预研方向>\
   │  └─ <示例项目>\
   └─ <独立示例项目>\
```

- `docs` 存放项目文档，包含 `plans`、`需求分析`、`uml`；`Server` 存放后端项目，`Client` 存放 UI 客户端项目，`Demo` 存放示例和技术预研项目。
- 客户端项目直接位于 `Client/<客户端项目>`；每个 Demo 独立管理自己的目录结构和文件。
- 先在 `docs/需求分析/` 编写并确认需求，再编写 `docs/plans/001-总体计划/design.md`，最后由总体计划拆分业务主题；总体计划只保存总体设计和主题拆分依据。
- 计划目录命名为 `NNN-业务主题`，`NNN` 仅用于排序；每个业务主题包含 `design.md`、`impl.md`、`testPlan.md`，并按范围拆分 `Client`、`Server`、`Pages`、`Components` 等文档目录。
- `docs/plans/` 下的所有子目录只存放设计、实现和测试文档，不存放源代码。
- 仅当需求引入独立业务主题且用户明确要求时，才新建 `NNN-业务主题` 目录。

# 文档维护策略

- `docs` 中的文档只保留当前有效状态，不保留历史版本、变更记录、实施日志或失效内容。
- 修改文档前先检索 `docs/`，直接更新最贴合的已有文件；既有主题范围内的增量继续维护该主题，归属不明确时先询问用户。
- 需求流程图和 UML 图使用可编辑源文件，分别存放于 `docs/需求分析/` 和 `docs/uml/`；渲染图片不能替代源文件。
- 除非用户明确要求，不创建 changelog、会议纪要、迁移记录、执行日志或临时设计；完成后说明更新的文件及新建文件的必要性和授权。

# 工作树

- 未指定位置时，手工创建的 Git 工作树统一放在仓库根目录 `.worktrees/<工作树名称>`，并确保 `.worktrees/` 被忽略。
- 用户指定位置、名称、分支或创建方式时以用户要求为准；此约定不影响 Codex App 自动创建工作树的宿主路径。

# 注意事项

- **新增或修改文件时应保持 CRLF 行尾；若工具产生 LF 行尾，提交前需转换为 CRLF 并检查确认**
- **不要主动提交代码**：执行 `git commit` 前必须询问用户确认
- **提交说明必须使用中文**：标题使用“`类型: 中文描述`”，类型可用 `feat`、`fix`、`refactor`、`docs`，例如 `feat: 添加数据导入功能`
- **禁止自己创建分支**：创建分支的动作需要获得用户的确定

<!-- gitnexus:start -->
# GitNexus — Code Intelligence

This project is indexed by GitNexus as **RoundTableMeeting** (224 symbols, 293 relationships, 8 execution flows). Use the GitNexus MCP tools to understand code, assess impact, and navigate safely.

> Index stale? Run `node .gitnexus/run.cjs analyze` from the project root — it auto-selects an available runner. No `.gitnexus/run.cjs` yet? `npx gitnexus analyze` (npm 11 crash → `npm i -g gitnexus`; #1939).

## Always Do

- **MUST run impact analysis before editing any symbol.** Before modifying a function, class, or method, run `impact({target: "symbolName", direction: "upstream"})` and report the blast radius (direct callers, affected processes, risk level) to the user.
- **MUST run `detect_changes()` before committing** to verify your changes only affect expected symbols and execution flows. For regression review, compare against the default branch: `detect_changes({scope: "compare", base_ref: "main"})`.
- **MUST warn the user** if impact analysis returns HIGH or CRITICAL risk before proceeding with edits.
- When exploring unfamiliar code, use `query({search_query: "concept"})` to find execution flows instead of grepping. It returns process-grouped results ranked by relevance.
- When you need full context on a specific symbol — callers, callees, which execution flows it participates in — use `context({name: "symbolName"})`.
- For security review, `explain({target: "fileOrSymbol"})` lists taint findings (source→sink flows; needs `analyze --pdg`).

## Never Do

- NEVER edit a function, class, or method without first running `impact` on it.
- NEVER ignore HIGH or CRITICAL risk warnings from impact analysis.
- NEVER rename symbols with find-and-replace — use `rename` which understands the call graph.
- NEVER commit changes without running `detect_changes()` to check affected scope.

## Resources

| Resource | Use for |
|----------|---------|
| `gitnexus://repo/RoundTableMeeting/context` | Codebase overview, check index freshness |
| `gitnexus://repo/RoundTableMeeting/clusters` | All functional areas |
| `gitnexus://repo/RoundTableMeeting/processes` | All execution flows |
| `gitnexus://repo/RoundTableMeeting/process/{name}` | Step-by-step execution trace |

## CLI

| Task | Read this skill file |
|------|---------------------|
| Understand architecture / "How does X work?" | `.claude/skills/gitnexus/gitnexus-exploring/SKILL.md` |
| Blast radius / "What breaks if I change X?" | `.claude/skills/gitnexus/gitnexus-impact-analysis/SKILL.md` |
| Trace bugs / "Why is X failing?" | `.claude/skills/gitnexus/gitnexus-debugging/SKILL.md` |
| Rename / extract / split / refactor | `.claude/skills/gitnexus/gitnexus-refactoring/SKILL.md` |
| Tools, resources, schema reference | `.claude/skills/gitnexus/gitnexus-guide/SKILL.md` |
| Index, status, clean, wiki CLI commands | `.claude/skills/gitnexus/gitnexus-cli/SKILL.md` |
| Work in the 驻留参与者协作实验 area (16 symbols) | `.claude/skills/generated/skill/SKILL.md` |
| Work in the Wwwroot area (4 symbols) | `.claude/skills/generated/wwwroot/SKILL.md` |

<!-- gitnexus:end -->
