import { describe, expect, it } from "vitest";
import { parseToolArgs, toolSchemas } from "../src/tools/schemas.js";

describe("tool schema validation", () => {
  it("rejects window.focus without windowId", () => {
    expect(() => parseToolArgs("window.focus", {})).toThrow();
    expect(() => parseToolArgs("window.focus", { windowId: "" })).toThrow();
  });

  it("rejects ui.set_value with missing value", () => {
    expect(() =>
      parseToolArgs("ui.set_value", { elementId: "el_1" }),
    ).toThrow();
  });

  it("rejects process.launch without executable", () => {
    expect(() => parseToolArgs("process.launch", { args: ["--help"] })).toThrow();
  });

  it("rejects filesystem.write_text without contents or value", () => {
    expect(() =>
      parseToolArgs("filesystem.write_text", { path: "C:\\tmp\\a.txt" }),
    ).toThrow();
  });

  it("rejects plan.cancel without planId or id", () => {
    expect(() => parseToolArgs("plan.cancel", {})).toThrow();
  });

  it("rejects plan.execute with empty steps", () => {
    expect(() => parseToolArgs("plan.execute", { steps: [] })).toThrow();
  });

  it("rejects unknown keys on strict schemas", () => {
    expect(() =>
      parseToolArgs("window.focus", { windowId: "win_1", extra: true }),
    ).toThrow();
  });

  it("accepts valid window.focus args", () => {
    const parsed = parseToolArgs("window.focus", { windowId: "win_1" });
    expect(parsed).toEqual({ windowId: "win_1" });
  });

  it("accepts empty object for desktop.get_state", () => {
    expect(parseToolArgs("desktop.get_state", {})).toEqual({});
  });

  it("exposes zod schemas for every registered tool", () => {
    for (const schema of Object.values(toolSchemas)) {
      expect(schema).toBeDefined();
      expect(typeof schema.parse).toBe("function");
    }
  });
});
