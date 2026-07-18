// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text.Json;
using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;

namespace FhirPkg.ProcessTestHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        HostArguments arguments = HostArguments.Parse(args);
        using CancellationTokenSource cancellationSource = new();
        if (arguments.CancelAfterMilliseconds is int cancelAfter)
        {
            cancellationSource.CancelAfter(
                TimeSpan.FromMilliseconds(cancelAfter));
        }

        FileFaultObserver observer = new(
            arguments.ProgressPath,
            arguments.PauseFault,
            arguments.ReleasePath);
        FileContentionObserver contentionObserver = new(
            arguments.ContentionPath);
        PackageInstallLimits limits =
            PackageInstallLimits.ResolveManager(
                new PackageInstallLimits());
        using DiskPackageCache cache = new(
            arguments.CachePath,
            logger: null,
            timeProvider: null,
            limits,
            SystemPackageCacheFileOperations.Instance,
            observer,
            contentionObserver);

        try
        {
            HostResult result = arguments.Command switch
            {
                "install" => await InstallAsync(
                        cache,
                        arguments,
                        cancellationSource.Token)
                    .ConfigureAwait(false),
                "manager-install" => await ManagerInstallAsync(
                        arguments,
                        contentionObserver,
                        cancellationSource.Token)
                    .ConfigureAwait(false),
                "import" => await ImportAsync(
                        cache,
                        arguments,
                        cancellationSource.Token)
                    .ConfigureAwait(false),
                "remove" => await RemoveAsync(
                        cache,
                        arguments,
                        cancellationSource.Token)
                    .ConfigureAwait(false),
                "clear" => await ClearAsync(
                        cache,
                        cancellationSource.Token)
                    .ConfigureAwait(false),
                "list" => await ListAsync(
                        cache,
                        cancellationSource.Token)
                    .ConfigureAwait(false),
                _ => throw new ArgumentException(
                    $"Unsupported command '{arguments.Command}'.")
            };
            await WriteResultAsync(
                    arguments.ResultPath,
                    result,
                    CancellationToken.None)
                .ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            await WriteResultAsync(
                    arguments.ResultPath,
                    new HostResult
                    {
                        Success = false,
                        Cancelled = true
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return 2;
        }
        catch (PackageInstallException exception)
        {
            await WriteResultAsync(
                    arguments.ResultPath,
                    new HostResult
                    {
                        Success = false,
                        ErrorCode = exception.ErrorCode,
                        ErrorStage = exception.Stage,
                        ErrorMessage = exception.Message
                    },
                    CancellationToken.None)
                .ConfigureAwait(false);
            return 3;
        }
    }

    private static async Task<HostResult> InstallAsync(
        DiskPackageCache cache,
        HostArguments arguments,
        CancellationToken cancellationToken)
    {
        PackageReference reference = new(
            arguments.PackageName
                ?? throw new ArgumentException(
                    "--name is required for install."),
            arguments.PackageVersion
                ?? throw new ArgumentException(
                    "--version is required for install."));
        await using FileStream file = new(
            arguments.ArchivePath
                ?? throw new ArgumentException(
                    "--archive is required for install."),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        await using CountingBarrierStream source = new(
            file,
            arguments.CounterPath,
            arguments.BarrierPath,
            arguments.ReleasePath);
        PackageRecord record = await cache.InstallAsync(
                reference,
                source,
                new InstallCacheOptions
                {
                    OverwriteExisting = arguments.Overwrite,
                    Progress = new FileProgressReporter(
                        arguments.ProgressPath)
                },
                cancellationToken)
            .ConfigureAwait(false);
        return HostResult.FromRecord(record, source.BytesRead);
    }

    private static async Task<HostResult> ImportAsync(
        DiskPackageCache cache,
        HostArguments arguments,
        CancellationToken cancellationToken)
    {
        await using FileStream file = new(
            arguments.ArchivePath
                ?? throw new ArgumentException(
                    "--archive is required for import."),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        await using CountingBarrierStream source = new(
            file,
            arguments.CounterPath,
            arguments.BarrierPath,
            arguments.ReleasePath);
        PackageRecord record = await cache.ImportAsync(
                source,
                new InstallCacheOptions
                {
                    OverwriteExisting = arguments.Overwrite,
                    Progress = new FileProgressReporter(
                        arguments.ProgressPath)
                },
                cancellationToken)
            .ConfigureAwait(false);
        return HostResult.FromRecord(record, source.BytesRead);
    }

    private static async Task<HostResult> ManagerInstallAsync(
        HostArguments arguments,
        IPackageCacheContentionObserver contentionObserver,
        CancellationToken cancellationToken)
    {
        PackageReference reference = new(
            arguments.PackageName
                ?? throw new ArgumentException(
                    "--name is required for manager-install."),
            arguments.PackageVersion
                ?? throw new ArgumentException(
                    "--version is required for manager-install."));
        await using FileStream file = new(
            arguments.ArchivePath
                ?? throw new ArgumentException(
                    "--archive is required for manager-install."),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        await using CountingBarrierStream source = new(
            file,
            arguments.CounterPath,
            arguments.BarrierPath,
            arguments.ReleasePath);
        using FhirPackageManager manager =
            FhirPackageManager.CreateWithContentionObserver(
                new FhirPackageManagerOptions
                {
                    CachePath = arguments.CachePath,
                    ResourceCacheSize = 0
                },
                contentionObserver);
        PackageRecord record = await manager.InstallAsync(
                reference,
                source,
                new PackageSourceInstallOptions
                {
                    OverwriteExisting = arguments.Overwrite,
                    Progress = new FileProgressReporter(
                        arguments.ProgressPath)
                },
                cancellationToken)
            .ConfigureAwait(false);
        return HostResult.FromRecord(record, source.BytesRead);
    }

    private static async Task<HostResult> RemoveAsync(
        DiskPackageCache cache,
        HostArguments arguments,
        CancellationToken cancellationToken)
    {
        PackageReference reference = new(
            arguments.PackageName
                ?? throw new ArgumentException(
                    "--name is required for remove."),
            arguments.PackageVersion
                ?? throw new ArgumentException(
                    "--version is required for remove."));
        bool removed = await cache.RemoveAsync(
                reference,
                cancellationToken)
            .ConfigureAwait(false);
        return new HostResult
        {
            Success = true,
            Removed = removed
        };
    }

    private static async Task<HostResult> ClearAsync(
        DiskPackageCache cache,
        CancellationToken cancellationToken)
    {
        int removed = await cache.ClearAsync(cancellationToken)
            .ConfigureAwait(false);
        return new HostResult
        {
            Success = true,
            RemovedCount = removed
        };
    }

    private static async Task<HostResult> ListAsync(
        DiskPackageCache cache,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<PackageRecord> records =
            await cache.ListPackagesAsync(
                    ct: cancellationToken)
                .ConfigureAwait(false);
        return new HostResult
        {
            Success = true,
            PackageCount = records.Count,
            References = records
                .Select(record => record.Reference.FhirDirective)
                .Order(StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static async Task WriteResultAsync(
        string? resultPath,
        HostResult result,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });
        if (string.IsNullOrWhiteSpace(resultPath))
        {
            await Console.Out.WriteLineAsync(json)
                .ConfigureAwait(false);
            return;
        }

        string fullPath = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
                fullPath,
                json,
                cancellationToken)
            .ConfigureAwait(false);
    }
}

internal sealed record HostArguments
{
    internal required string Command { get; init; }
    internal required string CachePath { get; init; }
    internal string? ArchivePath { get; init; }
    internal string? PackageName { get; init; }
    internal string? PackageVersion { get; init; }
    internal string? CounterPath { get; init; }
    internal string? BarrierPath { get; init; }
    internal string? ReleasePath { get; init; }
    internal string? ProgressPath { get; init; }
    internal string? ContentionPath { get; init; }
    internal string? ResultPath { get; init; }
    internal string? PauseFault { get; init; }
    internal int? CancelAfterMilliseconds { get; init; }
    internal bool Overwrite { get; init; }

    internal static HostArguments Parse(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("A command is required.");

        Dictionary<string, string?> values =
            new(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Unexpected argument '{argument}'.");
            }

            string key = argument[2..];
            if (string.Equals(
                key,
                "overwrite",
                StringComparison.OrdinalIgnoreCase))
            {
                values[key] = null;
                continue;
            }

            if (++index >= args.Length)
            {
                throw new ArgumentException(
                    $"Option '--{key}' requires a value.");
            }

            values[key] = args[index];
        }

        string cachePath = GetRequired(values, "cache");
        int? cancelAfter = values.TryGetValue(
            "cancel-after-ms",
            out string? cancelValue)
            ? int.Parse(
                cancelValue!,
                System.Globalization.CultureInfo.InvariantCulture)
            : null;
        return new HostArguments
        {
            Command = args[0].ToLowerInvariant(),
            CachePath = Path.GetFullPath(cachePath),
            ArchivePath = GetOptional(values, "archive"),
            PackageName = GetOptional(values, "name"),
            PackageVersion = GetOptional(values, "version"),
            CounterPath = GetOptional(values, "counter"),
            BarrierPath = GetOptional(values, "barrier"),
            ReleasePath = GetOptional(values, "release"),
            ProgressPath = GetOptional(values, "progress"),
            ContentionPath = GetOptional(values, "contention"),
            ResultPath = GetOptional(values, "result"),
            PauseFault = GetOptional(values, "pause-fault"),
            CancelAfterMilliseconds = cancelAfter,
            Overwrite = values.ContainsKey("overwrite")
        };
    }

    private static string GetRequired(
        IReadOnlyDictionary<string, string?> values,
        string key) =>
        GetOptional(values, key)
        ?? throw new ArgumentException(
            $"Option '--{key}' is required.");

    private static string? GetOptional(
        IReadOnlyDictionary<string, string?> values,
        string key) =>
        values.TryGetValue(key, out string? value)
            ? value
            : null;
}

internal sealed class CountingBarrierStream : Stream
{
    private readonly Stream _inner;
    private readonly string? _counterPath;
    private readonly string? _barrierPath;
    private readonly string? _releasePath;
    private bool _barrierReached;

    internal CountingBarrierStream(
        Stream inner,
        string? counterPath,
        string? barrierPath,
        string? releasePath)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _counterPath = counterPath;
        _barrierPath = barrierPath;
        _releasePath = releasePath;
    }

    internal long BytesRead { get; private set; }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => _inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        ReachBarrierAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        int bytesRead = _inner.Read(buffer, offset, count);
        RecordBytes(bytesRead);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await ReachBarrierAsync(cancellationToken)
            .ConfigureAwait(false);
        int bytesRead = await _inner.ReadAsync(
                buffer,
                cancellationToken)
            .ConfigureAwait(false);
        RecordBytes(bytesRead);
        return bytesRead;
    }

    private async Task ReachBarrierAsync(
        CancellationToken cancellationToken)
    {
        if (_barrierReached
            || string.IsNullOrWhiteSpace(_barrierPath))
        {
            return;
        }

        _barrierReached = true;
        WriteMarker(_barrierPath, "ready");
        while (!string.IsNullOrWhiteSpace(_releasePath)
            && !File.Exists(_releasePath))
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private void RecordBytes(int bytesRead)
    {
        if (bytesRead > 0)
            BytesRead += bytesRead;

        WriteMarker(
            _counterPath,
            BytesRead.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void WriteMarker(
        string? path,
        string content)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public override void Flush() => _inner.Flush();

    public override long Seek(
        long offset,
        SeekOrigin origin) =>
        _inner.Seek(offset, origin);

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(
        byte[] buffer,
        int offset,
        int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

internal sealed class FileProgressReporter(string? path) :
    IProgress<PackageProgress>
{
    public void Report(PackageProgress value)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string fullPath = Path.GetFullPath(path);
        HostFile.AppendLine(
            fullPath,
            $"{value.Phase}|{value.PackageId}");
    }
}

internal sealed class FileContentionObserver(string? path) :
    IPackageCacheContentionObserver
{
    public void OnRetry(
        PackageCacheContentionEvent contentionEvent)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        string fullPath = Path.GetFullPath(path);
        HostFile.AppendLine(
            fullPath,
            $"retry|{contentionEvent.RetryAttempt}|" +
            contentionEvent.LogicalKey);
    }
}

internal sealed class FileFaultObserver(
    string? progressPath,
    string? pauseFault,
    string? releasePath) :
    IPackageCacheFaultObserver
{
    public async ValueTask OnEventAsync(
        PackageCacheFaultEvent faultEvent,
        CancellationToken cancellationToken)
    {
        string marker =
            $"fault|{faultEvent.Point}|{faultEvent.State}|" +
            $"{faultEvent.OperationId}|{faultEvent.CanonicalIdentity}";
        if (!string.IsNullOrWhiteSpace(progressPath))
        {
            string fullPath = Path.GetFullPath(progressPath);
            HostFile.AppendLine(fullPath, marker);
        }

        string currentFault =
            $"{faultEvent.Point}:{faultEvent.State}";
        if (!string.Equals(
                currentFault,
                pauseFault,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        while (!string.IsNullOrWhiteSpace(releasePath)
            && !File.Exists(releasePath))
        {
            await Task.Delay(
                    TimeSpan.FromMilliseconds(10),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}

internal static class HostFile
{
    internal static void AppendLine(
        string path,
        string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = new(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite);
        using StreamWriter writer = new(
            stream,
            new System.Text.UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false));
        writer.WriteLine(content);
        writer.Flush();
    }
}

internal sealed record HostResult
{
    public bool Success { get; init; }
    public bool Cancelled { get; init; }
    public bool? Removed { get; init; }
    public int? RemovedCount { get; init; }
    public int? PackageCount { get; init; }
    public string[]? References { get; init; }
    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? ContentPath { get; init; }
    public long BytesRead { get; init; }
    public PackageInstallErrorCode? ErrorCode { get; init; }
    public PackageInstallStage? ErrorStage { get; init; }
    public string? ErrorMessage { get; init; }

    internal static HostResult FromRecord(
        PackageRecord record,
        long bytesRead) =>
        new()
        {
            Success = true,
            Name = record.Reference.Name,
            Version = record.Reference.Version,
            ContentPath = record.ContentPath,
            BytesRead = bytesRead
        };
}
