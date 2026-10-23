// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using FhirPkg.IO;

namespace FhirPkg.Tests.Support;

internal sealed record FileSystemPrimitiveCall(
    string Operation,
    string? Path = null,
    string? DestinationPath = null,
    int? Descriptor = null,
    long? Flags = null,
    FileSystemEntryKind? Kind = null);

/// <summary>
/// Scripts raw outcomes, including a side effect followed by an unsafe failure.
/// Unscripted physical calls fail the test rather than touching the real disk.
/// </summary>
internal sealed class ScriptedFileSystemPrimitives : IFileSystemPrimitives
{
    private readonly Dictionary<string, Queue<Func<FileSystemPrimitiveCall, object>>> _scripts = [];
    private readonly Dictionary<string, Func<FileSystemPrimitiveCall, object>> _defaults = [];
    private int _nextDescriptor = 40;

    internal ScriptedFileSystemPrimitives(FileSystemPlatform platform = FileSystemPlatform.Linux) =>
        Platform = platform;

    public FileSystemPlatform Platform { get; }

    internal List<FileSystemPrimitiveCall> Calls { get; } = [];

    internal List<int> Waits { get; } = [];

    internal List<int> AsyncWaits { get; } = [];

    internal int Count(string operation) => Calls.Count(call => call.Operation == operation);

    internal void Enqueue<T>(string operation, Func<FileSystemPrimitiveCall, FileSystemReplayOutcome<T>> step)
    {
        if (!_scripts.TryGetValue(operation, out Queue<Func<FileSystemPrimitiveCall, object>>? steps))
        {
            steps = new Queue<Func<FileSystemPrimitiveCall, object>>();
            _scripts.Add(operation, steps);
        }
        steps.Enqueue(call => step(call));
    }

    internal void Enqueue<T>(string operation, FileSystemReplayOutcome<T> outcome) =>
        Enqueue(operation, _ => outcome);

    internal void Set<T>(string operation, Func<FileSystemPrimitiveCall, FileSystemReplayOutcome<T>> step) =>
        _defaults[operation] = call => step(call);

    internal void Set<T>(string operation, FileSystemReplayOutcome<T> outcome) =>
        Set(operation, _ => outcome);

    internal void EnableDirectorySynchronization()
    {
        Set<int>("OpenDescriptor", _ => FileSystemReplayOutcome<int>.Complete(_nextDescriptor++));
        Set("SynchronizeDescriptor", FileSystemReplayOutcome<FileSystemDirectorySyncOutcome>.Complete(FileSystemDirectorySyncOutcome.Standard));
        Set("CloseDescriptor", FileSystemReplayOutcome<bool>.Complete(true));
    }

    internal FileSystemContext CreateContext(int retries = 5) =>
        new FileSystemContext(
            new FileSystemRetryOptions
            {
                RetryCount = retries,
                MinimumDelayMilliseconds = 10,
                MaximumJitterMilliseconds = 0,
            },
            this,
            CreateExecutor());

    internal FileSystemRetryExecutor CreateExecutor() =>
        new FileSystemRetryExecutor(
            _ => 0,
            (milliseconds, token) =>
            {
                token.ThrowIfCancellationRequested();
                Waits.Add(milliseconds);
            },
            (milliseconds, token) =>
            {
                token.ThrowIfCancellationRequested();
                AsyncWaits.Add(milliseconds);
                return ValueTask.CompletedTask;
            });

    internal static FileSystemReplayOutcome<T> Fail<T>(
        Exception exception,
        string operation,
        string? path = null,
        FileSystemReplaySafety safety = FileSystemReplaySafety.Safe,
        int? nativeError = null) =>
        FileSystemReplayOutcome<T>.Failed(FileSystemFailureContext.CapturePrimitiveFailure(
            exception, safety, operation, path, nativeErrorCode: nativeError));

    internal static FileSystemReplayOutcome<T> NativeFailure<T>(
        int error,
        string operation,
        string? path = null,
        FileSystemReplaySafety safety = FileSystemReplaySafety.Safe) =>
        Fail<T>(new IOException("Scripted native failure.", new Win32Exception(error)), operation, path, safety, error);

    internal static FileSystemReplayOutcome<FileSystemProbeResult> Missing() =>
        FileSystemReplayOutcome<FileSystemProbeResult>.Missing(new FileSystemProbeResult(FileSystemEntryKind.Missing));

    internal static FileSystemReplayOutcome<FileSystemProbeResult> Entry(FileSystemEntryKind kind) =>
        FileSystemReplayOutcome<FileSystemProbeResult>.Complete(new FileSystemProbeResult(kind));

    public FileSystemReplayOutcome<string> GetCurrentDirectory() =>
        Invoke<string>(new FileSystemPrimitiveCall("GetCurrentDirectory"));

    public FileSystemReplayOutcome<string> GetTempPath() =>
        Invoke<string>(new FileSystemPrimitiveCall("GetTempPath"));

    public FileSystemReplayOutcome<string> GetHomeDirectory() =>
        Invoke<string>(new FileSystemPrimitiveCall("GetHomeDirectory"));

