using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Serialization;

namespace SemanticDesktop.Adapters.RobloxStudio;

public sealed class RobloxStudioBridge : IRobloxBridge
{
    private readonly RobloxBridgeOptions _options;
    private readonly byte[] _tokenBytes;
    private readonly ConcurrentDictionary<string, RobloxBridgeSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _requestGate;
    private readonly object _listenerGate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private Task? _cleanupLoop;
    private int _activeRequests;
    private string? _startError;

    public RobloxStudioBridge(RobloxBridgeOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrWhiteSpace(options.Token))
        {
            throw new ArgumentException("Bridge token is required.", nameof(options));
        }

        _tokenBytes = Encoding.UTF8.GetBytes(options.Token);
        Port = options.Port;
        _requestGate = new SemaphoreSlim(Math.Max(1, options.MaxConcurrentRequests), Math.Max(1, options.MaxConcurrentRequests));
    }

    public bool IsListening { get; private set; }

    public int Port { get; }

    public RobloxBridgeHealth GetHealth()
    {
        PurgeExpiredSessions(DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        var sessions = _sessions.Values
            .Where(s => now - s.LastSeenUtc <= _options.SessionHeartbeatTimeout)
            .Select(s => new RobloxBridgeSessionInfo
            {
                SessionId = s.SessionId,
                StudioVersion = s.StudioVersion,
                PlaceName = s.PlaceName,
                PollingEnabled = s.PollingEnabled,
                LastSeenUtc = s.LastSeenUtc,
                HasOutstandingCommand = s.OutstandingCommand is not null
            })
            .OrderBy(s => s.SessionId, StringComparer.Ordinal)
            .ToList();

        return new RobloxBridgeHealth
        {
            Listening = IsListening,
            PluginConnected = sessions.Any(s => s.PollingEnabled),
            Port = Port,
            StartError = _startError,
            Sessions = sessions
        };
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_listenerGate)
        {
            if (IsListening)
            {
                return Task.CompletedTask;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://{_options.Host}:{Port}/");
            try
            {
                _listener.Start();
                _startError = null;
            }
            catch (HttpListenerException ex)
            {
                _listener.Close();
                _listener = null;
                _startError = $"Roblox bridge port {Port} is unavailable: {ex.Message}";
                IsListening = false;
                return Task.CompletedTask;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loop = Task.Run(() => ListenAsync(_cts.Token));
            _cleanupLoop = Task.Run(() => CleanupLoopAsync(_cts.Token));
            IsListening = true;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? loop;
        Task? cleanupLoop;
        HttpListener? listener;
        CancellationTokenSource? cts;
        lock (_listenerGate)
        {
            if (!IsListening)
            {
                return;
            }

            loop = _loop;
            cleanupLoop = _cleanupLoop;
            listener = _listener;
            cts = _cts;
            IsListening = false;
            _loop = null;
            _cleanupLoop = null;
            _listener = null;
            _cts = null;
        }

        cts?.Cancel();
        listener?.Stop();
        listener?.Close();
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch
            {
                // expected on shutdown
            }
        }

        if (cleanupLoop is not null)
        {
            try
            {
                await cleanupLoop.ConfigureAwait(false);
            }
            catch
            {
                // expected on shutdown
            }
        }

        cts?.Dispose();
        FailAllPending(ErrorCodes.Cancelled, "Roblox bridge stopped.");
    }

    public async Task<RobloxCommandResult> ExecuteCommandAsync(
        string operation,
        Dictionary<string, object?>? parameters,
        string? sessionId,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!IsListening)
        {
            return RobloxCommandResult.Fail(ErrorCodes.AdapterUnavailable, "Roblox bridge is not listening.");
        }

        if (!RobloxBridgeOperations.Allowlist.Contains(operation))
        {
            return RobloxCommandResult.Fail(ErrorCodes.Unsupported, $"Operation '{operation}' is not allowed.");
        }

        var session = ResolveSession(sessionId);
        if (session is null)
        {
            return RobloxCommandResult.Fail(ErrorCodes.AdapterUnavailable, "No connected Roblox Studio plugin session.");
        }

        if (!session.PollingEnabled)
        {
            return RobloxCommandResult.Fail(ErrorCodes.AdapterUnavailable, "Roblox Studio plugin polling is not enabled.");
        }

        var command = new RobloxBridgeCommand
        {
            Id = Guid.NewGuid().ToString("N"),
            Operation = operation,
            Params = parameters
        };

        TaskCompletionSource<RobloxCommandResult> tcs;
        lock (session.Gate)
        {
            if (session.OutstandingCommand is not null)
            {
                return RobloxCommandResult.Fail(ErrorCodes.InvalidArgument, "Roblox Studio session already has an outstanding command.");
            }

            tcs = new TaskCompletionSource<RobloxCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.OutstandingCommand = command;
            session.PendingResult = tcs;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        await using var reg = timeoutCts.Token.Register(() =>
        {
            lock (session.Gate)
            {
                if (session.OutstandingCommand?.Id == command.Id)
                {
                    session.OutstandingCommand = null;
                    session.PendingResult = null;
                }
            }

            tcs.TrySetResult(RobloxCommandResult.Fail(ErrorCodes.Timeout, "Timed out waiting for Roblox Studio plugin response."));
        });

        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            reg.Dispose();
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _requestGate.Dispose();
    }

    internal static async Task<(bool Ok, byte[] Body, HttpStatusCode? RejectStatus)> ReadBoundedBodyAsync(
        HttpListenerRequest request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (!request.HasEntityBody)
        {
            return (true, Array.Empty<byte>(), null);
        }

        if (request.ContentLength64 > maxBytes)
        {
            return (false, Array.Empty<byte>(), HttpStatusCode.RequestEntityTooLarge);
        }

        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var read = await request.InputStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (ms.Length + read > maxBytes)
            {
                return (false, Array.Empty<byte>(), HttpStatusCode.RequestEntityTooLarge);
            }

            ms.Write(buffer, 0, read);
        }

        return (true, ms.ToArray(), null);
    }

    private RobloxBridgeSession? ResolveSession(string? sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            if (!RobloxBridgeSecurity.TryNormalizeSessionId(sessionId, out var normalized, out _))
            {
                return null;
            }

            if (_sessions.TryGetValue(normalized!, out var exact) && now - exact.LastSeenUtc <= _options.SessionHeartbeatTimeout)
            {
                return exact;
            }

            return null;
        }

        return _sessions.Values
            .Where(s => s.PollingEnabled && now - s.LastSeenUtc <= _options.SessionHeartbeatTimeout)
            .OrderByDescending(s => s.LastSeenUtc)
            .FirstOrDefault();
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListener? listener;
            lock (_listenerGate)
            {
                listener = _listener;
            }

            if (listener is null)
            {
                break;
            }

            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                if (!IsListening)
                {
                    break;
                }

                continue;
            }

            if (!await _requestGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    ctx.Response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    ctx.Response.Close();
                }
                catch
                {
                    // ignore
                }

                continue;
            }

            Interlocked.Increment(ref _activeRequests);
            _ = Task.Run(async () =>
            {
                try
                {
                    await HandleRequestAsync(ctx, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeRequests);
                    _requestGate.Release();
                }
            }, CancellationToken.None);
        }
    }

    private async Task CleanupLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.SessionCleanupInterval, cancellationToken).ConfigureAwait(false);
                PurgeExpiredSessions(DateTimeOffset.UtcNow);
            }
            catch when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private void PurgeExpiredSessions(DateTimeOffset now)
    {
        foreach (var pair in _sessions)
        {
            if (now - pair.Value.LastSeenUtc <= _options.SessionHeartbeatTimeout)
            {
                continue;
            }

            if (!_sessions.TryRemove(pair.Key, out var session))
            {
                continue;
            }

            TaskCompletionSource<RobloxCommandResult>? tcs;
            lock (session.Gate)
            {
                tcs = session.PendingResult;
                session.OutstandingCommand = null;
                session.PendingResult = null;
            }

            tcs?.TrySetResult(RobloxCommandResult.Fail(ErrorCodes.Timeout, "Roblox Studio session expired."));
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext ctx, CancellationToken cancellationToken)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
            if (!ValidateToken(ctx.Request))
            {
                await WriteJsonAsync(ctx, HttpStatusCode.Unauthorized, new { ok = false, error = "unauthorized" }).ConfigureAwait(false);
                return;
            }

            if (ctx.Request.HttpMethod.Equals("GET", StringComparison.OrdinalIgnoreCase))
            {
                if (path.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/v1/health", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleHealthAsync(ctx).ConfigureAwait(false);
                    return;
                }

                if (path.Equals("/poll", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/v1/poll", StringComparison.OrdinalIgnoreCase))
                {
                    await HandlePollAsync(ctx, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else if (ctx.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                var (ok, body, rejectStatus) = await ReadBoundedBodyAsync(
                    ctx.Request,
                    _options.MaxRequestBodyBytes,
                    cancellationToken).ConfigureAwait(false);
                if (!ok)
                {
                    await WriteJsonAsync(ctx, rejectStatus ?? HttpStatusCode.RequestEntityTooLarge, new { ok = false, error = "body_too_large" })
                        .ConfigureAwait(false);
                    return;
                }

                if (path.Equals("/register", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/v1/register", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleRegisterAsync(ctx, body).ConfigureAwait(false);
                    return;
                }

                if (path.Equals("/heartbeat", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/v1/heartbeat", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleHeartbeatAsync(ctx, body).ConfigureAwait(false);
                    return;
                }

                if (path.Equals("/result", StringComparison.OrdinalIgnoreCase) ||
                    path.Equals("/v1/result", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleResultAsync(ctx, body).ConfigureAwait(false);
                    return;
                }
            }

            await WriteJsonAsync(ctx, HttpStatusCode.NotFound, new { ok = false, error = "not_found" }).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                ctx.Response.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task HandleHealthAsync(HttpListenerContext ctx)
    {
        var health = GetHealth();
        await WriteJsonAsync(ctx, HttpStatusCode.OK, new
        {
            ok = true,
            listening = health.Listening,
            pluginConnected = health.PluginConnected,
            port = health.Port,
            sessions = health.Sessions.Select(s => new
            {
                sessionId = s.SessionId,
                studioVersion = s.StudioVersion,
                placeName = s.PlaceName,
                pollingEnabled = s.PollingEnabled,
                lastSeenUtc = s.LastSeenUtc,
                hasOutstandingCommand = s.HasOutstandingCommand
            })
        }).ConfigureAwait(false);
    }

    private async Task HandleRegisterAsync(HttpListenerContext ctx, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sessionIdRaw = GetString(root, "sessionId");
        if (!RobloxBridgeSecurity.TryNormalizeSessionId(sessionIdRaw, out var sessionId, out var sessionError))
        {
            await WriteJsonAsync(ctx, HttpStatusCode.BadRequest, new { ok = false, error = sessionError }).ConfigureAwait(false);
            return;
        }

        var session = _sessions.GetOrAdd(sessionId!, id => new RobloxBridgeSession { SessionId = id });
        lock (session.Gate)
        {
            session.StudioVersion = TruncateMetadata(GetString(root, "studioVersion"));
            session.PlaceName = TruncateMetadata(GetString(root, "placeName"));
            session.PollingEnabled = GetBool(root, "pollingEnabled") ?? false;
            session.LastSeenUtc = DateTimeOffset.UtcNow;
        }

        await WriteJsonAsync(ctx, HttpStatusCode.OK, new
        {
            ok = true,
            sessionId,
            pollingEnabled = session.PollingEnabled,
            port = Port
        }).ConfigureAwait(false);
    }

    private async Task HandleHeartbeatAsync(HttpListenerContext ctx, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var sessionIdRaw = GetString(doc.RootElement, "sessionId");
        if (!RobloxBridgeSecurity.TryNormalizeSessionId(sessionIdRaw, out var sessionId, out var sessionError) ||
            !_sessions.TryGetValue(sessionId!, out var session))
        {
            await WriteJsonAsync(ctx, HttpStatusCode.NotFound, new { ok = false, error = sessionError ?? "session_not_found" }).ConfigureAwait(false);
            return;
        }

        lock (session.Gate)
        {
            session.LastSeenUtc = DateTimeOffset.UtcNow;
            session.PlaceName = TruncateMetadata(GetString(doc.RootElement, "placeName")) ?? session.PlaceName;
        }

        await WriteJsonAsync(ctx, HttpStatusCode.OK, new { ok = true }).ConfigureAwait(false);
    }

    private async Task HandlePollAsync(HttpListenerContext ctx, CancellationToken cancellationToken)
    {
        var sessionIdRaw = ctx.Request.QueryString["sessionId"];
        if (!RobloxBridgeSecurity.TryNormalizeSessionId(sessionIdRaw, out var sessionId, out var sessionError) ||
            !_sessions.TryGetValue(sessionId!, out var session))
        {
            await WriteJsonAsync(ctx, HttpStatusCode.NotFound, new { ok = false, error = sessionError ?? "session_not_found" }).ConfigureAwait(false);
            return;
        }

        lock (session.Gate)
        {
            session.LastSeenUtc = DateTimeOffset.UtcNow;
        }

        var deadline = DateTimeOffset.UtcNow + _options.PollTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RobloxBridgeCommand? command;
            lock (session.Gate)
            {
                command = session.OutstandingCommand;
                if (command is not null && DateTimeOffset.UtcNow - command.CreatedUtc > _options.CommandTtl)
                {
                    session.OutstandingCommand = null;
                    session.PendingResult?.TrySetResult(RobloxCommandResult.Fail(ErrorCodes.Timeout, "Command expired before plugin polled."));
                    session.PendingResult = null;
                    command = null;
                }
            }

            if (command is not null)
            {
                await WriteJsonAsync(ctx, HttpStatusCode.OK, new
                {
                    ok = true,
                    command = new
                    {
                        id = command.Id,
                        operation = command.Operation,
                        @params = command.Params ?? new Dictionary<string, object?>()
                    }
                }).ConfigureAwait(false);
                return;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        await WriteJsonAsync(ctx, HttpStatusCode.OK, new { ok = true, command = (object?)null }).ConfigureAwait(false);
    }

    private Task HandleResultAsync(HttpListenerContext ctx, byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var sessionIdRaw = GetString(root, "sessionId");
        var commandId = GetString(root, "commandId");
        if (!RobloxBridgeSecurity.TryNormalizeSessionId(sessionIdRaw, out var sessionId, out var sessionError) ||
            string.IsNullOrWhiteSpace(commandId) ||
            !_sessions.TryGetValue(sessionId!, out var session))
        {
            return WriteJsonAsync(ctx, HttpStatusCode.BadRequest, new { ok = false, error = sessionError ?? "invalid_session_or_command" });
        }

        var payloadBytes = Encoding.UTF8.GetBytes(root.GetRawText());
        if (payloadBytes.Length > _options.MaxResponseBytes)
        {
            return WriteJsonAsync(ctx, HttpStatusCode.RequestEntityTooLarge, new { ok = false, error = "response_too_large" });
        }

        RobloxCommandResult result;
        lock (session.Gate)
        {
            session.LastSeenUtc = DateTimeOffset.UtcNow;
            if (session.OutstandingCommand?.Id != commandId)
            {
                return WriteJsonAsync(ctx, HttpStatusCode.Conflict, new { ok = false, error = "unknown_command" });
            }

            session.OutstandingCommand = null;
            var ok = GetBool(root, "ok") ?? false;
            if (ok)
            {
                object? data = root.TryGetProperty("data", out var dataEl)
                    ? JsonSerializer.Deserialize<object>(dataEl.GetRawText(), JsonDefaults.Options)
                    : null;
                result = RobloxCommandResult.Success(data);
            }
            else
            {
                var message = RobloxBridgeSecurity.SanitizeErrorMessage(
                    GetString(root, "error") ?? GetString(root, "message"));
                result = RobloxCommandResult.Fail(ErrorCodes.AdapterFailed, message);
            }

            session.PendingResult?.TrySetResult(result);
            session.PendingResult = null;
        }

        return WriteJsonAsync(ctx, HttpStatusCode.OK, new { ok = true });
    }

    private bool ValidateToken(HttpListenerRequest request)
    {
        var provided = request.QueryString["token"]
                       ?? request.Headers["X-DesktopUseAgent-Token"];
        if (string.IsNullOrWhiteSpace(provided))
        {
            return false;
        }

        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return providedBytes.Length == _tokenBytes.Length &&
               CryptographicOperations.FixedTimeEquals(providedBytes, _tokenBytes);
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, HttpStatusCode status, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.StatusCode = (int)status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static string? TruncateMetadata(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? value
            : value.Length <= 256
                ? value
                : value[..256];

    private void FailAllPending(string code, string message)
    {
        foreach (var session in _sessions.Values)
        {
            TaskCompletionSource<RobloxCommandResult>? tcs;
            lock (session.Gate)
            {
                tcs = session.PendingResult;
                session.OutstandingCommand = null;
                session.PendingResult = null;
            }

            tcs?.TrySetResult(RobloxCommandResult.Fail(code, message));
        }
    }
}
