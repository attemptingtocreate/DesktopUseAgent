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
});
