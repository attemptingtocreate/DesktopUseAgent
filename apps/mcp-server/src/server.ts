import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { AgentClient } from "./agent-client.js";
import { registerTools } from "./tools/registry.js";

export interface CreateServerOptions {
  client?: AgentClient;
  name?: string;
  version?: string;
}

/** Concise operator guidance surfaced to ChatGPT via MCP server instructions. */
export const SERVER_INSTRUCTIONS =
  "Inspect live desktop state first (desktop.get_state, desktop.describe, window.list). Prefer semantic ui.* and browser.* tools over raw input.* or vision.* fallbacks. Consequential actions—writes, clicks, launches, plan.execute, desktop.batch—may require DesktopUseAgent permission confirmation; honor user approvals and never bypass the agent gate.";

export function createServer(options: CreateServerOptions = {}): {
  server: McpServer;
  client: AgentClient;
} {
  const client = options.client ?? new AgentClient();
  const server = new McpServer(
    {
      name: options.name ?? "semantic-desktop",
      version: options.version ?? "0.1.0",
    },
    {
      instructions: SERVER_INSTRUCTIONS,
    },
  );
  registerTools(server, client);
  return { server, client };
}
