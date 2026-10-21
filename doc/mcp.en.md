# MCP

[简体中文](./mcp.md) | [繁體中文](./mcp.zh-TW.md) | **English**

Back to the [documentation index](./README.en.md).

Connects stdio MCP servers and hands their tools to the main agent. Off by default: enabling it starts child processes.

## Setup

The **MCP** page in the sidebar:

1. Turn on the master switch.
2. Add a server: name, command, arguments (one per line), optionally a working directory and a call timeout (default 30 seconds). Name and command are required.
   A bare command name is only looked up on the system PATH; otherwise give the full path.
3. Save. The consumer starts each server on launch and registers the result of `tools/list` in its tool table.

## Tool names

Tool names get the prefix `mcp_{server}_`, so two servers with the same tool name do not overwrite each other.

## Approval

- MCP tools are not governed by the approval level in the input area; they have their own allowlist.
- Every call asks by default. Turn on "Skip approval" for a server and its tools run directly.
- Enabling a server decides which tools exist, not which ones may run without asking.

## What a settings change restarts

| Change | Effect |
|---|---|
| Server list | Restarts the consumer; child processes start again |
| Skip-approval list | No restart; applies on the next call |

## When something fails

A server that fails to start does not affect the others; the reason is recorded in the consumer status.

In a single turn, MCP tools get a step budget of 32; ordinary tools get 8.
