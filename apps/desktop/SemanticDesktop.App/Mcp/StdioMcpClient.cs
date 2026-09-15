using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SemanticDesktop.App.Providers;
using SemanticDesktop.App.Tools;

namespace SemanticDesktop.App.Mcp;

public sealed class StdioMcpClient : IMcpClient, IAsyncDisposable
{
    private readonly string _command;
    private readonly IReadOnlyList<string> _args;
    private readonly IReadOnlyDictionary<string, string>? _env;
    private Process? _process;
    private int _nextId = 1;
    private readonly SemaphoreSlim _io = new(1, 1);
    private bool _initialized;

    public StdioMcpClient(string command, IReadOnlyList<string>? args = null, IReadOnlyDictionary<string, string>? env = null)
    {
        _command = command;
        _args = args ?? Array.Empty<string>();
        _env = env;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = _command,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in _args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (_env is not null)
        {
            foreach (var (key, value) in _env)
            {
                psi.Environment[key] = value;
            }
        }

        _process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start MCP process '{_command}'.");
        await RpcAsync("initialize", new
        {
            protocolVersion = "2024-11-05",
            capabilities = new { },
            clientInfo = new { name = "DesktopUseAgent", version = "1.0" }
        }, cancellationToken).ConfigureAwait(false);
        await NotifyAsync("notifications/initialized", new { }, cancellationToken).ConfigureAwait(false);
        _initialized = true;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _initialized = false;
        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }

        await Task.CompletedTask.ConfigureAwait(false);
        _process.Dispose();
        _process = null;
    }

    public async Task<IReadOnlyList<AgentToolDefinition>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var result = await RpcAsync("tools/list", new { }, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<AgentToolDefinition>();
        }

        var list = new List<AgentToolDefinition>();
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var description = tool.TryGetProperty("description", out var d) ? d.GetString() ?? name : name;
            JsonElement schema = tool.TryGetProperty("inputSchema", out var s) ? s.Clone() : JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
            list.Add(new AgentToolDefinition
            {
                Name = name,
                Description = description,
                JsonSchema = schema
            });
        }

        return list;
    }

    public async Task<ToolRouterResult> CallToolAsync(string name, JsonElement arguments, CancellationToken cancellationToken = default)
    {
        if (ToolCatalog.IsComputerControl(name))
        {
            return ToolRouterResult.Denied("External MCP cannot execute DesktopUseAgent computer-control tools.");
        }

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        object argsObj;
        try
        {
            argsObj = JsonSerializer.Deserialize<JsonElement>(arguments.GetRawText());
        }
        catch
        {
            argsObj = new { };
        }

        var result = await RpcAsync("tools/call", new { name, arguments = argsObj }, cancellationToken).ConfigureAwait(false);
        var isError = result.TryGetProperty("isError", out var errFlag) && errFlag.ValueKind == JsonValueKind.True;
        var summary = Summarize(result);
        return isError
            ? ToolRouterResult.Fail(summary)
            : ToolRouterResult.Ok(summary);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _io.Dispose();
    }

    private async Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureProcess();
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters });
            await WriteMessageAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
    }

    private async Task<JsonElement> RpcAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureProcess();
            var id = _nextId++;
            var payload = JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
            await WriteMessageAsync(payload, cancellationToken).ConfigureAwait(false);
            var response = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() : error.GetRawText();
                throw new InvalidOperationException($"MCP {method} failed: {message}");
            }

            return doc.RootElement.TryGetProperty("result", out var result) ? result.Clone() : doc.RootElement.Clone();
        }
        finally
        {
            _io.Release();
        }
    }

    private void EnsureProcess()
    {
        if (_process is null || _process.HasExited || _process.StandardInput is null || _process.StandardOutput is null)
        {
            throw new InvalidOperationException("MCP process is not running.");
        }
    }

    private async Task WriteMessageAsync(string json, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = $"Content-Length: {bytes.Length}\r\n\r\n";
        await _process!.StandardInput.WriteAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var stdout = _process!.StandardOutput.BaseStream;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lineBuffer = new MemoryStream();
        while (true)
        {
            var line = await ReadLineAsync(stdout, lineBuffer, cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                throw new InvalidOperationException("MCP process closed stdout.");
            }

            if (line.Length == 0)
            {
                break;
            }

            var idx = line.IndexOf(':');
            if (idx > 0)
            {
                headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
            }
        }

        if (!headers.TryGetValue("Content-Length", out var lenText) || !int.TryParse(lenText, out var length))
        {
            throw new InvalidOperationException("MCP response missing Content-Length.");
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stdout.ReadAsync(buffer.AsMemory(read, length - read), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                throw new InvalidOperationException("MCP process closed stdout mid-message.");
            }

            read += n;
        }

        return Encoding.UTF8.GetString(buffer);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, MemoryStream buffer, CancellationToken cancellationToken)
    {
        buffer.SetLength(0);
        var b = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(b, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (b[0] == (byte)'\n')
            {
                break;
            }

            if (b[0] != (byte)'\r')
            {
                buffer.WriteByte(b[0]);
            }
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Summarize(JsonElement result)
    {
        var raw = result.GetRawText();
        return raw.Length <= 500 ? raw : raw[..500] + "…";
    }
}
