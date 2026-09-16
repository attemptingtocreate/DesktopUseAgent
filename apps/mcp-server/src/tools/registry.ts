import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { AgentClient } from "../agent-client.js";
import { getToolMetadata } from "./metadata.js";
import { TOOL_DESCRIPTIONS, TOOL_NAMES, toolSchemas, type ToolName } from "./schemas.js";
import { toMcpToolName } from "./tool-names.js";

export { TOOL_NAMES, type ToolName };

export function listRegisteredToolNames(): readonly ToolName[] {
  return TOOL_NAMES;
}

/** Underscored MCP names plus dotted agent-method aliases (when distinct). */
export function listRegisteredMcpToolNames(): readonly string[] {
  const names: string[] = [];
  for (const name of TOOL_NAMES) {
    const mcpName = toMcpToolName(name);
    names.push(mcpName);
    if (mcpName !== name) {
      names.push(name);
    }
  }
  return names;
}

function asParams(value: unknown): Record<string, unknown> {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

export function registerTools(server: McpServer, client: AgentClient): void {
  const registered = new Set<string>();

  for (const name of TOOL_NAMES) {
    const schema = toolSchemas[name];
    const { title, annotations } = getToolMetadata(name);
    const mcpName = toMcpToolName(name);
    const aliases = mcpName === name ? [mcpName] : [mcpName, name];

    const handler = async (args: unknown) => {
      const parsed = schema.parse(args ?? {});
      const result = await client.send(name, asParams(parsed));
      return {
        content: [
          {
            type: "text" as const,
            text: JSON.stringify(result),
          },
        ],
      };
    };

    for (const alias of aliases) {
      if (registered.has(alias)) continue;
      registered.add(alias);
      server.registerTool(
        alias,
        {
          title,
          description: TOOL_DESCRIPTIONS[name],
          inputSchema: schema,
          annotations,
        },
        handler,
      );
    }
  }
}
