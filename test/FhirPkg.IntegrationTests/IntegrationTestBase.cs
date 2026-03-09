// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Text;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;

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
        using (var gzip = new GZipOutputStream(ms) { IsStreamOwner = false })
        using (var tar = new TarOutputStream(gzip, Encoding.UTF8) { IsStreamOwner = false })
        {
            // Create package/ directory entry
            var dirEntry = TarEntry.CreateTarEntry("package/");
            tar.PutNextEntry(dirEntry);
            tar.CloseEntry();

            // Create package/package.json
            var manifest = $$"""{"name":"{{packageName}}","version":"{{version}}"}""";
            var manifestBytes = Encoding.UTF8.GetBytes(manifest);
            var entry = TarEntry.CreateTarEntry("package/package.json");
            entry.Size = manifestBytes.Length;
            tar.PutNextEntry(entry);
            tar.Write(manifestBytes, 0, manifestBytes.Length);
            tar.CloseEntry();

            if (extraFiles is not null)
            {
                foreach (var (name, content) in extraFiles)
                {
                    var bytes = Encoding.UTF8.GetBytes(content);
                    var fileEntry = TarEntry.CreateTarEntry($"package/{name}");
                    fileEntry.Size = bytes.Length;
                    tar.PutNextEntry(fileEntry);
                    tar.Write(bytes, 0, bytes.Length);
                    tar.CloseEntry();
                }
            }
        }

        ms.Position = 0;
        return ms;
    }
}
