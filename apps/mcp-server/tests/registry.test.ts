import { describe, expect, it, vi } from "vitest";
import { AgentClient } from "../src/agent-client.js";
import { createServer } from "../src/server.js";
import { TOOL_METADATA } from "../src/tools/metadata.js";
import { listRegisteredMcpToolNames, listRegisteredToolNames, TOOL_NAMES } from "../src/tools/registry.js";
import { toMcpToolName } from "../src/tools/tool-names.js";

const EXPECTED_TOOLS = [
  "desktop.get_state",
  "desktop.get_capabilities",
  "desktop.describe",
  "desktop.get_graph",
  "desktop.batch",
  "desktop.diff",
  "monitor.list",
  "window.list",
  "window.get",
  "window.focus",
  "window.minimize",
  "window.maximize",
  "window.restore",
  "window.move",
  "window.resize",
  "window.wait_for",
  "ui.get_tree",
  "ui.find",
  "ui.invoke",
  "ui.set_value",
  "ui.get_text",
  "ui.wait_for",
  "process.list",
  "process.launch",
  "app.launch",
  "shell.open",
  "clipboard.read",
  "clipboard.write",
  "filesystem.list",
  "filesystem.exists",
  "filesystem.read_text",
  "filesystem.write_text",
  "filesystem.stat",
  "filesystem.inspect",
  "filesystem.copy",
  "filesystem.move",
  "filesystem.delete",
  "filesystem.open",
  "system.power",
  "search.files",
  "plan.execute",
  "plan.cancel",
  "system.emergency_stop",
  "system.performance",
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
  "blender.batch",
  "blender.render",
  "blender.import_mesh",
  "blender.create_mesh",
  "blender.export_for_roblox",
  "blender.mesh_extrude",
  "blender.mesh_inset",
  "blender.mesh_bevel",
  "blender.mesh_loop_cut",
  "blender.modifier_boolean",
  "blender.modifier_mirror",
  "blender.modifier_array",
  "blender.material_set",
  "blender.uv_unwrap",
  "blender.select_geometry",
  "blender.animation_apply",
  "blender.animation_inspect",
  "blender.animation_preview",
  "blender.asset_validate",
  "vscode.open_file",
  "vscode.open_folder",
  "vscode.execute_command",
  "vscode.get_workspace",
  "visualstudio.get_solution",
  "visualstudio.build",
  "visualstudio.open_file",
  "visualstudio.open_solution",
  "discord.open",
  "discord.join_voice",
  "discord.quick_switch",
  "office.open",
  "office.mail_compose",
  "office.calendar_week",
  "media.transport",
  "media.volume",
  "roblox.open_place",
  "roblox.plugin_ping",
  "roblox.get_hierarchy",
  "roblox.get_selection",
  "roblox.select",
  "roblox.set_property",
  "roblox.create_instance",
  "roblox.destroy_instance",
  "roblox.clone_instance",
  "roblox.set_parent",
  "roblox.find_instances",
  "roblox.get_script_source",
  "roblox.set_script_source",
  "roblox.batch",
  "roblox.playtest_start",
  "roblox.playtest_stop",
  "roblox.terrain_fill_block",
  "roblox.terrain_fill_ball",
  "roblox.terrain_clear",
  "roblox.insert_asset",
  "roblox.import_local_model",
  "roblox.publish_place",
  "roblox.execute_luau",
  "roblox.animation_configure",
  "roblox.animation_bind",
  "roblox.animation_marker_add",
  "roblox.sequence_apply",
  "roblox.output_read",
  "roblox.playtest_inspect",
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
  "vision.ocr",
] as const;

const EXPECTED_REGISTERED_MCP_NAMES = EXPECTED_TOOLS.flatMap((name) => {
  const mcpName = toMcpToolName(name);
  return mcpName === name ? [mcpName] : [mcpName, name];
});

