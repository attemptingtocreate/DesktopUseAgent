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
  "plan.execute": z
    .object({
      id: z.string().optional(),
      name: z.string().optional(),
      steps: z.array(planStepSchema).min(1),
      options: z
        .object({
          stopOnFailure: z.boolean().optional(),
          defaultTimeoutMs: z.number().int().positive().optional(),
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
} as const;

export type ToolName = keyof typeof toolSchemas;

export const TOOL_NAMES = Object.keys(toolSchemas) as ToolName[];

export const TOOL_DESCRIPTIONS: Record<ToolName, string> = {
  "desktop.get_state": "Return a concise snapshot of desktop windows and agent session state.",
  "desktop.get_capabilities": "Return agent capability flags and supported tool names.",
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
  "plan.execute": "Execute a multi-step automation plan.",
  "plan.cancel": "Cancel a running plan by id.",
  "system.emergency_stop": "Engage the agent emergency stop gate.",
};

export function parseToolArgs<T extends ToolName>(
  name: T,
  args: unknown,
): z.infer<(typeof toolSchemas)[T]> {
  return toolSchemas[name].parse(args ?? {}) as z.infer<(typeof toolSchemas)[T]>;
}
