// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Installation;
using FhirPkg.Models;
using FhirPkg.Tests.Support;
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
