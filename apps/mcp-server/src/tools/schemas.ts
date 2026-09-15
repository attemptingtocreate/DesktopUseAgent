import { z } from "zod";

const emptyParams = z.object({}).strict();

export const uiFindSelectorSchema = z
  .object({
    name: z.string().optional(),
    nameContains: z.string().optional(),
    automationId: z.string().optional(),
    className: z.string().optional(),
    controlType: z.string().optional(),
    frameworkId: z.string().optional(),
    enabled: z.boolean().optional(),
    offscreen: z.boolean().optional(),
  })
  .strict();

export const browserSelectorSchema = z
  .object({
    css: z.string().optional(),
    role: z.string().optional(),
    name: z.string().optional(),
    text: z.string().optional(),
    placeholder: z.string().optional(),
    label: z.string().optional(),
    testId: z.string().optional(),
  })
  .strict();

const browserTabIdParams = z
  .object({
    tabId: z.string().optional(),
  })
  .strict();

const browserElementParams = z
  .object({
    tabId: z.string().optional(),
    elementId: z.string().optional(),
    selector: browserSelectorSchema.optional(),
  })
  .strict();

export const conditionSchema = z
  .object({
    type: z.string().min(1),
    process: z.string().optional(),
    titleContains: z.string().optional(),
    windowId: z.string().optional(),
    path: z.string().optional(),
    name: z.string().optional(),
    automationId: z.string().optional(),
    controlType: z.string().optional(),
    value: z.string().optional(),
    property: z.string().optional(),
    delayMs: z.number().int().nonnegative().optional(),
    timeoutMs: z.number().int().positive().optional(),
    selector: z.record(z.unknown()).optional(),
  })
  .strict();

export const planStepSchema = z
  .object({
    id: z.string().min(1),
    action: z.string().min(1),
    args: z.record(z.unknown()).optional(),
    timeoutMs: z.number().int().positive().optional(),
    retries: z
      .object({
        count: z.number().int().nonnegative(),
        delayMs: z.number().int().nonnegative().optional(),
        backoff: z.number().positive().optional(),
      })
      .strict()
      .optional(),
    when: conditionSchema.optional(),
    waitAfter: conditionSchema.optional(),
    onFailure: z.enum(["stop", "continue", "rollback"]).optional(),
  })
  .strict();