    public FileSystemReplayOutcome<string> GetFullPath(string path) =>
        Invoke<string>(new FileSystemPrimitiveCall("GetFullPath", path));

    public FileSystemReplayOutcome<FileSystemProbeResult> Probe(string path, bool followLinks = false) =>
        Invoke<FileSystemProbeResult>(new FileSystemPrimitiveCall("Probe", path, Flags: followLinks ? 1 : 0));

    public FileSystemReplayOutcome<FileAttributes> GetAttributes(string path) =>
        Invoke<FileAttributes>(new FileSystemPrimitiveCall("GetAttributes", path));

    public FileSystemReplayOutcome<long> GetLength(string path) =>
        Invoke<long>(new FileSystemPrimitiveCall("GetLength", path));

    public FileSystemReplayOutcome<IEnumerator<string>> CreateDirectoryEnumerator(string path) =>
        Invoke<IEnumerator<string>>(new FileSystemPrimitiveCall("CreateDirectoryEnumerator", path));

    public FileSystemReplayOutcome<bool> MoveNext(IEnumerator<string> enumerator, string path) =>
        Invoke(
            new FileSystemPrimitiveCall("MoveNext", path),
            () => SystemFileSystemPrimitives.Instance.MoveNext(enumerator, path));

    public FileSystemReplayOutcome<string> GetCurrent(IEnumerator<string> enumerator, string path) =>
        Invoke(
            new FileSystemPrimitiveCall("GetCurrent", path),
            () => SystemFileSystemPrimitives.Instance.GetCurrent(enumerator, path));

    public FileSystemReplayOutcome<bool> DisposeEnumerator(IEnumerator<string> enumerator, string path) =>
        Invoke(
            new FileSystemPrimitiveCall("DisposeEnumerator", path),
            () => SystemFileSystemPrimitives.Instance.DisposeEnumerator(enumerator, path));

    public FileSystemReplayOutcome<FileStream> OpenFile(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize,
        FileOptions options) =>
        Invoke<FileStream>(new FileSystemPrimitiveCall("OpenFile", path, Flags: (long)options));

    public FileSystemReplayOutcome<bool> CreateDirectory(string path) =>
        Invoke<bool>(new FileSystemPrimitiveCall("CreateDirectory", path));

    public FileSystemReplayOutcome<bool> MovePath(string sourcePath, string destinationPath, bool replace) =>
        Invoke<bool>(new FileSystemPrimitiveCall("MovePath", sourcePath, destinationPath, Flags: replace ? 1 : 0));

    public FileSystemReplayOutcome<bool> DeleteEntry(string path, FileSystemEntryKind kind) =>
        Invoke<bool>(new FileSystemPrimitiveCall("DeleteEntry", path, Kind: kind));

    public FileSystemReplayOutcome<int> OpenDescriptor(string path, int flags, uint mode) =>
        Invoke<int>(new FileSystemPrimitiveCall("OpenDescriptor", path, Flags: flags));

    public FileSystemReplayOutcome<FileSystemProbeResult> InspectDescriptor(int descriptor, string? path = null) =>
        Invoke<FileSystemProbeResult>(new FileSystemPrimitiveCall("InspectDescriptor", path, Descriptor: descriptor));

    public FileSystemReplayOutcome<int> DuplicateDescriptor(int descriptor, int command, int minimum, string? path = null) =>
        Invoke<int>(new FileSystemPrimitiveCall("DuplicateDescriptor", path, Descriptor: descriptor, Flags: command));

    public FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> SynchronizeDescriptor(int descriptor, bool fullSync, string? path = null) =>
        Invoke<FileSystemDirectorySyncOutcome>(new FileSystemPrimitiveCall(
            "SynchronizeDescriptor", path, Descriptor: descriptor, Flags: fullSync ? 1 : 0));

    public FileSystemReplayOutcome<bool> CloseDescriptor(int descriptor, string? path = null) =>
        Invoke<bool>(new FileSystemPrimitiveCall("CloseDescriptor", path, Descriptor: descriptor));

    public FileSystemReplayOutcome<bool> TryLock(FileStream stream) =>
        Invoke<bool>(new FileSystemPrimitiveCall("TryLock"));

    public FileSystemReplayOutcome<bool> Unlock(FileStream stream) =>
        Invoke<bool>(new FileSystemPrimitiveCall("Unlock"));

    private FileSystemReplayOutcome<T> Invoke<T>(
        FileSystemPrimitiveCall call,
        Func<FileSystemReplayOutcome<T>>? fallback = null)
    {
        Calls.Add(call);
        if (_scripts.TryGetValue(call.Operation, out Queue<Func<FileSystemPrimitiveCall, object>>? steps)
            && steps.TryDequeue(out Func<FileSystemPrimitiveCall, object>? step))
        {
            return (FileSystemReplayOutcome<T>)step(call);
        }
        if (_defaults.TryGetValue(call.Operation, out Func<FileSystemPrimitiveCall, object>? defaultStep))
            return (FileSystemReplayOutcome<T>)defaultStep(call);
        if (fallback is not null)
            return fallback();
        throw new InvalidOperationException($"Unexpected raw filesystem call: {call}.");
    }
}
