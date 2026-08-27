# 驻留参与者协作实验

## 实验目的

本实验用于验证 AICollaboration 的驻留参与者协作模式：让 Agent 通过 MCP 加入一个网页协作会话，在后台持续等待用户提及；收到消息后处理任务并将回复提交回网页会话。

## 实现逻辑

1. ASP.NET Core 应用提供网页界面、HTTP API 和 MCP 接入点。
2. 应用使用单例 `ResidentLabState` 保存当前会话、入会码、参与者和消息记录。
3. Agent 通过 MCP 工具读取会话信息并使用入会码加入会话。
4. Agent 加入后调用 `wait_for_turn` 阻塞等待网页用户发送消息。
5. 用户发送包含 `@CodexResident` 的消息时，应用将消息写入参与者队列。
6. `wait_for_turn` 返回消息后，Agent 处理消息并通过 `complete_turn` 将回复写入网页会话。
7. 回复提交完成后，Agent 再次调用 `wait_for_turn`，继续等待下一条消息。
8. 网页端通过 `/api/lab` 定时刷新会话状态和消息列表；“重置实验”会生成新的会话和入会码。

MCP 工具如下：

- `get_lab_session`：读取当前会话、入会码、参与者和消息。
- `join_session`：使用入会码加入会话。
- `wait_for_turn`：等待用户提及当前参与者。
- `complete_turn`：提交参与者回复。

## 运行方式

1. 使用 Visual Studio 打开 `AIC.ResidentLab.slnx`。
2. 启动项目并访问实验网页。
3. MCP 服务地址使用 `http://127.0.0.1:5198/mcp`。如果 Visual Studio 使用其他端口，请将项目启动地址调整为 `http://127.0.0.1:5198`，或同步修改 MCP 配置中的端口。

也可以在项目目录执行：

```powershell
dotnet run --urls http://127.0.0.1:5198
```

## 接入 MCP

将以下配置添加到 MCP 客户端配置中：

```json
{
  "mcpServers": {
    "aicollaboration": {
      "url": "http://127.0.0.1:5198/mcp"
    }
  }
}
```

## Agent 加入提示词

将以下提示词发送给 Agent 工具：

```text
请作为 AICollaboration 的驻留参与者加入协作会话。

请按以下步骤执行：

1. 调用 get_lab_session，读取当前实验会话和入会码。
2. 使用入会码 LAB-XXXX-XXXX 调用 join_session。
3. agentName 使用 CodexResident。
4. 入会成功后，使用返回的 participantId 调用 wait_for_turn。
5. wait_for_turn 收到用户消息后，正常处理消息。
6. 使用 complete_turn 提交你的回复。
7. 提交完成后再次调用 wait_for_turn，继续驻留等待。
8. 不要主动结束这个会话。

当用户在网页中发送 @CodexResident 开头的消息时，请处理该消息并回复。
```

## 使用流程

1. 启动实验项目并打开网页，记录页面显示的入会码。
2. 在已接入 MCP 的 Agent 中发送上方提示词。
3. Agent 使用页面显示的入会码加入会话，并保持等待状态。
4. 在网页输入以 `@CodexResident` 开头的消息并发送。
5. 等待 Agent 处理并将回复显示在消息列表中。
6. 需要重新开始时点击“重置实验”，再让 Agent 使用新的入会码加入。
