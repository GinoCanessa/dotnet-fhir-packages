// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.IO;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
using Microsoft.Win32.SafeHandles;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Cache;

public sealed class DiskPackageCacheMutationTests : IDisposable
{
    private static readonly PackageReference s_reference = new(
        "Example.Package",
        "1.0.0");

    private readonly string _cacheRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"fhir-mutations-{Guid.NewGuid():N}");

    [Fact]
    public async Task Subscriptions_NotifyExactIdentityBeforeOverwriteAndRemove()
    {
        using DiskPackageCache cache = CreateCache();
        await InstallAsync(cache, s_reference, "old");
        IPackageCacheMutationPublisher publisher = cache;
        List<PackageReference> firstEvents = [];
        List<PackageReference> secondEvents = [];
        int disposedEvents = 0;
        bool oldContentObserved = false;
        using IDisposable firstSubscription = publisher.Subscribe(
            reference =>
            {
                firstEvents.Add(reference);
                string patientPath = Path.Combine(
                    PackageCacheKey.Create(reference)
                        .GetPackageDirectoryPath(_cacheRoot),
                    "package",
                    "patient.json");
                oldContentObserved |= File.Exists(patientPath)
                    && File.ReadAllText(patientPath)
                        .Contains("\"id\":\"old\"", StringComparison.Ordinal);
            },
            () => { });
        using IDisposable secondSubscription = publisher.Subscribe(
            reference => secondEvents.Add(reference),
            () => { });
        IDisposable disposedSubscription = publisher.Subscribe(
            _ => disposedEvents++,
            () => disposedEvents++);
        disposedSubscription.Dispose();

        await InstallAsync(
            cache,
            s_reference,
            "new",
            overwriteExisting: true);
        await cache.RemoveAsync(
            s_reference,
            TestContext.Current.CancellationToken);

        PackageReference expected =
            PackageCacheKey.Create(s_reference).CanonicalReference;
        firstEvents.ShouldBe([expected, expected]);
        secondEvents.ShouldBe([expected, expected]);
        disposedEvents.ShouldBe(0);
        oldContentObserved.ShouldBeTrue();
    }

    [Fact]
    public async Task Repair_NotifiesBeforeReplacingCorruptTarget()
    {
        PackageCacheKey cacheKey = PackageCacheKey.Create(s_reference);
        string contentPath = Path.Combine(
            cacheKey.GetPackageDirectoryPath(_cacheRoot),
            "package");
        Directory.CreateDirectory(contentPath);
        await File.WriteAllTextAsync(
            Path.Combine(contentPath, "package.json"),
            """{"name":"other.package","version":"1.0.0"}""",
            TestContext.Current.CancellationToken);
        using DiskPackageCache cache = CreateCache();
        IPackageCacheMutationPublisher publisher = cache;
        List<PackageReference> invalidations = [];
        using IDisposable subscription = publisher.Subscribe(
            reference => invalidations.Add(reference),
            () => { });

        await InstallAsync(cache, s_reference, "repaired");

        invalidations.ShouldBe([cacheKey.CanonicalReference]);
    }

    [Fact]
    public async Task Recovery_NotifiesWhileRecoveringExactIdentity()
    {
        ThrowOnceObserver observer = new(
            PackageCacheFaultPoint.JournalWritten,
            PackageCacheTransactionState.Prepared);
        using (DiskPackageCache faultingCache = CreateCache(observer))
        {
            await Should.ThrowAsync<PackageCacheInjectedFaultException>(
                () => InstallAsync(
                    faultingCache,
                    s_reference,
                    "pending"));
        }

        using DiskPackageCache recoveringCache = CreateCache();
        IPackageCacheMutationPublisher publisher = recoveringCache;
        List<PackageReference> invalidations = [];
        using IDisposable subscription = publisher.Subscribe(
            reference => invalidations.Add(reference),
            () => { });

        _ = await recoveringCache.IsInstalledAsync(
            s_reference,
            TestContext.Current.CancellationToken);

        invalidations.ShouldBe(
            [PackageCacheKey.Create(s_reference).CanonicalReference]);
    }

    [Fact]
    public async Task Clear_NotifiesPackagesThenPublishesOneClear()
    {
        PackageReference second = new(
            "other.package",
            "2.0.0");
        using DiskPackageCache cache = CreateCache();
        await InstallAsync(cache, s_reference, "first");
        await InstallAsync(cache, second, "second");
        IPackageCacheMutationPublisher publisher = cache;
        List<PackageReference> invalidations = [];
        int clearNotifications = 0;
        bool packagesGoneAtClear = false;
        using IDisposable subscription = publisher.Subscribe(
            reference => invalidations.Add(reference),
            () =>
            {
                clearNotifications++;
                packagesGoneAtClear =
                    !Directory.Exists(
                        PackageCacheKey.Create(s_reference)
                            .GetPackageDirectoryPath(_cacheRoot))
                    && !Directory.Exists(
                        PackageCacheKey.Create(second)
                            .GetPackageDirectoryPath(_cacheRoot));
            });

        int removed = await cache.ClearAsync(
            TestContext.Current.CancellationToken);

        removed.ShouldBe(2);
        invalidations.ShouldBe(
        [
            PackageCacheKey.Create(s_reference).CanonicalReference,
            PackageCacheKey.Create(second).CanonicalReference
        ]);
        clearNotifications.ShouldBe(1);
        packagesGoneAtClear.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFile_LinkedResourceUsesFollowingProtectedRead(bool resourceStore)
    {
        string contentPath = CreateCachedContent(s_reference);
        string targetPath = Path.Combine(_cacheRoot, "external-resource.json");
        const string expected = """{"resourceType":"Patient","id":"linked"}""";
        await File.WriteAllTextAsync(targetPath, expected, TestContext.Current.CancellationToken);
        string resourcePath = Path.Combine(contentPath, "linked.json");
        CreateSymbolicLink(resourcePath, targetPath, isDirectory: false);
        ScriptedFileSystemPrimitives primitives = CompositionTestSupport.SystemFiles(_cacheRoot);
        int resourceOpens = 0;
        primitives.Set<SafeFileHandle>("OpenFileHandle", call =>
        {
            if (call.Path == resourcePath && resourceOpens++ < 2)
                return ScriptedFileSystemPrimitives.NativeFailure<SafeFileHandle>(5, "OpenFileHandle", call.Path);

            return SystemFileSystemPrimitives.Instance.OpenFileHandle(
                call.Path!, call.Mode!.Value, call.Access!.Value, call.Share!.Value, call.Options!.Value);
        });
        using DiskPackageCache cache = new(
            _cacheRoot, null, null, new PackageInstallLimits(), primitives.CreateContext(retries: 2));
        primitives.Calls.Clear();

        string? content = resourceStore
            ? await ((IPackageCacheResourceStore)cache).ReadFileAsync<string>(
                s_reference, "linked.json", _ => null, (_, text) => text, TestContext.Current.CancellationToken)
            : await cache.GetFileContentAsync(s_reference, "linked.json", TestContext.Current.CancellationToken);

        content.ShouldBe(expected);
        resourceOpens.ShouldBe(3);
        primitives.Calls.Single(call => call.Operation == "Probe" && call.Path == resourcePath)
            .Flags.ShouldBe(1);
        FileSystemPrimitiveCall[] opens = primitives.Calls
            .Where(call => call.Operation == "OpenFileHandle" && call.Path == resourcePath)
            .ToArray();
        opens.Length.ShouldBe(3);
        opens.ShouldAllBe(call => call.Mode == FileMode.Open
            && call.Access == FileAccess.Read
            && call.Share == FileShare.Read
            && call.Options == (FileOptions.Asynchronous | FileOptions.SequentialScan));
        primitives.Calls.Count(call => call.Operation == "CloseFile" && call.Path == resourcePath).ShouldBe(1);
        primitives.AsyncWaits.ShouldBe([10, 10]);
        primitives.Waits.ShouldBeEmpty();
        File.ReadAllText(targetPath).ShouldBe(expected);
    }

    [Fact]
    public async Task ReadFile_MissingResourceRemainsAbsentWithoutOpenOrRetry()
    {
        string contentPath = CreateCachedContent(s_reference);
        string resourcePath = Path.Combine(contentPath, "missing.json");
        ScriptedFileSystemPrimitives primitives = CompositionTestSupport.SystemFiles(_cacheRoot);
        using DiskPackageCache cache = new(
            _cacheRoot, null, null, new PackageInstallLimits(), primitives.CreateContext(retries: 2));
        bool materialized = false;
        primitives.Calls.Clear();

        string? content = await ((IPackageCacheResourceStore)cache).ReadFileAsync<string>(
            s_reference,
            "missing.json",
            _ => null,
            (_, text) =>
            {
                materialized = true;
                return text;
            },
            TestContext.Current.CancellationToken);

        content.ShouldBeNull();
        materialized.ShouldBeFalse();
        primitives.Calls.Single(call => call.Operation == "Probe" && call.Path == resourcePath)
            .Flags.ShouldBe(1);
        primitives.Calls.ShouldNotContain(call => call.Operation == "OpenFileHandle" && call.Path == resourcePath);
        primitives.AsyncWaits.ShouldBeEmpty();
        primitives.Waits.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadFile_JsonNamedDirectoryIsNotAResource(bool linked)
    {
        string contentPath = CreateCachedContent(s_reference);
        string resourcePath = Path.Combine(contentPath, "directory.json");
        if (linked)
        {
            string directoryPath = Path.Combine(_cacheRoot, "directory-target");
            Directory.CreateDirectory(directoryPath);
            CreateSymbolicLink(resourcePath, directoryPath, isDirectory: true);
        }
        else
        {
            Directory.CreateDirectory(resourcePath);
        }
        ScriptedFileSystemPrimitives primitives = CompositionTestSupport.SystemFiles(_cacheRoot);
        using DiskPackageCache cache = new(
            _cacheRoot, null, null, new PackageInstallLimits(), primitives.CreateContext(retries: 2));
        primitives.Calls.Clear();

        string? content = await cache.GetFileContentAsync(
            s_reference, "directory.json", TestContext.Current.CancellationToken);

        content.ShouldBeNull();
        primitives.Calls.Single(call => call.Operation == "Probe" && call.Path == resourcePath)
            .Flags.ShouldBe(1);
        primitives.Calls.ShouldNotContain(call => call.Operation == "OpenFileHandle" && call.Path == resourcePath);
        primitives.AsyncWaits.ShouldBeEmpty();
        primitives.Waits.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("content-directory")]
    [InlineData("scope-ancestor")]
    public async Task ReadFile_LinkCompatibilityDoesNotRelaxCacheValidation(string shape)
    {
        PackageReference reference = shape == "scope-ancestor"
            ? new PackageReference("@example/package", "1.0.0")
            : s_reference;
        string targetPath = PackageCacheKey.Create(reference).GetPackageDirectoryPath(_cacheRoot);
        string contentPath = Path.Combine(targetPath, "package");
        string linkPath;
        if (shape == "scope-ancestor")
        {
            string actualScopePath = Path.Combine(_cacheRoot, "scope-target");
            CreateCachedContent(reference, Path.Combine(actualScopePath, Path.GetFileName(targetPath), "package"));
            linkPath = Path.Combine(_cacheRoot, "@example");
            CreateSymbolicLink(linkPath, actualScopePath, isDirectory: true);
        }
        else if (shape == "content-directory")
        {
            string actualContentPath = CreateCachedContent(reference, Path.Combine(_cacheRoot, "content-target"));
            Directory.CreateDirectory(targetPath);
            linkPath = contentPath;
            CreateSymbolicLink(linkPath, actualContentPath, isDirectory: true);
        }
        else
        {
            CreateCachedContent(reference);
            linkPath = Path.Combine(contentPath, "package.json");
            string manifestTarget = Path.Combine(_cacheRoot, "manifest-target.json");
            File.Move(linkPath, manifestTarget);
            CreateSymbolicLink(linkPath, manifestTarget, isDirectory: false);
        }
        ScriptedFileSystemPrimitives primitives = CompositionTestSupport.SystemFiles(_cacheRoot);
        using DiskPackageCache cache = new(
            _cacheRoot, null, null, new PackageInstallLimits(), primitives.CreateContext(retries: 2));
        primitives.Calls.Clear();

        PackageCacheInspection inspection = await cache.InspectAsync(
            reference, TestContext.Current.CancellationToken);
        string? content = await cache.GetFileContentAsync(
            reference, "patient.json", TestContext.Current.CancellationToken);

        inspection.State.ShouldBe(PackageCacheInspectionState.Corrupt);
        inspection.CorruptionReason.ShouldNotBeNullOrWhiteSpace();
        if (shape == "scope-ancestor")
            inspection.IsRepairable.ShouldBeFalse();
        content.ShouldBeNull();
        FileSystemPrimitiveCall[] linkProbes = primitives.Calls
            .Where(call => call.Operation == "Probe" && call.Path == linkPath)
            .ToArray();
        linkProbes.ShouldNotBeEmpty();
        linkProbes.ShouldAllBe(call => call.Flags == 0);
        primitives.Calls.ShouldNotContain(
            call => call.Operation == "Probe" && call.Path == Path.Combine(contentPath, "patient.json"));
        File.Exists(Path.Combine(contentPath, "patient.json")).ShouldBeTrue();
        primitives.AsyncWaits.ShouldBeEmpty();
        primitives.Waits.ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);
    }

    private DiskPackageCache CreateCache(
        IPackageCacheFaultObserver? observer = null) =>
        new(
            _cacheRoot,
            logger: null,
            timeProvider: null,
            new PackageInstallLimits(),
            SystemPackageCacheFileOperations.Instance,
            observer ?? NullPackageCacheFaultObserver.Instance);

    private string CreateCachedContent(PackageReference reference, string? contentPath = null)
    {
        contentPath ??= Path.Combine(
            PackageCacheKey.Create(reference).GetPackageDirectoryPath(_cacheRoot), "package");
        Directory.CreateDirectory(contentPath);
        File.WriteAllText(
            Path.Combine(contentPath, "package.json"),
            $$"""{"name":"{{reference.Name}}","version":"{{reference.Version}}"}""");
        File.WriteAllText(
            Path.Combine(contentPath, "patient.json"),
            """{"resourceType":"Patient","id":"original"}""");
        return contentPath;
    }

    private static void CreateSymbolicLink(string linkPath, string targetPath, bool isDirectory)
    {
        try
        {
            if (isDirectory)
                Directory.CreateSymbolicLink(linkPath, targetPath);
            else
                File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or PlatformNotSupportedException
            || (exception is IOException && (exception.HResult & 0xFFFF) is 50 or 95 or 1314))
        {
            Assert.Skip($"Symbolic links are unavailable on this host: {exception.Message}");
        }
    }

    private static async Task InstallAsync(
        DiskPackageCache cache,
        PackageReference reference,
        string patientId,
        bool overwriteExisting = false)
    {
        using MemoryStream archive = ArbitraryTarBuilder.Create(
            ArbitraryTarBuilder.File(
                "package/package.json",
                $$"""{"name":"{{reference.Name}}","version":"{{reference.Version}}"}"""),
            ArbitraryTarBuilder.File(
                "package/patient.json",
                $$"""{"resourceType":"Patient","id":"{{patientId}}"}"""));
        await cache.InstallAsync(
            reference,
            archive,
            new InstallCacheOptions
            {
                VerifyChecksum = false,
                OverwriteExisting = overwriteExisting
            },
            TestContext.Current.CancellationToken);
    }

    private sealed class ThrowOnceObserver :
        IPackageCacheFaultObserver
    {
        private readonly PackageCacheFaultPoint _point;
        private readonly PackageCacheTransactionState _state;
        private int _thrown;

        internal ThrowOnceObserver(
            PackageCacheFaultPoint point,
            PackageCacheTransactionState state)
        {
            _point = point;
            _state = state;
        }

        public ValueTask OnEventAsync(
            PackageCacheFaultEvent faultEvent,
            CancellationToken cancellationToken)
        {
            if (faultEvent.Point == _point
                && faultEvent.State == _state
                && Interlocked.Exchange(ref _thrown, 1) == 0)
            {
                throw new PackageCacheInjectedFaultException(
                    $"Injected fault at {_point}/{_state}.");
            }

            return ValueTask.CompletedTask;
        }
    }
}
