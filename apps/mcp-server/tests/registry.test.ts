import { describe, expect, it, vi } from "vitest";
import { AgentClient } from "../src/agent-client.js";
import { createServer } from "../src/server.js";
import { listRegisteredToolNames, TOOL_NAMES } from "../src/tools/registry.js";

const EXPECTED_TOOLS = [
  "desktop.get_state",
  "desktop.get_capabilities",
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
  "plan.execute",
  "plan.cancel",
  "system.emergency_stop",
] as const;

describe("tool registry", () => {
  it("lists the expected Phase 4 tool names", () => {
    expect(listRegisteredToolNames()).toEqual([...EXPECTED_TOOLS]);
    expect(TOOL_NAMES).toEqual([...EXPECTED_TOOLS]);
    expect(TOOL_NAMES).not.toContain("browser.tabs");
    expect(TOOL_NAMES).not.toContain("browser.navigate");
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