describe("tool registry", () => {
  it("lists the expected Phase 11 tool names", () => {
    expect(listRegisteredToolNames()).toEqual([...EXPECTED_TOOLS]);
    expect(TOOL_NAMES).toEqual([...EXPECTED_TOOLS]);
    expect(TOOL_NAMES).toContain("desktop.batch");
    expect(TOOL_NAMES).toContain("filesystem.inspect");
  });

  it("lists underscored and dotted MCP tool name aliases", () => {
    expect(listRegisteredMcpToolNames()).toEqual([...EXPECTED_REGISTERED_MCP_NAMES]);
    for (const name of EXPECTED_TOOLS) {
      const mcpName = toMcpToolName(name);
      expect(listRegisteredMcpToolNames()).toContain(mcpName);
      expect(listRegisteredMcpToolNames()).toContain(name);
    }
  });

  it("registers underscored and dotted aliases on McpServer", () => {
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

    expect(registered).toEqual([...EXPECTED_REGISTERED_MCP_NAMES].sort());
  });

  it("forwards underscored and dotted tool calls to dotted agent methods", async () => {
    const client = new AgentClient({ pipeName: "unused" });
    client.send = vi.fn(async () => ({
      ok: true,
      data: {},
      meta: {
        requestId: "request-1",
        startedAt: "2026-09-21T12:00:00Z",
        completedAt: "2026-09-21T12:00:00Z",
        durationMs: 0,
      },
    }));

    const { server } = createServer({ client });
    const registeredTools = (
      server as unknown as {
        _registeredTools: Record<
          string,
          {
            handler?: (args: unknown) => Promise<unknown>;
          }
        >;
      }
    )._registeredTools;

    await registeredTools["browser_open_tab"]!.handler!({});
    expect(client.send).toHaveBeenCalledWith("browser.open_tab", expect.any(Object));

    (client.send as ReturnType<typeof vi.fn>).mockClear();
    await registeredTools["browser.open_tab"]!.handler!({});
    expect(client.send).toHaveBeenCalledWith("browser.open_tab", expect.any(Object));

    await registeredTools["desktop_get_state"]!.handler!({});
    expect(client.send).toHaveBeenCalledWith("desktop.get_state", expect.any(Object));
    (client.send as ReturnType<typeof vi.fn>).mockClear();
    await registeredTools["desktop.get_state"]!.handler!({});
    expect(client.send).toHaveBeenCalledWith("desktop.get_state", expect.any(Object));
  });

  it("defines metadata for exactly the registered tools", () => {
    expect(Object.keys(TOOL_METADATA).sort()).toEqual([...TOOL_NAMES].sort());
  });

  it("assigns ChatGPT-compatible metadata to every tool", () => {
    for (const name of TOOL_NAMES) {
      const meta = TOOL_METADATA[name];
      expect(meta.title.length).toBeGreaterThan(0);
      expect(typeof meta.annotations.readOnlyHint).toBe("boolean");
      expect(typeof meta.annotations.destructiveHint).toBe("boolean");
      expect(typeof meta.annotations.openWorldHint).toBe("boolean");
    }
  });

  it("classifies representative tools with OpenAI semantics", () => {
    expect(TOOL_METADATA["desktop.get_state"].annotations.readOnlyHint).toBe(true);
    expect(TOOL_METADATA["filesystem.write_text"].annotations.destructiveHint).toBe(true);
    expect(TOOL_METADATA["browser.navigate"].annotations.openWorldHint).toBe(true);
    expect(TOOL_METADATA["browser.navigate"].annotations.destructiveHint).toBe(false);
    expect(TOOL_METADATA["browser.close_tab"].annotations.destructiveHint).toBe(true);
    expect(TOOL_METADATA["desktop.batch"].annotations.destructiveHint).toBe(true);
    expect(TOOL_METADATA["desktop.batch"].annotations.openWorldHint).toBe(true);
    expect(TOOL_METADATA["plan.execute"].annotations.destructiveHint).toBe(true);
    expect(TOOL_METADATA["plan.cancel"].annotations.destructiveHint).toBe(false);
    expect(TOOL_METADATA["plan.cancel"].annotations.readOnlyHint).toBe(false);
    expect(TOOL_METADATA["window.focus"].annotations.destructiveHint).toBe(false);
    expect(TOOL_METADATA["window.focus"].annotations.readOnlyHint).toBe(false);
    expect(TOOL_METADATA["ui.invoke"].annotations.readOnlyHint).toBe(false);
    expect(TOOL_METADATA["ui.invoke"].annotations.destructiveHint).toBe(true);
    expect(TOOL_METADATA["input.type"].annotations.destructiveHint).toBe(true);
    expect(TOOL_METADATA["input.mouse_move"].annotations.destructiveHint).toBe(false);
    expect(TOOL_METADATA["input.scroll"].annotations.destructiveHint).toBe(false);
  });

  it("registers title and annotations on McpServer tools", () => {
    const client = new AgentClient({ pipeName: "unused" });
    client.send = vi.fn(async () => ({ ok: true, data: {} }));

    const { server } = createServer({ client });
    const registeredTools = (
      server as unknown as {
        _registeredTools: Record<
          string,
          {
            title?: string;
            annotations?: {
              readOnlyHint?: boolean;
              destructiveHint?: boolean;
              openWorldHint?: boolean;
            };
          }
        >;
      }
    )._registeredTools;

    for (const name of TOOL_NAMES) {
      const mcpName = toMcpToolName(name);
      const meta = TOOL_METADATA[name];
      for (const alias of mcpName === name ? [mcpName] : [mcpName, name]) {
        const tool = registeredTools[alias];
        expect(tool.title).toBe(meta.title);
        expect(tool.annotations).toEqual(meta.annotations);
      }
    }
  });

  it("registers an output schema on every tool alias", () => {
    const client = new AgentClient({ pipeName: "unused" });
    const { server } = createServer({ client });
    const registeredTools = (
      server as unknown as {
        _registeredTools: Record<string, { outputSchema?: unknown }>;
      }
    )._registeredTools;

    for (const name of listRegisteredMcpToolNames()) {
      expect(registeredTools[name]?.outputSchema).toBeDefined();
    }
  });

  it("returns the agent envelope as MCP structured content", async () => {
    const response = {
      ok: true,
      data: { windows: [] },
      meta: {
        requestId: "request-1",
        startedAt: "2026-09-21T12:00:00Z",
        completedAt: "2026-09-21T12:00:00Z",
        durationMs: 0,
      },
    };
    const client = new AgentClient({ pipeName: "unused" });
    client.send = vi.fn(async () => response);

    const { server } = createServer({ client });
    const tool = (
      server as unknown as {
        _registeredTools: Record<string, { handler: (args: unknown) => Promise<unknown> }>;
      }
    )._registeredTools["desktop_get_state"]!;

    await expect(tool.handler({})).resolves.toMatchObject({
      structuredContent: response,
      content: [{ type: "text", text: JSON.stringify(response) }],
    });
  });
});
