using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace SemanticDesktop.Agent;

public enum AgentSingleInstanceAcquireStatus
{
    Acquired,
    AlreadyRunning,
    Failed
}

public sealed record AgentSingleInstanceAcquireResult(
    AgentSingleInstance? Instance,
    AgentSingleInstanceAcquireStatus Status,
    string? Message);

public sealed class AgentSingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;

    private AgentSingleInstance(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static AgentSingleInstanceAcquireResult Acquire(string pipeName)
    {
        var mutexName = BuildMutexName(pipeName);
        try
        {
            if (TryOpenExisting(mutexName, out var existing))
            {
                existing.Dispose();
                return new AgentSingleInstanceAcquireResult(
                    null,
                    AgentSingleInstanceAcquireStatus.AlreadyRunning,
                    $"Another DesktopUseAgent instance holds the session mutex for pipe '{pipeName}'.");
            }

            var mutex = new Mutex(initiallyOwned: true, mutexName, out _);
            return new AgentSingleInstanceAcquireResult(
                new AgentSingleInstance(mutex, ownsMutex: true),
                AgentSingleInstanceAcquireStatus.Acquired,
                null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return new AgentSingleInstanceAcquireResult(
                null,
                AgentSingleInstanceAcquireStatus.Failed,
                $"Insufficient privilege to create agent mutex '{mutexName}': {ex.Message}");
        }
        catch (IOException ex)
        {
            return new AgentSingleInstanceAcquireResult(
                null,
                AgentSingleInstanceAcquireStatus.Failed,
                $"Agent mutex IO failure for '{mutexName}': {ex.Message}");
        }
        catch (Exception ex)
        {
            return new AgentSingleInstanceAcquireResult(
                null,
                AgentSingleInstanceAcquireStatus.Failed,
                $"Agent single-instance mutex failed: {ex.Message}");
        }
    }

    private static bool TryOpenExisting(string mutexName, out Mutex mutex)
    {
        try
        {
            mutex = Mutex.OpenExisting(mutexName);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            mutex = null!;
            return false;
        }
        catch (AbandonedMutexException ex)
        {
            mutex = ex.Mutex;
            return true;
        }
    }

    public static string BuildMutexName(string pipeName)
    {
        var sid = WindowsIdentity.GetCurrent()?.User?.Value ?? "unknown-sid";
        var sessionId = Process.GetCurrentProcess().SessionId;
        var pipeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pipeName ?? "")))
            .ToLowerInvariant()[..16];
        var name = $@"Local\DesktopUseAgent-{sid}-{sessionId}-{pipeHash}";
        return name.Length <= 260 ? name : name[..260];
    }

    public void Dispose()
    {
        if (!_ownsMutex)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // already released
        }

        _mutex.Dispose();
    }
}
