using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SemanticDesktop.App.Persistence;

namespace SemanticDesktop.App.Lifecycle;

public enum OpenAiTunnelState
{
    Disabled,
    Stopped,
    Starting,
    Running,
    Stopping,
    Error
}

public sealed class OpenAiTunnelStatus
{
    public OpenAiTunnelState State { get; init; } = OpenAiTunnelState.Stopped;
    public string Message { get; init; } = string.Empty;
    public int? ProcessId { get; init; }
    public string? DiagnosticTail { get; init; }
}

public sealed class OpenAiTunnelLaunchRequest
{
    public required string TunnelClientPath { get; init; }
    public required string ProfileName { get; init; }
    public required string ProfileDirectory { get; init; }
    public required string RuntimeKey { get; init; }
    public bool ShowConsoleWindow { get; init; }
}

public sealed class OpenAiTunnelDoctorResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string DiagnosticTail { get; init; } = string.Empty;
}

public interface IOpenAiTunnelProcessHandle : IDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    string? DiagnosticTail { get; }
    Task WaitForExitAsync(CancellationToken cancellationToken = default);
    void KillEntireProcessTree();
}

public interface IOpenAiTunnelProcessGateway
{
    Task<OpenAiTunnelDoctorResult> RunDoctorAsync(OpenAiTunnelLaunchRequest request, CancellationToken cancellationToken = default);
    Task<IOpenAiTunnelProcessHandle> StartRunAsync(OpenAiTunnelLaunchRequest request, CancellationToken cancellationToken = default);
}

public interface IOpenAiTunnelPathResolver
{
    string ResolveTunnelClientPath(OpenAiTunnelSettings settings);
    string ResolveProfileDirectory(OpenAiTunnelSettings settings);
    string ResolveProfileFilePath(OpenAiTunnelSettings settings);
}

public sealed class OpenAiTunnelPathResolver : IOpenAiTunnelPathResolver
{
    public string ResolveTunnelClientPath(OpenAiTunnelSettings settings) =>
        string.IsNullOrWhiteSpace(settings.TunnelClientPath)
            ? DefaultTunnelClientPath()
            : settings.TunnelClientPath;

    public string ResolveProfileDirectory(OpenAiTunnelSettings settings) =>
        string.IsNullOrWhiteSpace(settings.ProfileDirectory)
            ? DefaultProfileDirectory()
            : settings.ProfileDirectory;

    public string ResolveProfileFilePath(OpenAiTunnelSettings settings) =>
        Path.Combine(ResolveProfileDirectory(settings), $"{settings.ProfileName}.yaml");

    public static string DefaultTunnelClientPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopUseAgent",
            "tools",
            "tunnel-client",
            "current",
            "tunnel-client.exe");

    public static string DefaultProfileDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DesktopUseAgent",
            "tunnel-profiles");
}

internal sealed class BoundedDiagnosticBuffer
{
    private const int MaxChars = 4000;
    private readonly StringBuilder _builder = new();
    private readonly object _gate = new();

    public void AppendLine(string line)
    {
        lock (_gate)
        {
            _builder.AppendLine(line);
            if (_builder.Length > MaxChars)
            {
                _builder.Remove(0, _builder.Length - MaxChars);
            }
        }
    }

    public string Snapshot(string? runtimeKey = null) =>
        OpenAiTunnelLifecycle.RedactDiagnosticTail(ToString(), runtimeKey);

    public override string ToString()
    {
        lock (_gate)
        {
            return _builder.ToString();
        }
    }
}

public sealed class OpenAiTunnelProcessGateway : IOpenAiTunnelProcessGateway
{
    private static readonly TimeSpan DoctorTimeout = TimeSpan.FromMinutes(2);

