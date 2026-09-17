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
  "monitor.list": emptyParams,
  "window.list": emptyParams,
  "window.get": z
    .object({
      windowId: z.string().min(1),
    })
    .strict(),
  "window.focus": z
    .object({
      windowId: z.string().min(1),
    })
    .strict(),
  "window.minimize": z
    .object({
      windowId: z.string().min(1),
    })
    .strict(),
  "window.maximize": z
    .object({
      windowId: z.string().min(1),
    })
    .strict(),
  "window.restore": z
    .object({
      windowId: z.string().min(1),
    })
    .strict(),
  "window.move": z
    .object({
      windowId: z.string().min(1),
      x: z.number().int().optional(),
      y: z.number().int().optional(),
      monitor: z.number().int().nonnegative().optional(),
      placement: z.enum(["preserve", "maximize"]).optional(),
    })
    .strict()
    .superRefine((value, ctx) => {
      const hasMonitor = value.monitor !== undefined;
      const hasX = value.x !== undefined;
      const hasY = value.y !== undefined;

      if (!hasMonitor && !(hasX && hasY)) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: "window.move requires monitor and/or both x and y",
        });
      }

      if ((hasX && !hasY) || (!hasX && hasY)) {
        ctx.addIssue({
          code: z.ZodIssueCode.custom,
          message: "window.move requires both x and y when either coordinate is provided",
        });
      }
    }),
  "window.resize": z
    .object({
      windowId: z.string().min(1),
      width: z.number().int().positive(),
      height: z.number().int().positive(),
      x: z.number().int().optional(),
      y: z.number().int().optional(),
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
  "system.performance": emptyParams,
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
      sessionId: z.string().optional(),
      instanceId: z.string().optional(),
      instanceIds: z.array(z.string().min(1)).optional(),
      ids: z.array(z.string().min(1)).optional(),
      property: z.string().optional(),
      value: z.union([z.string(), z.number(), z.boolean()]).optional(),
      rootPath: z.string().optional(),
      depth: z.number().int().positive().max(10).optional(),
      maxNodes: z.number().int().positive().max(500).optional(),
      timeoutSeconds: z.number().int().positive().max(120).optional(),
    })
    .strict(),
  "blender.open": z
    .object({
      path: z.string().min(1).optional(),
      file: z.string().min(1).optional(),
      mode: z.enum(["background", "gui"]).optional(),
    })
    .strict(),
  "blender.get_scene": z
    .object({
      path: z.string().optional(),
      file: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.get_objects": z
    .object({
      path: z.string().optional(),
      file: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.select_object": z
    .object({
      name: z.string().optional(),
      object: z.string().optional(),
      path: z.string().optional(),
      file: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.execute_python": z
    .object({
      code: z.string().optional(),
      python: z.string().optional(),
      source: z.string().optional(),
      confirm: z.boolean().optional(),
      path: z.string().optional(),
      file: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.export": z
    .object({
      output: z.string().optional(),
      destination: z.string().optional(),
      format: z.string().optional(),
      path: z.string().optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      overwrite: z.boolean().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
    })
    .strict(),
  "blender.save": z
    .object({
      path: z.string().optional(),
      file: z.string().optional(),
      destination: z.string().optional(),
      blendFile: z.string().optional(),
      overwrite: z.boolean().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
    })
    .strict(),
  "blender.batch": z
    .object({
      operations: z
        .array(
          z
            .object({
              op: z.string().min(1).optional(),
              operation: z.string().min(1).optional(),
            })
            .passthrough(),
        )
        .min(1)
        .max(32),
      failFast: z.boolean().optional(),
      path: z.string().optional(),
      file: z.string().optional(),
    })
    .strict(),
  "blender.render": z
    .object({
      output: z.string().min(1),
      path: z.string().optional(),
      file: z.string().optional(),
      engine: z.string().optional(),
      frame: z.number().int().positive().optional(),
      animation: z.boolean().optional(),
      overwrite: z.boolean().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.import_mesh": z
    .object({
      path: z.string().min(1).optional(),
      input: z.string().min(1).optional(),
      format: z.string().optional(),
      name: z.string().optional(),
      objectName: z.string().optional(),
      collection: z.string().optional(),
      collectionName: z.string().optional(),
      saveAs: z.string().optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      overwrite: z.boolean().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.create_mesh": z
    .object({
      kind: z.enum(["cube", "uv_sphere", "ico_sphere", "cylinder", "cone", "plane", "torus"]).optional(),
      primitive: z.enum(["cube", "uv_sphere", "ico_sphere", "cylinder", "cone", "plane", "torus"]).optional(),
      name: z.string().optional(),
      location: z.array(z.number()).length(3).optional(),
      scale: z.array(z.number()).length(3).optional(),
      size: z.number().positive().optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.export_for_roblox": z
    .object({
      output: z.string().min(1).optional(),
      destination: z.string().min(1).optional(),
      format: z.enum(["fbx", "obj", "gltf", "glb"]).optional(),
      overwrite: z.boolean().optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.mesh_extrude": z
    .object({
      name: z.string().min(1).optional(),
      object: z.string().min(1).optional(),
      element: z.enum(["VERT", "EDGE", "FACE"]).optional(),
      geometryMode: z.enum(["VERT", "EDGE", "FACE"]).optional(),
      value: z.number().optional(),
      offset: z.number().optional(),
      indices: z.array(z.number().int()).optional(),
      index: z.union([z.number().int(), z.array(z.number().int())]).optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.mesh_inset": z
    .object({
      name: z.string().min(1).optional(),
      object: z.string().min(1).optional(),
      thickness: z.number().optional(),
      depth: z.number().optional(),
      indices: z.array(z.number().int()).optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.mesh_bevel": z
    .object({
      name: z.string().min(1).optional(),
      object: z.string().min(1).optional(),
      element: z.enum(["VERT", "EDGE", "FACE"]).optional(),
      geometryMode: z.enum(["VERT", "EDGE", "FACE"]).optional(),
      offset: z.number().optional(),
      width: z.number().optional(),
      segments: z.number().int().positive().optional(),
      indices: z.array(z.number().int()).optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.mesh_loop_cut": z
    .object({
      name: z.string().min(1).optional(),
      object: z.string().min(1).optional(),
      cuts: z.number().int().positive().max(64).optional(),
      edgeIndex: z.number().int().nonnegative().optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.modifier_boolean": z
    .object({
      name: z.string().min(1).optional(),
      object: z.string().min(1).optional(),
      target: z.string().min(1).optional(),
      operand: z.string().min(1).optional(),
      operation: z.enum(["UNION", "DIFFERENCE", "INTERSECT"]).optional(),
      apply: z.boolean().optional(),
      modifierName: z.string().optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.modifier_mirror": z
    .object({
      name: z.string().min(1).optional(),
      object: z.string().min(1).optional(),
      axis: z.enum(["X", "Y", "Z"]).optional(),
      apply: z.boolean().optional(),
      modifierName: z.string().optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.modifier_array": z
    .object({
      name: z.string().min(1).optional(),
      object: z.string().min(1).optional(),
      count: z.number().int().min(2).max(64).optional(),
      relativeOffset: z.array(z.number()).length(3).optional(),
      offset: z.array(z.number()).length(3).optional(),
      apply: z.boolean().optional(),
      modifierName: z.string().optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.material_set": z
    .object({
      name: z.string().min(1).optional(),
      object: z.string().min(1).optional(),
      material: z.string().optional(),
      materialName: z.string().optional(),
      color: z.array(z.number()).min(3).max(4).optional(),
      baseColor: z.array(z.number()).min(3).max(4).optional(),
      roughness: z.number().min(0).max(1).optional(),
      metallic: z.number().min(0).max(1).optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.uv_unwrap": z
    .object({
      name: z.string().min(1).optional(),
      object: z.string().min(1).optional(),
      method: z.enum(["ANGLE_BASED", "CONFORMAL", "SMART"]).optional(),
      margin: z.number().optional(),
      angleLimit: z.number().optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
    })
    .strict(),
  "blender.select_geometry": z
    .object({
      name: z.string().min(1).optional(),
      object: z.string().min(1).optional(),
      element: z.enum(["VERT", "EDGE", "FACE"]).optional(),
      geometryMode: z.enum(["VERT", "EDGE", "FACE"]).optional(),
      indices: z.array(z.number().int()).optional(),
      index: z.union([z.number().int(), z.array(z.number().int())]).optional(),
      selectAll: z.boolean().optional(),
      file: z.string().optional(),
      blendFile: z.string().optional(),
      mode: z.enum(["auto", "live", "background"]).optional(),
      sessionId: z.string().optional(),
      processId: z.string().optional(),
      windowId: z.string().optional(),
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
  "discord.open": z
    .object({
      monitor: z.number().int().nonnegative().optional(),
      placement: z.enum(["preserve", "normal", "maximize"]).optional(),
      sessionId: z.string().optional(),
    })
    .strict(),
  "discord.join_voice": z
    .object({
      channel: z.string().min(1),
      server: z.string().optional(),
      monitor: z.number().int().nonnegative().optional(),
      placement: z.enum(["preserve", "normal", "maximize"]).optional(),
      sessionId: z.string().optional(),
    })
    .strict(),
  "discord.quick_switch": z
    .object({
      query: z.string().min(1).optional(),
      channel: z.string().min(1).optional(),
      monitor: z.number().int().nonnegative().optional(),
      placement: z.enum(["preserve", "normal", "maximize"]).optional(),
      sessionId: z.string().optional(),
    })
    .strict()
    .refine((v) => Boolean(v.query || v.channel), {
      message: "query or channel is required",
    }),
  "roblox.open_place": z
    .object({
      path: z.string().min(1).optional(),
      file: z.string().min(1).optional(),
      monitor: z.number().int().nonnegative().optional(),
      placement: z.enum(["preserve", "normal", "maximize"]).optional(),
      x: z.number().int().optional(),
      y: z.number().int().optional(),
      timeoutSeconds: z.number().int().positive().max(120).optional(),
    })
    .strict(),
  "roblox.plugin_ping": z.object({ sessionId: z.string().optional() }).strict(),
  "roblox.get_hierarchy": z
    .object({
      sessionId: z.string().optional(),
      rootPath: z.string().optional(),
      depth: z.number().int().positive().max(10).optional(),
      maxNodes: z.number().int().positive().max(500).optional(),
      timeoutSeconds: z.number().int().positive().max(120).optional(),
    })
    .strict(),
  "roblox.get_selection": z
    .object({
      sessionId: z.string().optional(),
      timeoutSeconds: z.number().int().positive().max(120).optional(),
    })
    .strict(),
  "roblox.select": z
    .object({
      sessionId: z.string().optional(),
      instanceIds: z.array(z.string().min(1)).min(1).max(50).optional(),
      ids: z.array(z.string().min(1)).min(1).max(50).optional(),
      timeoutSeconds: z.number().int().positive().max(120).optional(),
    })
    .strict(),
  "roblox.set_property": z
    .object({
      sessionId: z.string().optional(),
      instanceId: z.string().min(1),
      property: z.string().min(1),
      value: z.union([
        z.string(),
        z.number(),
        z.boolean(),
        z
          .object({
            type: z.literal("Color3"),
            r: z.number().min(0).max(1),
            g: z.number().min(0).max(1),
            b: z.number().min(0).max(1),
          })
          .strict(),
        z
          .object({
            type: z.literal("Vector3"),
            x: z.number(),
            y: z.number(),
            z: z.number(),
          })
          .strict(),
        z
          .object({
            type: z.literal("UDim2"),
            xScale: z.number(),
            xOffset: z.number(),
            yScale: z.number(),
            yOffset: z.number(),
          })
          .strict(),
        z
          .object({
            type: z.literal("CFrame"),
            components: z.array(z.number()).length(12).optional(),
            position: z
              .object({ type: z.literal("Vector3"), x: z.number(), y: z.number(), z: z.number() })
              .strict()
              .optional(),
            orientation: z
              .object({ type: z.literal("Vector3"), x: z.number(), y: z.number(), z: z.number() })
              .strict()
              .optional(),
          })
          .strict(),
      ]),
      timeoutSeconds: z.number().int().positive().max(120).optional(),
    })
    .strict(),
  "roblox.create_instance": z
    .object({
      sessionId: z.string().optional(),
      className: z.string().min(1),
      parentId: z.string().min(1).optional(),
      parentPath: z.string().min(1).optional(),
      name: z.string().optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.destroy_instance": z
    .object({
      sessionId: z.string().optional(),
      instanceId: z.string().min(1),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.clone_instance": z
    .object({
      sessionId: z.string().optional(),
      instanceId: z.string().min(1),
      parentId: z.string().min(1).optional(),
      parentPath: z.string().min(1).optional(),
      name: z.string().optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.set_parent": z
    .object({
      sessionId: z.string().optional(),
      instanceId: z.string().min(1),
      parentId: z.string().min(1).optional(),
      parentPath: z.string().min(1).optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.find_instances": z
    .object({
      sessionId: z.string().optional(),
      className: z.string().optional(),
      nameContains: z.string().optional(),
      pathPrefix: z.string().optional(),
      rootPath: z.string().optional(),
      maxResults: z.number().int().positive().max(200).optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.get_script_source": z
    .object({
      sessionId: z.string().optional(),
      instanceId: z.string().min(1),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.set_script_source": z
    .object({
      sessionId: z.string().optional(),
      instanceId: z.string().min(1),
      source: z.string().max(262144),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.batch": z
    .object({
      sessionId: z.string().optional(),
      operations: z
        .array(
          z
            .object({
              operation: z.string().min(1),
              params: z.record(z.string(), z.unknown()).optional(),
            })
            .strict(),
        )
        .min(1)
        .max(32),
      stopOnError: z.boolean().optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.playtest_start": z
    .object({
      sessionId: z.string().optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.playtest_stop": z
    .object({
      sessionId: z.string().optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.terrain_fill_block": z
    .object({
      sessionId: z.string().optional(),
      material: z.string().optional(),
      size: z
        .object({ type: z.literal("Vector3"), x: z.number(), y: z.number(), z: z.number() })
        .strict()
        .optional(),
      position: z
        .object({ type: z.literal("Vector3"), x: z.number(), y: z.number(), z: z.number() })
        .strict()
        .optional(),
      orientation: z
        .object({ type: z.literal("Vector3"), x: z.number(), y: z.number(), z: z.number() })
        .strict()
        .optional(),
      cframe: z.record(z.string(), z.unknown()).optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.terrain_fill_ball": z
    .object({
      sessionId: z.string().optional(),
      material: z.string().optional(),
      radius: z.number().positive().max(2048).optional(),
      center: z
        .object({ type: z.literal("Vector3"), x: z.number(), y: z.number(), z: z.number() })
        .strict()
        .optional(),
      position: z
        .object({ type: z.literal("Vector3"), x: z.number(), y: z.number(), z: z.number() })
        .strict()
        .optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.terrain_clear": z
    .object({
      sessionId: z.string().optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.insert_asset": z
    .object({
      sessionId: z.string().optional(),
      assetId: z.union([z.number().int().positive(), z.string().min(1)]),
      parentId: z.string().min(1).optional(),
      parentPath: z.string().min(1).optional(),
      name: z.string().optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.import_local_model": z
    .object({
      sessionId: z.string().optional(),
      path: z.string().min(1).optional(),
      file: z.string().min(1).optional(),
      parentId: z.string().min(1).optional(),
      parentPath: z.string().min(1).optional(),
      name: z.string().optional(),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.publish_place": z
    .object({
      sessionId: z.string().optional(),
      confirm: z.literal(true),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
  "roblox.execute_luau": z
    .object({
      sessionId: z.string().optional(),
      source: z.string().min(1).max(16384),
      confirm: z.literal(true),
      timeoutSeconds: z.number().int().positive().max(180).optional(),
    })
    .strict(),
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
      maxWidth: z.number().int().positive().optional(),
      format: z.enum(["jpeg", "jpg", "png"]).optional(),
      returnBase64: z.boolean().optional(),
    })
    .strict(),
  "vision.capture_window": z
    .object({
      windowId: z.string().min(1),
      visionReason: z.string().optional(),
      vision_reason: z.string().optional(),
      maxWidth: z.number().int().positive().optional(),
      format: z.enum(["jpeg", "jpg", "png"]).optional(),
      returnBase64: z.boolean().optional(),
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
      maxWidth: z.number().int().positive().optional(),
      format: z.enum(["jpeg", "jpg", "png"]).optional(),
      returnBase64: z.boolean().optional(),
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
  "monitor.list": "List connected monitors with bounds, work area, primary flag, and DPI scale.",
  "window.list": "List top-level windows.",
  "window.get": "Get a single window by id with bounds, restore bounds, monitor index, and show state.",
  "window.focus": "Focus a window by handle id.",
  "window.minimize": "Minimize a window by id.",
  "window.maximize": "Maximize a window by id.",
  "window.restore": "Restore a minimized or maximized window by id.",
  "window.move": "Move a window using virtual coordinates and/or monitor index; optional placement maximize.",
  "window.resize":
    "Resize a window to explicit width/height in physical pixels; optional x/y reposition with omitted axes preserving current position.",
  "window.wait_for": "Wait until a window matching process/title/titleRegex appears.",
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
  "system.performance": "Return local in-process timing aggregates (count, success/failure, p50/p95) by operation.",
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
  "adapter.execute": "Execute an application-adapter action (Blender/VS Code/Visual Studio/Roblox Studio/Discord).",
  "blender.open": "Open a .blend file. Default mode background validates headlessly; mode gui launches visible Blender.",
  "blender.get_scene": "Inspect the Blender scene via Python scripting.",
  "blender.get_objects": "List Blender objects via Python scripting.",
  "blender.select_object": "Select a Blender object by name via Python.",
  "blender.execute_python": "Run a Blender Python snippet (background unrestricted; live bridge requires confirm=true, 32KB max, restricted builtins).",
  "blender.export": "Export the Blender scene (obj/fbx/gltf/stl) via Python.",
  "blender.save": "Save the Blender file via Python.",
  "blender.batch": "Run 1..32 allowlisted Blender operations in one background process.",
  "blender.render": "Render still frame to an absolute output path (background or live bridge).",
  "blender.import_mesh": "Import obj/fbx/gltf/glb/stl mesh (background or live bridge).",
  "blender.create_mesh": "Create an allowlisted mesh primitive (cube, sphere, cylinder, …).",
  "blender.export_for_roblox": "Export with Roblox-oriented FBX/OBJ/glTF presets (apply rotation/scale).",
  "blender.mesh_extrude": "Extrude selected mesh region (optionally by indices) along Z by value.",
  "blender.mesh_inset": "Inset selected faces with thickness/depth.",
  "blender.mesh_bevel": "Bevel selected edges/verts with offset and segments.",
  "blender.mesh_loop_cut": "Loop-cut (or subdivide fallback) with optional edgeIndex.",
  "blender.modifier_boolean": "Boolean UNION/DIFFERENCE/INTERSECT with a target mesh (apply optional).",
  "blender.modifier_mirror": "Mirror modifier on X/Y/Z (apply optional).",
  "blender.modifier_array": "Array modifier with count and relative offset (apply optional).",
  "blender.material_set": "Assign/create a Principled BSDF material with base color/roughness/metallic.",
  "blender.uv_unwrap": "UV unwrap (ANGLE_BASED, CONFORMAL, or SMART).",
  "blender.select_geometry": "Select verts/edges/faces by indices (or selectAll) on a named mesh.",
  "vscode.open_file": "Open a file in VS Code via the code CLI.",
  "vscode.open_folder": "Open a folder/workspace in VS Code via the code CLI.",
  "vscode.execute_command": "Attempt a VS Code CLI --command invocation.",
  "vscode.get_workspace": "Return workspace path metadata for VS Code.",
  "visualstudio.get_solution": "Parse Visual Studio solution projects from a .sln path.",
  "visualstudio.build": "Build a solution via MSBuild or devenv.",
  "visualstudio.open_file": "Open a file in Visual Studio (devenv /Edit).",
  "visualstudio.open_solution": "Open a .sln in Visual Studio (devenv).",
  "discord.open": "Launch or focus Discord; optional monitor/placement.",
  "discord.join_voice": "Join a Discord voice channel via Ctrl+K quick switch (best-effort title verify).",
  "discord.quick_switch": "Focus Discord and run Ctrl+K quick switch for a query (no join verify).",
  "roblox.open_place": "Launch Roblox Studio with an absolute .rbxl/.rbxlx place path.",
  "roblox.plugin_ping": "Report Roblox bridge listener and Studio plugin connection state.",
  "roblox.get_hierarchy": "Get a bounded instance hierarchy from the connected Roblox Studio plugin.",
  "roblox.get_selection": "Get selected instances from the connected Roblox Studio plugin.",
  "roblox.select": "Select instances by session-scoped IDs via the Roblox Studio plugin.",
  "roblox.set_property": "Set an allowlisted property on a Roblox instance via the Studio plugin.",
  "roblox.create_instance": "Create an allowlisted Roblox instance under a parent (by id or path).",
  "roblox.destroy_instance": "Destroy a non-service Roblox instance by session-scoped id.",
  "roblox.clone_instance": "Clone a Roblox instance, optionally under a new parent.",
  "roblox.set_parent": "Reparent a Roblox instance (dedicated Parent mutation).",
  "roblox.find_instances": "Find instances by class/name/path without dumping the full hierarchy.",
  "roblox.get_script_source": "Read Source from Script/LocalScript/ModuleScript.",
  "roblox.set_script_source": "Write Source on Script/LocalScript/ModuleScript (max 256KB; no free Luau exec).",
  "roblox.batch": "Run up to 32 allowlisted Roblox mutations/reads in one plugin round-trip.",
  "roblox.playtest_start": "Start Studio play solo via the plugin.",
  "roblox.playtest_stop": "Stop Studio play solo via the plugin.",
  "roblox.terrain_fill_block": "Fill terrain with a material using FillBlock (CFrame + size).",
  "roblox.terrain_fill_ball": "Fill terrain with a material using FillBall (center + radius).",
  "roblox.terrain_clear": "Clear all workspace Terrain voxels.",
  "roblox.insert_asset": "Insert a Toolbox/cloud asset by assetId via InsertService:LoadAsset.",
  "roblox.import_local_model": "Import a local .rbxm/.rbxmx via InsertService:LoadLocalAsset (not FBX).",
  "roblox.publish_place": "Best-effort publish/prompt-publish (requires confirm=true).",
  "roblox.execute_luau": "Gated edge-case Luau exec (confirm=true, max 16KB). Prefer structured tools.",
  "input.mouse_move": "Fallback: move the mouse to physical screen coordinates (DPI/virtual-desktop aware).",
  "input.mouse_click": "Fallback: click at physical screen coordinates via SendInput.",
  "input.mouse_drag": "Fallback: drag between physical screen coordinates via SendInput.",
  "input.scroll": "Fallback: scroll vertically or horizontally via SendInput.",
  "input.key": "Fallback: press/down/up a key via SendInput.",
  "input.hotkey": "Fallback: chord hotkey via SendInput (keys array or '+'-joined).",
  "input.type": "Fallback: type unicode text via SendInput.",
  "vision.capture_screen": "Capture screen/monitor; returns PNG path + JPEG thumbnail by default (set returnBase64=true for legacy inline).",
  "vision.capture_window": "Capture a window; returns PNG path + JPEG thumbnail by default (set returnBase64=true for legacy inline).",
  "vision.capture_region": "Capture a screen region; returns PNG path + JPEG thumbnail by default (set returnBase64=true for legacy inline).",
};

export function parseToolArgs<T extends ToolName>(
  name: T,
  args: unknown,
): z.infer<(typeof toolSchemas)[T]> {
  return toolSchemas[name].parse(args ?? {}) as z.infer<(typeof toolSchemas)[T]>;
}
