import { describe, expect, it, vi } from "vitest";
import { AgentClient } from "../src/agent-client.js";
import { createServer } from "../src/server.js";
import { listRegisteredToolNames, TOOL_NAMES } from "../src/tools/registry.js";

const EXPECTED_TOOLS = [
  "desktop.get_state",
  "desktop.get_capabilities",
  "desktop.describe",
  "desktop.get_graph",
  "desktop.batch",
  "desktop.diff",
  "window.list",
  "window.focus",
  "window.wait_for",
  "ui.get_tree",
  "ui.find",
  "ui.invoke",
  "ui.set_value",
  "ui.get_text",
  "ui.wait_for",
  "process.list",
  "process.launch",
  "filesystem.list",
  "filesystem.exists",
  "filesystem.read_text",
  "filesystem.write_text",
  "filesystem.stat",
  "filesystem.inspect",
  "plan.execute",
  "plan.cancel",
  "system.emergency_stop",
  "events.subscribe",
  "events.poll",
  "events.unsubscribe",
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
  "adapter.list",
  "adapter.capabilities",
  "adapter.execute",
  "blender.open",
  "blender.get_scene",
  "blender.get_objects",
  "blender.select_object",
  "blender.execute_python",
  "blender.export",
  "blender.save",
  "vscode.open_file",
  "vscode.open_folder",
  "vscode.execute_command",
  "vscode.get_workspace",
  "visualstudio.get_solution",
  "visualstudio.build",
  "visualstudio.open_file",
  "visualstudio.open_solution",
  "input.mouse_move",
  "input.mouse_click",
  "input.mouse_drag",
  "input.scroll",
  "input.key",
  "input.hotkey",
  "input.type",
  "vision.capture_screen",
  "vision.capture_window",
  "vision.capture_region",
] as const;

describe("tool registry", () => {
  it("lists the expected Phase 11 tool names", () => {
    expect(listRegisteredToolNames()).toEqual([...EXPECTED_TOOLS]);
    expect(TOOL_NAMES).toEqual([...EXPECTED_TOOLS]);
    expect(TOOL_NAMES).toContain("desktop.batch");
    expect(TOOL_NAMES).toContain("filesystem.inspect");
  });

  it("registers exactly those tools on McpServer", () => {
    const client = new AgentClient({ pipeName: "unused" });
    client.send = vi.fn(async () => ({ ok: true, data: {} }));

    const { server } = createServer({ client });
    const registered = Object.keys(
      (
        server as unknown as {
          _registeredTools: Record<string, unknown>;
        }
      )._registeredTools,
    ).sort();

    expect(registered).toEqual([...EXPECTED_TOOLS].sort());
  });
});
