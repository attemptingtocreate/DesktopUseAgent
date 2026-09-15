import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { AgentClient } from "../agent-client.js";
import { getToolMetadata } from "./metadata.js";
import { TOOL_DESCRIPTIONS, TOOL_NAMES, toolSchemas, type ToolName } from "./schemas.js";

export { TOOL_NAMES, type ToolName };

export function listRegisteredToolNames(): readonly ToolName[] {
  return TOOL_NAMES;
}

function asParams(value: unknown): Record<string, unknown> {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}

export function registerTools(server: McpServer, client: AgentClient): void {
  for (const name of TOOL_NAMES) {
    const schema = toolSchemas[name];
    const { title, annotations } = getToolMetadata(name);
    server.registerTool(
      name,
      {
        title,
        description: TOOL_DESCRIPTIONS[name],
        inputSchema: schema,
        annotations,
      },
      async (args: unknown) => {
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
      },
    );
  }
}
