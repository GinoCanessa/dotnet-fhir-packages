// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FhirPkg.Models;

namespace FhirPkg.Qualification;

internal sealed class QualificationInvariantException(string message) :
    InvalidOperationException(message);

internal static class QualificationAssert
{
    internal static void True(bool condition, string message)
    {
        if (!condition)
            throw new QualificationInvariantException(message);
    }

    internal static void Equal<T>(
        T expected,
        T actual,
        string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new QualificationInvariantException(
                $"{message} Expected '{expected}', observed '{actual}'.");
        }
    }
}

internal sealed class QualificationProgress :
    IProgress<PackageProgress>
{
    private readonly object _sync = new();
    private readonly List<PackageProgressPhase> _phases = [];
    private readonly TaskCompletionSource _waitingForLock =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal IReadOnlyList<PackageProgressPhase> Phases
    {
        get
        {
            lock (_sync)
                return _phases.ToArray();
        }
    }

    internal Task WaitingForLock => _waitingForLock.Task;

    public void Report(PackageProgress value)
    {
        lock (_sync)
            _phases.Add(value.Phase);

        if (value.Phase == PackageProgressPhase.WaitingForLock)
            _waitingForLock.TrySetResult();
    }
}

internal sealed class CountingReadStream : Stream
{
    private readonly Stream _inner;
    private bool _disposed;

    internal CountingReadStream(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    internal long BytesRead { get; private set; }

    internal bool WasDisposed => _disposed;

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => !_disposed && _inner.CanSeek;
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
        int bytesRead = _inner.Read(buffer, offset, count);
        BytesRead += bytesRead;
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        int bytesRead = await _inner.ReadAsync(
                buffer,
                cancellationToken)
            .ConfigureAwait(false);
        BytesRead += bytesRead;
        return bytesRead;
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
        _disposed = true;
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

internal sealed class CoordinatedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _release;
    private int _startedOnce;
    private bool _disposed;

    internal CoordinatedReadStream(
        Stream inner,
        Task release)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(release);
        _inner = inner;
        _release = release;
    }

    internal Task Started => _started.Task;

    internal long BytesRead { get; private set; }

    internal bool WasDisposed => _disposed;

    public override bool CanRead => !_disposed && _inner.CanRead;
    public override bool CanSeek => !_disposed && _inner.CanSeek;
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
        BytesRead += bytesRead;
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
        BytesRead += bytesRead;
        return bytesRead;
    }

    private async Task ReachBarrierAsync(
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _startedOnce, 1) == 0)
            _started.TrySetResult();

        await _release.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
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
        _disposed = true;
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

internal sealed class BlockingReadStream : Stream
{
    private bool _disposed;

    internal bool WasDisposed => _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(
                Timeout.InfiniteTimeSpan,
                cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }

