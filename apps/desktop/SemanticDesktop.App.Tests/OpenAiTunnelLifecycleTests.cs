using SemanticDesktop.App.Lifecycle;
using SemanticDesktop.App.Persistence;

namespace SemanticDesktop.App.Tests;

public class OpenAiTunnelLifecycleTests
{
    private sealed class TestOpenAiTunnelPathResolver : IOpenAiTunnelPathResolver
    {
        public required string TunnelClientPath { get; init; }
        public required string ProfileDirectory { get; init; }
        public required string ProfileFilePath { get; init; }

        public string ResolveTunnelClientPath(OpenAiTunnelSettings settings) => TunnelClientPath;
        public string ResolveProfileDirectory(OpenAiTunnelSettings settings) => ProfileDirectory;
        public string ResolveProfileFilePath(OpenAiTunnelSettings settings) => ProfileFilePath;
    }

    private static (OpenAiTunnelLifecycle lifecycle, AppSettingsStore settings, CredentialStore creds, FakeOpenAiTunnelProcessGateway gateway, string root)
        CreateHarness(bool enabled = false, bool withKey = true, bool withFiles = true, string profileName = "desktopuseagent", string? runtimeKey = null)
    {
        var root = TestHarness.TempRoot();
        var settingsStore = new AppSettingsStore(root);
        var creds = new CredentialStore(root);
        var gateway = new FakeOpenAiTunnelProcessGateway();
        var toolsDir = Path.Combine(root, "tools", "tunnel-client", "current");
        Directory.CreateDirectory(toolsDir);
        var clientPath = Path.Combine(toolsDir, "tunnel-client.exe");
        var profileDir = Path.Combine(root, "tunnel-profiles");
        Directory.CreateDirectory(profileDir);
        var profilePath = Path.Combine(profileDir, $"{profileName}.yaml");
        if (withFiles)
        {
            File.WriteAllText(clientPath, string.Empty);
            File.WriteAllText(profilePath, $"profile: {profileName}");
        }

        if (withKey)
        {
            creds.Set(CredentialStore.OpenAiTunnelRuntimeKey(), runtimeKey ?? "sk-test-runtime-key");
        }

        settingsStore.Save(new AppSettings
        {
            OpenAiTunnel = new OpenAiTunnelSettings { Enabled = enabled, ProfileName = profileName }
        });

        var paths = new TestOpenAiTunnelPathResolver
        {
            TunnelClientPath = clientPath,
            ProfileDirectory = profileDir,
            ProfileFilePath = profilePath
        };
        var lifecycle = new OpenAiTunnelLifecycle(gateway, settingsStore, creds, paths);
        return (lifecycle, settingsStore, creds, gateway, root);
    }