export const toolSchemas = {
  "desktop.get_state": emptyParams,
  "desktop.get_capabilities": emptyParams,
  "desktop.describe": z
    .object({
      includeControls: z.boolean().optional(),
    })
    .strict(),
  "desktop.get_graph": z
    .object({
      includeControls: z.boolean().optional(),
      forceRefresh: z.boolean().optional(),
    })
    .strict(),
  "desktop.batch": z
    .object({
      calls: z
        .array(
          z
            .object({
              id: z.string().optional(),
              method: z.string().min(1),
              params: z.record(z.unknown()).optional(),
            })
            .strict(),
        )
        .min(1)
        .max(32),
      parallel: z.boolean().optional(),
    })
    .strict(),
  "desktop.diff": z
    .object({
      includeControls: z.boolean().optional(),
    })
    .strict(),
  "window.list": emptyParams,
  "window.focus": z
    .object({
      windowId: z.string().min(1),
    })
    .strict(),
  "window.wait_for": z
    .object({
      process: z.string().optional(),
      titleContains: z.string().optional(),
      title: z.string().optional(),
      titleRegex: z.string().optional(),
      timeoutMs: z.number().int().positive().optional(),
    })
    .strict(),
  "ui.get_tree": z
    .object({
      rootId: z.string().optional(),
      windowId: z.string().optional(),
      treeMode: z.enum(["control", "content", "raw"]).optional(),
      depth: z.number().int().positive().optional(),
      maxNodes: z.number().int().positive().optional(),
      interactiveOnly: z.boolean().optional(),
      includeText: z.boolean().optional(),
      includeBounds: z.boolean().optional(),
    })
    .strict(),
  "ui.find": z
    .object({
      windowId: z.string().optional(),
      rootId: z.string().optional(),
      selector: uiFindSelectorSchema.default({}),
      depth: z.number().int().positive().optional(),
      maxResults: z.number().int().positive().optional(),
    })
    .strict(),
  "ui.invoke": z
    .object({
      elementId: z.string().min(1),
    })
    .strict(),
  "ui.set_value": z
    .object({
      elementId: z.string().min(1),
      value: z.string(),
    })
    .strict(),
  "ui.get_text": z
    .object({
      elementId: z.string().min(1),
    })
    .strict(),
  "ui.wait_for": z
    .object({
      windowId: z.string().optional(),
      selector: uiFindSelectorSchema.optional(),
      name: z.string().optional(),
      nameContains: z.string().optional(),
      automationId: z.string().optional(),
      controlType: z.string().optional(),
      type: z.string().optional(),
      className: z.string().optional(),
      timeoutMs: z.number().int().positive().optional(),
    })
    .strict(),
  "process.list": emptyParams,
  "process.launch": z
    .object({
      executable: z.string().min(1),
      args: z.array(z.string()).optional(),
      workingDirectory: z.string().optional(),
    })
    .strict(),
  "filesystem.list": z
    .object({
      path: z.string().optional(),
    })
    .strict(),
  "filesystem.exists": z
    .object({
      path: z.string().min(1),
    })
    .strict(),
  "filesystem.read_text": z
    .object({
      path: z.string().min(1),
    })
    .strict(),
  "filesystem.write_text": z
    .object({
      path: z.string().min(1),
      contents: z.string().optional(),
      value: z.string().optional(),
    })
    .strict()
    .refine((v) => v.contents !== undefined || v.value !== undefined, {
      message: "contents or value is required",
    }),
  "filesystem.stat": z
    .object({
      path: z.string().min(1),
    })
    .strict(),
  "filesystem.inspect": z
    .object({
      path: z.string().optional(),
      paths: z.array(z.string().min(1)).optional(),
    })
    .strict(),
  "plan.execute": z
    .object({
      id: z.string().optional(),
      name: z.string().optional(),
      steps: z.array(planStepSchema).min(1),
      options: z
        .object({
          stopOnFailure: z.boolean().optional(),
          defaultTimeoutMs: z.number().int().positive().optional(),
          optimize: z.boolean().optional(),
          parallelSafeReads: z.boolean().optional(),
        })
        .strict()
        .optional(),
    })
    .strict(),
  "plan.cancel": z
    .object({
      planId: z.string().min(1).optional(),
      id: z.string().min(1).optional(),
    })
    .strict()
    .refine((v) => Boolean(v.planId ?? v.id), {
      message: "planId or id is required",
    }),
  "system.emergency_stop": emptyParams,
  "events.subscribe": z
    .object({
      types: z.array(z.string().min(1)).optional(),
    })
    .strict(),
  "events.poll": z
    .object({
      subscriptionId: z.string().min(1).optional(),
      id: z.string().min(1).optional(),
      max: z.number().int().positive().optional(),
      timeoutMs: z.number().int().nonnegative().optional(),
    })
    .strict()
    .refine((v) => Boolean(v.subscriptionId ?? v.id), {
      message: "subscriptionId or id is required",
    }),
  "events.unsubscribe": z
    .object({
      subscriptionId: z.string().min(1).optional(),
      id: z.string().min(1).optional(),
    })
    .strict()
    .refine((v) => Boolean(v.subscriptionId ?? v.id), {
      message: "subscriptionId or id is required",
    }),
  "browser.list": emptyParams,
  "browser.tabs": z
    .object({
      browserId: z.string().optional(),
    })
    .strict(),
  "browser.get_tab": z
    .object({
      tabId: z.string().min(1),
    })
    .strict(),
  "browser.open_tab": z
    .object({
      url: z.string().optional(),
    })
    .strict(),
  "browser.close_tab": z
    .object({
      tabId: z.string().min(1),
    })
    .strict(),
  "browser.navigate": z
    .object({
      url: z.string().min(1),
      tabId: z.string().optional(),
      timeoutMs: z.number().int().positive().optional(),
    })
    .strict(),
  "browser.back": browserTabIdParams,
  "browser.forward": browserTabIdParams,
  "browser.reload": browserTabIdParams,
  "browser.query": z
    .object({
      tabId: z.string().optional(),
      selector: browserSelectorSchema.optional(),
    })
    .strict(),
  "browser.query_all": z
    .object({
      tabId: z.string().optional(),
      selector: browserSelectorSchema.optional(),
    })
    .strict(),
  "browser.click": browserElementParams,
  "browser.fill": z
    .object({
      value: z.string(),
      tabId: z.string().optional(),
      elementId: z.string().optional(),
      selector: browserSelectorSchema.optional(),
    })
    .strict(),
  "browser.select": z
    .object({
      value: z.string(),
      tabId: z.string().optional(),
      elementId: z.string().optional(),
      selector: browserSelectorSchema.optional(),
      label: z.string().optional(),
    })
    .strict(),
  "browser.focus": browserElementParams,
  "browser.get_text": browserElementParams,
  "browser.get_dom": z
    .object({
      tabId: z.string().optional(),
      depth: z.number().int().positive().optional(),
      maxNodes: z.number().int().positive().optional(),
      maxChars: z.number().int().positive().optional(),
    })
    .strict(),
  "browser.get_accessibility_tree": z
    .object({
      tabId: z.string().optional(),
      depth: z.number().int().positive().optional(),
      maxNodes: z.number().int().positive().optional(),
      maxDepth: z.number().int().positive().optional(),
    })
    .strict(),
  "browser.wait_for": z
    .object({
      tabId: z.string().optional(),
      selector: browserSelectorSchema.optional(),
      timeoutMs: z.number().int().positive().optional(),
    })
    .strict(),
  "browser.wait_for_navigation": z
    .object({
      tabId: z.string().optional(),
      timeoutMs: z.number().int().positive().optional(),
    })
    .strict(),
  "browser.wait_for_network_idle": z
    .object({
      tabId: z.string().optional(),
      quietMs: z.number().int().nonnegative().optional(),
      timeoutMs: z.number().int().positive().optional(),
    })
    .strict(),
  "browser.get_downloads": emptyParams,
  "adapter.list": emptyParams,
  "adapter.capabilities": z
    .object({
      adapterId: z.string().min(1).optional(),
    })
    .strict(),
  "adapter.execute": z
    .object({
      action: z.string().min(1),
      adapterId: z.string().min(1).optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
      path: z.string().optional(),
      file: z.string().optional(),
      folder: z.string().optional(),
      output: z.string().optional(),
      destination: z.string().optional(),
      format: z.string().optional(),
      name: z.string().optional(),
      object: z.string().optional(),
      code: z.string().optional(),
      python: z.string().optional(),
      command: z.string().optional(),
      solution: z.string().optional(),
      configuration: z.string().optional(),
      line: z.number().int().positive().optional(),
      column: z.number().int().positive().optional(),
    })
    .strict(),
  "blender.open": z.object({ path: z.string().min(1).optional(), file: z.string().min(1).optional() }).strict(),
  "blender.get_scene": z.object({ path: z.string().optional(), file: z.string().optional() }).strict(),
  "blender.get_objects": z.object({ path: z.string().optional(), file: z.string().optional() }).strict(),
  "blender.select_object": z
    .object({
      name: z.string().optional(),
      object: z.string().optional(),
      path: z.string().optional(),
      file: z.string().optional(),
    })
    .strict(),
  "blender.execute_python": z
    .object({
      code: z.string().optional(),
      python: z.string().optional(),
      path: z.string().optional(),
      file: z.string().optional(),
    })
    .strict(),
  "blender.export": z
    .object({
      output: z.string().optional(),
      destination: z.string().optional(),
      format: z.string().optional(),
      path: z.string().optional(),
      file: z.string().optional(),
    })
    .strict(),
  "blender.save": z
    .object({
      path: z.string().optional(),
      file: z.string().optional(),
      destination: z.string().optional(),
    })
    .strict(),
  "vscode.open_file": z
    .object({
      path: z.string().optional(),
      file: z.string().optional(),
      line: z.number().int().positive().optional(),
      column: z.number().int().positive().optional(),
    })
    .strict(),
  "vscode.open_folder": z.object({ path: z.string().optional(), folder: z.string().optional() }).strict(),
  "vscode.execute_command": z.object({ command: z.string().min(1) }).strict(),
  "vscode.get_workspace": z.object({ path: z.string().optional() }).strict(),
  "visualstudio.get_solution": z.object({ path: z.string().optional(), solution: z.string().optional() }).strict(),
  "visualstudio.build": z
    .object({
      path: z.string().optional(),
      solution: z.string().optional(),
      configuration: z.string().optional(),
    })
    .strict(),
  "visualstudio.open_file": z.object({ path: z.string().optional(), file: z.string().optional() }).strict(),
  "visualstudio.open_solution": z.object({ path: z.string().optional(), solution: z.string().optional() }).strict(),
  "input.mouse_move": z
    .object({
      x: z.number().int(),
      y: z.number().int(),
      dpiScale: z.number().positive().optional(),
      fallbackReason: z.string().optional(),
      fallback_reason: z.string().optional(),
    })
    .strict(),
  "input.mouse_click": z
    .object({
      x: z.number().int(),
      y: z.number().int(),
      button: z.enum(["left", "right", "middle"]).optional(),
      clickCount: z.number().int().positive().optional(),
      dpiScale: z.number().positive().optional(),
      fallbackReason: z.string().optional(),
      fallback_reason: z.string().optional(),
    })
    .strict(),
  "input.mouse_drag": z
    .object({
      fromX: z.number().int(),
      fromY: z.number().int(),
      toX: z.number().int(),
      toY: z.number().int(),
      button: z.enum(["left", "right", "middle"]).optional(),
      dpiScale: z.number().positive().optional(),
      fallbackReason: z.string().optional(),
      fallback_reason: z.string().optional(),
    })
    .strict(),
  "input.scroll": z
    .object({
      x: z.number().int().optional(),
      y: z.number().int().optional(),
      delta: z.number().int().optional(),
      axis: z.enum(["vertical", "horizontal"]).optional(),
      dpiScale: z.number().positive().optional(),
      fallbackReason: z.string().optional(),
      fallback_reason: z.string().optional(),
    })
    .strict(),
  "input.key": z
    .object({
      key: z.string().min(1),
      action: z.enum(["press", "down", "up"]).optional(),
      fallbackReason: z.string().optional(),
      fallback_reason: z.string().optional(),
    })
    .strict(),
  "input.hotkey": z
    .object({
      keys: z.union([z.array(z.string().min(1)).min(1), z.string().min(1)]),
      fallbackReason: z.string().optional(),
      fallback_reason: z.string().optional(),
    })
    .strict(),
  "input.type": z
    .object({
      text: z.string().optional(),
      value: z.string().optional(),
      delayMs: z.number().int().nonnegative().optional(),
      fallbackReason: z.string().optional(),
      fallback_reason: z.string().optional(),
    })
    .strict(),
  "vision.capture_screen": z
    .object({
      monitor: z.number().int().nonnegative().optional(),
      visionReason: z.string().optional(),
      vision_reason: z.string().optional(),
    })
    .strict(),
  "vision.capture_window": z
    .object({
      windowId: z.string().min(1),
      visionReason: z.string().optional(),
      vision_reason: z.string().optional(),
    })
    .strict(),
  "vision.capture_region": z
    .object({
      x: z.number().int(),
      y: z.number().int(),
      width: z.number().int().positive(),
      height: z.number().int().positive(),
      visionReason: z.string().optional(),
      vision_reason: z.string().optional(),
    })
    .strict(),
} as const;

