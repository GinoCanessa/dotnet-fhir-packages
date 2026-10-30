// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.ComponentModel;
using FhirPkg.IO;
using Microsoft.Win32.SafeHandles;

namespace FhirPkg.Tests.Support;

internal sealed record FileSystemPrimitiveCall(
    string Operation,
    string? Path = null,
    string? DestinationPath = null,
    int? Descriptor = null,
    long? Flags = null,
    FileSystemEntryKind? Kind = null,
    SafeFileHandle? Handle = null,
    FileStream? Stream = null,
    Memory<byte> Buffer = default,
    ReadOnlyMemory<byte> Content = default,
    long? Offset = null,
    long? Length = null,
    FileMode? Mode = null,
    FileAccess? Access = null,
    FileShare? Share = null,
    FileOptions? Options = null);

/// <summary>
/// Scripts raw outcomes, including a side effect followed by an unsafe failure.
/// Unscripted physical calls fail the test rather than touching the real disk.
/// </summary>
internal sealed class ScriptedFileSystemPrimitives : IFileSystemPrimitives
{
    private readonly Dictionary<string, Queue<Func<FileSystemPrimitiveCall, object>>> _scripts = [];
    private readonly Dictionary<string, Func<FileSystemPrimitiveCall, object>> _defaults = [];
    private int _nextDescriptor = 40;
    private bool _useSystemFiles;

    internal ScriptedFileSystemPrimitives(FileSystemPlatform platform = FileSystemPlatform.Linux) =>
        Platform = platform;

    public FileSystemPlatform Platform { get; }

    internal List<FileSystemPrimitiveCall> Calls { get; } = [];

    internal List<int> Waits { get; } = [];

    internal List<int> AsyncWaits { get; } = [];

    internal Action? OnWait { get; set; }

