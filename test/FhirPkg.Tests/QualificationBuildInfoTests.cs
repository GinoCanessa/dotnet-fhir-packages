// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Qualification;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests;

public sealed class QualificationBuildInfoTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        $"fhirpkg-qualification-build-{Guid.NewGuid():N}");

    public QualificationBuildInfoTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void GetDependencyContextFiles_UsesHostDelimiterAndFallback()
    {
        string configuredDeps = Path.Combine(
            _tempRoot,
            "configured.deps.json");
        string missingDeps = Path.Combine(
            _tempRoot,
            "missing.deps.json");
        string entryAssembly = Path.Combine(
            _tempRoot,
            "qualification.dll");
        string fallbackDeps = Path.ChangeExtension(
            entryAssembly,
            ".deps.json");
        File.WriteAllText(configuredDeps, "{}");
        File.WriteAllText(fallbackDeps, "{}");

        string configured =
            $" {missingDeps} ; {configuredDeps} ; ";

        string[] actual = QualificationBuildInfo
            .GetDependencyContextFiles(
                configured,
                entryAssembly)
            .ToArray();

        actual.ShouldBe([
            configuredDeps,
            fallbackDeps
        ]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }
}
