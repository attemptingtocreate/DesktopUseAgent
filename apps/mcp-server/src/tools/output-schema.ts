import { z } from "zod";

const errorSchema = z
  .object({
    code: z.string(),
    message: z.string(),
    retryable: z.boolean(),
    details: z.record(z.unknown()).nullable().optional(),
  })
  .strict();

const resultMetaSchema = z
  .object({
    requestId: z.string(),
    startedAt: z.string(),
    completedAt: z.string(),
    durationMs: z.number().nonnegative(),
  })
  .strict();

const performanceMetaSchema = z
  .object({
    operation: z.string(),
    durationMs: z.number().nonnegative(),
    elementsInspected: z.number().int().nonnegative(),
    cacheHit: z.boolean().nullable().optional(),
    provider: z.string(),
    fallbackReason: z.string().nullable().optional(),
    visionReason: z.string().nullable().optional(),
  })
  .strict();

/**
 * Every Windows-agent command returns this stable envelope. Individual tools put
 * their command-specific payload in `data`, whose shape intentionally remains
 * open because it is produced by many independently versioned adapters.
 */
export const toolOutputSchema = z
  .object({
    ok: z.boolean(),
    data: z.unknown().nullable().optional(),
    error: errorSchema.nullable().optional(),
    meta: resultMetaSchema,
    stateChanged: z.boolean().nullable().optional(),
    warnings: z.array(z.string()).nullable().optional(),
    performance: performanceMetaSchema.nullable().optional(),
  })
  .strict();

export type ToolOutput = z.infer<typeof toolOutputSchema>;

export function parseToolOutput(value: unknown): ToolOutput {
  return toolOutputSchema.parse(value);
}
