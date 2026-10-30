// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using FhirPkg.Models;
using FhirPkg.Qualification.Models;
using FhirPkg.Registry;

namespace FhirPkg.Qualification;

internal sealed class FixtureRequestLog
{
    private readonly ConcurrentQueue<string> _entries = new();

    internal void Record(string entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entry);
        _entries.Enqueue(entry);
    }

    internal IReadOnlyList<string> Drain()
    {
        List<string> entries = [];
        while (_entries.TryDequeue(out string? entry))
            entries.Add(entry);

        return entries;
    }
}

internal sealed record FixtureRoute(
    string MediaType,
    string? BlobPath,
    byte[] Content,
    DateTimeOffset? LastModified);

internal sealed class FixtureRoutes
{
    internal const string ArchiveMediaType = "application/octet-stream";

    internal const string ListingMediaType = "application/json";

    internal const long MaximumManifestBytes = 16L * 1024L * 1024L;

    private const string ManifestEntryName = "package/package.json";

    private readonly FrozenDictionary<string, FixtureRoute> _routes;

    private FixtureRoutes(Dictionary<string, FixtureRoute> routes)
    {
        _routes = routes.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    internal IReadOnlyCollection<string> Uris => _routes.Keys;

    internal static async Task<FixtureRoutes> CreateAsync(
        VerifiedFixtureBundle bundle,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        QualificationCorpus corpus = bundle.EffectiveCorpus;
        Dictionary<string, FixtureRoute> routes =
            new(StringComparer.Ordinal);
        Dictionary<string, List<ListedVersion>> listings =
            new(StringComparer.Ordinal);
        foreach (ImmutableArtifactDefinition definition in
            corpus.ImmutableArtifacts)
        {
            string blobPath = AddArchiveRoute(
                routes,
                bundle,
                definition.Id,
                definition.Uri);
            PackageManifestFacts manifest = await ReadManifestFactsAsync(
                    blobPath,
                    cancellationToken)
                .ConfigureAwait(false);
            string sha1 = await ComputeSha1Async(
                    blobPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!listings.TryGetValue(
                    definition.Name,
                    out List<ListedVersion>? versions))
            {
                versions = [];
                listings.Add(definition.Name, versions);
            }

            versions.Add(new ListedVersion(
                definition.Version,
                definition.Uri,
                sha1,
                manifest));
        }

        foreach (MutableArtifactDefinition definition in
            corpus.MutableArtifacts)
        {
            AddArchiveRoute(
                routes,
                bundle,
                definition.Id,
                definition.Uri);
        }

        foreach ((string name, List<ListedVersion> versions) in listings)
        {
            AddRoute(
                routes,
                GetListingUri(name),
                new FixtureRoute(
                    ListingMediaType,
                    BlobPath: null,
                    CreateListing(name, versions),
                    LastModified: null));
        }

        return new FixtureRoutes(routes);
    }

    internal static string GetListingUri(string name) =>
        new Uri(
                $"{RegistryEndpoint.FhirSecondary.Url.TrimEnd('/')}/{Uri.EscapeDataString(name)}")
            .AbsoluteUri;

    internal static FileStream OpenBlob(string path) =>
        new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 65_536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    internal bool TryGetRoute(
        Uri? uri,
        [NotNullWhen(true)] out FixtureRoute? route)
    {
        route = null;
        return uri is { IsAbsoluteUri: true }
            && _routes.TryGetValue(uri.AbsoluteUri, out route);
    }

    private static string AddArchiveRoute(
        Dictionary<string, FixtureRoute> routes,
        VerifiedFixtureBundle bundle,
        string id,
        string uri)
    {
        string blobPath = bundle.GetBlobPath(id);
        AddRoute(
            routes,
            new Uri(uri).AbsoluteUri,
            new FixtureRoute(
                ArchiveMediaType,
                blobPath,
                [],
                bundle.GetArtifact(id).PublicationUtc));
        return blobPath;
    }

    private static void AddRoute(
        Dictionary<string, FixtureRoute> routes,
        string uri,
        FixtureRoute route)
    {
        if (!routes.TryAdd(uri, route))
        {
            throw new QualificationInvariantException(
                $"The fixture registry would route '{uri}' more than once.");
        }
    }

    private static async Task<PackageManifestFacts> ReadManifestFactsAsync(
        string blobPath,
        CancellationToken cancellationToken)
    {
        await using FileStream blob = OpenBlob(blobPath);
        await using GZipStream gzip = new(
            blob,
            CompressionMode.Decompress,
            leaveOpen: true);
        using TarReader reader = new(
            gzip,
            leaveOpen: false);
        while (await reader.GetNextEntryAsync(
                copyData: false,
                cancellationToken)
            .ConfigureAwait(false) is TarEntry entry)
        {
            if (entry.EntryType is not (
                    TarEntryType.RegularFile
                        or TarEntryType.V7RegularFile
                        or TarEntryType.ContiguousFile)
                || !string.Equals(
                    entry.Name,
                    ManifestEntryName,
                    StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Length > MaximumManifestBytes)
            {
                throw new InvalidDataException(
                    $"The {ManifestEntryName} entry of blob '{Path.GetFileName(blobPath)}' exceeds {MaximumManifestBytes} bytes.");
            }

            byte[] content = new byte[entry.Length];
            if (entry.DataStream is Stream data)
            {
                await data.ReadExactlyAsync(
                        content,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return ParseManifestFacts(content, blobPath);
        }

        return PackageManifestFacts.None;
    }

    private static PackageManifestFacts ParseManifestFacts(
        byte[] content,
        string blobPath)
    {
        ReadOnlyMemory<byte> json = content;
        if (json.Span.StartsWith(Utf8ByteOrderMark))
            json = json[Utf8ByteOrderMark.Length..];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"The {ManifestEntryName} entry of blob '{Path.GetFileName(blobPath)}' is not a JSON object.");
            }

            string? fhirVersion = ReadFirstString(root, "fhirVersions")
                ?? ReadFirstString(root, "fhirVersion");
            JsonElement? dependencies =
                root.TryGetProperty(
                    "dependencies",
                    out JsonElement value)
                && value.ValueKind == JsonValueKind.Object
                    ? value.Clone()
                    : null;
            return new PackageManifestFacts(fhirVersion, dependencies);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"The {ManifestEntryName} entry of blob '{Path.GetFileName(blobPath)}' is not valid JSON: {exception.Message}",
                exception);
        }
    }