    public override int Read(
        byte[] buffer,
        int offset,
        int count) =>
        throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(
        long offset,
        SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(
        byte[] buffer,
        int offset,
        int count) =>
        throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _disposed = true;
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        _disposed = true;
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FailOnReadStream : Stream
{
    internal int ReadCount { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(
        byte[] buffer,
        int offset,
        int count)
    {
        ReadCount++;
        throw new QualificationInvariantException(
            "A strict corrupt-cache source was read.");
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        ReadCount++;
        throw new QualificationInvariantException(
            "A strict corrupt-cache source was read.");
    }

    public override void Flush()
    {
    }

    public override long Seek(
        long offset,
        SeekOrigin origin) =>
        throw new NotSupportedException();

    public override void SetLength(long value) =>
        throw new NotSupportedException();

    public override void Write(
        byte[] buffer,
        int offset,
        int count) =>
        throw new NotSupportedException();
}

internal sealed class ReportSanitizer
{
    private readonly string[] _paths;

    internal ReportSanitizer(IEnumerable<string?> paths)
    {
        _paths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal)
            .OrderByDescending(path => path.Length)
            .ToArray();
    }

    internal string Sanitize(string value)
    {
        string sanitized = value;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        foreach (string path in _paths)
        {
            sanitized = sanitized.Replace(
                path,
                "<local-path>",
                comparison);
        }

        return sanitized;
    }
}

internal sealed record QualificationBuildSnapshot(
    string Mode,
    string? RequestedPackageVersion,
    string? PackageVersion,
    string FhirPkgAssemblyVersion,
    string? FhirPkgInformationalVersion);

internal static class QualificationBuildInfo
{
    private const string PackageId = "fhir-pkg-lib";

    internal static QualificationBuildSnapshot Inspect()
    {
        Assembly assembly = typeof(FhirPackageManager).Assembly;
        string mode = GetMode();
        string? requestedPackageVersion =
            GetMetadata("FhirPkgQualificationPackageVersion");
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        string? packageVersion = string.Equals(
            mode,
            "published",
            StringComparison.Ordinal)
            ? FindResolvedPackageVersion()
            : NormalizeInformationalVersion(informationalVersion);
        return new QualificationBuildSnapshot(
            mode,
            requestedPackageVersion,
            packageVersion,
            assembly.GetName().Version?.ToString()
                ?? string.Empty,
            informationalVersion);
    }

    internal static QualificationBuildSnapshot InspectUnchecked()
    {
        Assembly assembly = typeof(FhirPackageManager).Assembly;
        string mode = GetMode();
        string? informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return new QualificationBuildSnapshot(
            mode,
            GetMetadata("FhirPkgQualificationPackageVersion"),
            string.Equals(
                mode,
                "published",
                StringComparison.Ordinal)
                ? null
                : NormalizeInformationalVersion(
                    informationalVersion),
            assembly.GetName().Version?.ToString()
                ?? string.Empty,
            informationalVersion);
    }

    internal static void Validate(
        QualificationBuildSnapshot snapshot)
    {
        if (!string.Equals(
                snapshot.Mode,
                "published",
                StringComparison.Ordinal))
        {
            return;
        }

        string requested = snapshot.RequestedPackageVersion
            ?? throw new QualificationInvariantException(
                "Published qualification did not declare an exact package version.");
        string resolved = snapshot.PackageVersion
            ?? throw new QualificationInvariantException(
                "Published qualification did not resolve fhir-pkg-lib as a package dependency.");
        if (!string.Equals(
                requested,
                resolved,
                StringComparison.Ordinal))
        {
            throw new QualificationInvariantException(
                $"Published qualification requested '{requested}' but resolved '{resolved}'.");
        }

        if (!Version.TryParse(
                $"0.{requested}",
                out Version? expectedAssemblyVersion))
        {
            throw new QualificationInvariantException(
                $"Published package version '{requested}' cannot be mapped to an assembly version.");
        }

        if (!Version.TryParse(
                snapshot.FhirPkgAssemblyVersion,
                out Version? loadedAssemblyVersion)
            || loadedAssemblyVersion != expectedAssemblyVersion)
        {
            throw new QualificationInvariantException(
                $"Loaded FhirPkg assembly version '{snapshot.FhirPkgAssemblyVersion}' does not match package '{requested}'.");
        }

        string? normalizedInformational =
            NormalizeInformationalVersion(
                snapshot.FhirPkgInformationalVersion);
        if (!string.Equals(
                normalizedInformational,
                requested,
                StringComparison.Ordinal))
        {
            throw new QualificationInvariantException(
                $"Loaded FhirPkg informational version '{snapshot.FhirPkgInformationalVersion}' does not match package '{requested}'.");
        }
    }

    private static string GetMode()
    {
        string? value = GetMetadata("FhirPkgQualificationMode");
        return string.Equals(
            value,
            "true",
            StringComparison.OrdinalIgnoreCase)
            ? "published"
            : "source";
    }

    private static string? FindResolvedPackageVersion()
    {
        HashSet<string> versions =
            new(StringComparer.Ordinal);
        foreach (string depsFile in GetDependencyContextFiles())
        {
            using FileStream stream = new(
                depsFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using JsonDocument document =
                JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty(
                    "libraries",
                    out JsonElement libraries)
                || libraries.ValueKind
                    != JsonValueKind.Object)
            {
                continue;
            }

            foreach (JsonProperty library in
                libraries.EnumerateObject())
            {
                string prefix = $"{PackageId}/";
                if (!library.Name.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!library.Value.TryGetProperty(
                        "type",
                        out JsonElement type)
                    || !string.Equals(
                        type.GetString(),
                        "package",
                        StringComparison.Ordinal))
                {
                    throw new QualificationInvariantException(
                        "Published qualification resolved fhir-pkg-lib without package dependency metadata.");
                }

                versions.Add(library.Name[prefix.Length..]);
            }
        }

        return versions.Count switch
        {
            0 => null,
            1 => versions.Single(),
            _ => throw new QualificationInvariantException(
                "Published qualification resolved multiple fhir-pkg-lib package versions.")
        };
    }

    private static IEnumerable<string> GetDependencyContextFiles()
    {
        string? configured = AppContext.GetData(
            "APP_CONTEXT_DEPS_FILES") as string;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (string path in configured.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries))
            {
                if (File.Exists(path))
                    yield return path;
            }

            yield break;
        }

        string? entryLocation =
            Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(entryLocation))
            yield break;

        string fallback = Path.ChangeExtension(
            entryLocation,
            ".deps.json");
        if (File.Exists(fallback))
            yield return fallback;
    }

    private static string? NormalizeInformationalVersion(
        string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return null;

        string version = informationalVersion.Split(
            '+',
            count: 2)[0];
        return Version.TryParse(
            version,
            out Version? parsed)
            ? parsed.ToString()
            : version;
    }

    private static string? GetMetadata(string key)
    {
        string? value = typeof(QualificationBuildInfo).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => string.Equals(
                attribute.Key,
                key,
                StringComparison.Ordinal))
            ?.Value;
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value;
    }
}

internal static class QualificationFormatting
{
    internal static string Invariant(long value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
