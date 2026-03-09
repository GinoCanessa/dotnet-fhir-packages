// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace FhirPkg.IntegrationTests;

/// <summary>
/// Base class for integration tests that provides a temporary cache directory
/// and helper methods for creating test data.
/// </summary>
public abstract class IntegrationTestBase : IDisposable
{
    protected readonly string TempCacheDir;

    protected IntegrationTestBase()
    {
        TempCacheDir = Path.Combine(Path.GetTempPath(), $"fhir-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempCacheDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(TempCacheDir))
            Directory.Delete(TempCacheDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    protected FhirPackageManagerOptions CreateTestOptions() => new()
    {
        CachePath = TempCacheDir,
        IncludeCiBuilds = false
    };

    protected string CreateTestProject(string manifestJson)
    {
        var dir = Path.Combine(TempCacheDir, "test-project");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), manifestJson);
        return dir;
    }

    protected static Stream CreateTestTarball(
        string packageName,
        string version,
        Dictionary<string, string>? extraFiles = null)
    {
        var ms = new MemoryStream();
        using (var gzip = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true);

            // Create package/ directory entry
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "package/"));

            // Create package/package.json
            var manifest = $$"""{"name":"{{packageName}}","version":"{{version}}"}""";
            var manifestBytes = Encoding.UTF8.GetBytes(manifest);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "package/package.json")
            {
                DataStream = new MemoryStream(manifestBytes)
            };
            tar.WriteEntry(entry);

            if (extraFiles is not null)
            {
                foreach (var (name, content) in extraFiles)
                {
                    var bytes = Encoding.UTF8.GetBytes(content);
                    var fileEntry = new PaxTarEntry(TarEntryType.RegularFile, $"package/{name}")
                    {
                        DataStream = new MemoryStream(bytes)
                    };
                    tar.WriteEntry(fileEntry);
                }
            }
        }

        ms.Position = 0;
        return ms;
    }
}
