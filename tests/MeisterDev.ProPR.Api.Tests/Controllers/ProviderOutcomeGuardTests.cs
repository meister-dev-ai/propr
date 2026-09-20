// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Net;
using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Drivers;
using MeisterDev.Ai.Providers.Enums;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

namespace MeisterDev.ProPR.Api.Tests.Controllers;

/// <summary>
///     What the host does with the text a provider family reports about an endpoint, before it answers the
///     console with it or writes it to the verification snapshot.
/// </summary>
public sealed class ProviderOutcomeGuardTests
{
    private const string PresentedKey = "sk-live-must-never-be-returned";

    // Several OpenAI-compatible providers quote part of the presented key back in a refusal, so the body a
    // family puts in its summary is one of the places the credential comes home again.
    [Fact]
    public void AnEndpointQuotingThePresentedKeyBackDoesNotPutItInTheSummary()
    {
        var adopted = ProviderOutcomeGuard.Adopt(
            DriverFailureMapper.Failed(
                HttpStatusCode.Unauthorized,
                $"Incorrect API key provided: {PresentedKey}. You can find your API key at ..."),
            Presented());

        Assert.NotNull(adopted.Summary);
        Assert.DoesNotContain(PresentedKey, adopted.Summary, StringComparison.Ordinal);
        Assert.Contains("Incorrect API key provided", adopted.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ASummaryLongerThanTheHostKeepsIsCutShortAndSaysSo()
    {
        var adopted = ProviderOutcomeGuard.Adopt(
            DriverFailureMapper.Failed(HttpStatusCode.BadGateway, new string('x', 200_000)),
            Presented());

        Assert.NotNull(adopted.Summary);
        Assert.Equal(ProviderHostLimits.MaximumMessageLength, adopted.Summary.Length);
        Assert.EndsWith("…", adopted.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AShortNonSensitiveFailureReachesTheSummaryUnchanged()
    {
        var adopted = ProviderOutcomeGuard.Adopt(
            DriverFailureMapper.Failed(HttpStatusCode.NotFound, "No such deployment."),
            Presented());

        Assert.Equal("No such deployment.", adopted.Summary);
    }

    [Fact]
    public void AVerifiedOutcomeIsAdoptedAsItStands()
    {
        var adopted = ProviderOutcomeGuard.Adopt(
            DriverFailureMapper.Verified("Verified OpenAI connectivity for 'https://api.example.com/v1'.", []),
            Presented());

        Assert.Equal(AiVerificationStatus.Verified, adopted.Status);
        Assert.Equal("Verified OpenAI connectivity for 'https://api.example.com/v1'.", adopted.Summary);
    }

    // The warnings and the driver metadata are family-authored too, and both are written to a jsonb column with
    // no length of its own.
    [Fact]
    public void AWarningCarryingThePresentedKeyIsScrubbedAndTheListIsBounded()
    {
        var adopted = ProviderOutcomeGuard.Adopt(
            new ProviderVerificationResult(
                AiVerificationStatus.Verified,
                Summary: "Verified.",
                Warnings: [.. Enumerable.Range(0, 500).Select(_ => $"The key {PresentedKey} expires soon.")]),
            Presented());

        Assert.NotNull(adopted.Warnings);
        Assert.True(adopted.Warnings.Count < 500);
        Assert.All(
            adopted.Warnings,
            warning => Assert.DoesNotContain(PresentedKey, warning, StringComparison.Ordinal));
    }

    [Fact]
    public void DriverMetadataCarryingThePresentedKeyIsScrubbed()
    {
        var adopted = ProviderOutcomeGuard.Adopt(
            new ProviderVerificationResult(
                AiVerificationStatus.Failed,
                AiVerificationFailureCategory.Credentials,
                Summary: "Refused.",
                DriverMetadata: new Dictionary<string, string> { ["presented"] = PresentedKey }),
            Presented());

        Assert.NotNull(adopted.DriverMetadata);
        Assert.DoesNotContain(PresentedKey, adopted.DriverMetadata["presented"], StringComparison.Ordinal);
    }

    // The host does not hold another connection's credential in this context, so there is nothing to search for
    // and the message is left as the family wrote it.
    [Fact]
    public void AMessageCarryingACredentialTheHostDoesNotHoldIsLeftAlone()
    {
        var adopted = ProviderOutcomeGuard.Adopt(
            DriverFailureMapper.Failed(HttpStatusCode.Unauthorized, "Incorrect API key: sk-live-someone-elses."),
            Presented());

        Assert.Equal("Incorrect API key: sk-live-someone-elses.", adopted.Summary);
    }

    [Fact]
    public void ADiscoveryWarningCarryingThePresentedKeyIsScrubbed()
    {
        var adopted = ProviderOutcomeGuard.Adopt(
            new ProviderModelDiscoveryResult("failed", true, [$"Rejected the key {PresentedKey}."], []),
            DateTimeOffset.UnixEpoch,
            Presented());

        Assert.DoesNotContain(PresentedKey, Assert.Single(adopted.Warnings), StringComparison.Ordinal);
    }

    // The credential is read back through the envelope, which is how a single-field mode and a multi-field one
    // both arrive at the values the host presented.
    [Fact]
    public void TheCredentialOfASavedConnectionIsReadBackThroughItsEnvelope()
    {
        var connection = new AiConnectionDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Example",
            "meisterdev/openAi",
            "https://api.example.com/v1",
            "meisterdev/openAi:ApiKey",
            AiDiscoveryMode.ManualOnly,
            true,
            [],
            [],
            AiVerificationResultDto.NeverVerified,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            null,
            PresentedKey)
        {
            DeclaredSecrets = new Dictionary<string, string> { ["clientSecret"] = "cs-also-held" },
        };

        var secrets = ProviderOutcomeGuard.SecretsOf(connection);

        Assert.Contains(PresentedKey, secrets);
        Assert.Contains("cs-also-held", secrets);
    }

    [Fact]
    public void AProfileWithNoCredentialYieldsNothingToScrubFor()
    {
        var options = new AiConnectionProbeOptionsDto(
            "meisterdev/openAi",
            "https://api.example.com/v1",
            "meisterdev/openAi:AzureIdentity");

        Assert.Empty(ProviderOutcomeGuard.SecretsOf(options));
    }

    // A family declares secret-marked settings of its own beside the authentication envelope, and an operator
    // probing an unsaved profile has just typed them. A family quoting one back in its answer would otherwise
    // have it pass through unscrubbed, into the response and into any log line rendering it.
    [Fact]
    public void AnUnsavedProfilesDeclaredSecretsAreScrubbedForToo()
    {
        var options = new AiConnectionProbeOptionsDto(
            "meisterdev/openAi",
            "https://api.example.com/v1",
            "meisterdev/openAi:ApiKey")
        {
            Secret = PresentedKey,
        };

        var secrets = ProviderOutcomeGuard.SecretsOf(
            options,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["clientSecret"] = "cs-just-typed" });

        Assert.Contains(PresentedKey, secrets);
        Assert.Contains("cs-just-typed", secrets);
    }

    // A model identifier is matched against the provider's own when the operator saves it, so a host that
    // shortened one would offer a name no provider answers to. A model whose identifier carries the presented
    // credential is not offered at all, and the operator is told how many were dropped.
    [Fact]
    public void ADiscoveredModelWhoseIdentifierCarriesThePresentedKeyIsNotOfferedAndTheOthersAreUnchanged()
    {
        var adopted = ProviderOutcomeGuard.Adopt(
            new ProviderModelDiscoveryResult(
                "succeeded",
                true,
                [],
                [
                    new ProviderDiscoveredModel("gpt-example", "GPT Example", [AiOperationKind.Chat], [ProviderDeclaredProtocolModes.Auto]),
                    new ProviderDiscoveredModel($"echo-{PresentedKey}", "Echoed", [AiOperationKind.Chat], [ProviderDeclaredProtocolModes.Auto]),
                ]),
            DateTimeOffset.UnixEpoch,
            Presented());

        var offered = Assert.Single(adopted.Models);
        Assert.Equal("gpt-example", offered.RemoteModelId);
        Assert.Contains(
            adopted.Warnings,
            warning => warning.Contains("not offered", StringComparison.Ordinal));
    }

    // The model list is family-authored and reaches a response and, on a save, a row per entry. A family
    // answering with a generated list is bounded, and the operator is told the list was cut so a model the
    // provider does list can still be entered by hand.
    [Fact]
    public void AModelListLongerThanTheHostOffersIsCutAndTheOperatorIsTold()
    {
        var models = Enumerable
            .Range(0, 1500)
            .Select(index => new ProviderDiscoveredModel(
                $"model-{index}",
                $"Model {index}",
                [AiOperationKind.Chat],
                [ProviderDeclaredProtocolModes.Auto]))
            .ToList();

        var adopted = ProviderOutcomeGuard.Adopt(
            new ProviderModelDiscoveryResult("succeeded", true, [], models),
            DateTimeOffset.UnixEpoch,
            Presented());

        Assert.Equal(1000, adopted.Models.Count);
        Assert.Equal("model-0", adopted.Models[0].RemoteModelId);
        Assert.Contains(adopted.Warnings, warning => warning.Contains("1500 models", StringComparison.Ordinal));
    }

    // A value shorter than a key looks like is still credential material: a declared secret field states no
    // minimum length, and a family returning a short one unscrubbed puts it on an operator's page.
    [Fact]
    public void AShortCredentialQuotedBackIsStillScrubbed()
    {
        var adopted = ProviderOutcomeGuard.Adopt(
            DriverFailureMapper.Failed(HttpStatusCode.Unauthorized, "Rejected the code abcd."),
            ["abcd"]);

        Assert.NotNull(adopted.Summary);
        Assert.DoesNotContain("abcd", adopted.Summary, StringComparison.Ordinal);
    }

    private static IReadOnlyCollection<string> Presented()
    {
        return [PresentedKey];
    }
}
