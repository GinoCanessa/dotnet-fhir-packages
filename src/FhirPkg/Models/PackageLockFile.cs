// Copyright (c) Gino Canessa. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using FhirPkg.Cache;
using FhirPkg.Utilities;

namespace FhirPkg.Models;

/// <summary>
/// Resolution policy identity persisted in a schema-v2 package lock file.
/// </summary>
public record PackageLockPolicy
{
    /// <summary>The conflict strategy used to resolve the closure.</summary>
    public required ConflictResolutionStrategy ConflictStrategy { get; init; }

    /// <summary>Whether prerelease versions were eligible.</summary>
    public required bool AllowPreRelease { get; init; }

    /// <summary>The preferred FHIR release, when one was configured.</summary>
    public FhirRelease? PreferredFhirRelease { get; init; }

    /// <summary>The root-relative maximum dependency depth.</summary>
    public required int MaxDepth { get; init; }

    /// <summary>Deterministic identity of the active version-fixup policy.</summary>
    public required string VersionFixupHash { get; init; }
}

/// <summary>
/// Represents a <c>fhirpkg.lock.json</c> lock file that records exact roots,
/// resolution policy, and resolved dependency versions.
/// </summary>
public record PackageLockFile
{
    /// <summary>The current package lock schema version.</summary>
    public const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions s_serializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters =
        {
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: true),
        },
    };

    /// <summary>
    /// The lock schema version. Files without this field deserialize as legacy
    /// schema version 1.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The timestamp of the last lock file update.</summary>
    [JsonPropertyName("updated")]
    public required DateTime Updated { get; init; }

    /// <summary>
    /// Exact identity of the project package whose dependency graph was
    /// resolved. Required for schema version 2.
    /// </summary>
    [JsonPropertyName("rootPackage")]
    public string? RootPackage { get; init; }

    /// <summary>
    /// Exact direct dependency directive strings used as resolution roots.
    /// Required for schema version 2.
    /// </summary>
    [JsonPropertyName("roots")]
    public IReadOnlyList<string>? Roots { get; init; }

    /// <summary>
    /// Resolution policy identity. Required for schema version 2.
    /// </summary>
    [JsonPropertyName("policy")]
    public PackageLockPolicy? Policy { get; init; }

    /// <summary>The resolved dependency map: package name to exact version.</summary>
    [JsonPropertyName("dependencies")]
    public required IReadOnlyDictionary<string, string> Dependencies { get; init; }

    /// <summary>
    /// Complete dependency-first installation directives used to replay the
    /// locked graph, including mutable aliases such as <c>current</c>.
    /// Required for schema version 2.
    /// </summary>
    [JsonPropertyName("installOrder")]
    public IReadOnlyList<string>? InstallOrder { get; init; }

    /// <summary>
    /// Packages that could not be resolved during the last install.
    /// Legacy locks may contain this field; complete schema-v2 locks do not.
    /// </summary>
    [JsonPropertyName("missing")]
    public IReadOnlyDictionary<string, string>? Missing { get; init; }

    /// <summary>
    /// Structured closure failures retained for compatibility with incomplete
    /// locks written by earlier or external implementations.
    /// </summary>
    [JsonPropertyName("failures")]
    public IReadOnlyList<DependencyResolutionFailure> Failures { get; init; } =
        [];

    /// <summary>Loads a package lock file synchronously.</summary>
    public static PackageLockFile Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Lock file not found: {path}", path);

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete);
        PackageLockFile lockFile =
            JsonSerializer.Deserialize<PackageLockFile>(
                stream,
                s_serializerOptions)
            ?? throw new JsonException(
                "Deserialization of lock file returned null.");
        ValidateSchema(lockFile);
        return lockFile;
    }

    /// <summary>Loads a package lock file asynchronously.</summary>
    public static async Task<PackageLockFile> LoadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!File.Exists(path))
            throw new FileNotFoundException($"Lock file not found: {path}", path);

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        PackageLockFile lockFile =
            await JsonSerializer.DeserializeAsync<PackageLockFile>(
                    stream,
                    s_serializerOptions,
                    cancellationToken)
                .ConfigureAwait(false)
            ?? throw new JsonException(
                "Deserialization of lock file returned null.");
        ValidateSchema(lockFile);
        return lockFile;
    }

    /// <summary>Saves this lock file synchronously using a durable atomic write.</summary>
    public void Save(string path) =>
        SaveAsync(path).GetAwaiter().GetResult();

    /// <summary>Saves this lock file using a durable same-directory atomic write.</summary>
    public Task SaveAsync(
        string path,
        CancellationToken cancellationToken = default) =>
        SaveAsync(
            path,
            SystemPackageCacheFileOperations.Instance,
            cancellationToken);

    internal async Task SaveAsync(
        string path,
        IDurableFileOperations fileOperations,
        CancellationToken cancellationToken)
    {
        await SaveAsync(
                path,
                fileOperations,
                beforeCommit: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task SaveAsync(
        string path,
        Func<CancellationToken, ValueTask> beforeCommit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(beforeCommit);
        return SaveAsync(
            path,
            SystemPackageCacheFileOperations.Instance,
            beforeCommit,
            cancellationToken);
    }

    private async Task SaveAsync(
        string path,
        IDurableFileOperations fileOperations,
        Func<CancellationToken, ValueTask>? beforeCommit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(fileOperations);
        ValidateSchema(this);

        byte[] content = JsonSerializer.SerializeToUtf8Bytes(
            this,
            s_serializerOptions);
        await DurableFileWriter.WriteAsync(
                path,
                content,
                fileOperations,
                cancellationToken,
                beforeCommit)
            .ConfigureAwait(false);
    }

    private static void ValidateSchema(PackageLockFile lockFile)
    {
        if (lockFile.SchemaVersion < 1)
        {
            throw new InvalidDataException(
                $"Package lock schema version {lockFile.SchemaVersion} is invalid.");
        }

        if (lockFile.SchemaVersion > CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Package lock schema version {lockFile.SchemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }

        if (lockFile.SchemaVersion == CurrentSchemaVersion
            && (string.IsNullOrWhiteSpace(
                    lockFile.RootPackage)
                || lockFile.Roots is null
                || lockFile.Policy is null
                || lockFile.InstallOrder is null
                || string.IsNullOrWhiteSpace(
                    lockFile.Policy.VersionFixupHash)
                || lockFile.Policy.MaxDepth < 0
                || !Enum.IsDefined(
                    lockFile.Policy.ConflictStrategy)
                || (lockFile.Policy.PreferredFhirRelease
                        is FhirRelease release
                    && !Enum.IsDefined(release))))
        {
            throw new InvalidDataException(
                "Package lock schema version 2 is missing a valid roots or policy identity.");
        }
    }
}
