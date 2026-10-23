// Copyright (c) Gino Canessa. Licensed under the MIT License.

namespace FhirPkg.IO;

/// <summary>
/// The sole filesystem fault scheduler. Only an explicit primitive failure can
/// spend a retry; application code, observers, and semantic failures are not caught
/// for replay.
/// </summary>
internal sealed class FileSystemRetryExecutor
{
    private readonly Func<long, long> _nextInt64;
    private readonly Action<int, CancellationToken> _delay;
    private readonly Func<int, CancellationToken, ValueTask> _delayAsync;

    internal FileSystemRetryExecutor()
        : this(
            Random.Shared.NextInt64,
            Wait,
            static (milliseconds, cancellationToken) =>
                new ValueTask(Task.Delay(milliseconds, cancellationToken)))
    {
    }

    internal FileSystemRetryExecutor(
        Func<long, long> nextInt64,
        Action<int, CancellationToken> delay,
        Func<int, CancellationToken, ValueTask> delayAsync)
    {
        ArgumentNullException.ThrowIfNull(nextInt64);
        ArgumentNullException.ThrowIfNull(delay);
        ArgumentNullException.ThrowIfNull(delayAsync);
        _nextInt64 = nextInt64;
        _delay = delay;
        _delayAsync = delayAsync;
    }

    internal FileSystemReplayOutcome<T> Execute<T>(
        FileSystemReplayOperation<T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.EnterExecution();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation.BeginAttempt();
                FileSystemReplayOutcome<T> outcome = operation.ExecuteStep(cancellationToken);
                if (!ProcessOutcome(operation, outcome, cancellationToken, out int? delay))
                    return outcome;

                if (delay.HasValue)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _delay(delay.Value, cancellationToken);
                }
            }
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
        finally
        {
            operation.ExitExecution();
        }
    }

    internal async ValueTask<FileSystemReplayOutcome<T>> ExecuteAsync<T>(
        FileSystemReplayOperation<T> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        operation.EnterExecution();
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                operation.BeginAttempt();
                FileSystemReplayOutcome<T> outcome = await operation.ExecuteStepAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (!ProcessOutcome(operation, outcome, cancellationToken, out int? delay))
                    return outcome;

                if (delay.HasValue)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await _delayAsync(delay.Value, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception)
        {
            operation.Fail(exception);
            throw;
        }
        finally
        {
            operation.ExitExecution();
        }
    }

    private bool ProcessOutcome<T>(
        FileSystemReplayOperation<T> operation,
        FileSystemReplayOutcome<T> outcome,
        CancellationToken cancellationToken,
        out int? delay)
    {
        delay = null;
        switch (outcome.Kind)
        {
            case FileSystemReplayOutcomeKind.Completed:
            case FileSystemReplayOutcomeKind.Missing:
            case FileSystemReplayOutcomeKind.KindRejected:
            case FileSystemReplayOutcomeKind.Contended:
                operation.FinishStep(outcome.Kind);
                return false;

            case FileSystemReplayOutcomeKind.Continue:
                operation.FinishStep(outcome.Kind);
                return true;

            case FileSystemReplayOutcomeKind.SemanticFailure:
                outcome.SemanticException!.Throw();
                throw new InvalidOperationException("Unreachable semantic failure.");

            case FileSystemReplayOutcomeKind.PrimitiveFailure:
                FileSystemPrimitiveFailure failure = outcome.PrimitiveFailure!;
                if (failure.Exception is OperationCanceledException)
                    failure.Throw();

                operation.RecordFault();
                if (FileSystemFailureContext.TryGetTerminal(failure.Exception, out _))
                    failure.Throw();

                if (failure.ReplaySafety == FileSystemReplaySafety.Unsafe)
                {
                    failure.ThrowTerminal(
                        operation.Phase,
                        operation.AttemptCount,
                        operation.RetriesUsed,
                        FileSystemFailureTermination.Unsafe);
                }

                if (operation.RemainingRetries == 0)
                {
                    failure.ThrowTerminal(
                        operation.Phase,
                        operation.AttemptCount,
                        operation.RetriesUsed,
                        FileSystemFailureTermination.Exhausted);
                }

                cancellationToken.ThrowIfCancellationRequested();
                // The exclusive bound needs 64 bits when the inclusive maximum is Int32.MaxValue.
                long jitter = _nextInt64((long)operation.Policy.MaximumJitterMilliseconds + 1);
                if (jitter < 0 || jitter > operation.Policy.MaximumJitterMilliseconds)
                    throw new InvalidOperationException("The jitter provider returned an out-of-range sample.");

                cancellationToken.ThrowIfCancellationRequested();
                delay = checked((int)((long)operation.Policy.MinimumDelayMilliseconds + jitter));
                operation.ScheduleRetry();
                return true;

            default:
                throw new InvalidOperationException("An uninitialized replay outcome is not a successful operation.");
        }
    }

    private static void Wait(int milliseconds, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.WaitHandle.WaitOne(milliseconds);
            cancellationToken.ThrowIfCancellationRequested();
        }
        else
        {
            Thread.Sleep(milliseconds);
        }
    }
}
