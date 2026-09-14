using System.Collections.Concurrent;
using SemanticDesktop.Core.Security;

namespace SemanticDesktop.Permissions;

public sealed class SessionManager
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new(StringComparer.Ordinal);

    public AgentSession GetOrCreateDefault(string clientId = "local-cli")
    {
        const string defaultId = "sess_default";
        return _sessions.GetOrAdd(defaultId, _ => new AgentSession
        {
            SessionId = defaultId,
            ClientId = clientId,
            AutoApproveAsk = true
        });
    }

    public AgentSession Create(string clientId, bool autoApproveAsk = false)
    {
        var session = new AgentSession
        {
            SessionId = "sess_" + Guid.NewGuid().ToString("N")[..12],
            ClientId = clientId,
            AutoApproveAsk = autoApproveAsk
        };
        _sessions[session.SessionId] = session;
        return session;
    }

    public AgentSession? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public bool Terminate(string sessionId) => _sessions.TryRemove(sessionId, out _);
}

public sealed class ApprovalBroker
{
    private readonly ConcurrentDictionary<string, ApprovalRequest> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _waiters = new(StringComparer.Ordinal);

    public IReadOnlyList<ApprovalRequest> ListPending() =>
        _pending.Values.Where(p => p.Status == "pending").OrderBy(p => p.CreatedAt).ToList();

    public ApprovalRequest Create(AgentSession session, string action, PermissionEvaluation evaluation)
    {
        var request = new ApprovalRequest
        {
            Id = "apr_" + Guid.NewGuid().ToString("N")[..12],
            SessionId = session.SessionId,
            Action = action,
            Capability = evaluation.Capability,
            Risk = evaluation.Risk,
            Target = evaluation.Target,
            Application = evaluation.Application,
            Reason = evaluation.Reason
        };
        _pending[request.Id] = request;
        _waiters[request.Id] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return request;
    }

    public async Task<bool> WaitAsync(string approvalId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!_waiters.TryGetValue(approvalId, out var tcs))
        {
            return false;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        await using var reg = linked.Token.Register(() => tcs.TrySetCanceled(linked.Token));
        try
        {
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (_pending.TryGetValue(approvalId, out var req) && req.Status == "pending")
            {
                req.Status = "timeout";
            }

            return false;
        }
    }

    public bool Resolve(string approvalId, bool allow, PermissionGrantScope scope, AgentSession? session, PermissionEngine engine)
    {
        if (!_pending.TryGetValue(approvalId, out var req) || req.Status != "pending")
        {
            return false;
        }

        req.Status = allow ? "approved" : "denied";
        if (allow && session is not null)
        {
            engine.Grant(session, req.Capability, scope);
        }

        if (_waiters.TryRemove(approvalId, out var tcs))
        {
            tcs.TrySetResult(allow);
        }

        return true;
    }
}

public sealed class EmergencyStopGate
{
    private int _stopped;

    public bool IsStopped => Volatile.Read(ref _stopped) == 1;

    public void Stop() => Interlocked.Exchange(ref _stopped, 1);

    public void Clear() => Interlocked.Exchange(ref _stopped, 0);
}
