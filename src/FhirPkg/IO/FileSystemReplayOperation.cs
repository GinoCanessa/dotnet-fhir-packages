// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Runtime.ExceptionServices;

namespace FhirPkg.IO;

internal enum FileSystemReplayState
{
    Ready,
    Running,
    WaitingToRetry,
    Suspended,
    Completed,
    Faulted,
    Canceled,
}

internal enum FileSystemReplayOutcomeKind
{
    Uninitialized,
    Completed,
    Continue,
    Missing,
    Contended,
    KindRejected,
    PrimitiveFailure,
    SemanticFailure,
}

internal enum FileSystemReplaySafety
{
    Unsafe,
    Safe,
}

/// <summary>
/// A step reports physical failures explicitly. Thrown exceptions are never retry signals.
/// </summary>
internal readonly struct FileSystemReplayOutcome<T>
{
    private readonly T? _value;

    private FileSystemReplayOutcome(
        FileSystemReplayOutcomeKind kind,
        T? value = default,
        FileSystemPrimitiveFailure? primitiveFailure = null,
        ExceptionDispatchInfo? semanticFailure = null)
    {
        Kind = kind;
        _value = value;
        PrimitiveFailure = primitiveFailure;
        SemanticException = semanticFailure;
    }

    internal FileSystemReplayOutcomeKind Kind { get; }

    internal FileSystemPrimitiveFailure? PrimitiveFailure { get; }

    internal ExceptionDispatchInfo? SemanticException { get; }

    internal T Value => Kind switch
    {
        FileSystemReplayOutcomeKind.Completed
            or FileSystemReplayOutcomeKind.Missing
            or FileSystemReplayOutcomeKind.Contended
            or FileSystemReplayOutcomeKind.KindRejected => _value!,
        _ => throw new InvalidOperationException("This replay outcome has no value."),
    };

    internal static FileSystemReplayOutcome<T> Complete(T value) =>
        new FileSystemReplayOutcome<T>(FileSystemReplayOutcomeKind.Completed, value);

    internal static FileSystemReplayOutcome<T> Continue() =>
        new FileSystemReplayOutcome<T>(FileSystemReplayOutcomeKind.Continue);

    internal static FileSystemReplayOutcome<T> Missing(T value) =>
        new FileSystemReplayOutcome<T>(FileSystemReplayOutcomeKind.Missing, value);

    internal static FileSystemReplayOutcome<T> Contended(T value) =>
        new FileSystemReplayOutcome<T>(FileSystemReplayOutcomeKind.Contended, value);

    internal static FileSystemReplayOutcome<T> KindRejected(T value) =>
        new FileSystemReplayOutcome<T>(FileSystemReplayOutcomeKind.KindRejected, value);

    internal static FileSystemReplayOutcome<T> Failed(FileSystemPrimitiveFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new FileSystemReplayOutcome<T>(
            FileSystemReplayOutcomeKind.PrimitiveFailure,
            primitiveFailure: failure);
    }

    internal static FileSystemReplayOutcome<T> SemanticFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new FileSystemReplayOutcome<T>(
            FileSystemReplayOutcomeKind.SemanticFailure,
            semanticFailure: ExceptionDispatchInfo.Capture(exception));
    }
}

/// <summary>
/// Owns one fault budget and any retained handles, buffers, or phase progress.
/// A contended outcome suspends execution for domain-owned coordination without
/// replenishing the budget. This object is not concurrently executable.
/// </summary>
internal abstract class FileSystemReplayOperation<T>
{
    private int _executing;
    private ExceptionDispatchInfo? _completionFailure;

    protected FileSystemReplayOperation(
        FileSystemRetryPolicy policy,
        string phase,
        long initialAttemptCount = 0,
        long initialRetriesUsed = 0)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentException.ThrowIfNullOrEmpty(phase);
        ArgumentOutOfRangeException.ThrowIfNegative(initialAttemptCount);
        ArgumentOutOfRangeException.ThrowIfNegative(initialRetriesUsed);
        if (initialRetriesUsed > policy.RetryCount || initialRetriesUsed > initialAttemptCount)
            throw new ArgumentOutOfRangeException(nameof(initialRetriesUsed));

        Policy = policy;
        Phase = phase;
        // Seeding is an internal seam for boundary-size accounting tests, not a new budget.
        AttemptCount = initialAttemptCount;
        RetriesUsed = initialRetriesUsed;
        FaultCount = initialRetriesUsed;
    }

    internal FileSystemRetryPolicy Policy { get; }

    internal string Phase { get; private set; }

    internal FileSystemReplayState State { get; private set; }

    // Counts step executions, including successful phase continuations and contention polls.
    internal long AttemptCount { get; private set; }

    internal long FaultCount { get; private set; }

    internal long RetriesUsed { get; private set; }

    internal long RemainingRetries => (long)Policy.RetryCount - RetriesUsed;

    internal abstract FileSystemReplayOutcome<T> ExecuteStep(CancellationToken cancellationToken);

    // Native/synchronous primitives still get asynchronous waits in an async protocol.
    internal virtual ValueTask<FileSystemReplayOutcome<T>> ExecuteStepAsync(
        CancellationToken cancellationToken) =>
        new ValueTask<FileSystemReplayOutcome<T>>(ExecuteStep(cancellationToken));

    protected void SetPhase(string phase)
    {
        ArgumentException.ThrowIfNullOrEmpty(phase);
        Phase = phase;
    }

    internal void EnterExecution()
    {
        if (Interlocked.CompareExchange(ref _executing, 1, 0) != 0)
            throw new InvalidOperationException("A replay operation is already executing.");

        try
        {
            _completionFailure?.Throw();
            if (State == FileSystemReplayState.Completed)
                throw new InvalidOperationException("A completed replay operation cannot be restarted.");
        }
        catch
        {
            ExitExecution();
            throw;
        }
    }

    internal void ExitExecution() => Volatile.Write(ref _executing, 0);

    internal void BeginAttempt()
    {
        AttemptCount = checked(AttemptCount + 1);
        State = FileSystemReplayState.Running;
    }

    internal void RecordFault() => FaultCount = checked(FaultCount + 1);

    internal void ScheduleRetry()
    {
        if (RemainingRetries == 0)
            throw new InvalidOperationException("The replay operation has no remaining retries.");

        RetriesUsed = checked(RetriesUsed + 1);
        State = FileSystemReplayState.WaitingToRetry;
    }

    internal void FinishStep(FileSystemReplayOutcomeKind kind)
    {
        State = kind switch
        {
            FileSystemReplayOutcomeKind.Continue => FileSystemReplayState.Ready,
            FileSystemReplayOutcomeKind.Contended => FileSystemReplayState.Suspended,
            _ => FileSystemReplayState.Completed,
        };
    }

    internal void Fail(Exception exception)
    {
        _completionFailure = ExceptionDispatchInfo.Capture(exception);
        State = exception is OperationCanceledException
            ? FileSystemReplayState.Canceled
            : FileSystemReplayState.Faulted;
    }
}