    private static string? ReadFirstString(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement value))
            return null;

        JsonElement first = value;
        if (value.ValueKind == JsonValueKind.Array)
        {
            using JsonElement.ArrayEnumerator items = value.EnumerateArray();
            if (!items.MoveNext())
                return null;

            first = items.Current;
        }

        return first.ValueKind == JsonValueKind.String
            && first.GetString() is string text
            && !string.IsNullOrWhiteSpace(text)
                ? text
                : null;
    }

    private static async Task<string> ComputeSha1Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using FileStream blob = OpenBlob(path);
        byte[] hash = await SHA1.HashDataAsync(
                blob,
                cancellationToken)
            .ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static byte[] CreateListing(
        string name,
        IReadOnlyList<ListedVersion> versions)
    {
        ListedVersion[] ordered = versions
            .OrderBy(
                version => version.Version,
                StringComparer.Ordinal)
            .ToArray();
        using MemoryStream listing = new();
        using (Utf8JsonWriter writer = new(listing))
        {
            writer.WriteStartObject();
            writer.WriteString("_id", name);
            writer.WriteString("name", name);
            writer.WriteStartObject("dist-tags");
            writer.WriteString("latest", SelectLatest(ordered));
            writer.WriteEndObject();
            writer.WriteStartObject("versions");
            foreach (ListedVersion version in ordered)
            {
                writer.WriteStartObject(version.Version);
                writer.WriteString("name", name);
                writer.WriteString("version", version.Version);
                if (version.Manifest.FhirVersion is string fhirVersion)
                    writer.WriteString("fhirVersion", fhirVersion);

                if (version.Manifest.Dependencies is JsonElement dependencies)
                {
                    writer.WritePropertyName("dependencies");
                    dependencies.WriteTo(writer);
                }

                writer.WriteStartObject("dist");
                writer.WriteString("tarball", version.TarballUri);
                writer.WriteString("shasum", version.Sha1);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return listing.ToArray();
    }

    private static string SelectLatest(
        IReadOnlyList<ListedVersion> versions)
    {
        ListedVersion latest = versions[^1];
        FhirSemVer? latestVersion = null;
        foreach (ListedVersion version in versions)
        {
            if (FhirSemVer.TryParse(
                    version.Version,
                    out FhirSemVer? parsed)
                && (latestVersion is null
                    || parsed.CompareTo(latestVersion) > 0))
            {
                latest = version;
                latestVersion = parsed;
            }
        }

        return latest.Version;
    }

    private sealed record ListedVersion(
        string Version,
        string TarballUri,
        string Sha1,
        PackageManifestFacts Manifest);

    private sealed record PackageManifestFacts(
        string? FhirVersion,
        JsonElement? Dependencies)
    {
        internal static PackageManifestFacts None { get; } =
            new(FhirVersion: null, Dependencies: null);
    }
}

// Every handler instance shares one route table and one request log;
// disposing a handler (as IHttpClientFactory does when it recycles one)
// leaves both intact.
internal sealed class FixtureRegistryHandler : HttpMessageHandler
{
    private readonly FixtureRoutes _routes;
    private readonly FixtureRequestLog _log;

    internal FixtureRegistryHandler(
        FixtureRoutes routes,
        FixtureRequestLog log)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(log);
        _routes = routes;
        _log = log;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);

        bool isGet = request.Method == HttpMethod.Get;
        if ((isGet || request.Method == HttpMethod.Head)
            && _routes.TryGetRoute(
                request.RequestUri,
                out FixtureRoute? route))
        {
            return Task.FromResult(CreateRoutedResponse(
                request,
                route,
                includeBody: isGet));
        }

        _log.Record(
            $"{request.Method.Method} {DescribeUri(request.RequestUri)}");
        return Task.FromResult(new HttpResponseMessage(
            HttpStatusCode.NotFound)
        {
            Content = new ByteArrayContent([]),
            RequestMessage = request
        });
    }

    private static HttpResponseMessage CreateRoutedResponse(
        HttpRequestMessage request,
        FixtureRoute route,
        bool includeBody)
    {
        HttpContent content;
        long contentLength;
        if (route.BlobPath is not string blobPath)
        {
            contentLength = route.Content.LongLength;
            content = new ByteArrayContent(
                includeBody ? route.Content : []);
        }
        else if (includeBody)
        {
            FileStream blob = FixtureRoutes.OpenBlob(blobPath);
            contentLength = blob.Length;
            content = new StreamContent(blob);
        }
        else
        {
            contentLength = new FileInfo(blobPath).Length;
            content = new ByteArrayContent([]);
        }

        content.Headers.ContentType =
            new MediaTypeHeaderValue(route.MediaType);
        content.Headers.ContentLength = contentLength;
        content.Headers.LastModified = route.LastModified;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
            RequestMessage = request
        };
    }

    private static string DescribeUri(Uri? uri) =>
        uri switch
        {
            null => "(no request URI)",
            { IsAbsoluteUri: true } => uri.AbsoluteUri,
            _ => uri.OriginalString
        };
}
