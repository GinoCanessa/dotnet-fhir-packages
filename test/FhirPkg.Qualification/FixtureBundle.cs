// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using FhirPkg.Qualification.Models;

namespace FhirPkg.Qualification;

internal static class FixtureBundle
{
    internal const string ManifestFileName = "bundle-manifest.json";

    internal const string BlobDirectoryName = "blobs";

    internal const string ArtifactNamePrefix = "fixture-bundle-v1-";

    internal const int SchemaVersion = 1;

    internal const string ImmutableKind = "immutable";

    internal const string MutableKind = "mutable";

    internal const string FreezeCaseId = "bundle-freeze";

    internal const string BundleSha256DetailName = "bundleSha256";

    internal const string BlockedArtifactsDetailName =
        "blockedArtifacts";

    internal static string GetBlobFileName(string sha256) =>
        $"{sha256}.tgz";

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is (>= '0' and <= '9')
                or (>= 'a' and <= 'f'));

    internal static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content))
            .ToLowerInvariant();

    internal static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(
                stream,
                cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureBundleManifest
{
    public required int SchemaVersion { get; init; }

    public required string CorpusSha256 { get; init; }

    public required string CorpusHashAlgorithm { get; init; }

    public required DateTimeOffset RecordedUtc { get; init; }

    public FixtureBundleRecorder? RecordedBy { get; init; }

    public required IReadOnlyList<FixtureBundleArtifact> Artifacts { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureBundleRecorder
{
    public required string Repository { get; init; }

    public required string RunId { get; init; }

    public required string Commit { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record FixtureBundleArtifact
{
    public required string Id { get; init; }

    public required string Kind { get; init; }

    public required string Uri { get; init; }

    public required string Sha256 { get; init; }

    public required long CompressedBytes { get; init; }

    public required long ExpandedBytes { get; init; }

    public required long LargestEntryBytes { get; init; }

    public required int EntryCount { get; init; }

    public DateTimeOffset? PublicationUtc { get; init; }
}

internal sealed record VerifiedFixtureBundle(
    string Digest,
    FixtureBundleManifest Manifest,
    IReadOnlyDictionary<string, string> BlobPaths,
    QualificationCorpus EffectiveCorpus);

internal sealed class FixtureBundleException(
    string code,
    string message) :
    Exception(message)
{
    internal string Code { get; } = code;
}

internal static class FixtureBundleWriter
{
    internal static Task<string> WriteFrozenAsync(
        string outputDirectory,
        QualificationCorpus corpus,
        string corpusSha256,
        IReadOnlyDictionary<string, DownloadedArtifact> artifacts,
        CancellationToken cancellationToken) =>
        WriteFrozenAsync(
            outputDirectory,
            corpus,
            corpusSha256,
            artifacts,
            Environment.GetEnvironmentVariable,
            cancellationToken);

    internal static async Task<string> WriteFrozenAsync(
        string outputDirectory,
        QualificationCorpus corpus,
        string corpusSha256,
        IReadOnlyDictionary<string, DownloadedArtifact> artifacts,
        Func<string, string?> getEnvironmentVariable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusSha256);
        ArgumentNullException.ThrowIfNull(artifacts);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        string root = Path.GetFullPath(outputDirectory);
        bool createdRoot = PrepareOutputDirectory(root);
        try
        {
            string blobRoot = Path.Combine(
                root,
                FixtureBundle.BlobDirectoryName);
            Directory.CreateDirectory(blobRoot);
            HashSet<string> writtenBlobs = new(StringComparer.Ordinal);
            List<FixtureBundleArtifact> entries = [];
            foreach (ImmutableArtifactDefinition definition in
                corpus.ImmutableArtifacts)
            {
                entries.Add(await FreezeArtifactAsync(
                        blobRoot,
                        writtenBlobs,
                        FixtureBundle.ImmutableKind,
                        definition.Id,
                        definition.Uri,
                        definition.Sha256,
                        artifacts,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            foreach (MutableArtifactDefinition definition in
                corpus.MutableArtifacts)
            {
                entries.Add(await FreezeArtifactAsync(
                        blobRoot,
                        writtenBlobs,
                        FixtureBundle.MutableKind,
                        definition.Id,
                        definition.Uri,
                        pinnedSha256: null,
                        artifacts,
                        cancellationToken)
                    .ConfigureAwait(false));
            }

            FixtureBundleManifest manifest = new()
            {
                SchemaVersion = FixtureBundle.SchemaVersion,
                CorpusSha256 = corpusSha256,
                CorpusHashAlgorithm = QualificationCorpusHash.Algorithm,
                RecordedUtc = DateTimeOffset.UtcNow,
                RecordedBy = CreateRecorder(getEnvironmentVariable),
                Artifacts = entries
                    .OrderBy(
                        entry => entry.Kind,
                        StringComparer.Ordinal)
                    .ThenBy(
                        entry => entry.Id,
                        StringComparer.Ordinal)
                    .ToArray()
            };
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                QualificationJson.SerializerOptions);
            await File.WriteAllBytesAsync(
                    Path.Combine(root, FixtureBundle.ManifestFileName),
                    manifestBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            return FixtureBundle.ComputeSha256(manifestBytes);
        }
        catch
        {
            DeleteWrittenContent(root, createdRoot);
            throw;
        }
    }

    private static async Task<FixtureBundleArtifact> FreezeArtifactAsync(
        string blobRoot,
        HashSet<string> writtenBlobs,
        string kind,
        string id,
        string uri,
        string? pinnedSha256,
        IReadOnlyDictionary<string, DownloadedArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        if (!artifacts.TryGetValue(
                id,
                out DownloadedArtifact? artifact))
        {
            throw Blocked(
                $"Artifact '{id}' was not acquired, so no bundle was frozen.");
        }

        if (!FixtureBundle.IsSha256(artifact.ActualSha256))
        {
            throw Blocked(
                $"Artifact '{id}' has no valid SHA-256, so no bundle was frozen.");
        }

        if (pinnedSha256 is not null
            && !string.Equals(
                pinnedSha256,
                artifact.ActualSha256,
                StringComparison.Ordinal))
        {
            throw Blocked(
                $"Artifact '{id}' SHA-256 {artifact.ActualSha256} does not match its pin {pinnedSha256}, so no bundle was frozen.");
        }

        if (writtenBlobs.Add(artifact.ActualSha256))
        {
            string blobPath = Path.Combine(
                blobRoot,
                FixtureBundle.GetBlobFileName(artifact.ActualSha256));
            File.Copy(
                artifact.FilePath,
                blobPath,
                overwrite: false);
            string copiedSha256 = await FixtureBundle
                .ComputeFileSha256Async(
                    blobPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    copiedSha256,
                    artifact.ActualSha256,
                    StringComparison.Ordinal))
            {
                throw Blocked(
                    $"Artifact '{id}' changed after it was verified: its bytes now hash to {copiedSha256}, so no bundle was frozen.");
            }
        }

        return new FixtureBundleArtifact
        {
            Id = id,
            Kind = kind,
            Uri = uri,
            Sha256 = artifact.ActualSha256,
            CompressedBytes = artifact.CompressedBytes,
            ExpandedBytes = artifact.Metrics.ExpandedBytes,
            LargestEntryBytes = artifact.Metrics.LargestEntryBytes,
            EntryCount = artifact.Metrics.EntryCount,
            PublicationUtc = artifact.PublicationDate?.ToUniversalTime()
        };
    }

    private static FixtureBundleRecorder? CreateRecorder(
        Func<string, string?> getEnvironmentVariable)
    {
        string? repository = getEnvironmentVariable("GITHUB_REPOSITORY");
        string? runId = getEnvironmentVariable("GITHUB_RUN_ID");
        string? commit = getEnvironmentVariable("GITHUB_SHA");
        if (string.IsNullOrWhiteSpace(repository)
            || string.IsNullOrWhiteSpace(runId)
            || string.IsNullOrWhiteSpace(commit))
        {
            return null;
        }

        return new FixtureBundleRecorder
        {
            Repository = repository,
            RunId = runId,
            Commit = commit
        };
    }

    private static bool PrepareOutputDirectory(string root)
    {
        if (File.Exists(root))
        {
            throw Blocked(
                "The bundle output path is a file; it must be absent or an empty directory.");
        }

        if (!Directory.Exists(root))
        {
            Directory.CreateDirectory(root);
            return true;
        }

        if (Directory.EnumerateFileSystemEntries(root).Any())
        {
            throw Blocked(
                "The bundle output directory must be absent or empty.");
        }

        return false;
    }

    private static void DeleteWrittenContent(
        string root,
        bool createdRoot)
    {
        try
        {
            if (!Directory.Exists(root))
                return;

            if (createdRoot)
            {
                Directory.Delete(root, recursive: true);
                return;
            }

            foreach (string entry in Directory.GetFileSystemEntries(root))
            {
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                else
                    File.Delete(entry);
            }
        }
        catch (IOException)
        {
            // Cleanup is best effort; the caller rethrows the original failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Cleanup is best effort; the caller rethrows the original failure.
        }
    }

    private static FixtureBundleException Blocked(string message) =>
        new(QualificationFailureCodes.BundleFreezeBlocked, message);
}

internal static class FixtureBundleVerifier
{
    internal static async Task<VerifiedFixtureBundle> VerifyAsync(
        string bundleDirectory,
        string expectedSha256,
        QualificationCorpus corpus,
        string corpusSha256,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectory);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(corpusSha256);

        string root = Path.GetFullPath(bundleDirectory);
        if (!Directory.Exists(root))
            throw Failed("The bundle directory does not exist.");

        string manifestPath = Path.Combine(
            root,
            FixtureBundle.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw Failed(
                $"The bundle has no {FixtureBundle.ManifestFileName}.");
        }

        byte[] manifestBytes = await File.ReadAllBytesAsync(
                manifestPath,
                cancellationToken)
            .ConfigureAwait(false);
        string digest = FixtureBundle.ComputeSha256(manifestBytes);
        if (!string.Equals(
                digest,
                expectedSha256,
                StringComparison.Ordinal))
        {
            throw Failed(
                $"The bundle manifest SHA-256 was {digest}, not the expected {expectedSha256}.");
        }

        FixtureBundleManifest manifest = ParseManifest(manifestBytes);
        if (manifest.SchemaVersion != FixtureBundle.SchemaVersion)
        {
            throw Failed(
                $"The bundle manifest declares schema version {manifest.SchemaVersion}; only version {FixtureBundle.SchemaVersion} is supported.");
        }

        if (!string.Equals(
                manifest.CorpusHashAlgorithm,
                QualificationCorpusHash.Algorithm,
                StringComparison.Ordinal))
        {
            throw Failed(
                $"The bundle manifest declares corpus hash algorithm '{manifest.CorpusHashAlgorithm}', not '{QualificationCorpusHash.Algorithm}'.");
        }

        if (!string.Equals(
                manifest.CorpusSha256,
                corpusSha256,
                StringComparison.Ordinal))
        {
            throw Failed(
                $"The bundle was frozen for corpus {manifest.CorpusSha256}, not for corpus {corpusSha256}.");
        }

        VerifyBundleEntries(root);
        VerifyArtifacts(manifest, corpus);
        IReadOnlyDictionary<string, string> blobPaths =
            await VerifyBlobsAsync(
                    root,
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
        return new VerifiedFixtureBundle(
            digest,
            manifest,
            blobPaths,
            corpus);
    }

    private static FixtureBundleManifest ParseManifest(
        byte[] manifestBytes)
    {
        FixtureBundleManifest? manifest;
        try
        {
            using (JsonDocument document =
                JsonDocument.Parse(manifestBytes))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && (root.TryGetProperty("approvals", out _)
                        || root.TryGetProperty(
                            "baseBundleSha256",
                            out _)))
                {
                    throw Failed(
                        "The bundle manifest carries approval data, which this harness does not accept.");
                }
            }

            manifest = JsonSerializer.Deserialize<FixtureBundleManifest>(
                manifestBytes,
                QualificationJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw Failed(
                $"The bundle manifest is not a valid schema version {FixtureBundle.SchemaVersion} manifest: {exception.Message}");
        }

        if (manifest is null
            || manifest.Artifacts is null
            || manifest.Artifacts.Any(artifact => artifact is null))
        {
            throw Failed("The bundle manifest is incomplete.");
        }

        return manifest;
    }

    private static void VerifyBundleEntries(string root)
    {
        bool hasBlobDirectory = false;
        foreach (FileSystemInfo entry in
            new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if (entry is FileInfo
                && string.Equals(
                    entry.Name,
                    FixtureBundle.ManifestFileName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (entry is DirectoryInfo
                && string.Equals(
                    entry.Name,
                    FixtureBundle.BlobDirectoryName,
                    StringComparison.Ordinal))
            {
                hasBlobDirectory = true;
                continue;
            }

            throw Failed(
                $"The bundle contains the unexpected entry '{entry.Name}'; only {FixtureBundle.ManifestFileName} and {FixtureBundle.BlobDirectoryName}/ are allowed.");
        }

        if (!hasBlobDirectory)
        {
            throw Failed(
                $"The bundle has no {FixtureBundle.BlobDirectoryName}/ directory.");
        }
    }

    private static void VerifyArtifacts(
        FixtureBundleManifest manifest,
        QualificationCorpus corpus)
    {
        Dictionary<string, FixtureBundleArtifact> immutable =
            new(StringComparer.Ordinal);
        Dictionary<string, FixtureBundleArtifact> mutable =
            new(StringComparer.Ordinal);
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (FixtureBundleArtifact artifact in manifest.Artifacts)
        {
            if (string.IsNullOrEmpty(artifact.Id))
                throw Failed("The bundle lists an artifact without an id.");

            if (!ids.Add(artifact.Id))
            {
                throw Failed(
                    $"The bundle lists artifact '{artifact.Id}' more than once.");
            }

            if (!FixtureBundle.IsSha256(artifact.Sha256))
            {
                throw Failed(
                    $"Bundle artifact '{artifact.Id}' does not record a valid SHA-256.");
            }

            switch (artifact.Kind)
            {
                case FixtureBundle.ImmutableKind:
                    immutable.Add(artifact.Id, artifact);
                    break;
                case FixtureBundle.MutableKind:
                    mutable.Add(artifact.Id, artifact);
                    break;
                default:
                    throw Failed(
                        $"Bundle artifact '{artifact.Id}' has the unsupported kind '{artifact.Kind}'.");
            }
        }

        VerifyIds(
            FixtureBundle.ImmutableKind,
            immutable.Keys,
            corpus.ImmutableArtifacts.Select(definition => definition.Id));
        foreach (ImmutableArtifactDefinition definition in
            corpus.ImmutableArtifacts)
        {
            FixtureBundleArtifact entry = immutable[definition.Id];
            VerifyUri(entry, definition.Uri);
            if (!string.Equals(
                    entry.Sha256,
                    definition.Sha256,
                    StringComparison.Ordinal))
            {
                throw Failed(
                    $"Bundle artifact '{definition.Id}' records SHA-256 {entry.Sha256}, not the corpus pin {definition.Sha256}.");
            }
        }

        VerifyIds(
            FixtureBundle.MutableKind,
            mutable.Keys,
            corpus.MutableArtifacts.Select(definition => definition.Id));
        foreach (MutableArtifactDefinition definition in
            corpus.MutableArtifacts)
        {
            VerifyUri(mutable[definition.Id], definition.Uri);
        }
    }

    private static void VerifyIds(
        string kind,
        IEnumerable<string> bundleIds,
        IEnumerable<string> corpusIds)
    {
        string[] actual = bundleIds
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] expected = corpusIds
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (actual.SequenceEqual(
                expected,
                StringComparer.Ordinal))
        {
            return;
        }

        string missing = string.Join(
            ",",
            expected.Except(
                actual,
                StringComparer.Ordinal));
        string unexpected = string.Join(
            ",",
            actual.Except(
                expected,
                StringComparer.Ordinal));
        throw Failed(
            $"The bundle's {kind} artifacts differ from the corpus; missing=[{missing}], unexpected=[{unexpected}].");
    }

    private static void VerifyUri(
        FixtureBundleArtifact entry,
        string corpusUri)
    {
        if (!string.Equals(
                entry.Uri,
                corpusUri,
                StringComparison.Ordinal))
        {
            throw Failed(
                $"Bundle artifact '{entry.Id}' records URI '{entry.Uri}', not the corpus URI '{corpusUri}'.");
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>>
        VerifyBlobsAsync(
            string root,
            FixtureBundleManifest manifest,
            CancellationToken cancellationToken)
    {
        string blobRoot = Path.Combine(
            root,
            FixtureBundle.BlobDirectoryName);
        Dictionary<string, string> referencedBy =
            new(StringComparer.Ordinal);
        foreach (FixtureBundleArtifact artifact in manifest.Artifacts)
        {
            referencedBy.TryAdd(
                FixtureBundle.GetBlobFileName(artifact.Sha256),
                artifact.Id);
        }

        HashSet<string> present = new(StringComparer.Ordinal);
        foreach (FileSystemInfo entry in
            new DirectoryInfo(blobRoot).EnumerateFileSystemInfos())
        {
            if (entry is not FileInfo
                || !referencedBy.ContainsKey(entry.Name))
            {
                throw Failed(
                    $"The bundle's {FixtureBundle.BlobDirectoryName}/ directory contains the unexpected entry '{entry.Name}'.");
            }

            present.Add(entry.Name);
        }

        string[] blobNames = referencedBy.Keys
            .Order(StringComparer.Ordinal)
            .ToArray();
        foreach (string blobName in blobNames)
        {
            if (!present.Contains(blobName))
            {
                throw Failed(
                    $"The bundle is missing blob '{blobName}' for artifact '{referencedBy[blobName]}'.");
            }
        }

        foreach (string blobName in blobNames)
        {
            string actualSha256 = await FixtureBundle
                .ComputeFileSha256Async(
                    Path.Combine(blobRoot, blobName),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(
                    FixtureBundle.GetBlobFileName(actualSha256),
                    blobName,
                    StringComparison.Ordinal))
            {
                throw Failed(
                    $"Bundle blob '{blobName}' hashes to {actualSha256}.");
            }
        }

        return manifest.Artifacts.ToDictionary(
            artifact => artifact.Id,
            artifact => Path.Combine(
                blobRoot,
                FixtureBundle.GetBlobFileName(artifact.Sha256)),
            StringComparer.Ordinal);
    }

    private static FixtureBundleException Failed(string message) =>
        new(QualificationFailureCodes.BundleVerificationFailed, message);
}