    public async Task<OpenAiTunnelDoctorResult> RunDoctorAsync(OpenAiTunnelLaunchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var psi = CreateStartInfo(request.TunnelClientPath, request.RuntimeKey);
        psi.ArgumentList.Add("doctor");
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add(request.ProfileName);
        psi.ArgumentList.Add("--profile-dir");
        psi.ArgumentList.Add(request.ProfileDirectory);
        psi.ArgumentList.Add("--explain");

        var (exitCode, tail) = await RunToCompletionAsync(psi, request.RuntimeKey, DoctorTimeout, cancellationToken).ConfigureAwait(false);
        return new OpenAiTunnelDoctorResult
        {
            Success = exitCode == 0,
            ExitCode = exitCode,
            DiagnosticTail = tail
        };
    }

    public Task<IOpenAiTunnelProcessHandle> StartRunAsync(OpenAiTunnelLaunchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var showConsole = request.ShowConsoleWindow;
        var psi = CreateStartInfo(request.TunnelClientPath, request.RuntimeKey, redirectOutput: !showConsole, createNoWindow: !showConsole);
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add(request.ProfileName);
        psi.ArgumentList.Add("--profile-dir");
        psi.ArgumentList.Add(request.ProfileDirectory);

        var output = new BoundedDiagnosticBuffer();
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!showConsole)
        {
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    output.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    output.AppendLine(e.Data);
                }
            };
        }

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Failed to start tunnel-client run.");
        }

        if (!showConsole)
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        return Task.FromResult<IOpenAiTunnelProcessHandle>(new ProcessTunnelHandle(process, output, request.RuntimeKey));
    }

    private static ProcessStartInfo CreateStartInfo(
        string tunnelClientPath,
        string runtimeKey,
        bool redirectOutput = true,
        bool createNoWindow = true)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tunnelClientPath,
            UseShellExecute = false,
            CreateNoWindow = createNoWindow,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput
        };

        psi.Environment.Remove("OPENAI_ADMIN_KEY");
        psi.Environment.Remove("OPENAI_API_KEY");
        psi.Environment["CONTROL_PLANE_API_KEY"] = runtimeKey;
        return psi;
    }

    private static async Task<(int ExitCode, string Tail)> RunToCompletionAsync(
        ProcessStartInfo psi,
        string runtimeKey,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var output = new BoundedDiagnosticBuffer();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                output.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start tunnel-client.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw new TimeoutException("tunnel-client doctor timed out.");
        }

        return (process.ExitCode, OpenAiTunnelLifecycle.RedactDiagnosticTail(output.ToString(), runtimeKey));
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // ignored
        }
    }

    private sealed class ProcessTunnelHandle : IOpenAiTunnelProcessHandle
    {
        private readonly Process _process;
        private readonly BoundedDiagnosticBuffer _output;
        private readonly string _runtimeKey;

        public ProcessTunnelHandle(Process process, BoundedDiagnosticBuffer output, string runtimeKey)
        {
            _process = process;
            _output = output;
            _runtimeKey = runtimeKey;
        }

        public int ProcessId => _process.Id;

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.HasExited ? _process.ExitCode : -1;

        public string? DiagnosticTail => OpenAiTunnelLifecycle.RedactDiagnosticTail(_output.ToString(), _runtimeKey);

        public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
            _process.WaitForExitAsync(cancellationToken);

        public void KillEntireProcessTree()
        {
            if (_process.HasExited)
            {
                return;
            }

            _process.Kill(entireProcessTree: true);
        }

        public void Dispose() => _process.Dispose();
    }
}

