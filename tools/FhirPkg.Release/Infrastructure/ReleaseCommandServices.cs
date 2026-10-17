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
        IFixtureBundleLocator fixtureBundleLocator,
        IReleaseGitHubClient gitHubClient,
        IGitHubOutputWriter gitHubOutputWriter,
        IGitHubStepSummaryWriter stepSummaryWriter,
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
        FixtureBundleLocator =
            fixtureBundleLocator ??
            throw new ArgumentNullException(nameof(fixtureBundleLocator));
        GitHubClient =
            gitHubClient ??
            throw new ArgumentNullException(nameof(gitHubClient));
        GitHubOutputWriter =
            gitHubOutputWriter ??
            throw new ArgumentNullException(nameof(gitHubOutputWriter));
        StepSummaryWriter =
            stepSummaryWriter ??
            throw new ArgumentNullException(nameof(stepSummaryWriter));
        StandardOutput =
            standardOutput ??
            throw new ArgumentNullException(nameof(standardOutput));
        StandardError =
            standardError ??
            throw new ArgumentNullException(nameof(standardError));
    }

    internal IReleaseCandidateValidator CandidateValidator { get; }

    internal IFixtureBundleLocator FixtureBundleLocator { get; }

    internal IReleaseGitHubClient GitHubClient { get; }

    internal IGitHubOutputWriter GitHubOutputWriter { get; }

    internal IReleaseInputValidator InputValidator { get; }

    internal IPublishedPackageValidator PublishedPackageValidator { get; }

    internal IReleasePublicationStateValidator PublicationStateValidator
    {
        get;
    }

    internal TextWriter StandardError { get; }

    internal TextWriter StandardOutput { get; }

    internal IGitHubStepSummaryWriter StepSummaryWriter { get; }

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
        ReleaseGitHubClient gitHubClient =
            ReleaseGitHubClient.CreateFromEnvironment();
        FixtureBundleLocator fixtureBundleLocator = new(
            gitClient,
            gitClient,
            gitHubClient);
        GitHubOutputWriter gitHubOutputWriter = new();
        GitHubStepSummaryWriter stepSummaryWriter = new();

        return new ReleaseCommandServices(
            inputValidator,
            versionAvailabilityValidator,
            candidateValidator,
            publicationStateValidator,
            publishedPackageValidator,
            fixtureBundleLocator,
            gitHubClient,
            gitHubOutputWriter,
            stepSummaryWriter,
            Console.Out,
            Console.Error);
    }
}
