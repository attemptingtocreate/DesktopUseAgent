import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { AgentClient } from "./agent-client.js";
import { registerTools } from "./tools/registry.js";

export interface CreateServerOptions {
  client?: AgentClient;
  name?: string;
  version?: string;
}

export function createServer(options: CreateServerOptions = {}): {
  server: McpServer;
  client: AgentClient;
} {
  const client = options.client ?? new AgentClient();
  const server = new McpServer({
    name: options.name ?? "semantic-desktop",
    version: options.version ?? "0.1.0",
  });
  registerTools(server, client);
  return { server, client };
}
