// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FhirPkg.IO;
using FhirPkg.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.IO;

public class FileSystemFailureContextTests
{
    [Fact]
    public void TerminalResultProvenance_DoesNotChangePublicShapeOrEquality()
    {
        PublishResult result = new PublishResult
        {
            Success = false,
            Message = "local failure",
            StatusCode = HttpStatusCode.InternalServerError,
        };
        PublishResult equalResult = result with { };
        int originalHash = result.GetHashCode();
        string originalJson = JsonSerializer.Serialize(result);
        JsonSerializerOptions webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string originalWebJson = JsonSerializer.Serialize(result, webOptions);
        HashSet<PublishResult> values = [result];
        IOException failure = new IOException("physical read", new Win32Exception(5));
        MakeTerminal(failure);
        HttpRequestException wrapper = new HttpRequestException("transport wrapped local failure", failure);

        FileSystemFailureContext.AttachToResult(result, wrapper).ShouldBeSameAs(result);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("local failure");
        result.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        result.ShouldBe(equalResult);
        (result == equalResult).ShouldBeTrue();
        result.GetHashCode().ShouldBe(originalHash);
        equalResult.GetHashCode().ShouldBe(originalHash);
        values.Contains(equalResult).ShouldBeTrue();
        JsonSerializer.Serialize(result).ShouldBe(originalJson);
        JsonSerializer.Serialize(result, webOptions).ShouldBe(originalWebJson);
        typeof(PublishResult).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe(new[] { "Message", "StatusCode", "Success" });
        using JsonDocument document = JsonDocument.Parse(originalJson);
        document.RootElement.EnumerateObject()
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe(new[] { "Message", "StatusCode", "Success" });

        FileSystemFailureContext.TryGetResult(result, out FileSystemFailureContext? context).ShouldBeTrue();
        FileSystemFailureContext.TryGetTerminal(wrapper, out FileSystemFailureContext? exceptionContext).ShouldBeTrue();
        context.ShouldBeSameAs(exceptionContext);
        FileSystemFailureContext.TryGetResult(equalResult, out _).ShouldBeFalse();
        FileSystemFailureContext.TryGetResult(result with { }, out _).ShouldBeFalse();
        PublishResult restored = JsonSerializer.Deserialize<PublishResult>(originalJson).ShouldNotBeNull();
        restored.ShouldBe(result);
        FileSystemFailureContext.TryGetResult(restored, out _).ShouldBeFalse();
    }

    [Fact]
    public void PrimitiveOrigin_IsNotTerminalUntilExhaustion()
    {
        IOException exception = new IOException("physical failure");
        FileSystemPrimitiveFailure failure = ThrowAtPrimitive(exception);

        failure.Exception.ShouldBeSameAs(exception);
        failure.ReplaySafety.ShouldBe(FileSystemReplaySafety.Safe);
        FileSystemFailureContext.TryGetOrigin(exception, out FileSystemFailureContext? origin).ShouldBeTrue();
        origin.ShouldNotBeNull().IsTerminal.ShouldBeFalse();
        origin.Termination.ShouldBe(FileSystemFailureTermination.None);
        origin.Operation.ShouldBe("read");
        origin.Path.ShouldBe("source");
        origin.DestinationPath.ShouldBe("destination");
        origin.NativeErrorCode.ShouldBe(5);
        origin.NativeFlags.ShouldBe(0x80000000L);
        origin.AttemptCount.ShouldBe(0L);
        origin.RetriesUsed.ShouldBe(0L);
        FileSystemFailureContext.TryGetTerminal(exception, out _).ShouldBeFalse();
        FileSystemFailureContext.FormatMessage(exception).ShouldBe("physical failure");
        PublishResult result = new PublishResult { Success = false };
        FileSystemFailureContext.AttachToResult(result, exception);
        FileSystemFailureContext.TryGetResult(result, out _).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => FileSystemFailureContext.AttachToResult(result, origin));

        ExecuteToTerminal(failure);

