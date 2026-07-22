// Copyright (c) Gino Canessa. Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text.Json;
using FhirPkg.Installation;
using FhirPkg.Qualification.Models;

namespace FhirPkg.Qualification;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        string? outputPath = TryGetOption(args, "output");
        string corpusSha256 = new('0', 64);
        bool validationOnly = false;
        QualificationBuildSnapshot build =
            QualificationBuildInfo.InspectUnchecked();
        QualificationReport report = CreateFailureReport(
            startedUtc,
            corpusSha256,
            validationOnly,
            build);
        ReportSanitizer sanitizer = new(
        [
            Environment.CurrentDirectory,
            AppContext.BaseDirectory
        ]);
        using CancellationTokenSource cancellationSource = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationSource.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        bool reportWritten = false;
        try
        {
            QualificationArguments arguments =
                QualificationArguments.Parse(args);
            outputPath = arguments.OutputPath;
            validationOnly = arguments.ValidateOnly;
            sanitizer = new ReportSanitizer(
            [
                arguments.CacheRoot,
                arguments.OutputPath,
                arguments.CorpusPath,
                arguments.ProcessHostPath,
                Environment.CurrentDirectory,
                AppContext.BaseDirectory
            ]);
            corpusSha256 = await QualificationCorpusHash
                .ComputeFileAsync(
                    arguments.CorpusPath,
                    cancellationSource.Token)
                .ConfigureAwait(false);
            await QualificationSchemaValidator.ValidateFileAsync(
                    arguments.CorpusPath,
                    QualificationSchemaPaths.Corpus,
                    cancellationSource.Token)
                .ConfigureAwait(false);
            QualificationCorpus corpus =
                await QualificationCorpus.LoadAsync(
                        arguments.CorpusPath,
                        cancellationSource.Token)
                    .ConfigureAwait(false);
            build = QualificationBuildInfo.Inspect();
            QualificationBuildInfo.Validate(build);
            QualificationRunner runner = new(
                arguments,
                corpus,
                build,
                sanitizer,
                startedUtc,
                corpusSha256);
            report = arguments.ValidateOnly
                ? await runner.ValidateOnlyAsync(
                        cancellationSource.Token)
                    .ConfigureAwait(false)
                : await runner.RunAsync(
                        cancellationSource.Token)
                    .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            report = CreateFailureReport(
                startedUtc,
                corpusSha256,
                validationOnly,
                build,
                CreateFailure(exception, sanitizer));
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
            report.CompletedUtc = DateTimeOffset.UtcNow;
            try
            {
                string json = JsonSerializer.Serialize(
                    report,
                    QualificationJson.SerializerOptions);
                QualificationSchemaValidator.ValidateJson(
                    json,
                    QualificationSchemaPaths.Report);
                if (string.IsNullOrWhiteSpace(outputPath))
                {
                    await Console.Out.WriteLineAsync(json)
                        .ConfigureAwait(false);
                }
                else
                {
                    await WriteReportAtomicallyAsync(
                            outputPath,
                            json)
                        .ConfigureAwait(false);
                }

                reportWritten = true;
            }
            catch (Exception writeException)
            {
                string fallback = JsonSerializer.Serialize(
                    CreateFailureReport(
                        startedUtc,
                        corpusSha256,
                        validationOnly,
                        build,
                        CreateFailure(
                            writeException,
                            sanitizer)),
                    QualificationJson.SerializerOptions);
                await Console.Out.WriteLineAsync(fallback)
                    .ConfigureAwait(false);
            }
        }

        return reportWritten && report.Success ? 0 : 1;
    }

    private static QualificationReport CreateFailureReport(
        DateTimeOffset startedUtc,
        string corpusSha256,
        bool validationOnly,
        QualificationBuildSnapshot build,
        QualificationFailure? failure = null) =>
        new()
        {
            Mode = build.Mode,
            ValidationOnly = validationOnly,
            RequestedPackageVersion =
                build.RequestedPackageVersion,
            PackageVersion = build.PackageVersion,
            FhirPkgAssemblyVersion =
                build.FhirPkgAssemblyVersion,
            FhirPkgInformationalVersion =
                build.FhirPkgInformationalVersion,
            CorpusSha256 = corpusSha256,
            CorpusHashAlgorithm =
                QualificationCorpusHash.Algorithm,
            Framework = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            StartedUtc = startedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            Success = false,
            Artifacts = [],
            Cases =
            [
                new QualificationCaseResult
                {
                    Id = "bootstrap",
                    Success = false,
                    DurationMilliseconds = 0,
                    Details = [],
                    Failures = failure is null ? [] : [failure]
                }
            ],
            Summary = new QualificationSummary
            {
                ArtifactCount = 0,
                ArtifactFailures = 0,
                CaseCount = 1,
                CaseFailures = 1
            }
        };

    private static QualificationFailure CreateFailure(
        Exception exception,
        ReportSanitizer sanitizer)
    {
        if (exception is PackageInstallException installException)
        {
            return new QualificationFailure
            {
                Code = installException.ErrorCode.ToString(),
                Stage = installException.Stage.ToString(),
                ExceptionType = exception.GetType().Name,
                Message = sanitizer.Sanitize(exception.Message)
            };
        }

        return new QualificationFailure
        {
            ExceptionType = exception.GetType().Name,
            Message = sanitizer.Sanitize(exception.Message)
        };
    }

    private static async Task WriteReportAtomicallyAsync(
        string outputPath,
        string json)
    {
        string fullPath = Path.GetFullPath(outputPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        string temporaryPath =
            $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    json,
                    CancellationToken.None)
                .ConfigureAwait(false);
            File.Move(
                temporaryPath,
                fullPath,
                overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string? TryGetOption(
        IReadOnlyList<string> args,
        string name)
    {
        string option = $"--{name}";
        for (int index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(
                args[index],
                option,
                StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
