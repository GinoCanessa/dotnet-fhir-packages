// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Cache;
using FhirPkg.Models;
using FhirPkg.Utilities;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Models;

public sealed class PackageLockFileTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        $"package-lock-{Guid.NewGuid():N}");

    public PackageLockFileTests()
    {
        Directory.CreateDirectory(_testRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SaveAsync_SchemaV2_RoundTripsPolicyAndRoots()
    {
        string path = Path.Combine(
            _testRoot,
            "fhirpkg.lock.json");
        PackageLockFile expected = CreateLockFile();

        await expected.SaveAsync(
            path,
            TestContext.Current.CancellationToken);
        PackageLockFile actual = await PackageLockFile.LoadAsync(
            path,
            TestContext.Current.CancellationToken);

        actual.SchemaVersion.ShouldBe(
            PackageLockFile.CurrentSchemaVersion);
        actual.RootPackage.ShouldBe(expected.RootPackage);
        actual.Roots.ShouldBe(expected.Roots);
        actual.Policy.ShouldBe(expected.Policy);
        actual.Dependencies.ShouldBe(expected.Dependencies);
        actual.InstallOrder.ShouldBe(expected.InstallOrder);
    }

    [Fact]
    public async Task SaveAsync_SchemaV2_UsesStableCamelCaseStringEnums()
    {
        string path = Path.Combine(
            _testRoot,
            "schema-shape.lock.json");

        await CreateLockFile().SaveAsync(
            path,
            TestContext.Current.CancellationToken);
        string json = await File.ReadAllTextAsync(
            path,
            TestContext.Current.CancellationToken);

        json.ShouldContain(
            "\"conflictStrategy\": \"highestWins\"");
        json.ShouldContain(
            "\"preferredFhirRelease\": \"r4\"");
        json.Contains(
                "\"ConflictStrategy\"",
                StringComparison.Ordinal)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task LoadAsync_LegacyFileDefaultsToSchemaV1()
    {
        string path = Path.Combine(
            _testRoot,
            "legacy.lock.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "updated": "2026-07-18T12:00:00Z",
              "dependencies": {
                "example.package": "1.0.0"
              }
            }
            """,
            TestContext.Current.CancellationToken);

        PackageLockFile lockFile =
            await PackageLockFile.LoadAsync(
                path,
                TestContext.Current.CancellationToken);

        lockFile.SchemaVersion.ShouldBe(1);
        lockFile.Roots.ShouldBeNull();
        lockFile.Policy.ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_FutureSchema_IsRejected()
    {
        string path = Path.Combine(
            _testRoot,
            "future.lock.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 99,
              "updated": "2026-07-18T12:00:00Z",
              "dependencies": {}
            }
            """,
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NotSupportedException>(
            () => PackageLockFile.LoadAsync(
                path,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveAsync_PreCommitFailure_PreservesPriorBytes()
    {
        string path = Path.Combine(
            _testRoot,
            "preserved.lock.json");
        byte[] original = "original-lock"u8.ToArray();
        await File.WriteAllBytesAsync(
            path,
            original,
            TestContext.Current.CancellationToken);
        FaultingDurableFileOperations fileOperations =
            new(throwBeforeCommit: true);

        await Should.ThrowAsync<IOException>(
            () => CreateLockFile().SaveAsync(
                path,
                fileOperations,
                TestContext.Current.CancellationToken));

        (await File.ReadAllBytesAsync(
            path,
            TestContext.Current.CancellationToken))
            .ShouldBe(original);
        Directory.GetFiles(
            _testRoot,
            ".*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveAsync_CancelledBeforeCommit_PreservesPriorBytes()
    {
        string path = Path.Combine(
            _testRoot,
            "cancelled.lock.json");
        byte[] original = "original-lock"u8.ToArray();
        await File.WriteAllBytesAsync(
            path,
            original,
            TestContext.Current.CancellationToken);
        using CancellationTokenSource source =
            new();
        FaultingDurableFileOperations fileOperations =
            new(cancelAfterWrite: source);

        await Should.ThrowAsync<OperationCanceledException>(
            () => CreateLockFile().SaveAsync(
                path,
                fileOperations,
                source.Token));

        (await File.ReadAllBytesAsync(
            path,
            TestContext.Current.CancellationToken))
            .ShouldBe(original);
        Directory.GetFiles(
            _testRoot,
            ".*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveAsync_ConcurrentReader_AllowsAtomicReplacement()
    {
        string path = Path.Combine(
            _testRoot,
            "reader.lock.json");
        PackageLockFile original = CreateLockFile();
        await original.SaveAsync(
            path,
            TestContext.Current.CancellationToken);
        FileStream reader = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            4096,
            FileOptions.Asynchronous);
        PackageLockFile replacement = CreateLockFile() with
        {
            Updated = original.Updated.AddMinutes(1),
        };

        Task saveTask = replacement.SaveAsync(
            path,
            TestContext.Current.CancellationToken);
        await Task.Delay(
            50,
            TestContext.Current.CancellationToken);
        await reader.DisposeAsync();
        await saveTask;

        PackageLockFile actual =
            await PackageLockFile.LoadAsync(
                path,
                TestContext.Current.CancellationToken);
        actual.Updated.ShouldBe(replacement.Updated);
    }

    private static PackageLockFile CreateLockFile() =>
        new()
        {
            SchemaVersion =
                PackageLockFile.CurrentSchemaVersion,
            Updated = new DateTime(
                2026,
                7,
                18,
                12,
                0,
                0,
                DateTimeKind.Utc),
            RootPackage = "root.package#1.0.0",
            Roots = ["example.package#^1.0.0"],
            Policy = new PackageLockPolicy
            {
                ConflictStrategy =
                    ConflictResolutionStrategy.HighestWins,
                AllowPreRelease = true,
                PreferredFhirRelease = FhirRelease.R4,
                MaxDepth = 20,
                VersionFixupHash = "fixup-hash",
            },
            Dependencies =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["example.package"] = "1.2.3",
                },
            InstallOrder = ["example.package#1.2.3"],
            Failures = [],
        };

    private sealed class FaultingDurableFileOperations :
        IDurableFileOperations
    {
        private readonly IDurableFileOperations _inner =
            SystemPackageCacheFileOperations.Instance;
        private readonly bool _throwBeforeCommit;
        private readonly CancellationTokenSource? _cancelAfterWrite;

        internal FaultingDurableFileOperations(
            bool throwBeforeCommit = false,
            CancellationTokenSource? cancelAfterWrite = null)
        {
            _throwBeforeCommit = throwBeforeCommit;
            _cancelAfterWrite = cancelAfterWrite;
        }

        public bool FileExists(string path) =>
            _inner.FileExists(path);

        public void CreateDirectory(string path) =>
            _inner.CreateDirectory(path);

        public void DeleteFile(string path) =>
            _inner.DeleteFile(path);

        public async ValueTask WriteFileAndFlushAsync(
            string path,
            ReadOnlyMemory<byte> content,
            CancellationToken cancellationToken)
        {
            await _inner.WriteFileAndFlushAsync(
                    path,
                    content,
                    cancellationToken)
                .ConfigureAwait(false);
            _cancelAfterWrite?.Cancel();
        }

        public void AtomicReplaceFile(
            string sourcePath,
            string destinationPath)
        {
            if (_throwBeforeCommit)
            {
                throw new IOException(
                    "Injected pre-commit failure.");
            }

            _inner.AtomicReplaceFile(
                sourcePath,
                destinationPath);
        }

        public void SynchronizeDirectory(
            string directoryPath) =>
            _inner.SynchronizeDirectory(directoryPath);
    }
}