    [Fact]
    public async Task Disabled_Startup_Is_NoOp()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: false);
        try
        {
            await lifecycle.StartIfEnabledAsync();
            Assert.Equal(0, gateway.DoctorCallCount);
            Assert.Equal(0, gateway.StartCallCount);
            Assert.Equal(OpenAiTunnelState.Disabled, lifecycle.GetStatus().State);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Enabled_With_Credential_Starts_Exactly_Once()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true);
        try
        {
            await lifecycle.StartIfEnabledAsync();
            Assert.Equal(1, gateway.DoctorCallCount);
            Assert.Equal(1, gateway.StartCallCount);
            Assert.Equal(OpenAiTunnelState.Running, lifecycle.GetStatus().State);

            await lifecycle.StartAsync();
            Assert.Equal(1, gateway.DoctorCallCount);
            Assert.Equal(1, gateway.StartCallCount);
        }
        finally
        {
            await lifecycle.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Missing_Key_Surfaces_Error_Without_Start()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true, withKey: false);
        try
        {
            await lifecycle.StartAsync();
            Assert.Equal(0, gateway.DoctorCallCount);
            Assert.Equal(0, gateway.StartCallCount);
            Assert.Equal(OpenAiTunnelState.Error, lifecycle.GetStatus().State);
            Assert.Contains("Runtime API key not stored", lifecycle.GetStatus().Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Toggle_Off_Stops_And_Persists_Disabled()
    {
        var (lifecycle, settingsStore, _, gateway, root) = CreateHarness(enabled: true);
        try
        {
            await lifecycle.StartIfEnabledAsync();
            Assert.Equal(OpenAiTunnelState.Running, lifecycle.GetStatus().State);
            var handle = gateway.Running;
            Assert.NotNull(handle);

            await lifecycle.SetEnabledAsync(false);
            Assert.Equal(OpenAiTunnelState.Disabled, lifecycle.GetStatus().State);
            Assert.False(settingsStore.Get().OpenAiTunnel.Enabled);
            Assert.Equal(1, handle!.KillCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StopAsync_Always_Kills_Process()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true);
        try
        {
            await lifecycle.StartAsync();
            var handle = gateway.Running;
            Assert.NotNull(handle);

            await lifecycle.StopAsync();
            Assert.Equal(OpenAiTunnelState.Stopped, lifecycle.GetStatus().State);
            Assert.Equal(1, handle!.KillCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Restart_While_Disabled_Is_NoOp()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: false);
        try
        {
            await lifecycle.RestartAsync();
            Assert.Equal(OpenAiTunnelState.Disabled, lifecycle.GetStatus().State);
            Assert.Equal(0, gateway.DoctorCallCount);
            Assert.Equal(0, gateway.StartCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartIfEnabled_Disable_Race_Does_Not_Start()
    {
        var (lifecycle, settingsStore, _, gateway, root) = CreateHarness(enabled: true);
        gateway.DoctorBlock = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var startTask = lifecycle.StartIfEnabledAsync();
            await WaitUntilAsync(() => gateway.DoctorCallCount == 1, TimeSpan.FromSeconds(2));

            settingsStore.Save(new AppSettings
            {
                OpenAiTunnel = new OpenAiTunnelSettings { Enabled = false }
            });
            await lifecycle.SetEnabledAsync(false);

            gateway.DoctorBlock.TrySetResult(null);
            await startTask;

            Assert.Equal(0, gateway.StartCallCount);
            Assert.NotEqual(OpenAiTunnelState.Running, lifecycle.GetStatus().State);
        }
        finally
        {
            gateway.DoctorBlock.TrySetResult(null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Stop_Cancels_Blocked_Doctor_Promptly()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true);
        gateway.DoctorBlock = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var startTask = lifecycle.StartAsync();
            await WaitUntilAsync(() => gateway.DoctorCallCount == 1, TimeSpan.FromSeconds(2));

            var stopStarted = DateTime.UtcNow;
            await lifecycle.StopAsync();
            var elapsed = DateTime.UtcNow - stopStarted;

            Assert.True(elapsed < TimeSpan.FromSeconds(2));
            Assert.Equal(0, gateway.StartCallCount);
            await startTask;
        }
        finally
        {
            gateway.DoctorBlock.TrySetResult(null);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Gateway_Start_Failure_Sets_Error_Status()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true);
        gateway.StartShouldThrow = true;
        try
        {
            await lifecycle.StartAsync();
            Assert.Equal(OpenAiTunnelState.Error, lifecycle.GetStatus().State);
            Assert.Contains("Failed to start tunnel-client run", lifecycle.GetStatus().Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_Failure_Sets_Error_Status_With_Redacted_Diagnostics()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true);
        gateway.DoctorShouldSucceed = false;
        gateway.DoctorDiagnosticTail = "bad key sk-secret123 and CONTROL_PLANE_API_KEY=abc";
        try
        {
            await lifecycle.StartAsync();
            Assert.Equal(OpenAiTunnelState.Error, lifecycle.GetStatus().State);
            Assert.DoesNotContain("sk-secret123", lifecycle.GetStatus().DiagnosticTail ?? string.Empty);
            Assert.DoesNotContain("CONTROL_PLANE_API_KEY=abc", lifecycle.GetStatus().DiagnosticTail ?? string.Empty);
            Assert.Equal(0, gateway.StartCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Exact_NonSk_Runtime_Key_Is_Redacted_From_Diagnostics()
    {
        const string runtimeKey = "rtk-plaintext-secret-value";
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true, runtimeKey: runtimeKey);
        gateway.DoctorShouldSucceed = false;
        gateway.EchoRuntimeKeyInDoctorDiagnostics = true;
        try
        {
            await lifecycle.StartAsync();
            Assert.Equal(OpenAiTunnelState.Error, lifecycle.GetStatus().State);
            Assert.DoesNotContain(runtimeKey, lifecycle.GetStatus().DiagnosticTail ?? string.Empty);
            Assert.Contains("[REDACTED]", lifecycle.GetStatus().DiagnosticTail ?? string.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_ProfileName_Does_Not_Start()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true, profileName: "../bad");
        try
        {
            await lifecycle.StartAsync();
            Assert.Equal(OpenAiTunnelState.Error, lifecycle.GetStatus().State);
            Assert.Contains("Invalid OpenAiTunnel.ProfileName", lifecycle.GetStatus().Message);
            Assert.Equal(0, gateway.DoctorCallCount);
            Assert.Equal(0, gateway.StartCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Immediate_Process_Exit_Is_Not_Reported_As_Running()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true);
        gateway.ExitImmediatelyOnStart = true;
        try
        {
            await lifecycle.StartAsync();
            Assert.Equal(OpenAiTunnelState.Error, lifecycle.GetStatus().State);
            Assert.Contains("immediately", lifecycle.GetStatus().Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Unexpected_Exit_Updates_Status()
    {
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true);
        try
        {
            await lifecycle.StartAsync();
            gateway.SimulateExit(42);
            await Task.Delay(100);
            Assert.Equal(OpenAiTunnelState.Error, lifecycle.GetStatus().State);
            Assert.Contains("unexpectedly", lifecycle.GetStatus().Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Clear_Runtime_Key_Stops_And_Disables()
    {
        var (lifecycle, settingsStore, creds, gateway, root) = CreateHarness(enabled: true);
        try
        {
            await lifecycle.StartAsync();
            var handle = gateway.Running;
            await lifecycle.ClearRuntimeKeyAsync();
            Assert.False(settingsStore.Get().OpenAiTunnel.Enabled);
            Assert.False(creds.Exists(CredentialStore.OpenAiTunnelRuntimeKey()));
            Assert.Equal(OpenAiTunnelState.Disabled, lifecycle.GetStatus().State);
            Assert.Equal(1, handle!.KillCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Runtime_Key_And_Diagnostics_Not_Persisted_In_App_Settings()
    {
        var root = TestHarness.TempRoot();
        try
        {
            var settingsStore = new AppSettingsStore(root);
            var creds = new CredentialStore(root);
            creds.Set(CredentialStore.OpenAiTunnelRuntimeKey(), "sk-never-in-json");
            settingsStore.Save(new AppSettings
            {
                OpenAiTunnel = new OpenAiTunnelSettings { Enabled = true }
            });

            var json = File.ReadAllText(Path.Combine(root, "app-settings.json"));
            Assert.DoesNotContain("sk-never-in-json", json);
            Assert.DoesNotContain("runtimeKey", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CONTROL_PLANE_API_KEY", json);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Launch_Arguments_Do_Not_Contain_Secret()
    {
        const string runtimeKey = "sk-test-runtime-key";
        var (lifecycle, _, _, gateway, root) = CreateHarness(enabled: true, runtimeKey: runtimeKey);
        gateway.DoctorDiagnosticTail = $"doctor saw {runtimeKey} and sk-other-secret";
        try
        {
            await lifecycle.StartAsync();
            Assert.DoesNotContain(runtimeKey, string.Join(' ', gateway.DoctorArguments));
            Assert.DoesNotContain(runtimeKey, string.Join(' ', gateway.RunArguments));
            Assert.DoesNotContain("CONTROL_PLANE_API_KEY", string.Join(' ', gateway.DoctorArguments));
            Assert.DoesNotContain("CONTROL_PLANE_API_KEY", string.Join(' ', gateway.RunArguments));
            Assert.True(gateway.RuntimeKeyReceived);
            Assert.Contains(runtimeKey, gateway.LastReturnedDiagnosticTail ?? string.Empty);
            Assert.DoesNotContain(runtimeKey, lifecycle.GetStatus().DiagnosticTail ?? string.Empty);
            Assert.DoesNotContain("sk-other-secret", lifecycle.GetStatus().DiagnosticTail ?? string.Empty);
        }
        finally
        {
            await lifecycle.StopAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }
}

internal sealed class FakeOpenAiTunnelProcessGateway : IOpenAiTunnelProcessGateway
{
    public int DoctorCallCount { get; private set; }
    public int StartCallCount { get; private set; }
    public List<string> DoctorArguments { get; } = new();
    public List<string> RunArguments { get; } = new();
    public bool DoctorShouldSucceed { get; set; } = true;
    public int DoctorExitCode { get; set; }
    public bool StartShouldThrow { get; set; }
    public int FakePid { get; set; } = 9001;
    public string DoctorDiagnosticTail { get; set; } = "doctor ok";
    public bool EchoRuntimeKeyInDoctorDiagnostics { get; set; }
    public bool ExitImmediatelyOnStart { get; set; }
    public TaskCompletionSource<object?>? DoctorBlock { get; set; }
    public bool RuntimeKeyReceived { get; private set; }
    public string? LastReturnedDiagnosticTail { get; private set; }
    public FakeOpenAiTunnelProcessHandle? Running { get; private set; }

    public async Task<OpenAiTunnelDoctorResult> RunDoctorAsync(OpenAiTunnelLaunchRequest request, CancellationToken cancellationToken = default)
    {
        DoctorCallCount++;
        DoctorArguments.Clear();
        DoctorArguments.AddRange(new[]
        {
            "doctor",
            "--profile",
            request.ProfileName,
            "--profile-dir",
            request.ProfileDirectory,
            "--explain"
        });

        RuntimeKeyReceived = !string.IsNullOrWhiteSpace(request.RuntimeKey);
        if (DoctorArguments.Any(a => a.Contains(request.RuntimeKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Secret leaked into doctor arguments.");
        }

        if (DoctorBlock is not null)
        {
            await DoctorBlock.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var tail = EchoRuntimeKeyInDoctorDiagnostics
            ? $"doctor failed using {request.RuntimeKey}"
            : DoctorDiagnosticTail;
        LastReturnedDiagnosticTail = tail;
        return new OpenAiTunnelDoctorResult
        {
            Success = DoctorShouldSucceed,
            ExitCode = DoctorShouldSucceed ? 0 : (DoctorExitCode == 0 ? 1 : DoctorExitCode),
            DiagnosticTail = tail
        };
    }

    public Task<IOpenAiTunnelProcessHandle> StartRunAsync(OpenAiTunnelLaunchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StartCallCount++;
        RunArguments.Clear();
        RunArguments.AddRange(new[]
        {
            "run",
            "--profile",
            request.ProfileName,
            "--profile-dir",
            request.ProfileDirectory
        });

        if (RunArguments.Any(a => a.Contains(request.RuntimeKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Secret leaked into run arguments.");
        }

        if (StartShouldThrow)
        {
            throw new InvalidOperationException("Simulated start failure.");
        }

        Running?.Dispose();
        Running = new FakeOpenAiTunnelProcessHandle(FakePid);
        if (ExitImmediatelyOnStart)
        {
            Running.SimulateExit(1);
        }

        return Task.FromResult<IOpenAiTunnelProcessHandle>(Running);
    }

    public void SimulateExit(int exitCode = 1) => Running?.SimulateExit(exitCode);
}

internal sealed class FakeOpenAiTunnelProcessHandle : IOpenAiTunnelProcessHandle
{
    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _exitCode = -1;

    public FakeOpenAiTunnelProcessHandle(int processId) => ProcessId = processId;

    public int ProcessId { get; }
    public bool HasExited => _exit.Task.IsCompleted;
    public int ExitCode => _exitCode;
    public string? DiagnosticTail => null;
    public int KillCallCount { get; private set; }

    public Task WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exit.Task.WaitAsync(cancellationToken);

    public void KillEntireProcessTree()
    {
        if (_exit.Task.IsCompleted)
        {
            return;
        }

        KillCallCount++;
        _exitCode = 0;
        _exit.TrySetResult();
    }

    public void SimulateExit(int exitCode = 1)
    {
        if (_exit.Task.IsCompleted)
        {
            return;
        }

        _exitCode = exitCode;
        _exit.TrySetResult();
    }

    public void Dispose()
    {
    }
}
