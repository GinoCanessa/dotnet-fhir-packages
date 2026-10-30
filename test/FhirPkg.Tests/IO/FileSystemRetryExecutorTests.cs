// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FhirPkg.Installation;
using FhirPkg.IO;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.IO;

public class FileSystemRetryExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultPolicy_UsesSixAttemptsAndFiveWaits(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule(0, 10, 1, 9, 5);
        List<IOException> failures = [];
        List<string> originalStacks = [];
        ScriptedOperation operation = new ScriptedOperation(CreatePolicy(), (current, _) =>
        {
            schedule.Events.Add($"attempt:{current.Calls}");
            IOException exception = new IOException(
                $"physical failure {current.Calls}", new Win32Exception(5));
            failures.Add(exception);
            FileSystemPrimitiveFailure failure = FailAtPrimitive(exception);
            originalStacks.Add(exception.StackTrace!);
            return FileSystemReplayOutcome<int>.Failed(failure);
        });

        IOException final = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        operation.Calls.ShouldBe(6);
        operation.AttemptCount.ShouldBe(6L);
        operation.FaultCount.ShouldBe(6L);
        operation.RetriesUsed.ShouldBe(5L);
        operation.RemainingRetries.ShouldBe(0L);
        operation.State.ShouldBe(FileSystemReplayState.Faulted);
        schedule.AssertWaits(asynchronous, 10, 20, 11, 19, 15);
        schedule.Bounds.ShouldBe(new long[] { 11, 11, 11, 11, 11 });
        schedule.Events.ShouldBe(new[]
        {
            "attempt:1", "wait:10", "attempt:2", "wait:20",
            "attempt:3", "wait:11", "attempt:4", "wait:19",
            "attempt:5", "wait:15", "attempt:6",
        });
        final.ShouldBeSameAs(failures[5]);
        final.StackTrace.ShouldNotBeNull().ShouldStartWith(originalStacks[5]);
        final.StackTrace.ShouldContain(nameof(FailAtPrimitive));
        final.InnerException.ShouldBeOfType<Win32Exception>().NativeErrorCode.ShouldBe(5);
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? context).ShouldBeTrue();
        context.ShouldNotBeNull().AttemptCount.ShouldBe(6L);
        context.RetriesUsed.ShouldBe(5L);
        context.Termination.ShouldBe(FileSystemFailureTermination.Exhausted);
        foreach (IOException earlier in failures.Take(5))
        {
            FileSystemFailureContext.TryGetOrigin(earlier, out _).ShouldBeTrue();
            FileSystemFailureContext.TryGetTerminal(earlier, out _).ShouldBeFalse();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ZeroRetries_PerformsOneAttempt(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        IOException expected = new IOException("no retries");
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(retries: 0),
            (_, _) => FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(expected)));

        IOException final = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        final.ShouldBeSameAs(expected);
        final.StackTrace.ShouldNotBeNull().ShouldContain(nameof(FailAtPrimitive));
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.FaultCount.ShouldBe(1L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.RemainingRetries.ShouldBe(0L);
        schedule.AssertWaits(asynchronous);
        schedule.Bounds.ShouldBeEmpty();
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? context).ShouldBeTrue();
        context.ShouldNotBeNull().Termination.ShouldBe(FileSystemFailureTermination.Exhausted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Jitter_IsFreshAndInclusive(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule(0, 10, 2, 10, 0);
        ScriptedOperation operation = new ScriptedOperation(CreatePolicy(), (current, _) =>
            current.Calls == 6
                ? FileSystemReplayOutcome<int>.Complete(42)
                : FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(new IOException("retry"))));

        FileSystemReplayOutcome<int> result = await RunAsync(
            schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken);

        result.Kind.ShouldBe(FileSystemReplayOutcomeKind.Completed);
        result.Value.ShouldBe(42);
        operation.Calls.ShouldBe(6);
        operation.AttemptCount.ShouldBe(6L);
        operation.FaultCount.ShouldBe(5L);
        operation.RetriesUsed.ShouldBe(5L);
        operation.State.ShouldBe(FileSystemReplayState.Completed);
        schedule.AssertWaits(asynchronous, 10, 20, 12, 20, 10);
        schedule.Bounds.ShouldBe(new long[] { 11, 11, 11, 11, 11 });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Int32MaxRetryCount_DoesNotOverflowAccounting(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule(0);
        List<IOException> failures = [];
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(retries: int.MaxValue, minimum: 0, jitter: 0),
            (_, _) =>
            {
                IOException exception = new IOException("boundary");
                failures.Add(exception);
                return FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(exception));
            },
            initialAttemptCount: (long)int.MaxValue - 1,
            initialRetriesUsed: (long)int.MaxValue - 1);

        IOException final = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        operation.Policy.MaximumAttemptCount.ShouldBe(2_147_483_648L);
        operation.Calls.ShouldBe(2);
        operation.AttemptCount.ShouldBe(2_147_483_648L);
        operation.FaultCount.ShouldBe(2_147_483_648L);
        operation.RetriesUsed.ShouldBe((long)int.MaxValue);
        operation.RemainingRetries.ShouldBe(0L);
        schedule.AssertWaits(asynchronous, 0);
        schedule.Bounds.ShouldBe(new long[] { 1 });
        final.ShouldBeSameAs(failures[1]);
        final.StackTrace.ShouldNotBeNull().ShouldContain(nameof(FailAtPrimitive));
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? context).ShouldBeTrue();
        context.ShouldNotBeNull().AttemptCount.ShouldBe(2_147_483_648L);
        context.RetriesUsed.ShouldBe((long)int.MaxValue);
    }

    [Theory]
    [InlineData(false, 0, int.MaxValue, int.MaxValue)]
    [InlineData(true, 0, int.MaxValue, int.MaxValue)]
    [InlineData(false, int.MaxValue, 0, 0)]
    [InlineData(true, int.MaxValue, 0, 0)]
    [InlineData(false, int.MaxValue - 10, 10, 10)]
    [InlineData(true, int.MaxValue - 10, 10, 10)]
    public async Task Int32MaxDelay_UsesWideInclusiveSamplingWithoutSleeping(
        bool asynchronous,
        int minimum,
        int maximumJitter,
        int sample)
    {
        RecordingSchedule schedule = new RecordingSchedule(sample);
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(retries: 1, minimum: minimum, jitter: maximumJitter),
            (current, _) => current.Calls == 1
                ? FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(new IOException("retry")))
                : FileSystemReplayOutcome<int>.Complete(1));

        (await RunAsync(
            schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken)).Value.ShouldBe(1);

        operation.Calls.ShouldBe(2);
        operation.AttemptCount.ShouldBe(2L);
        operation.RetriesUsed.ShouldBe(1L);
        schedule.Bounds.ShouldBe(new long[] { (long)maximumJitter + 1 });
        schedule.AssertWaits(asynchronous, int.MaxValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyOperation_SchedulesNoWaits(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(),
            (_, _) => FileSystemReplayOutcome<int>.Complete(42));

        FileSystemReplayOutcome<int> result = await RunAsync(
            schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken);

        result.Value.ShouldBe(42);
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.FaultCount.ShouldBe(0L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.RemainingRetries.ShouldBe(5L);
        operation.State.ShouldBe(FileSystemReplayState.Completed);
        schedule.Bounds.ShouldBeEmpty();
        schedule.AssertWaits(asynchronous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_DoesNotRetry(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        using CancellationTokenSource source = new CancellationTokenSource();
        OperationCanceledException expected = new OperationCanceledException(source.Token);
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(),
            (_, _) => FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(expected)));

        OperationCanceledException? final = null;
        try
        {
            await RunAsync(schedule.Executor, operation, asynchronous, source.Token);
        }
        catch (OperationCanceledException exception)
        {
            // Await directly: the async assertion helper synthesizes a
            // TaskCanceledException instead of returning the original cancellation.
            final = exception;
        }

        final.ShouldNotBeNull().ShouldBeSameAs(expected);
        final.CancellationToken.ShouldBe(source.Token);
        final.StackTrace.ShouldNotBeNull().ShouldContain(nameof(FailAtPrimitive));
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.FaultCount.ShouldBe(0L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.State.ShouldBe(FileSystemReplayState.Canceled);
        schedule.Bounds.ShouldBeEmpty();
        schedule.AssertWaits(asynchronous);
        FileSystemFailureContext.TryGetOrigin(final, out _).ShouldBeFalse();
        FileSystemFailureContext.TryGetTerminal(final, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_BeforeInitialWork_PerformsNoAttempt(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        using CancellationTokenSource source = new CancellationTokenSource();
        source.Cancel();
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(),
            (_, _) => throw new InvalidOperationException("must not execute"));

        OperationCanceledException final = await Should.ThrowAsync<OperationCanceledException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, source.Token));

        final.CancellationToken.ShouldBe(source.Token);
        operation.Calls.ShouldBe(0);
        operation.AttemptCount.ShouldBe(0L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.State.ShouldBe(FileSystemReplayState.Canceled);
        schedule.Bounds.ShouldBeEmpty();
        schedule.AssertWaits(asynchronous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_AfterFailure_DoesNotScheduleRetry(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        using CancellationTokenSource source = new CancellationTokenSource();
        IOException failure = new IOException("canceled before retry");
        ScriptedOperation operation = new ScriptedOperation(CreatePolicy(), (_, _) =>
        {
            FileSystemPrimitiveFailure rawFailure = FailAtPrimitive(failure);
            source.Cancel();
            return FileSystemReplayOutcome<int>.Failed(rawFailure);
        });

        OperationCanceledException final = await Should.ThrowAsync<OperationCanceledException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, source.Token));

        final.CancellationToken.ShouldBe(source.Token);
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.State.ShouldBe(FileSystemReplayState.Canceled);
        schedule.Bounds.ShouldBeEmpty();
        schedule.AssertWaits(asynchronous);
        FileSystemFailureContext.TryGetTerminal(failure, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_DuringZeroDelay_PreventsReplayEvenWithMaximumBudget(bool asynchronous)
    {
        using CancellationTokenSource source = new CancellationTokenSource();
        RecordingSchedule schedule = new RecordingSchedule(0)
        {
            OnWait = (_, token) =>
            {
                token.ShouldBe(source.Token);
                source.Cancel();
            },
        };
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(retries: int.MaxValue, minimum: 0, jitter: 0),
            (_, _) => FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(new IOException("retry"))));

        OperationCanceledException final = await Should.ThrowAsync<OperationCanceledException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, source.Token));

        final.CancellationToken.ShouldBe(source.Token);
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.RetriesUsed.ShouldBe(1L);
        operation.RemainingRetries.ShouldBe((long)int.MaxValue - 1);
        operation.State.ShouldBe(FileSystemReplayState.Canceled);
        schedule.Bounds.ShouldBe(new long[] { 1 });
        schedule.AssertWaits(asynchronous, 0);
    }

    [Fact]
    public async Task AsyncWait_ObservesCancellationWhilePending()
    {
        using CancellationTokenSource source = new CancellationTokenSource();
        TaskCompletionSource enteredWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> waits = [];
        List<long> bounds = [];
        int synchronousWaits = 0;
        FileSystemRetryExecutor executor = new FileSystemRetryExecutor(
            maximum =>
            {
                bounds.Add(maximum);
                return 0;
            },
            (_, _) => synchronousWaits++,
            async (milliseconds, token) =>
            {
                token.ShouldBe(source.Token);
                waits.Add(milliseconds);
                using CancellationTokenRegistration registration = token.Register(
                    () => releaseWait.TrySetCanceled(token));
                enteredWait.SetResult();
                await releaseWait.Task;
            });
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(),
            (_, _) => FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(new IOException("retry"))));

        Task<FileSystemReplayOutcome<int>> execution = executor.ExecuteAsync(operation, source.Token).AsTask();
        await enteredWait.Task.WaitAsync(TestContext.Current.CancellationToken);
        execution.IsCompleted.ShouldBeFalse();
        operation.State.ShouldBe(FileSystemReplayState.WaitingToRetry);
        source.Cancel();

        OperationCanceledException final = await Should.ThrowAsync<OperationCanceledException>(() => execution);
        final.CancellationToken.ShouldBe(source.Token);
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.RetriesUsed.ShouldBe(1L);
        operation.State.ShouldBe(FileSystemReplayState.Canceled);
        synchronousWaits.ShouldBe(0);
        waits.ShouldBe(new[] { 10 });
        bounds.ShouldBe(new long[] { 11 });
    }

    [Fact]
    public async Task AsyncExecutor_AwaitsStepsAndDelaysWithoutSynchronousWaiting()
    {
        TaskCompletionSource<FileSystemReplayOutcome<int>> firstStep = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource enteredWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        List<int> waits = [];
        FileSystemRetryExecutor executor = new FileSystemRetryExecutor(
            bound =>
            {
                bound.ShouldBe(11L);
                return 10;
            },
            (_, _) => throw new InvalidOperationException("async execution must not block"),
            (milliseconds, _) =>
            {
                waits.Add(milliseconds);
                enteredWait.SetResult();
                return new ValueTask(releaseWait.Task);
            });
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(),
            (_, _) => throw new InvalidOperationException("must use the async step"))
        {
            AsyncStep = (current, _) => current.Calls == 1
                ? new ValueTask<FileSystemReplayOutcome<int>>(firstStep.Task)
                : new ValueTask<FileSystemReplayOutcome<int>>(FileSystemReplayOutcome<int>.Complete(42)),
        };

        Task<FileSystemReplayOutcome<int>> execution = executor.ExecuteAsync(
            operation, TestContext.Current.CancellationToken).AsTask();
        execution.IsCompleted.ShouldBeFalse();
        operation.Calls.ShouldBe(1);
        waits.ShouldBeEmpty();
        firstStep.SetResult(FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(new IOException("retry"))));
        await enteredWait.Task.WaitAsync(TestContext.Current.CancellationToken);
        execution.IsCompleted.ShouldBeFalse();
        operation.Calls.ShouldBe(1);
        releaseWait.SetResult();

        (await execution).Value.ShouldBe(42);
        operation.Calls.ShouldBe(2);
        operation.AttemptCount.ShouldBe(2L);
        operation.RetriesUsed.ShouldBe(1L);
        waits.ShouldBe(new[] { 20 });
    }

    [Theory]
    [InlineData(false, "argument")]
    [InlineData(true, "argument")]
    [InlineData(false, "json")]
    [InlineData(true, "json")]
    [InlineData(false, "gzip")]
    [InlineData(true, "gzip")]
    [InlineData(false, "tar")]
    [InlineData(true, "tar")]
    [InlineData(false, "limit")]
    [InlineData(true, "limit")]
    [InlineData(false, "checksum")]
    [InlineData(true, "checksum")]
    [InlineData(false, "identity")]
    [InlineData(true, "identity")]
    [InlineData(false, "io-observer")]
    [InlineData(true, "io-observer")]
    [InlineData(false, "unsupported-platform")]
    [InlineData(true, "unsupported-platform")]
    public async Task SemanticFailure_DoesNotSchedule(bool asynchronous, string kind)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        Exception expected = SemanticException(kind);
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(),
            (_, _) => FileSystemReplayOutcome<int>.SemanticFailure(expected));

        Exception final = await Should.ThrowAsync<Exception>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        final.ShouldBeSameAs(expected);
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.FaultCount.ShouldBe(0L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.RemainingRetries.ShouldBe(5L);
        operation.State.ShouldBe(FileSystemReplayState.Faulted);
        schedule.Bounds.ShouldBeEmpty();
        schedule.AssertWaits(asynchronous);
        FileSystemFailureContext.TryGetOrigin(final, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThrownCallbackIOException_IsNotAReplaySignal(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        IOException expected = new IOException("fault observer");
        int callbackCalls = 0;
        ScriptedOperation operation = new ScriptedOperation(CreatePolicy(), (_, _) =>
        {
            callbackCalls++;
            throw expected;
        });

        IOException final = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        final.ShouldBeSameAs(expected);
        callbackCalls.ShouldBe(1);
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.FaultCount.ShouldBe(0L);
        operation.RetriesUsed.ShouldBe(0L);
        schedule.AssertWaits(asynchronous);
        schedule.Bounds.ShouldBeEmpty();
        FileSystemFailureContext.TryGetOrigin(final, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "kind")]
    [InlineData(true, "kind")]
    public async Task ExpectedPrimitiveOutcomes_DoNotSpendFaultBudget(bool asynchronous, string kind)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(),
            (_, _) => kind == "missing"
                ? FileSystemReplayOutcome<int>.Missing(0)
                : FileSystemReplayOutcome<int>.KindRejected(0));

        FileSystemReplayOutcome<int> result = await RunAsync(
            schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken);

        result.Kind.ShouldBe(kind == "missing"
            ? FileSystemReplayOutcomeKind.Missing
            : FileSystemReplayOutcomeKind.KindRejected);
        result.Value.ShouldBe(0);
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.FaultCount.ShouldBe(0L);
        operation.State.ShouldBe(FileSystemReplayState.Completed);
        schedule.Bounds.ShouldBeEmpty();
        schedule.AssertWaits(asynchronous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompoundPhases_ShareRemainingBudget(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule(0, 2, 1);
        int moveCalls = 0;
        int sourceSyncCalls = 0;
        int destinationSyncCalls = 0;
        int completedMutations = 0;
        bool sourceSynced = false;
        IOException? lastFailure = null;
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(retries: 3, minimum: 7, jitter: 2),
            (current, _) =>
            {
                if (completedMutations == 0)
                {
                    current.ChangePhase("move");
                    moveCalls++;
                    if (moveCalls == 1)
                        return FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(new IOException("move")));

                    completedMutations++;
                    return FileSystemReplayOutcome<int>.Continue();
                }

                if (!sourceSynced)
                {
                    current.ChangePhase("sync source");
                    sourceSyncCalls++;
                    if (sourceSyncCalls == 1)
                        return FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(new IOException("sync source")));

                    sourceSynced = true;
                    return FileSystemReplayOutcome<int>.Continue();
                }

                current.ChangePhase("sync destination");
                destinationSyncCalls++;
                lastFailure = new IOException("sync destination");
                return FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(lastFailure));
            });

        IOException final = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        moveCalls.ShouldBe(2);
        completedMutations.ShouldBe(1);
        sourceSyncCalls.ShouldBe(2);
        destinationSyncCalls.ShouldBe(2);
        operation.Calls.ShouldBe(6);
        operation.AttemptCount.ShouldBe(6L);
        operation.FaultCount.ShouldBe(4L);
        operation.RetriesUsed.ShouldBe(3L);
        operation.RemainingRetries.ShouldBe(0L);
        schedule.Bounds.ShouldBe(new long[] { 3, 3, 3 });
        schedule.AssertWaits(asynchronous, 7, 9, 8);
        final.ShouldBeSameAs(lastFailure);
        final.StackTrace.ShouldNotBeNull().ShouldContain(nameof(FailAtPrimitive));
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? context).ShouldBeTrue();
        context.ShouldNotBeNull().Phase.ShouldBe("sync destination");
        context.AttemptCount.ShouldBe(6L);
        context.RetriesUsed.ShouldBe(3L);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryPhase_ConsumesTheFailedReadsRemainingBudget(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule(0, 0);
        int readCalls = 0;
        int restoreCalls = 0;
        bool restoreRequired = false;
        IOException? lastFailure = null;
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(retries: 2, minimum: 0, jitter: 0),
            (current, _) =>
            {
                if (restoreRequired)
                {
                    current.ChangePhase("restore");
                    restoreCalls++;
                    if (restoreCalls == 1)
                        return FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(new IOException("restore")));

                    restoreRequired = false;
                    return FileSystemReplayOutcome<int>.Continue();
                }

                current.ChangePhase("read");
                readCalls++;
                restoreRequired = true;
                lastFailure = new IOException("read changed position");
                return FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(lastFailure));
            });

        IOException final = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        readCalls.ShouldBe(2);
        restoreCalls.ShouldBe(2);
        operation.Calls.ShouldBe(4);
        operation.AttemptCount.ShouldBe(4L);
        operation.FaultCount.ShouldBe(3L);
        operation.RetriesUsed.ShouldBe(2L);
        operation.RemainingRetries.ShouldBe(0L);
        schedule.Bounds.ShouldBe(new long[] { 1, 1 });
        schedule.AssertWaits(asynchronous, 0, 0);
        final.ShouldBeSameAs(lastFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContendedOutcomes_ResumeWithoutReplenishingFaultBudget(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule(0, 1);
        IOException? lastFailure = null;
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(retries: 2),
            (current, _) =>
            {
                if (current.Calls is 2 or 3 or 5)
                    return FileSystemReplayOutcome<int>.Contended(0);

                lastFailure = new IOException("lock fault");
                return FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(lastFailure));
            });

        (await RunAsync(
            schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken))
            .Kind.ShouldBe(FileSystemReplayOutcomeKind.Contended);
        operation.Calls.ShouldBe(2);
        operation.State.ShouldBe(FileSystemReplayState.Suspended);
        operation.RemainingRetries.ShouldBe(1L);
        (await RunAsync(
            schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken))
            .Kind.ShouldBe(FileSystemReplayOutcomeKind.Contended);
        operation.Calls.ShouldBe(3);
        operation.RemainingRetries.ShouldBe(1L);
        (await RunAsync(
            schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken))
            .Kind.ShouldBe(FileSystemReplayOutcomeKind.Contended);
        operation.Calls.ShouldBe(5);
        operation.RemainingRetries.ShouldBe(0L);
        IOException final = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        final.ShouldBeSameAs(lastFailure);
        operation.Calls.ShouldBe(6);
        operation.AttemptCount.ShouldBe(6L);
        operation.FaultCount.ShouldBe(3L);
        operation.RetriesUsed.ShouldBe(2L);
        operation.State.ShouldBe(FileSystemReplayState.Faulted);
        schedule.Bounds.ShouldBe(new long[] { 11, 11 });
        schedule.AssertWaits(asynchronous, 10, 11);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsafePrimitiveFailure_IsTerminalWithoutRetry(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        IOException expected = new IOException("ambiguous side effect", new Win32Exception(31));
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(),
            (_, _) => FileSystemReplayOutcome<int>.Failed(
                FailAtPrimitive(expected, FileSystemReplaySafety.Unsafe)));

        IOException final = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        final.ShouldBeSameAs(expected);
        final.StackTrace.ShouldNotBeNull().ShouldContain(nameof(FailAtPrimitive));
        final.InnerException.ShouldBeSameAs(expected.InnerException);
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.FaultCount.ShouldBe(1L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.RemainingRetries.ShouldBe(5L);
        schedule.Bounds.ShouldBeEmpty();
        schedule.AssertWaits(asynchronous);
        FileSystemFailureContext.TryGetTerminal(final, out FileSystemFailureContext? context).ShouldBeTrue();
        context.ShouldNotBeNull().Termination.ShouldBe(FileSystemFailureTermination.Unsafe);
        context.AttemptCount.ShouldBe(1L);
        context.RetriesUsed.ShouldBe(0L);
    }

    [Theory]
    [InlineData(false, "access")]
    [InlineData(true, "access")]
    [InlineData(false, "device")]
    [InlineData(true, "device")]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "native-unsupported")]
    [InlineData(true, "native-unsupported")]
    public async Task SafeRawOrigin_NotExceptionTypeOrTransientAllowlist_DeterminesEligibility(
        bool asynchronous,
        string kind)
    {
        RecordingSchedule schedule = new RecordingSchedule(0);
        Exception expected = kind switch
        {
            "access" => new UnauthorizedAccessException("physical access failure"),
            "device" => new IOException("physical device failure", new Win32Exception(31)),
            "unknown" => new IOException("unknown physical failure", new Win32Exception(12345)),
            _ => new NotSupportedException("returned native unsupported failure"),
        };
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(retries: 1),
            (_, _) => FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(expected)));

        Exception final = await Should.ThrowAsync<Exception>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        final.ShouldBeSameAs(expected);
        final.GetType().ShouldBe(expected.GetType());
        final.StackTrace.ShouldNotBeNull().ShouldContain(nameof(FailAtPrimitive));
        operation.Calls.ShouldBe(2);
        operation.AttemptCount.ShouldBe(2L);
        operation.FaultCount.ShouldBe(2L);
        operation.RetriesUsed.ShouldBe(1L);
        schedule.Bounds.ShouldBe(new long[] { 11 });
        schedule.AssertWaits(asynchronous, 10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalOperationAndWrappedFailure_CannotReceiveAnotherBudget(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule(0);
        IOException expected = new IOException("terminal");
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(retries: 1),
            (_, _) => FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(expected)));
        IOException first = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));
        FileSystemFailureContext.TryGetTerminal(first, out FileSystemFailureContext? context).ShouldBeTrue();

        IOException second = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        second.ShouldBeSameAs(first);
        operation.Calls.ShouldBe(2);
        operation.AttemptCount.ShouldBe(2L);
        operation.RetriesUsed.ShouldBe(1L);
        HttpRequestException wrapper = new HttpRequestException("transport wrapper", first);
        ScriptedOperation boundary = new ScriptedOperation(
            CreatePolicy(),
            (_, _) => FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(wrapper)));

        HttpRequestException wrapped = await Should.ThrowAsync<HttpRequestException>(
            () => RunAsync(schedule.Executor, boundary, asynchronous, TestContext.Current.CancellationToken));

        wrapped.ShouldBeSameAs(wrapper);
        boundary.Calls.ShouldBe(1);
        boundary.AttemptCount.ShouldBe(1L);
        boundary.RetriesUsed.ShouldBe(0L);
        FileSystemFailureContext.TryGetTerminal(wrapped, out FileSystemFailureContext? retained).ShouldBeTrue();
        retained.ShouldBeSameAs(context);
        schedule.Bounds.ShouldBe(new long[] { 11 });
        schedule.AssertWaits(asynchronous, 10);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayCallbackFailure_IsNotReplayedOrMarkedAsFilesystemFailure(bool asynchronous)
    {
        IOException expected = new IOException("injected wait observer");
        RecordingSchedule schedule = new RecordingSchedule(0)
        {
            OnWait = (_, _) => throw expected,
        };
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(),
            (_, _) => FileSystemReplayOutcome<int>.Failed(FailAtPrimitive(new IOException("physical"))));

        IOException final = await Should.ThrowAsync<IOException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        final.ShouldBeSameAs(expected);
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.RetriesUsed.ShouldBe(1L);
        schedule.Bounds.ShouldBe(new long[] { 11 });
        schedule.AssertWaits(asynchronous, 10);
        FileSystemFailureContext.TryGetOrigin(final, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultOutcome_IsNotMistakenForSuccessfulWork(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        ScriptedOperation operation = new ScriptedOperation(CreatePolicy(), (_, _) => default);

        await Should.ThrowAsync<InvalidOperationException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.State.ShouldBe(FileSystemReplayState.Faulted);
        schedule.Bounds.ShouldBeEmpty();
        schedule.AssertWaits(asynchronous);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedOperation_CannotRepeatItsMutation(bool asynchronous)
    {
        RecordingSchedule schedule = new RecordingSchedule();
        ScriptedOperation operation = new ScriptedOperation(
            CreatePolicy(), (_, _) => FileSystemReplayOutcome<int>.Complete(1));

        (await RunAsync(
            schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken)).Value.ShouldBe(1);
        await Should.ThrowAsync<InvalidOperationException>(
            () => RunAsync(schedule.Executor, operation, asynchronous, TestContext.Current.CancellationToken));

        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.State.ShouldBe(FileSystemReplayState.Completed);
        schedule.Bounds.ShouldBeEmpty();
        schedule.AssertWaits(asynchronous);
    }

    private static FileSystemRetryPolicy CreatePolicy(int retries = 5, int minimum = 10, int jitter = 10) =>
        FileSystemRetryPolicy.Resolve(new FileSystemRetryOptions
        {
            RetryCount = retries,
            MinimumDelayMilliseconds = minimum,
            MaximumJitterMilliseconds = jitter,
        });

    private static async Task<FileSystemReplayOutcome<int>> RunAsync(
        FileSystemRetryExecutor executor,
        ScriptedOperation operation,
        bool asynchronous,
        CancellationToken cancellationToken) =>
        asynchronous
            ? await executor.ExecuteAsync(operation, cancellationToken)
            : executor.Execute(operation, cancellationToken);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FileSystemPrimitiveFailure FailAtPrimitive(
        Exception exception,
        FileSystemReplaySafety safety = FileSystemReplaySafety.Safe)
    {
        try
        {
            throw exception;
        }
        catch (Exception captured)
        {
            return FileSystemFailureContext.CapturePrimitiveFailure(
                captured, safety, "raw operation", "retained path", nativeErrorCode: 5, nativeFlags: 8);
        }
    }

    private static Exception SemanticException(string kind) => kind switch
    {
        "argument" => new ArgumentException("invalid path"),
        "json" => new JsonException("invalid JSON"),
        "gzip" => new InvalidDataException("invalid gzip"),
        "tar" => new EndOfStreamException("invalid tar"),
        "limit" => new PackageInstallException(
            PackageInstallErrorCode.ExpandedSizeLimitExceeded, PackageInstallStage.ArchiveValidation, "limit"),
        "checksum" => new PackageInstallException(
            PackageInstallErrorCode.ChecksumMismatch, PackageInstallStage.ChecksumValidation, "checksum"),
        "identity" => new PackageInstallException(
            PackageInstallErrorCode.InvalidPackageIdentity, PackageInstallStage.IdentityValidation, "identity"),
        "io-observer" => new IOException("fault observer"),
        _ => new PlatformNotSupportedException("managed platform rejection"),
    };

    private sealed class ScriptedOperation : FileSystemReplayOperation<int>
    {
        private readonly Func<ScriptedOperation, CancellationToken, FileSystemReplayOutcome<int>> _step;

        internal ScriptedOperation(
            FileSystemRetryPolicy policy,
            Func<ScriptedOperation, CancellationToken, FileSystemReplayOutcome<int>> step,
            long initialAttemptCount = 0,
            long initialRetriesUsed = 0)
            : base(policy, "test phase", initialAttemptCount, initialRetriesUsed)
        {
            _step = step;
        }

        internal int Calls { get; private set; }

        internal Func<ScriptedOperation, CancellationToken, ValueTask<FileSystemReplayOutcome<int>>>? AsyncStep { get; init; }

        internal void ChangePhase(string phase) => SetPhase(phase);

        internal override FileSystemReplayOutcome<int> ExecuteStep(CancellationToken cancellationToken)
        {
            Calls++;
            return _step(this, cancellationToken);
        }

        internal override ValueTask<FileSystemReplayOutcome<int>> ExecuteStepAsync(CancellationToken cancellationToken)
        {
            if (AsyncStep is null)
                return base.ExecuteStepAsync(cancellationToken);

            Calls++;
            return AsyncStep(this, cancellationToken);
        }
    }

    private sealed class RecordingSchedule
    {
        internal RecordingSchedule(params long[] samples)
        {
            Queue<long> pending = new Queue<long>(samples);
            Executor = new FileSystemRetryExecutor(
                bound =>
                {
                    Bounds.Add(bound);
                    return pending.Dequeue();
                },
                (milliseconds, token) =>
                {
                    SynchronousWaits++;
                    RecordWait(milliseconds, token);
                },
                (milliseconds, token) =>
                {
                    AsynchronousWaits++;
                    RecordWait(milliseconds, token);
                    return ValueTask.CompletedTask;
                });
        }

        internal FileSystemRetryExecutor Executor { get; }

        internal List<int> Waits { get; } = [];

        internal List<long> Bounds { get; } = [];

        internal List<string> Events { get; } = [];

        internal int SynchronousWaits { get; private set; }

        internal int AsynchronousWaits { get; private set; }

        internal Action<int, CancellationToken>? OnWait { get; init; }

        internal void AssertWaits(bool asynchronous, params int[] expected)
        {
            Waits.ShouldBe(expected);
            SynchronousWaits.ShouldBe(asynchronous ? 0 : expected.Length);
            AsynchronousWaits.ShouldBe(asynchronous ? expected.Length : 0);
        }

        private void RecordWait(int milliseconds, CancellationToken cancellationToken)
        {
            Waits.Add(milliseconds);
            Events.Add($"wait:{milliseconds}");
            OnWait?.Invoke(milliseconds, cancellationToken);
        }
    }
}
