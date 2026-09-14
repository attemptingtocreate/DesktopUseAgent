import { createConnection, type Socket } from "node:net";
import { randomUUID } from "node:crypto";

export const DEFAULT_PIPE_NAME = "semantic-desktop-agent";

export function resolvePipeName(env: NodeJS.ProcessEnv = process.env): string {
  const override = env.SEMANTIC_DESKTOP_PIPE?.trim();
  return override && override.length > 0 ? override : DEFAULT_PIPE_NAME;
}

export function toWindowsPipePath(pipeName: string): string {
  if (pipeName.startsWith("\\\\.\\pipe\\") || pipeName.startsWith("//./pipe/")) {
    return pipeName;
  }
  return `\\\\.\\pipe\\${pipeName}`;
}

export interface AgentRpcRequest {
  id: string;
  method: string;
  params?: Record<string, unknown>;
}

export interface AgentClientOptions {
  pipeName?: string;
  connectTimeoutMs?: number;
  responseTimeoutMs?: number;
}

/**
 * JSON-line RPC client for the Windows Agent named pipe.
 * Prefer connect → send one request → read one line → close (agent MaxInstances=1).
 */
export class AgentClient {
  private readonly pipePath: string;
  private readonly connectTimeoutMs: number;
  private readonly responseTimeoutMs: number;

  constructor(options: AgentClientOptions = {}) {
    const pipeName = options.pipeName ?? resolvePipeName();
    this.pipePath = toWindowsPipePath(pipeName);
    this.connectTimeoutMs = options.connectTimeoutMs ?? 5_000;
    this.responseTimeoutMs = options.responseTimeoutMs ?? 60_000;
  }

  get path(): string {
    return this.pipePath;
  }

  async send(method: string, params: Record<string, unknown> = {}): Promise<unknown> {
    const request: AgentRpcRequest = {
      id: randomUUID().replace(/-/g, ""),
      method,
      params,
    };

    const socket = await this.connect();
    try {
      const payload = `${JSON.stringify(request)}\n`;
      await writeAll(socket, Buffer.from(payload, "utf8"));
      const line = await readLine(socket, this.responseTimeoutMs);
      if (line === null) {
        throw new Error("Agent closed the pipe before sending a response.");
      }
      return JSON.parse(line) as unknown;
    } finally {
      socket.destroy();
    }
  }

  private connect(): Promise<Socket> {
    return new Promise((resolve, reject) => {
      const socket = createConnection(this.pipePath);
      let settled = false;

      const fail = (err: Error) => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        socket.destroy();
        reject(err);
      };

      const timer = setTimeout(() => {
        fail(new Error(`Timed out connecting to pipe '${this.pipePath}'.`));
      }, this.connectTimeoutMs);

      socket.once("connect", () => {
        if (settled) return;
        settled = true;
        clearTimeout(timer);
        socket.setEncoding("utf8");
        resolve(socket);
      });

      socket.once("error", (err: Error) => {
        fail(err);
      });
    });
  }
}

function writeAll(socket: Socket, data: Buffer): Promise<void> {
  return new Promise((resolve, reject) => {
    socket.write(data, (err) => {
      if (err) reject(err);
      else resolve();
    });
  });
}

function readLine(socket: Socket, timeoutMs: number): Promise<string | null> {
  return new Promise((resolve, reject) => {
    let buffer = "";
    let settled = false;

    const cleanup = () => {
      clearTimeout(timer);
      socket.off("data", onData);
      socket.off("error", onError);
      socket.off("end", onEnd);
      socket.off("close", onClose);
    };

    const finish = (value: string | null, err?: Error) => {
      if (settled) return;
      settled = true;
      cleanup();
      if (err) reject(err);
      else resolve(value);
    };

    const timer = setTimeout(() => {
      finish(null, new Error("Timed out waiting for agent response."));
    }, timeoutMs);

    const onData = (chunk: string | Buffer) => {
      buffer += typeof chunk === "string" ? chunk : chunk.toString("utf8");
      const nl = buffer.indexOf("\n");
      if (nl >= 0) {
        const line = buffer.slice(0, nl).replace(/\r$/, "");
        finish(line);
      }
    };

    const onError = (err: Error) => finish(null, err);
    const onEnd = () => {
      if (buffer.length > 0) {
        finish(buffer.replace(/\r$/, ""));
      } else {
        finish(null);
      }
    };
    const onClose = () => {
      if (!settled) {
        if (buffer.length > 0) finish(buffer.replace(/\r$/, ""));
        else finish(null);
      }
    };

    socket.on("data", onData);
    socket.once("error", onError);
    socket.once("end", onEnd);
    socket.once("close", onClose);
  });
}
