// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;

namespace FhirPkg.IO;

internal enum FileSystemFailureTermination
{
    None,
    Exhausted,
    Unsafe,
}

/// <summary>
/// Internal, identity-associated evidence. Neither exception data nor public result
/// members are changed; result records keep their equality and serialization contracts.
/// </summary>
internal sealed record FileSystemFailureContext
{
    private static readonly ConditionalWeakTable<Exception, FileSystemFailureContext> s_exceptions = new();
    private static readonly ConditionalWeakTable<object, FileSystemFailureContext> s_results = new();

    private FileSystemFailureContext(
        string operation,
        string? path,
        string? destinationPath,
        int? nativeErrorCode,
        long? nativeFlags)
    {
        Operation = operation;
        Path = path;
        DestinationPath = destinationPath;
        NativeErrorCode = nativeErrorCode;
        NativeFlags = nativeFlags;
    }

    internal string Operation { get; }

    internal string? Path { get; }

    internal string? DestinationPath { get; }

    internal int? NativeErrorCode { get; }

    internal long? NativeFlags { get; }

    internal string? Phase { get; private init; }

    internal long AttemptCount { get; private init; }

    internal long RetriesUsed { get; private init; }

    internal FileSystemFailureTermination Termination { get; private init; }

    internal bool IsTerminal => Termination != FileSystemFailureTermination.None;

    // Only single-attempt filesystem primitives may mint this evidence. Do not use
    // this factory in a catch around parsing, callbacks, or an entire domain operation.
    internal static FileSystemPrimitiveFailure CapturePrimitiveFailure(
        Exception exception,
        FileSystemReplaySafety replaySafety,
        string operation,
        string? path = null,
        string? destinationPath = null,
        int? nativeErrorCode = null,
        long? nativeFlags = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrEmpty(operation);
        if (exception is OperationCanceledException)
            ExceptionDispatchInfo.Capture(exception).Throw();

        if (!Enum.IsDefined(replaySafety))
            throw new ArgumentOutOfRangeException(nameof(replaySafety));

        FileSystemFailureContext context = new FileSystemFailureContext(
            operation, path, destinationPath, nativeErrorCode, nativeFlags);
        AttachException(exception, context);
        return new FileSystemPrimitiveFailure(
            ExceptionDispatchInfo.Capture(exception),
            replaySafety,
            context);
    }

    internal static bool TryGetOrigin(
        Exception exception,
        [NotNullWhen(true)] out FileSystemFailureContext? context) =>
        TryFind(exception, terminalOnly: false, out context);

    internal static bool TryGetTerminal(
        Exception exception,
        [NotNullWhen(true)] out FileSystemFailureContext? context) =>
        TryFind(exception, terminalOnly: true, out context);

    internal static T AttachToResult<T>(T result, Exception exception) where T : class
    {
        ArgumentNullException.ThrowIfNull(result);
        if (TryGetTerminal(exception, out FileSystemFailureContext? context))
            AttachToResult(result, context);

        return result;
    }

    internal static T AttachToResult<T>(T result, FileSystemFailureContext context) where T : class
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(context);
        if (!context.IsTerminal)
            throw new ArgumentException("Only terminal filesystem evidence belongs on a result.", nameof(context));

        // Keep the first terminal cause rather than replacing it with a later cleanup failure.
        s_results.GetValue(result, _ => context);
        return result;
    }

    internal static bool TryGetResult(
        object result,
        [NotNullWhen(true)] out FileSystemFailureContext? context)
    {
        ArgumentNullException.ThrowIfNull(result);
        return s_results.TryGetValue(result, out context);
    }

    internal static string FormatMessage(Exception exception) =>
        TryGetTerminal(exception, out FileSystemFailureContext? context)
            ? $"{exception.Message} ({context.Format()})"
            : exception.Message;

    internal string Format()
    {
        string text = string.Create(
            CultureInfo.InvariantCulture,
            $"Filesystem operation '{Operation}', phase '{Phase}', attempts {AttemptCount}, retries {RetriesUsed}, termination {Termination}");
        if (Path is not null)
            text += $", path '{Path}'";
        if (DestinationPath is not null)
            text += $", destination '{DestinationPath}'";
        if (NativeErrorCode.HasValue)
            text += ", native error " + NativeErrorCode.Value.ToString(CultureInfo.InvariantCulture);
        if (NativeFlags.HasValue)
            text += ", native flags 0x" + NativeFlags.Value.ToString("X", CultureInfo.InvariantCulture);

        return text;
    }

    internal void MarkTerminal(
        Exception exception,
        string phase,
        long attemptCount,
        long retriesUsed,
        FileSystemFailureTermination termination)
    {
        AttachException(
            exception,
            this with
            {
                Phase = phase,
                AttemptCount = attemptCount,
                RetriesUsed = retriesUsed,
                Termination = termination,
            });
    }

    private static void AttachException(Exception exception, FileSystemFailureContext context)
    {
        lock (s_exceptions)
        {
            if (s_exceptions.TryGetValue(exception, out FileSystemFailureContext? existing)
                && existing.IsTerminal)
            {
                return;
            }

            s_exceptions.Remove(exception);
            s_exceptions.Add(exception, context);
        }
    }

    private static bool TryFind(
        Exception exception,
        bool terminalOnly,
        [NotNullWhen(true)] out FileSystemFailureContext? context)
    {
        ArgumentNullException.ThrowIfNull(exception);
        HashSet<Exception> visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        Stack<Exception> pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out Exception? current))
        {
            if (!visited.Add(current))
                continue;

            if (s_exceptions.TryGetValue(current, out context)
                && (!terminalOnly || context.IsTerminal))
            {
                return true;
            }

            if (current is AggregateException aggregate)
            {
                for (int index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                    pending.Push(aggregate.InnerExceptions[index]);
            }
            if (current.InnerException is Exception inner)
            {
                pending.Push(inner);
            }
        }

        context = null;
        return false;
    }
}

/// <summary>
/// A captured raw failure and its replay-safety proof, not a general callback retry token.
/// </summary>
internal sealed class FileSystemPrimitiveFailure
{
    private readonly ExceptionDispatchInfo _exception;
    private readonly FileSystemFailureContext _context;

    internal FileSystemPrimitiveFailure(
        ExceptionDispatchInfo exception,
        FileSystemReplaySafety replaySafety,
        FileSystemFailureContext context)
    {
        _exception = exception;
        ReplaySafety = replaySafety;
        _context = context;
    }

    internal Exception Exception => _exception.SourceException;

    internal FileSystemReplaySafety ReplaySafety { get; }

    [DoesNotReturn]
    internal void Throw() => _exception.Throw();

    [DoesNotReturn]
    internal void ThrowTerminal(
        string phase,
        long attemptCount,
        long retriesUsed,
        FileSystemFailureTermination termination)
    {
        _context.MarkTerminal(Exception, phase, attemptCount, retriesUsed, termination);
        Throw();
    }
}
