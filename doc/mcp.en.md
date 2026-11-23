# MCP

[简体中文](./mcp.md) | [繁體中文](./mcp.zh-TW.md) | **English**

Return to the [documentation index](./README.en.md).

Connect MCP servers over stdio and make their tools available to the main agent. The feature is off by default; enabling it starts child processes.

## Setup

Open the **MCP** section in Settings:

1. Enable the master switch.
2. Add a server with its name, launch command and arguments (one argument per line). A working directory and maximum wait can be supplied as well; the default is 30 seconds. The name and command are required.
   The maximum wait applies to tool calls, not process startup. A cold `dotnet` process taking 300 ms or more is not treated as a failure; the handshake has its own 30-second allowance.
   A command written only as a name is resolved from the system PATH. Use an absolute path for anything else.
3. Save. When the consumer starts, it launches each server and registers the results of `tools/list` in its tool registry.

## Tool names

Every tool receives the prefix `mcp_{server}_`. Equal tool names from two servers therefore remain distinct.

## Approval

- MCP tools use their own allowlist rather than the approval level in the input area.
- Calls ask for approval by default. A server’s tools run directly only after “Skip approval” is enabled for that server.
- Enabling a server determines which tools are available; it does not make them automatically trusted.

## Effects of settings changes

| Change | Effect |
|---|---|
| Server list | Restarts the consumer and launches the child processes again |
| Skip-approval list | Does not restart anything; the next call sees the change |
| Other settings that rebuild the consumer | Existing connections and tool descriptions are reused when the MCP configuration is unchanged; disabling MCP, changing a server or stopping the consumer closes the connections |

## Failures

One server failing to start does not stop the others. Its reason is recorded in consumer status.

MCP tools have a per-turn budget of 32 steps; ordinary tools have a budget of 8.
