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

  it("rejects browser.navigate without url", () => {
    expect(() => parseToolArgs("browser.navigate", {})).toThrow();
    expect(() => parseToolArgs("browser.navigate", { url: "" })).toThrow();
  });

  it("rejects browser.get_tab without tabId", () => {
    expect(() => parseToolArgs("browser.get_tab", {})).toThrow();
  });

  it("rejects browser.fill without value", () => {
    expect(() =>
      parseToolArgs("browser.fill", { selector: { css: "#name" } }),
    ).toThrow();
  });

  it("rejects browser.select without value", () => {
    expect(() =>
      parseToolArgs("browser.select", { tabId: "tab_1" }),
    ).toThrow();
  });

  it("rejects unknown keys on browser.navigate", () => {
    expect(() =>
      parseToolArgs("browser.navigate", {
        url: "https://example.com",
        extra: true,
      }),
    ).toThrow();
  });

  it("rejects unknown keys on browser selector", () => {
    expect(() =>
      parseToolArgs("browser.query", {
        selector: { css: "button", xpath: "//button" },
      }),
    ).toThrow();
  });

  it("accepts valid browser.navigate args", () => {
    const parsed = parseToolArgs("browser.navigate", {
      url: "https://example.com",
      tabId: "tab_1",
      timeoutMs: 5000,
    });
    expect(parsed).toEqual({
      url: "https://example.com",
      tabId: "tab_1",
      timeoutMs: 5000,
    });
  });

  it("accepts valid browser.fill with selector", () => {
    const parsed = parseToolArgs("browser.fill", {
      value: "Ada",
      selector: { testId: "name-input", role: "textbox" },
    });
    expect(parsed).toEqual({
      value: "Ada",
      selector: { testId: "name-input", role: "textbox" },
    });
  });

  it("accepts valid window.focus args", () => {
    const parsed = parseToolArgs("window.focus", { windowId: "win_1" });
    expect(parsed).toEqual({ windowId: "win_1" });
  });

  it("accepts monitor.list and window control schemas", () => {
    expect(parseToolArgs("monitor.list", {})).toEqual({});
    expect(parseToolArgs("window.get", { windowId: "win_1" })).toEqual({ windowId: "win_1" });
    expect(
      parseToolArgs("window.move", { windowId: "win_1", monitor: 0, placement: "maximize" }),
    ).toEqual({ windowId: "win_1", monitor: 0, placement: "maximize" });
    expect(parseToolArgs("window.move", { windowId: "win_1", x: 10, y: 20 })).toEqual({
      windowId: "win_1",
      x: 10,
      y: 20,
    });
    expect(parseToolArgs("window.resize", { windowId: "win_1", width: 800, height: 600 })).toEqual({
      windowId: "win_1",
      width: 800,
      height: 600,
    });
    expect(parseToolArgs("window.resize", { windowId: "win_1", width: 800, height: 600, x: 12 })).toEqual({
      windowId: "win_1",
      width: 800,
      height: 600,
      x: 12,
    });
  });

  it("rejects invalid window.move target combinations", () => {
    expect(() => parseToolArgs("window.move", { windowId: "win_1" })).toThrow();
    expect(() => parseToolArgs("window.move", { windowId: "win_1", x: 10 })).toThrow();
    expect(() => parseToolArgs("window.move", { windowId: "win_1", y: 20 })).toThrow();
  });

  it("accepts empty object for desktop.get_state", () => {
    expect(parseToolArgs("desktop.get_state", {})).toEqual({});
  });

  it("accepts desktop.describe with optional includeControls", () => {
    expect(parseToolArgs("desktop.describe", {})).toEqual({});
    expect(parseToolArgs("desktop.describe", { includeControls: false })).toEqual({
      includeControls: false,
    });
  });

  it("accepts desktop.batch calls and filesystem.inspect", () => {
    expect(
      parseToolArgs("desktop.batch", {
        calls: [{ method: "filesystem.exists", params: { path: "C:\\\\tmp\\\\a" } }],
      }),
    ).toMatchObject({
      calls: [{ method: "filesystem.exists" }],
    });
    expect(
      parseToolArgs("filesystem.inspect", {
        path: "C:\\\\tmp",
        paths: ["C:\\\\tmp\\\\a"],
      }),
    ).toEqual({
      path: "C:\\\\tmp",
      paths: ["C:\\\\tmp\\\\a"],
    });
  });

  it("accepts empty object for browser.list", () => {
    expect(parseToolArgs("browser.list", {})).toEqual({});
  });

  it("exposes zod schemas for every registered tool", () => {
    for (const schema of Object.values(toolSchemas)) {
      expect(schema).toBeDefined();
      expect(typeof schema.parse).toBe("function");
    }
  });
});
