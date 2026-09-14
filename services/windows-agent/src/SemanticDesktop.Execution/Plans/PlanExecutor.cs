using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Plans;
using SemanticDesktop.Execution.Conditions;
using SemanticDesktop.Execution.References;

namespace SemanticDesktop.Execution.Plans;

public delegate Task<JsonElement> PlanActionRunner(
    string action,
    JsonElement args,
    CancellationToken cancellationToken);

public sealed class PlanExecutor
{
    private readonly ConcurrentDictionary<string, PlanExecutionState> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly ConditionEvaluator _conditions;
    private readonly PlanActionRunner _runner;

    public PlanExecutor(ConditionEvaluator conditions, PlanActionRunner runner)
    {
        _conditions = conditions;
        _runner = runner;
    }

    public PlanExecutionState? Get(string planId) =>
        _plans.TryGetValue(planId, out var state) ? state : null;

    public bool Cancel(string planId)
    {
        if (_cancellations.TryGetValue(planId, out var cts))
        {
            cts.Cancel();
            return true;
        }

        return false;
    }

    public int CancelAll()
    {
        var count = 0;
        foreach (var kv in _cancellations)
        {
            kv.Value.Cancel();
            count++;
        }

        return count;
    }

    public async Task<PlanExecutionState> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken)
    {
        var planId = string.IsNullOrWhiteSpace(plan.Id)
            ? "plan_" + Guid.NewGuid().ToString("N")[..12]
            : plan.Id!;
        plan.Id = planId;

        var options = plan.Options ?? new PlanOptions();
        var state = new PlanExecutionState
        {
            Id = planId,
            Name = plan.Name,
            Status = PlanStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };
        _plans[planId] = state;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellations[planId] = linked;

        try
        {
            foreach (var step in plan.Steps)
            {
                linked.Token.ThrowIfCancellationRequested();
                var stepResult = await ExecuteStepAsync(step, state, options, linked.Token).ConfigureAwait(false);
                state.Steps.Add(stepResult);

                if (stepResult.Status == PlanStatus.Succeeded || stepResult.Status == PlanStatus.Skipped)
                {
                    if (stepResult.Output.HasValue)
                    {
                        state.Outputs[step.Id] = stepResult.Output.Value.Clone();
                    }

                    continue;
                }

                state.Status = stepResult.Status == PlanStatus.Cancelled ? PlanStatus.Cancelled : PlanStatus.Failed;
                state.Error = stepResult.Error;
                if (!string.Equals(step.OnFailure, "continue", StringComparison.OrdinalIgnoreCase) &&
                    options.StopOnFailure)
                {
                    break;
                }
            }

            if (state.Status == PlanStatus.Running)
            {
                state.Status = PlanStatus.Succeeded;
            }
        }
        catch (OperationCanceledException)
        {
            state.Status = PlanStatus.Cancelled;
            state.Error = new ErrorInfoDto
            {
                Code = ErrorCodes.Cancelled,
                Message = "Plan cancelled.",
                Retryable = false
            };
        }
        finally
        {
            state.CompletedAt = DateTimeOffset.UtcNow;
            _cancellations.TryRemove(planId, out _);
            linked.Dispose();
        }

        return state;
    }

    private async Task<StepExecutionResult> ExecuteStepAsync(
        PlanStep step,
        PlanExecutionState state,
        PlanOptions options,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var attempts = 0;
        var maxAttempts = 1 + Math.Max(0, step.Retries?.Count ?? 0);
        Exception? lastError = null;

        try
        {
            if (step.When is not null)
            {
                var whenOk = await _conditions.EvaluateOnceAsync(step.When, cancellationToken).ConfigureAwait(false);
                if (!whenOk)
                {
                    return new StepExecutionResult
                    {
                        StepId = step.Id,
                        Action = step.Action,
                        Status = PlanStatus.Skipped,
                        Attempts = 0,
                        DurationMs = started.ElapsedMilliseconds
                    };
                }
            }

            JsonElement? output = null;
            while (attempts < maxAttempts)
            {
                attempts++;
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    stepCts.CancelAfter(step.TimeoutMs ?? options.DefaultTimeoutMs);

                    var args = StepReferenceResolver.ResolveArgs(step.Args, state.Outputs);
                    output = await _runner(step.Action, args, stepCts.Token).ConfigureAwait(false);

                    if (output.Value.ValueKind == JsonValueKind.Object &&
                        output.Value.TryGetProperty("ok", out var ok) &&
                        ok.ValueKind == JsonValueKind.False)
                    {
                        string code = ErrorCodes.StepFailed;
                        string message = "Step failed.";
                        if (output.Value.TryGetProperty("error", out var err) &&
                            err.ValueKind == JsonValueKind.Object)
                        {
                            if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                            {
                                code = c.GetString() ?? code;
                            }

                            if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                            {
                                message = m.GetString() ?? message;
                            }
                        }

                        throw new InvalidOperationException($"{code}: {message}");
                    }

                    if (step.WaitAfter is not null)
                    {
                        await _conditions.WaitAsync(step.WaitAfter, options.DefaultTimeoutMs, stepCts.Token)
                            .ConfigureAwait(false);
                    }

                    return new StepExecutionResult
                    {
                        StepId = step.Id,
                        Action = step.Action,
                        Status = PlanStatus.Succeeded,
                        Attempts = attempts,
                        DurationMs = started.ElapsedMilliseconds,
                        Output = output
                    };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (attempts < maxAttempts)
                {
                    lastError = ex;
                    var delay = step.Retries?.DelayMs ?? 200;
                    if (step.Retries?.Backoff is > 0)
                    {
                        delay = (int)(delay * Math.Pow(step.Retries.Backoff.Value, attempts - 1));
                    }

                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }

            lastError ??= new InvalidOperationException(ErrorCodes.StepFailed);
            return FailureResult(step, attempts, started.ElapsedMilliseconds, lastError);
        }
        catch (OperationCanceledException)
        {
            return new StepExecutionResult
            {
                StepId = step.Id,
                Action = step.Action,
                Status = PlanStatus.Cancelled,
                Attempts = attempts,
                DurationMs = started.ElapsedMilliseconds,
                Error = new ErrorInfoDto
                {
                    Code = ErrorCodes.Cancelled,
                    Message = "Step cancelled.",
                    Retryable = false
                }
            };
        }
        catch (Exception ex)
        {
            return FailureResult(step, attempts, started.ElapsedMilliseconds, ex);
        }
    }

    private static StepExecutionResult FailureResult(PlanStep step, int attempts, long durationMs, Exception ex)
    {
        var code = ErrorCodes.StepFailed;
        var message = ex.Message;
        if (message.StartsWith("CONDITION_TIMEOUT", StringComparison.Ordinal) ||
            ex is TimeoutException)
        {
            code = ErrorCodes.ConditionTimeout;
        }
        else if (message.StartsWith("REFERENCE_ERROR", StringComparison.Ordinal))
        {
            code = ErrorCodes.ReferenceError;
        }
        else if (message.Contains(':'))
        {
            var prefix = message.Split(':', 2)[0].Trim();
            if (prefix is ErrorCodes.Timeout or ErrorCodes.StaleTarget or ErrorCodes.NotFound
                or ErrorCodes.InvalidArgument or ErrorCodes.Unsupported or ErrorCodes.Cancelled)
            {
                code = prefix;
                message = message[(message.IndexOf(':') + 1)..].Trim();
            }
        }

        return new StepExecutionResult
        {
            StepId = step.Id,
            Action = step.Action,
            Status = PlanStatus.Failed,
            Attempts = attempts,
            DurationMs = durationMs,
            Error = new ErrorInfoDto
            {
                Code = code,
                Message = message,
                Retryable = code is ErrorCodes.Timeout or ErrorCodes.ConditionTimeout or ErrorCodes.StaleTarget
            }
        };
    }
}
