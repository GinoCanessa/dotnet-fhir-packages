// Copyright (c) Gino Canessa. Licensed under the MIT License.

using FhirPkg.Release.Validation;

namespace FhirPkg.Release.Infrastructure;

internal sealed class ReleaseCommandServices
{
    internal ReleaseCommandServices(
        IReleaseInputValidator inputValidator,
        IReleaseVersionAvailabilityValidator versionAvailabilityValidator,
        IReleaseCandidateValidator candidateValidator,
        IReleasePublicationStateValidator publicationStateValidator,
        IPublishedPackageValidator publishedPackageValidator,
        IGitHubOutputWriter gitHubOutputWriter,
        TextWriter standardOutput,
        TextWriter standardError)
    {
        InputValidator =
            inputValidator ??
            throw new ArgumentNullException(nameof(inputValidator));
        VersionAvailabilityValidator =
            versionAvailabilityValidator ??
            throw new ArgumentNullException(
                nameof(versionAvailabilityValidator));
        CandidateValidator =
            candidateValidator ??
            throw new ArgumentNullException(nameof(candidateValidator));
        PublicationStateValidator =
            publicationStateValidator ??
            throw new ArgumentNullException(
                nameof(publicationStateValidator));
        PublishedPackageValidator =
            publishedPackageValidator ??
            throw new ArgumentNullException(
                nameof(publishedPackageValidator));
        GitHubOutputWriter =
            gitHubOutputWriter ??
            throw new ArgumentNullException(nameof(gitHubOutputWriter));
        StandardOutput =
            standardOutput ??
            throw new ArgumentNullException(nameof(standardOutput));
        StandardError =
            standardError ??
            throw new ArgumentNullException(nameof(standardError));
    }

    internal IReleaseCandidateValidator CandidateValidator { get; }

    internal IGitHubOutputWriter GitHubOutputWriter { get; }

    internal IReleaseInputValidator InputValidator { get; }

    internal IPublishedPackageValidator PublishedPackageValidator { get; }

    internal IReleasePublicationStateValidator PublicationStateValidator
    {
        get;
    }

    internal TextWriter StandardError { get; }

    internal TextWriter StandardOutput { get; }

    internal IReleaseVersionAvailabilityValidator
        VersionAvailabilityValidator
    {
        get;
    }

    internal static ReleaseCommandServices CreateDefault()
    {
        HttpClient httpClient = new();
        ReleaseProcessRunner processRunner = new();
        ReleaseDelay delay = new();
        ReleasePackageValidator packageValidator = new();
        ReleaseSymbolPackageValidator symbolPackageValidator = new();
        ReleaseVersionAvailabilityValidator
            versionAvailabilityValidator = new(httpClient);
        ReleaseGitClient gitClient = new(processRunner);
        ReleaseInputValidator inputValidator = new(
            gitClient,
            versionAvailabilityValidator);
        ReleaseCandidateValidator candidateValidator = new(
            packageValidator,
            symbolPackageValidator);
        PublishedPackageValidator publishedPackageValidator = new(
            httpClient,
            processRunner,
            packageValidator,
            delay);
        ReleasePublicationStateValidator publicationStateValidator =
            new(
                httpClient,
                publishedPackageValidator,
                delay);
        GitHubOutputWriter gitHubOutputWriter = new();

        return new ReleaseCommandServices(
            inputValidator,
            versionAvailabilityValidator,
            candidateValidator,
            publicationStateValidator,
            publishedPackageValidator,
            gitHubOutputWriter,
            Console.Out,
            Console.Error);
    }
}
