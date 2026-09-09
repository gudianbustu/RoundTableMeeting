# MCP Transport Spike Tests

Independent MCP transport research tests for the resident lab Demo. These tests are not the formal Theme 5 implementation.

Run from the repository root:

    dotnet test "Demo/驻留参与者协作实验.Tests/AIC.ResidentLab.Transport.Tests.csproj" --logger "console;verbosity=detailed"

The tests create a real Kestrel WebApplication on a random local port and clean it up after each test. No Demo database, meeting code, cookie, full request, or full response is used as test output.

The default Stateful host uses AddMcpServer().WithHttpTransport() and MapMcp("/mcp"). It covers initialize, Mcp-Session-Id, same-session requests, second-session isolation, tool McpSession.SessionId, and tool cancellation.

The separate RunSessionHandler host uses another WebApplication and random port. Its callback calls McpServer.RunAsync as required by the SDK and records a redacted session hash and host-stop fact. It never shares an endpoint or session manager with the default host.

The current ModelContextProtocol.AspNetCore 1.0.0 result is that RunSessionHandler preserves stateful continuity when its callback calls McpServer.RunAsync and remains active. Protocol DELETE and host shutdown each produce one callback with a redacted hash matching the initialized protocol session. A real client HTTP request abort cancels the client operation, does not produce a protocol session-end callback, and leaves the same MCP session usable. The spike does not assume that this request abort must propagate to the server tool's cancellation token.

DELETE /mcp, client request abort, and host shutdown are tested separately. Host shutdown is recorded as a host lifecycle fact and its callback is also checked for one-time SessionId correlation. The client-abort test uses a request CancellationToken only to model the caller abandoning one operation; it explicitly verifies that this does not substitute for protocol session closure and that the session remains usable. Server-side tool cancellation is covered separately by notifications/cancelled.

The protocol-cancel test separately proves tool-level CancellationToken cancellation without closing the Stateful session. These tests establish the transport facts needed before formal Theme 5 implementation; they do not prove the Theme 5 Agent tools, database, message consumer, or client implementation, which remain out of scope for this test-only change.
