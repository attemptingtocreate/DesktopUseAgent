import { createServer as createNetServer, type Socket } from "node:net";
import { describe, expect, it } from "vitest";
import { AgentClient, resolvePipeName, toWindowsPipePath } from "../src/agent-client.js";

describe("AgentClient", () => {
  it("maps pipe names to Windows pipe paths", () => {
    expect(toWindowsPipePath("semantic-desktop-agent")).toBe(
      "\\\\.\\pipe\\semantic-desktop-agent",
    );
    expect(toWindowsPipePath("\\\\.\\pipe\\custom")).toBe("\\\\.\\pipe\\custom");
  });

  it("resolves SEMANTIC_DESKTOP_PIPE override", () => {
    expect(resolvePipeName({})).toBe("semantic-desktop-agent");
    expect(resolvePipeName({ SEMANTIC_DESKTOP_PIPE: "custom-pipe" })).toBe("custom-pipe");
  });

  it("resolves DESKTOPUSEAGENT_PIPE before SEMANTIC_DESKTOP_PIPE", () => {
    expect(resolvePipeName({ DESKTOPUSEAGENT_PIPE: "new-pipe" })).toBe("new-pipe");
    expect(
      resolvePipeName({
        DESKTOPUSEAGENT_PIPE: "new-pipe",
        SEMANTIC_DESKTOP_PIPE: "legacy-pipe",
      }),
    ).toBe("new-pipe");
    expect(resolvePipeName({ DESKTOPUSEAGENT_PIPE: "  " })).toBe("semantic-desktop-agent");
  });

  it("round-trips JSON-line RPC over a mock named-pipe server", async () => {
    const pipeName = `sd-mcp-test-${process.pid}-${Date.now()}`;
    const pipePath = toWindowsPipePath(pipeName);

    const server = createNetServer();
    const requestPromise = new Promise<{ method: string; params: unknown }>((resolve, reject) => {
      server.on("connection", (socket: Socket) => {
        let buffer = "";
        socket.setEncoding("utf8");
        socket.on("data", (chunk: string) => {
          buffer += chunk;
          const nl = buffer.indexOf("\n");
          if (nl < 0) return;
          try {
            const request = JSON.parse(buffer.slice(0, nl)) as {
              id: string;
              method: string;
              params?: unknown;
            };
            resolve({ method: request.method, params: request.params ?? {} });
            const response = JSON.stringify({
              ok: true,
              data: { echoed: request.method },
              meta: {
                requestId: request.id,
                startedAt: new Date().toISOString(),
                completedAt: new Date().toISOString(),
                durationMs: 1,
              },
            });
            socket.write(`${response}\n`);
          } catch (err) {
            reject(err);
          }
        });
      });
    });

    await new Promise<void>((resolve, reject) => {
      server.listen(pipePath, (err?: Error) => (err ? reject(err) : resolve()));
    });

    try {
      const client = new AgentClient({ pipeName, connectTimeoutMs: 3_000 });
      expect(client.path).toBe(pipePath);

      const resultPromise = client.send("window.list", {});
      const seen = await requestPromise;
      const result = (await resultPromise) as {
        ok: boolean;
        data: { echoed: string };
      };

      expect(seen.method).toBe("window.list");
      expect(seen.params).toEqual({});
      expect(result.ok).toBe(true);
      expect(result.data.echoed).toBe("window.list");
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });

  it("can be unit-tested by mocking send", async () => {
    const client = new AgentClient({ pipeName: "unused-pipe" });
    const mockResult = {
      ok: true,
      data: { windows: [] },
      meta: { requestId: "abc", startedAt: "", completedAt: "", durationMs: 0 },
    };
    client.send = async (method: string, params: Record<string, unknown> = {}) => {
      expect(method).toBe("desktop.get_state");
      expect(params).toEqual({});
      return mockResult;
    };

    await expect(client.send("desktop.get_state", {})).resolves.toEqual(mockResult);
  });
});
