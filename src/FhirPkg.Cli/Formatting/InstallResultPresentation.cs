// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Models;

namespace FhirPkg.Cli.Formatting;

internal sealed record InstallResultSummary(
    int Total,
    int CoarseInstalled,
    int AlreadyCached,
    int Failed,
    int DispositionInstalled,
    int Updated,
    int AlreadyCurrent,
    int Refreshed);

internal static class InstallResultPresentation
{
    internal static PackageInstallDisposition? GetEffectiveDisposition(
        PackageInstallResult result)
    {
        if (result.Status != PackageInstallStatus.Installed)
            return null;

        return result.Disposition switch
        {
            PackageInstallDisposition.Updated =>
                PackageInstallDisposition.Updated,
            PackageInstallDisposition.AlreadyCurrent =>
                PackageInstallDisposition.AlreadyCurrent,
            PackageInstallDisposition.Refreshed =>
                PackageInstallDisposition.Refreshed,
            _ => PackageInstallDisposition.Installed
        };
    }

    internal static InstallResultSummary Summarize(
        IReadOnlyList<PackageInstallResult> results)
    {
        int coarseInstalled = 0;
        int alreadyCached = 0;
        int dispositionInstalled = 0;
        int updated = 0;
        int alreadyCurrent = 0;
        int refreshed = 0;

        foreach (PackageInstallResult result in results)
        {
            if (result.Status == PackageInstallStatus.AlreadyCached)
            {
                alreadyCached++;
                continue;
            }

            PackageInstallDisposition? disposition =
                GetEffectiveDisposition(result);
            if (disposition is null)
                continue;

            coarseInstalled++;
            switch (disposition)
            {
                case PackageInstallDisposition.Updated:
                    updated++;
                    break;
                case PackageInstallDisposition.AlreadyCurrent:
                    alreadyCurrent++;
                    break;
                case PackageInstallDisposition.Refreshed:
                    refreshed++;
                    break;
                default:
                    dispositionInstalled++;
                    break;
            }
        }

        int failed = results.Count - coarseInstalled - alreadyCached;
        return new InstallResultSummary(
            results.Count,
            coarseInstalled,
            alreadyCached,
            failed,
            dispositionInstalled,
            updated,
            alreadyCurrent,
            refreshed);
    }
}
