import type { ToolName } from "./schemas.js";

export function toMcpToolName(agentMethod: ToolName): string {
  return agentMethod.replace(".", "_");
}

export function toAgentMethod(mcpName: string): ToolName {
  const i = mcpName.indexOf("_");
  if (i < 0) return mcpName as ToolName;
  return `${mcpName.slice(0, i)}.${mcpName.slice(i + 1)}` as ToolName;
}