export type ToolName = keyof typeof toolSchemas;

export const TOOL_NAMES = Object.keys(toolSchemas) as ToolName[];

export const TOOL_DESCRIPTIONS: Record<ToolName, string> = {
  "desktop.get_state": "Return a concise snapshot of desktop windows and agent session state.",
  "desktop.get_capabilities": "Return agent capability flags and supported tool names.",
  "desktop.describe": "Return a compact live semantic desktop graph (apps, windows, tabs, important controls, relevant state) as text plus structured graph.",
  "desktop.get_graph": "Return the structured semantic desktop graph without the compact text rendering.",
  "desktop.batch": "Execute multiple agent commands in one round trip, fusing filesystem stats and running safe reads in parallel.",
  "desktop.diff": "Return a compact state-change diff of the live desktop graph versus the previous snapshot.",
  "window.list": "List top-level windows.",
  "window.focus": "Focus a window by handle id.",
  "window.wait_for": "Wait until a window matching process/title appears.",
  "ui.get_tree": "Get a bounded UI Automation subtree for a window or root element.",
  "ui.find": "Find UI elements matching a semantic selector.",
  "ui.invoke": "Invoke the default action on a UI element.",
  "ui.set_value": "Set the value of an editable UI element.",
  "ui.get_text": "Read text from a UI element (sensitive values are redacted).",
  "ui.wait_for": "Wait until a UI element matching the selector exists.",
  "process.list": "List running processes.",
  "process.launch": "Launch an executable process.",
  "filesystem.list": "List files and directories at a path.",
  "filesystem.exists": "Check whether a filesystem path exists.",
  "filesystem.read_text": "Read a text file.",
  "filesystem.write_text": "Write a text file.",
  "filesystem.stat": "Return size/mtime/exists metadata for a filesystem path.",
  "filesystem.inspect": "List a directory with per-entry stats and optional extra path stats in one call.",
  "plan.execute": "Execute a multi-step automation plan.",
  "plan.cancel": "Cancel a running plan by id.",
  "system.emergency_stop": "Engage the agent emergency stop gate.",
  "events.subscribe": "Subscribe to desktop state-change events (window/focus/mutation).",
  "events.poll": "Poll a prior events.subscribe cursor for new events.",
  "events.unsubscribe": "Remove an event subscription.",
  "browser.list": "List discovered browser instances with CDP endpoints.",
  "browser.tabs": "List open tabs for a browser instance.",
  "browser.get_tab": "Get details for a browser tab by id.",
  "browser.open_tab": "Open a new browser tab, optionally navigating to a URL.",
  "browser.close_tab": "Close a browser tab by id.",
  "browser.navigate": "Navigate a tab to a URL.",
  "browser.back": "Navigate the tab history backward.",
  "browser.forward": "Navigate the tab history forward.",
  "browser.reload": "Reload the current tab page.",
  "browser.query": "Query the first element matching a browser selector.",
  "browser.query_all": "Query all elements matching a browser selector.",
  "browser.click": "Click an element by id or selector.",
  "browser.fill": "Fill an input element with a value.",
  "browser.select": "Select an option in a select element.",
  "browser.focus": "Focus an element by id or selector.",
  "browser.get_text": "Read text content from an element.",
  "browser.get_dom": "Get a bounded DOM snapshot for a tab.",
  "browser.get_accessibility_tree": "Get a bounded accessibility tree for a tab.",
  "browser.wait_for": "Wait until a selector matches an element.",
  "browser.wait_for_navigation": "Wait until navigation completes.",
  "browser.wait_for_network_idle": "Wait until network activity is quiet.",
  "browser.get_downloads": "List recent browser downloads tracked by the agent.",
  "adapter.list": "List application adapters and their availability/actions.",
  "adapter.capabilities": "Get capabilities for one adapter or all adapters.",
  "adapter.execute": "Execute an application-adapter action (Blender/VS Code/Visual Studio).",
  "blender.open": "Open a .blend file via Blender Python (background).",
  "blender.get_scene": "Inspect the Blender scene via Python scripting.",
  "blender.get_objects": "List Blender objects via Python scripting.",
  "blender.select_object": "Select a Blender object by name via Python.",
  "blender.execute_python": "Run a Blender Python snippet.",
  "blender.export": "Export the Blender scene (obj/fbx/gltf/stl) via Python.",
  "blender.save": "Save the Blender file via Python.",
  "vscode.open_file": "Open a file in VS Code via the code CLI.",
  "vscode.open_folder": "Open a folder/workspace in VS Code via the code CLI.",
  "vscode.execute_command": "Attempt a VS Code CLI --command invocation.",
  "vscode.get_workspace": "Return workspace path metadata for VS Code.",
  "visualstudio.get_solution": "Parse Visual Studio solution projects from a .sln path.",
  "visualstudio.build": "Build a solution via MSBuild or devenv.",
  "visualstudio.open_file": "Open a file in Visual Studio (devenv /Edit).",
  "visualstudio.open_solution": "Open a .sln in Visual Studio (devenv).",
  "input.mouse_move": "Fallback: move the mouse to physical screen coordinates (DPI/virtual-desktop aware).",
  "input.mouse_click": "Fallback: click at physical screen coordinates via SendInput.",
  "input.mouse_drag": "Fallback: drag between physical screen coordinates via SendInput.",
  "input.scroll": "Fallback: scroll vertically or horizontally via SendInput.",
  "input.key": "Fallback: press/down/up a key via SendInput.",
  "input.hotkey": "Fallback: chord hotkey via SendInput (keys array or '+'-joined).",
  "input.type": "Fallback: type unicode text via SendInput.",
  "vision.capture_screen": "Capture the virtual desktop or a monitor as PNG (vision fallback; record visionReason).",
  "vision.capture_window": "Capture a window by id as PNG via PrintWindow/BitBlt (vision fallback).",
  "vision.capture_region": "Capture a physical screen region as PNG (vision fallback).",
};

export function parseToolArgs<T extends ToolName>(
  name: T,
  args: unknown,
): z.infer<(typeof toolSchemas)[T]> {
  return toolSchemas[name].parse(args ?? {}) as z.infer<(typeof toolSchemas)[T]>;
}