public sealed class OpenAiTunnelLifecycle
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMilliseconds(500);
    private static readonly Regex SecretPattern = new(
        @"(?i)(sk-[A-Za-z0-9_-]+|CONTROL_PLANE_API_KEY\s*[=:]\s*\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    internal static readonly Regex ProfileNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IOpenAiTunnelProcessGateway _gateway;
    private readonly AppSettingsStore _settings;
    private readonly CredentialStore _credentials;
    private readonly IOpenAiTunnelPathResolver _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _startCtsGate = new();

    private IOpenAiTunnelProcessHandle? _process;
    private CancellationTokenSource? _startCts;
    private OpenAiTunnelStatus _status = new() { State = OpenAiTunnelState.Stopped, Message = "Tunnel stopped." };

    public OpenAiTunnelLifecycle(
        IOpenAiTunnelProcessGateway gateway,
        AppSettingsStore settings,
        CredentialStore credentials,
        IOpenAiTunnelPathResolver? paths = null)
    {
        _gateway = gateway;
        _settings = settings;
        _credentials = credentials;
        _paths = paths ?? new OpenAiTunnelPathResolver();
    }

    public event EventHandler<OpenAiTunnelStatus>? StatusChanged;

    public OpenAiTunnelStatus GetStatus() => _status;

    public async Task StartIfEnabledAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_settings.Get().OpenAiTunnel.Enabled)
            {
                SetStatus(new OpenAiTunnelStatus
                {
                    State = OpenAiTunnelState.Disabled,
                    Message = "Tunnel disabled."
                });
                return;
            }

            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (!enabled)
        {
            CancelInFlightStart();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settings.Get();
            settings.OpenAiTunnel.Enabled = enabled;
            _settings.Save(settings);

            if (enabled)
            {
                await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await StopCoreAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(new OpenAiTunnelStatus
                {
                    State = OpenAiTunnelState.Disabled,
                    Message = "Tunnel disabled."
                });
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_settings.Get().OpenAiTunnel.Enabled)
            {
                SetStatus(new OpenAiTunnelStatus
                {
                    State = OpenAiTunnelState.Disabled,
                    Message = "Tunnel disabled."
                });
                return;
            }

            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        CancelInFlightStart();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            SetStatusAfterStop();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearRuntimeKeyAsync(CancellationToken cancellationToken = default)
    {
        _credentials.Delete(CredentialStore.OpenAiTunnelRuntimeKey());
        CancelInFlightStart();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var settings = _settings.Get();
            settings.OpenAiTunnel.Enabled = false;
            _settings.Save(settings);
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Disabled,
                Message = "Runtime key cleared. Tunnel disabled."
            });
        }
        finally
        {
            _gate.Release();
        }
    }

    public void SaveRuntimeKey(string runtimeKey)
    {
        if (string.IsNullOrWhiteSpace(runtimeKey))
        {
            throw new ArgumentException("Runtime key is required.", nameof(runtimeKey));
        }

        _credentials.Set(CredentialStore.OpenAiTunnelRuntimeKey(), runtimeKey.Trim());
    }

    public bool HasRuntimeKey() => _credentials.Exists(CredentialStore.OpenAiTunnelRuntimeKey());

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_process is not null && !_process.HasExited)
        {
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Running,
                Message = "Tunnel already running.",
                ProcessId = _process.ProcessId
            });
            return;
        }

        if (_process is not null)
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!_settings.Get().OpenAiTunnel.Enabled)
        {
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Disabled,
                Message = "Tunnel disabled."
            });
            return;
        }

        var settings = _settings.Get().OpenAiTunnel;
        if (!ProfileNamePattern.IsMatch(settings.ProfileName))
        {
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Error,
                Message = "Invalid OpenAiTunnel.ProfileName in settings. Must match ^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$."
            });
            return;
        }

        var tunnelClientPath = _paths.ResolveTunnelClientPath(settings);
        var profileDirectory = _paths.ResolveProfileDirectory(settings);
        var profilePath = _paths.ResolveProfileFilePath(settings);

        // A previous Control Center session may have left tunnel-client running
        // (Leave agent running). Doctor binds the health port and fails if that
        // orphan still holds 127.0.0.1:8080 — clear it before we start.
        StopOrphanedTunnelClientProcesses(tunnelClientPath);

        if (!File.Exists(tunnelClientPath))
        {
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Error,
                Message = $"tunnel-client not found at {tunnelClientPath}. Run scripts\\install-openai-tunnel-client.ps1."
            });
            return;
        }

        if (!File.Exists(profilePath))
        {
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Error,
                Message = $"Profile not found at {profilePath}. Run scripts\\setup-chatgpt-tunnel.ps1 -ProfileName {settings.ProfileName}."
            });
            return;
        }

        var runtimeKey = _credentials.Get(CredentialStore.OpenAiTunnelRuntimeKey());
        if (string.IsNullOrWhiteSpace(runtimeKey))
        {
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Error,
                Message = "Runtime API key not stored. Save a restricted Tunnels Read + Use key in Settings."
            });
            return;
        }

        using var startCts = BeginStartOperation(cancellationToken);
        var startToken = startCts.Token;
        try
        {
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Starting,
                Message = "Running tunnel-client doctor..."
            });

            var request = new OpenAiTunnelLaunchRequest
            {
                TunnelClientPath = tunnelClientPath,
                ProfileName = settings.ProfileName,
                ProfileDirectory = profileDirectory,
                RuntimeKey = runtimeKey,
                ShowConsoleWindow = settings.ShowConsoleWindow
            };

            OpenAiTunnelDoctorResult doctor;
            try
            {
                doctor = await _gateway.RunDoctorAsync(request, startToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (startToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SetStatus(new OpenAiTunnelStatus
                {
                    State = OpenAiTunnelState.Error,
                    Message = $"tunnel-client doctor failed: {ex.Message}"
                });
                return;
            }

            var safeDoctorTail = RedactDiagnosticTail(doctor.DiagnosticTail, runtimeKey);

            if (!_settings.Get().OpenAiTunnel.Enabled)
            {
                SetStatus(new OpenAiTunnelStatus
                {
                    State = OpenAiTunnelState.Disabled,
                    Message = "Tunnel disabled."
                });
                return;
            }

            if (!doctor.Success)
            {
                SetStatus(new OpenAiTunnelStatus
                {
                    State = OpenAiTunnelState.Error,
                    Message = $"tunnel-client doctor failed with exit code {doctor.ExitCode}.",
                    DiagnosticTail = safeDoctorTail
                });
                return;
            }

            startToken.ThrowIfCancellationRequested();

            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Starting,
                Message = "Starting tunnel-client run...",
                DiagnosticTail = safeDoctorTail
            });

            IOpenAiTunnelProcessHandle process;
            try
            {
                process = await _gateway.StartRunAsync(request, startToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (startToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                SetStatus(new OpenAiTunnelStatus
                {
                    State = OpenAiTunnelState.Error,
                    Message = $"Failed to start tunnel-client run: {ex.Message}",
                    DiagnosticTail = safeDoctorTail
                });
                return;
            }

            _process = process;

            try
            {
                await Task.Delay(StartupGrace, startToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (startToken.IsCancellationRequested)
            {
                await StopCoreAsync(CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (_process.HasExited)
            {
                var tail = RedactDiagnosticTail(_process.DiagnosticTail, runtimeKey);
                var exitCode = _process.ExitCode;
                _process.Dispose();
                _process = null;
                SetStatus(new OpenAiTunnelStatus
                {
                    State = OpenAiTunnelState.Error,
                    Message = $"Tunnel exited immediately after start (code {exitCode}).",
                    DiagnosticTail = tail
                });
                return;
            }

            MonitorProcessExit(_process);
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Running,
                Message = "Tunnel running.",
                ProcessId = _process.ProcessId,
                DiagnosticTail = safeDoctorTail
            });
        }
        finally
        {
            EndStartOperation(startCts);
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            // Still clear orphans so disable/restart works after Leave-agent-running.
            try
            {
                var path = _paths.ResolveTunnelClientPath(_settings.Get().OpenAiTunnel);
                StopOrphanedTunnelClientProcesses(path);
            }
            catch
            {
                // best effort
            }

            return;
        }

        SetStatus(new OpenAiTunnelStatus
        {
            State = OpenAiTunnelState.Stopping,
            Message = "Stopping tunnel...",
            ProcessId = process.ProcessId
        });

        try
        {
            process.KillEntireProcessTree();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StopTimeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // best effort
        }
        catch
        {
            // already gone
        }
        finally
        {
            process.Dispose();
        }

        try
        {
            var path = _paths.ResolveTunnelClientPath(_settings.Get().OpenAiTunnel);
            StopOrphanedTunnelClientProcesses(path);
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Kills tunnel-client.exe instances that match our installed binary path but
    /// are not tracked by this lifecycle (orphaned after Control Center restart).
    /// </summary>
    internal static void StopOrphanedTunnelClientProcesses(string tunnelClientPath)
    {
        if (string.IsNullOrWhiteSpace(tunnelClientPath))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(tunnelClientPath);
        }
        catch
        {
            return;
        }

        foreach (var candidate in Process.GetProcessesByName("tunnel-client"))
        {
            try
            {
                string? modulePath = null;
                try
                {
                    modulePath = candidate.MainModule?.FileName;
                }
                catch
                {
                    // access denied / exited
                }

                if (modulePath is null)
                {
                    continue;
                }

                if (!string.Equals(Path.GetFullPath(modulePath), fullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidate.Kill(entireProcessTree: true);
                candidate.WaitForExit(3000);
            }
            catch
            {
                // best effort
            }
            finally
            {
                try { candidate.Dispose(); } catch { /* ignore */ }
            }
        }
    }

    private void MonitorProcessExit(IOpenAiTunnelProcessHandle process)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(_process, process))
                {
                    return;
                }

                _process = null;
                var tail = process.DiagnosticTail;
                var exitCode = process.ExitCode;
                process.Dispose();
                SetStatus(new OpenAiTunnelStatus
                {
                    State = OpenAiTunnelState.Error,
                    Message = $"Tunnel exited unexpectedly (code {exitCode}).",
                    DiagnosticTail = tail
                });
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private void CancelInFlightStart()
    {
        lock (_startCtsGate)
        {
            if (_startCts is null)
            {
                return;
            }

            try
            {
                _startCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // ignored
            }
        }
    }

    private CancellationTokenSource BeginStartOperation(CancellationToken callerToken)
    {
        lock (_startCtsGate)
        {
            DisposeStartCts();
            _startCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
            return _startCts;
        }
    }

    private void EndStartOperation(CancellationTokenSource startCts)
    {
        lock (_startCtsGate)
        {
            if (ReferenceEquals(_startCts, startCts))
            {
                _startCts = null;
            }
        }
    }

    private void DisposeStartCts()
    {
        if (_startCts is null)
        {
            return;
        }

        try
        {
            _startCts.Dispose();
        }
        catch
        {
            // ignored
        }

        _startCts = null;
    }

    private void SetStatusAfterStop()
    {
        if (!_settings.Get().OpenAiTunnel.Enabled)
        {
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Disabled,
                Message = "Tunnel disabled."
            });
        }
        else
        {
            SetStatus(new OpenAiTunnelStatus
            {
                State = OpenAiTunnelState.Stopped,
                Message = "Tunnel stopped."
            });
        }
    }

    private void SetStatus(OpenAiTunnelStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    internal static string RedactDiagnosticTail(string? text, string? runtimeKey = null, int maxChars = 2000)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var redacted = text;
        if (!string.IsNullOrEmpty(runtimeKey))
        {
            redacted = redacted.Replace(runtimeKey, "[REDACTED]", StringComparison.Ordinal);
        }

        redacted = SecretPattern.Replace(redacted, "[REDACTED]");
        if (redacted.Length <= maxChars)
        {
            return redacted.Trim();
        }

        return redacted[^maxChars..].Trim();
    }
}