        FileSystemFailureContext.TryGetTerminal(exception, out FileSystemFailureContext? terminal).ShouldBeTrue();
        terminal.ShouldNotBeNull().Termination.ShouldBe(FileSystemFailureTermination.Exhausted);
        terminal.AttemptCount.ShouldBe(1L);
        terminal.RetriesUsed.ShouldBe(0L);
        origin.IsTerminal.ShouldBeFalse();
    }

    [Fact]
    public void FinalException_PreservesIdentityTypeOriginalStackNativeInnerAndData()
    {
        Win32Exception native = new Win32Exception(31);
        IOException exception = new IOException("native device failure", native);
        object existingData = new object();
        exception.Data["existing"] = existingData;
        FileSystemPrimitiveFailure failure = ThrowAtPrimitive(exception);
        string originalStack = exception.StackTrace.ShouldNotBeNull();

        Exception final = ExecuteToTerminal(failure);

        final.ShouldBeSameAs(exception);
        final.GetType().ShouldBe(typeof(IOException));
        final.Message.ShouldBe("native device failure");
        final.InnerException.ShouldBeSameAs(native);
        native.NativeErrorCode.ShouldBe(31);
        final.StackTrace.ShouldNotBeNull().ShouldStartWith(originalStack);
        final.StackTrace.ShouldContain(nameof(ThrowAtPrimitive));
        final.Data.Count.ShouldBe(1);
        final.Data["existing"].ShouldBeSameAs(existingData);
        FileSystemFailureContext.TryGetTerminal(final, out _).ShouldBeTrue();
    }

    [Fact]
    public void UnmarkedExceptions_DoNotAcquireTerminalProvenanceByTypeOrMessage()
    {
        IOException physicalLooking = new IOException("Filesystem operation failed after 6 attempts");
        HttpRequestException wrapper = new HttpRequestException("network failure", physicalLooking);
        PublishResult result = new PublishResult { Success = false, Message = wrapper.Message };

        FileSystemFailureContext.TryGetOrigin(wrapper, out _).ShouldBeFalse();
        FileSystemFailureContext.TryGetTerminal(wrapper, out _).ShouldBeFalse();
        FileSystemFailureContext.FormatMessage(wrapper).ShouldBe(wrapper.Message);
        FileSystemFailureContext.AttachToResult(result, wrapper).ShouldBeSameAs(result);
        FileSystemFailureContext.TryGetResult(result, out _).ShouldBeFalse();
    }

    [Fact]
    public void TerminalProvenance_FollowsInnerExceptionsAndEveryAggregateBranch()
    {
        IOException terminal = new IOException("local");
        MakeTerminal(terminal);
        Exception wrapper = new HttpRequestException(
            "HTTP",
            new InvalidOperationException(
                "application wrapper",
                new AggregateException(
                    new IOException("ordinary first child"),
                    new AggregateException(
                        new IOException("ordinary nested child"),
                        terminal),
                    terminal)));

        FileSystemFailureContext.TryGetTerminal(wrapper, out FileSystemFailureContext? context).ShouldBeTrue();
        FileSystemFailureContext.TryGetTerminal(terminal, out FileSystemFailureContext? expected).ShouldBeTrue();
        context.ShouldBeSameAs(expected);
        FileSystemFailureContext.TryGetOrigin(wrapper, out FileSystemFailureContext? origin).ShouldBeTrue();
        origin.ShouldBeSameAs(expected);
    }

    [Fact]
    public void NonterminalOuterOrigin_DoesNotHideInnerTerminalEvidence()
    {
        IOException terminal = new IOException("local");
        MakeTerminal(terminal);
        IOException wrapper = new IOException("outer", new HttpRequestException("HTTP", terminal));
        ThrowAtPrimitive(wrapper);

        FileSystemFailureContext.TryGetOrigin(wrapper, out FileSystemFailureContext? outer).ShouldBeTrue();
        outer.ShouldNotBeNull().IsTerminal.ShouldBeFalse();
        FileSystemFailureContext.TryGetTerminal(wrapper, out FileSystemFailureContext? found).ShouldBeTrue();
        FileSystemFailureContext.TryGetTerminal(terminal, out FileSystemFailureContext? expected).ShouldBeTrue();
        found.ShouldBeSameAs(expected);
    }

    [Fact]
    public void ExceptionTraversal_ProtectsAgainstCyclesAndSharedBranches()
    {
        Exception cycle = new Exception("cycle");
        // Exception exposes no public way to construct a cycle; corrupt only this
        // test instance to exercise the traversal's reference-identity guard.
        FieldInfo innerException = typeof(Exception)
            .GetField("_innerException", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull();
        innerException.SetValue(cycle, cycle);
        FileSystemFailureContext.TryGetOrigin(cycle, out _).ShouldBeFalse();
        FileSystemFailureContext.TryGetTerminal(cycle, out _).ShouldBeFalse();

        IOException terminal = new IOException("reachable after cycle");
        MakeTerminal(terminal);
        AggregateException branches = new AggregateException(cycle, cycle, terminal);

        FileSystemFailureContext.TryGetTerminal(branches, out FileSystemFailureContext? context).ShouldBeTrue();
        FileSystemFailureContext.TryGetTerminal(terminal, out FileSystemFailureContext? expected).ShouldBeTrue();
        context.ShouldBeSameAs(expected);
    }

    [Fact]
    public void ExceptionEvidenceAndTraversal_UseReferenceIdentityRatherThanEquality()
    {
        EqualIOException unmarked = new EqualIOException();
        EqualIOException terminal = new EqualIOException();
        unmarked.Equals(terminal).ShouldBeTrue();
        MakeTerminal(terminal);

        FileSystemFailureContext.TryGetOrigin(unmarked, out _).ShouldBeFalse();
        FileSystemFailureContext.TryGetTerminal(unmarked, out _).ShouldBeFalse();
        AggregateException branches = new AggregateException(unmarked, terminal);
        FileSystemFailureContext.TryGetTerminal(branches, out FileSystemFailureContext? context).ShouldBeTrue();
        FileSystemFailureContext.TryGetTerminal(terminal, out FileSystemFailureContext? expected).ShouldBeTrue();
        context.ShouldBeSameAs(expected);
    }

    [Fact]
    public void TerminalResultEvidence_CanBeTransferredExplicitlyWithoutMarkingEqualPeers()
    {
        IOException exception = new IOException("terminal");
        MakeTerminal(exception);
        PublishResult original = new PublishResult { Success = false, Message = "failure" };
        FileSystemFailureContext.AttachToResult(original, exception);
        FileSystemFailureContext.TryGetResult(original, out FileSystemFailureContext? context).ShouldBeTrue();
        PublishResult converted = original with { Message = "formatted failure" };
        PublishResult equalPeer = converted with { };

        FileSystemFailureContext.AttachToResult(converted, context.ShouldNotBeNull()).ShouldBeSameAs(converted);

        FileSystemFailureContext.TryGetResult(converted, out FileSystemFailureContext? retained).ShouldBeTrue();
        retained.ShouldBeSameAs(context);
        FileSystemFailureContext.TryGetResult(equalPeer, out _).ShouldBeFalse();
        converted.ShouldBe(equalPeer);
    }

    [Fact]
    public void TerminalEvidence_RetainsFirstCauseRatherThanCleanupOrRecapture()
    {
        IOException primary = new IOException("primary");
        MakeTerminal(primary);
        FileSystemFailureContext.TryGetTerminal(primary, out FileSystemFailureContext? expected).ShouldBeTrue();
        PublishResult result = new PublishResult { Success = false };
        FileSystemFailureContext.AttachToResult(result, primary);
        IOException cleanup = new IOException("cleanup");
        MakeTerminal(cleanup, FileSystemReplaySafety.Unsafe);

        FileSystemFailureContext.AttachToResult(result, cleanup);
        FileSystemFailureContext.AttachToResult(result, new IOException("unmarked"));
        FileSystemFailureContext.CapturePrimitiveFailure(
            primary, FileSystemReplaySafety.Safe, "recaptured", "different path");

        FileSystemFailureContext.TryGetResult(result, out FileSystemFailureContext? resultContext).ShouldBeTrue();
        resultContext.ShouldBeSameAs(expected);
        FileSystemFailureContext.TryGetTerminal(primary, out FileSystemFailureContext? retained).ShouldBeTrue();
        retained.ShouldBeSameAs(expected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormatMessage_RetainsOperationPathsPhaseAttemptsNativeErrorAndFlags(bool unsafeReplay)
    {
        IOException exception = new IOException("physical failure");
        MakeTerminal(
            exception,
            unsafeReplay ? FileSystemReplaySafety.Unsafe : FileSystemReplaySafety.Safe);
        string termination = unsafeReplay ? "Unsafe" : "Exhausted";
        string expectedContext =
            $"Filesystem operation 'read', phase 'read block', attempts 1, retries 0, termination {termination}, " +
            "path 'source', destination 'destination', native error 5, native flags 0x80000000";

        FileSystemFailureContext.TryGetTerminal(exception, out FileSystemFailureContext? context).ShouldBeTrue();
        context.ShouldNotBeNull().Format().ShouldBe(expectedContext);
        FileSystemFailureContext.FormatMessage(exception).ShouldBe($"physical failure ({expectedContext})");
        HttpRequestException wrapper = new HttpRequestException("wrapped", exception);
        FileSystemFailureContext.FormatMessage(wrapper).ShouldBe($"wrapped ({expectedContext})");
        exception.Message.ShouldBe("physical failure");
        wrapper.Message.ShouldBe("wrapped");
    }

    [Fact]
    public void Cancellation_IsNotAnnotatedAsFilesystemOriginOrTerminal()
    {
        using CancellationTokenSource source = new CancellationTokenSource();
        OperationCanceledException expected = new OperationCanceledException(source.Token);

        OperationCanceledException actual = Should.Throw<OperationCanceledException>(
            () => ThrowAtPrimitive(expected));

        actual.ShouldBeSameAs(expected);
        actual.CancellationToken.ShouldBe(source.Token);
        actual.StackTrace.ShouldNotBeNull().ShouldContain(nameof(ThrowAtPrimitive));
        FileSystemFailureContext.TryGetOrigin(actual, out _).ShouldBeFalse();
        FileSystemFailureContext.TryGetTerminal(actual, out _).ShouldBeFalse();
    }

    [Fact]
    public void ResultProvenance_DoesNotKeepResultsAlive()
    {
        IOException terminal = new IOException("terminal");
        MakeTerminal(terminal);
        WeakReference<PublishResult> result = AssociateTemporaryResult(terminal);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        result.TryGetTarget(out _).ShouldBeFalse();
        GC.KeepAlive(terminal);
    }

    [Fact]
    public void ExceptionProvenance_DoesNotKeepExceptionsAlive()
    {
        WeakReference<Exception> exception = AssociateTemporaryException();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        exception.TryGetTarget(out _).ShouldBeFalse();
    }

    private static void MakeTerminal(
        Exception exception,
        FileSystemReplaySafety safety = FileSystemReplaySafety.Safe) =>
        ExecuteToTerminal(ThrowAtPrimitive(exception, safety));

    private static Exception ExecuteToTerminal(FileSystemPrimitiveFailure failure)
    {
        FileSystemRetryExecutor executor = new FileSystemRetryExecutor(
            _ => throw new InvalidOperationException("a terminal attempt must not sample jitter"),
            (_, _) => throw new InvalidOperationException("a terminal attempt must not wait"),
            (_, _) => throw new InvalidOperationException("a terminal attempt must not wait"));
        FailureOperation operation = new FailureOperation(failure);
        Exception? actual = Record.Exception(() => { executor.Execute(operation); });

        actual.ShouldBeSameAs(failure.Exception);
        operation.Calls.ShouldBe(1);
        operation.AttemptCount.ShouldBe(1L);
        operation.RetriesUsed.ShouldBe(0L);
        operation.State.ShouldBe(FileSystemReplayState.Faulted);
        return actual.ShouldNotBeNull();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static FileSystemPrimitiveFailure ThrowAtPrimitive(
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
                captured, safety, "read", "source", "destination", 5, 0x80000000);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<PublishResult> AssociateTemporaryResult(Exception terminal)
    {
        PublishResult result = new PublishResult { Success = false };
        FileSystemFailureContext.AttachToResult(result, terminal);
        return new WeakReference<PublishResult>(result);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<Exception> AssociateTemporaryException()
    {
        IOException exception = new IOException("temporary");
        MakeTerminal(exception);
        return new WeakReference<Exception>(exception);
    }

    private sealed class FailureOperation : FileSystemReplayOperation<int>
    {
        private readonly FileSystemPrimitiveFailure _failure;

        internal FailureOperation(FileSystemPrimitiveFailure failure)
            : base(
                FileSystemRetryPolicy.Resolve(new FileSystemRetryOptions
                {
                    RetryCount = 0,
                    MinimumDelayMilliseconds = 0,
                    MaximumJitterMilliseconds = 0,
                }),
                "read block")
        {
            _failure = failure;
        }

        internal int Calls { get; private set; }

        internal override FileSystemReplayOutcome<int> ExecuteStep(CancellationToken cancellationToken)
        {
            Calls++;
            return FileSystemReplayOutcome<int>.Failed(_failure);
        }
    }

    private sealed class EqualIOException : IOException
    {
        public override bool Equals(object? obj) => obj is EqualIOException;

        public override int GetHashCode() => 1;
    }
}
