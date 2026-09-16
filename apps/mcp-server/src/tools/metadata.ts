import type { ToolAnnotations } from "@modelcontextprotocol/sdk/types.js";
import { TOOL_NAMES, type ToolName } from "./schemas.js";

export interface ToolMetadataEntry {
  title: string;
  annotations: Required<Pick<ToolAnnotations, "readOnlyHint" | "destructiveHint" | "openWorldHint">>;
}

const DOMAIN_LABELS: Record<string, string> = {
  desktop: "Desktop",
  monitor: "Monitor",
  window: "Window",
  ui: "UI",
  process: "Process",
  filesystem: "Filesystem",
  plan: "Plan",
  system: "System",
  events: "Events",
  browser: "Browser",
  adapter: "Adapter",
  blender: "Blender",
  vscode: "VS Code",
  visualstudio: "Visual Studio",
  roblox: "Roblox Studio",
  input: "Input",
  vision: "Vision",
};

function toTitle(name: ToolName): string {
  const dot = name.indexOf(".");
  const domain = name.slice(0, dot);
  const action = name.slice(dot + 1);
  const domainLabel = DOMAIN_LABELS[domain] ?? domain;
  const actionLabel = action
    .split("_")
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
  return `${actionLabel} (${domainLabel})`;
}

/** Inspection, list, get, wait, and capture tools that do not mutate external state. */
const READ_ONLY_TOOLS = new Set<ToolName>([
  "desktop.get_state",
  "desktop.get_capabilities",
  "desktop.describe",
  "desktop.get_graph",
  "desktop.diff",
  "monitor.list",
  "window.list",
  "window.get",
  "window.wait_for",
  "ui.get_tree",
  "ui.find",
  "ui.get_text",
  "ui.wait_for",
  "process.list",
  "filesystem.list",
  "filesystem.exists",
  "filesystem.read_text",
  "filesystem.stat",
  "filesystem.inspect",
  "events.poll",
  "browser.list",
  "browser.tabs",
  "browser.get_tab",
  "browser.query",
  "browser.query_all",
  "browser.get_text",
  "browser.get_dom",
  "browser.get_accessibility_tree",
  "browser.wait_for",
  "browser.wait_for_navigation",
  "browser.wait_for_network_idle",
  "browser.get_downloads",
  "adapter.list",
  "adapter.capabilities",
  "blender.get_scene",
  "blender.get_objects",
  "vscode.get_workspace",
  "visualstudio.get_solution",
  "roblox.plugin_ping",
  "roblox.get_hierarchy",
  "roblox.get_selection",
  "vision.capture_screen",
  "vision.capture_window",
  "vision.capture_region",
  "system.performance",
]);

/** Irreversible writes, arbitrary dispatch/execution, or actions with likely data loss. */
const DESTRUCTIVE_TOOLS = new Set<ToolName>([
  "desktop.batch",
  "ui.invoke",
  "ui.set_value",
  "process.launch",
  "filesystem.write_text",
  "plan.execute",
  "browser.close_tab",
  "browser.click",
  "adapter.execute",
  "blender.execute_python",
  "blender.export",
  "blender.save",
  "blender.batch",
  "blender.render",
  "blender.import_mesh",
  "vscode.execute_command",
  "visualstudio.build",
  "input.mouse_click",
  "input.mouse_drag",
  "input.key",
  "input.hotkey",
  "input.type",
]);

/** Tools that reach arbitrary processes, sites, or open-ended execution surfaces. */
const OPEN_WORLD_TOOLS = new Set<ToolName>([
  "desktop.batch",
  "plan.execute",
  "process.launch",
  "browser.list",
  "browser.tabs",
  "browser.get_tab",
  "browser.open_tab",
  "browser.close_tab",
  "browser.navigate",
  "browser.back",
  "browser.forward",
  "browser.reload",
  "browser.query",
  "browser.query_all",
  "browser.click",
  "browser.fill",
  "browser.select",
  "browser.focus",
  "browser.get_text",
  "browser.get_dom",
  "browser.get_accessibility_tree",
  "browser.wait_for",
  "browser.wait_for_navigation",
  "browser.wait_for_network_idle",
  "browser.get_downloads",
  "adapter.execute",
  "blender.execute_python",
  "vscode.execute_command",
  "visualstudio.build",
]);

function buildAnnotations(name: ToolName): ToolMetadataEntry["annotations"] {
  return {
    readOnlyHint: READ_ONLY_TOOLS.has(name),
    destructiveHint: DESTRUCTIVE_TOOLS.has(name),
    openWorldHint: OPEN_WORLD_TOOLS.has(name),
  };
}

export const TOOL_METADATA = Object.fromEntries(
  TOOL_NAMES.map((name) => [
    name,
    {
      title: toTitle(name),
      annotations: buildAnnotations(name),
    },
  ]),
) as Record<ToolName, ToolMetadataEntry>;

export function getToolMetadata(name: ToolName): ToolMetadataEntry {
  return TOOL_METADATA[name];
}
