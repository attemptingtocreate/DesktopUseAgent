import { describe, expect, it } from "vitest";
import { TOOL_NAMES } from "../src/tools/registry.js";
import { toAgentMethod, toMcpToolName } from "../src/tools/tool-names.js";

describe("tool-names", () => {
  it("round-trips all TOOL_NAMES", () => {
    for (const name of TOOL_NAMES) {
      const mcp = toMcpToolName(name);
      expect(mcp).not.toContain(".");
      expect(toAgentMethod(mcp)).toBe(name);
    }
  });

  it("maps representative underscored and dotted pairs", () => {
    expect(toMcpToolName("desktop.get_state")).toBe("desktop_get_state");
    expect(toAgentMethod("desktop_get_state")).toBe("desktop.get_state");
    expect(toMcpToolName("window.list")).toBe("window_list");
    expect(toAgentMethod("window_list")).toBe("window.list");
  });
});
