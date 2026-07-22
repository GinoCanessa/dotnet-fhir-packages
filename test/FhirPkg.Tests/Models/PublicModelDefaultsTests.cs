// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;
using Shouldly;
using Xunit;

namespace FhirPkg.Tests.Models;

public class PublicModelDefaultsTests
{
    [Fact]
    public void NewCollectionAndCompletenessFields_AreBackwardCompatible()
    {
        PackageClosure closure = new()
        {
            Timestamp = DateTime.UtcNow,
            Resolved =
                new Dictionary<string, PackageReference>(
                    StringComparer.OrdinalIgnoreCase),
            Missing =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
        };
        PackageInstallResult installResult = new()
        {
            Directive = "example.package#1.0.0",
            Status = PackageInstallStatus.Installed
        };
        DependencyResolutionFailure failure = new()
        {
            Code = DependencyResolutionFailureCode.PackageNotFound,
            PackageId = "example.package",
            Message = "Not found."
        };
        PackageLockFile lockFile = new()
        {
            Updated = DateTime.UtcNow,
            Dependencies =
                new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase)
        };

        closure.Failures.ShouldBeEmpty();
        closure.InstallOrder.ShouldBeEmpty();
        closure.ReplayOrder.ShouldBeEmpty();
        closure.BootstrapInstallOrder.ShouldBeEmpty();
        closure.InstallOrderIsComplete.ShouldBeFalse();
        closure.IsComplete.ShouldBeTrue();
        installResult.DependencyFailures.ShouldBeEmpty();
        installResult.Disposition.ShouldBeNull();
        installResult.PreviousManifestDate.ShouldBeNull();
        installResult.ManifestDate.ShouldBeNull();
        failure.RequestedVersions.ShouldBeEmpty();
        failure.RegistryFailures.ShouldBeEmpty();
        lockFile.SchemaVersion.ShouldBe(1);
        lockFile.Failures.ShouldBeEmpty();
    }
}