    internal void UseSystemFiles() => _useSystemFiles = true;

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
                OnWait?.Invoke();
            },
            (milliseconds, token) =>
            {
                token.ThrowIfCancellationRequested();
                AsyncWaits.Add(milliseconds);
                OnWait?.Invoke();
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
        Invoke(new FileSystemPrimitiveCall("GetFullPath", path),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.GetFullPath(path)));

    public FileSystemReplayOutcome<FileSystemProbeResult> Probe(string path, bool followLinks = false) =>
        Invoke(new FileSystemPrimitiveCall("Probe", path, Flags: followLinks ? 1 : 0),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.Probe(path, followLinks)));

    public FileSystemReplayOutcome<FileAttributes> GetAttributes(string path) =>
        Invoke(new FileSystemPrimitiveCall("GetAttributes", path),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.GetAttributes(path)));

    public FileSystemReplayOutcome<long> GetLength(string path) =>
        Invoke(new FileSystemPrimitiveCall("GetLength", path),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.GetLength(path)));

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
        Invoke(new FileSystemPrimitiveCall("CreateDirectory", path),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.CreateDirectory(path)));

    public FileSystemReplayOutcome<bool> MovePath(string sourcePath, string destinationPath, bool replace) =>
        Invoke(new FileSystemPrimitiveCall("MovePath", sourcePath, destinationPath, Flags: replace ? 1 : 0),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.MovePath(sourcePath, destinationPath, replace)));

    public FileSystemReplayOutcome<bool> DeleteEntry(string path, FileSystemEntryKind kind) =>
        Invoke(new FileSystemPrimitiveCall("DeleteEntry", path, Kind: kind),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.DeleteEntry(path, kind)));

    public FileSystemReplayOutcome<int> OpenDescriptor(string path, int flags, uint mode) =>
        Invoke(new FileSystemPrimitiveCall("OpenDescriptor", path, Flags: flags),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.OpenDescriptor(path, flags, mode)));

    public FileSystemReplayOutcome<FileSystemProbeResult> InspectDescriptor(int descriptor, string? path = null) =>
        Invoke(new FileSystemPrimitiveCall("InspectDescriptor", path, Descriptor: descriptor),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.InspectDescriptor(descriptor, path)));

    public FileSystemReplayOutcome<int> DuplicateDescriptor(int descriptor, int command, int minimum, string? path = null) =>
        Invoke(new FileSystemPrimitiveCall("DuplicateDescriptor", path, Descriptor: descriptor, Flags: command),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.DuplicateDescriptor(descriptor, command, minimum, path)));

    public FileSystemReplayOutcome<FileSystemDirectorySyncOutcome> SynchronizeDescriptor(int descriptor, bool fullSync, string? path = null) =>
        Invoke(new FileSystemPrimitiveCall(
            "SynchronizeDescriptor", path, Descriptor: descriptor, Flags: fullSync ? 1 : 0),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.SynchronizeDescriptor(descriptor, fullSync, path)));

    public FileSystemReplayOutcome<bool> CloseDescriptor(int descriptor, string? path = null) =>
        Invoke(new FileSystemPrimitiveCall("CloseDescriptor", path, Descriptor: descriptor),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.CloseDescriptor(descriptor, path)));

    public FileSystemReplayOutcome<bool> TryLock(FileStream stream) =>
        Invoke<bool>(new FileSystemPrimitiveCall("TryLock"));

    public FileSystemReplayOutcome<bool> Unlock(FileStream stream) =>
        Invoke<bool>(new FileSystemPrimitiveCall("Unlock"));

    public FileSystemReplayOutcome<SafeFileHandle> OpenFileHandle(
        string path, FileMode mode, FileAccess access, FileShare share, FileOptions options) =>
        Invoke(new FileSystemPrimitiveCall("OpenFileHandle", path, Mode: mode, Access: access, Share: share, Options: options),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.OpenFileHandle(path, mode, access, share, options)));

    public FileSystemReplayOutcome<int> OpenOwnedDescriptor(
        string path, FileMode mode, FileAccess access, FileShare share, FileOptions options) =>
        Invoke(new FileSystemPrimitiveCall("OpenOwnedDescriptor", path, Mode: mode, Access: access, Share: share, Options: options),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.OpenOwnedDescriptor(path, mode, access, share, options)));

    public FileSystemReplayOutcome<bool> ConfigureFileSharing(SafeFileHandle handle, FileShare share, string path) =>
        Invoke(new FileSystemPrimitiveCall("ConfigureFileSharing", path, Handle: handle, Share: share),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.ConfigureFileSharing(handle, share, path)));

    public FileSystemReplayOutcome<bool> AdviseFileAccess(SafeFileHandle handle, FileOptions options, string path) =>
        Invoke(new FileSystemPrimitiveCall("AdviseFileAccess", path, Handle: handle, Options: options),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.AdviseFileAccess(handle, options, path)));

    public FileSystemReplayOutcome<int> ReadAt(SafeFileHandle handle, Memory<byte> buffer, long offset, string path) =>
        Invoke(new FileSystemPrimitiveCall("ReadAt", path, Handle: handle, Buffer: buffer, Offset: offset),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.ReadAt(handle, buffer, offset, path)));

    public ValueTask<FileSystemReplayOutcome<int>> ReadAtAsync(
        SafeFileHandle handle, Memory<byte> buffer, long offset, string path, CancellationToken cancellationToken) =>
        InvokeAsync(new FileSystemPrimitiveCall("ReadAtAsync", path, Handle: handle, Buffer: buffer, Offset: offset),
            () => SystemFileSystemPrimitives.Instance.ReadAtAsync(handle, buffer, offset, path, cancellationToken));

    public FileSystemReplayOutcome<bool> WriteAt(
        SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long offset, string path) =>
        Invoke(new FileSystemPrimitiveCall("WriteAt", path, Handle: handle, Content: buffer, Offset: offset),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.WriteAt(handle, buffer, offset, path)));

    public ValueTask<FileSystemReplayOutcome<bool>> WriteAtAsync(
        SafeFileHandle handle, ReadOnlyMemory<byte> buffer, long offset, string path, CancellationToken cancellationToken) =>
        InvokeAsync(new FileSystemPrimitiveCall("WriteAtAsync", path, Handle: handle, Content: buffer, Offset: offset),
            () => SystemFileSystemPrimitives.Instance.WriteAtAsync(handle, buffer, offset, path, cancellationToken));

    public FileSystemReplayOutcome<long> GetLength(SafeFileHandle handle, string path) =>
        Invoke(new FileSystemPrimitiveCall("GetHandleLength", path, Handle: handle),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.GetLength(handle, path)));

    public FileSystemReplayOutcome<bool> SetLength(SafeFileHandle handle, long length, string path) =>
        Invoke(new FileSystemPrimitiveCall("SetLength", path, Handle: handle, Length: length),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.SetLength(handle, length, path)));

    public FileSystemReplayOutcome<bool> FlushFile(SafeFileHandle handle, string path) =>
        Invoke(new FileSystemPrimitiveCall("FlushFile", path, Handle: handle),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.FlushFile(handle, path)));

    public FileSystemReplayOutcome<bool> CloseFile(SafeFileHandle handle, string path) =>
        Invoke(new FileSystemPrimitiveCall("CloseFile", path, Handle: handle),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.CloseFile(handle, path)));

    public FileSystemReplayOutcome<BorrowedFileReadState> PrepareBorrowedRead(FileStream stream) =>
        Invoke(new FileSystemPrimitiveCall("PrepareBorrowedRead", stream.Name, Stream: stream),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.PrepareBorrowedRead(stream)));

    public FileSystemReplayOutcome<long> GetPosition(FileStream stream) =>
        Invoke(new FileSystemPrimitiveCall("GetPosition", stream.Name, Stream: stream),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.GetPosition(stream)));

    public FileSystemReplayOutcome<long> SetPosition(FileStream stream, long position) =>
        Invoke(new FileSystemPrimitiveCall("SetPosition", stream.Name, Stream: stream, Offset: position),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.SetPosition(stream, position)));

    public FileSystemReplayOutcome<int> ReadBorrowed(FileStream stream, Memory<byte> buffer) =>
        Invoke(new FileSystemPrimitiveCall("ReadBorrowed", stream.Name, Stream: stream, Buffer: buffer),
            SystemFallback(() => SystemFileSystemPrimitives.Instance.ReadBorrowed(stream, buffer)));

    public ValueTask<FileSystemReplayOutcome<int>> ReadBorrowedAsync(
        FileStream stream, Memory<byte> buffer, CancellationToken cancellationToken) =>
        InvokeAsync(new FileSystemPrimitiveCall("ReadBorrowedAsync", stream.Name, Stream: stream, Buffer: buffer),
            () => SystemFileSystemPrimitives.Instance.ReadBorrowedAsync(stream, buffer, cancellationToken));

    private Func<FileSystemReplayOutcome<T>>? SystemFallback<T>(Func<FileSystemReplayOutcome<T>> fallback) =>
        _useSystemFiles ? fallback : null;

    private async ValueTask<FileSystemReplayOutcome<T>> InvokeAsync<T>(
        FileSystemPrimitiveCall call, Func<ValueTask<FileSystemReplayOutcome<T>>> fallback)
    {
        if (TryInvoke(call, out FileSystemReplayOutcome<T> result))
            return result;
        if (_useSystemFiles)
            return await fallback().ConfigureAwait(false);
        throw new InvalidOperationException($"Unexpected raw filesystem call: {call}.");
    }

    private FileSystemReplayOutcome<T> Invoke<T>(
        FileSystemPrimitiveCall call,
        Func<FileSystemReplayOutcome<T>>? fallback = null)
    {
        if (TryInvoke(call, out FileSystemReplayOutcome<T> result))
            return result;
        if (fallback is not null)
            return fallback();
        throw new InvalidOperationException($"Unexpected raw filesystem call: {call}.");
    }

    private bool TryInvoke<T>(FileSystemPrimitiveCall call, out FileSystemReplayOutcome<T> outcome)
    {
        Calls.Add(call);
        if (_scripts.TryGetValue(call.Operation, out Queue<Func<FileSystemPrimitiveCall, object>>? steps)
            && steps.TryDequeue(out Func<FileSystemPrimitiveCall, object>? step))
        {
            outcome = (FileSystemReplayOutcome<T>)step(call);
            return true;
        }
        if (_defaults.TryGetValue(call.Operation, out Func<FileSystemPrimitiveCall, object>? defaultStep))
        {
            outcome = (FileSystemReplayOutcome<T>)defaultStep(call);
            return true;
        }
        outcome = default;
        return false;
    }
}
