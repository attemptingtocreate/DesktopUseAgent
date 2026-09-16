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
  "MCP tool names use underscores (e.g. browser_open_tab); agent pipe methods use dots (browser.open_tab). Speed first: for simple open URL / open tab requests, call process_launch (browser exe + URL args) or browser_open_tab immediately in one shot — do NOT call desktop_get_capabilities, browser_list, or exploratory window scans first. Prefer process_launch when you only need a visible browser window fast; use browser_open_tab when you need CDP control afterward. For other tasks, inspect with desktop_get_state / desktop_describe / window_list as needed. Prefer semantic ui_* and browser_* tools over raw input_* or vision_* fallbacks. Consequential actions—writes, clicks, launches, plan_execute, desktop_batch—may require DesktopUseAgent permission confirmation; honor user approvals and never bypass the agent gate.";

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
