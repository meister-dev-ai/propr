// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.ProPR.Application.AI;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

namespace MeisterDev.ProPR.Api.Controllers;

/// <summary>
///     Adopts what a provider family reported about an endpoint, capping and scrubbing every string in it first.
/// </summary>
/// <remarks>
///     <para>
///         A verification outcome is family-authored text that reaches three places: the response the console
///         renders, the <c>ai_verification_snapshots</c> row the host keeps, and any log line that renders either.
///         A family composes the summary from what the endpoint answered, and several OpenAI-compatible providers
///         quote part of the presented key back in a refusal, so the credential the host just sent can arrive in
///         the outcome.
///     </para>
///     <para>
///         The treatment is applied where the host takes the outcome as its own, once, instead of at each of
///         those three places. That point is also where the credential is still in hand to scrub against.
///     </para>
/// </remarks>
internal static class ProviderOutcomeGuard
{
    /// <summary>The most entries the host keeps from a family-authored list.</summary>
    /// <remarks>
    ///     The lists these bound are warnings and driver metadata, which hold single figures for every family
    ///     that ships. The bound is there so a family returning them in a loop fills neither the response nor the
    ///     jsonb column that holds them.
    /// </remarks>
    private const int MaximumEntries = 32;

    /// <summary>The most models the host adopts from one discovery.</summary>
    /// <remarks>
    ///     Far above what any provider lists: the vendor catalogues run to tens, and a gateway fronting many
    ///     vendors to a few hundred. It bounds the response and the rows a save would write when a family answers
    ///     with a generated list, and it is stated apart from the warning bound because the two are different
    ///     sizes of thing.
    /// </remarks>
    private const int MaximumModels = 1000;

    /// <summary>What a family reported about verifying an endpoint, as the product's own snapshot.</summary>
    /// <param name="result">The outcome the family returned.</param>
    /// <param name="secrets">The credential values the host presented, which every string is scrubbed of.</param>
    public static AiVerificationResultDto Adopt(
        ProviderVerificationResult result,
        IReadOnlyCollection<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(secrets);

        var adopted = result.ToDto();

        return adopted with
        {
            Summary = Message(adopted.Summary, secrets),
            ActionHint = Message(adopted.ActionHint, secrets),
            Warnings = Texts(adopted.Warnings, secrets),
            DriverMetadata = Map(adopted.DriverMetadata, secrets),
        };
    }

    /// <summary>What a family discovered at an endpoint, as the product's own result.</summary>
    /// <param name="result">The outcome the family returned.</param>
    /// <param name="discoveredAt">When the discovery ran.</param>
    /// <param name="secrets">The credential values the host presented, which every string is scrubbed of.</param>
    /// <remarks>
    ///     A discovered model's identifier is never rewritten. It is matched against the provider's own when the
    ///     operator saves it, so a host that shortened one would offer a name no provider answers to. A model
    ///     whose identifier or name would change under the scrub is dropped instead, with a warning saying so:
    ///     such a value carries the credential the host just presented, which makes it something other than a
    ///     model identifier, and offering it would put the credential on the operator's page.
    /// </remarks>
    public static AiModelDiscoveryResultDto Adopt(
        ProviderModelDiscoveryResult result,
        DateTimeOffset discoveredAt,
        IReadOnlyCollection<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(secrets);

        var adopted = result.ToDto(discoveredAt);
        var offered = adopted.Models.Take(MaximumModels).ToList();
        var beyondTheBound = adopted.Models.Count - offered.Count;
        var kept = offered.Where(model => CarriesNoCredential(model, secrets)).ToList();
        var dropped = offered.Count - kept.Count;

        // The cap applies to what the family wrote. The host's own notice is appended after it, so a family
        // that fills the allowance cannot push the one line explaining why its models are missing off the end.
        List<string> warnings = [.. Texts(adopted.Warnings, secrets) ?? []];
        if (dropped > 0)
        {
            warnings.Add(
                $"{dropped} discovered model(s) were not offered because their identifier or name carried the "
                + "credential this connection presented.");
        }

        // Said rather than left to be noticed: an operator looking for a model that the provider does list has
        // to know the list was cut, and where.
        if (beyondTheBound > 0)
        {
            warnings.Add(
                $"The provider listed {adopted.Models.Count} models. The first {MaximumModels} are offered and "
                + $"the remaining {beyondTheBound} are not; enter one of those by hand if you need it.");
        }

        return adopted with
        {
            DiscoveryStatus = ProviderMessageGuard.Sanitize(
                adopted.DiscoveryStatus,
                secrets,
                ProviderHostLimits.MaximumFieldTextLength),
            Warnings = warnings,
            Models = kept,
        };
    }

    /// <summary>Whether a discovered model can be offered as the family named it.</summary>
    /// <param name="model">The model as the family reported it.</param>
    /// <param name="secrets">The credential values the host presented.</param>
    private static bool CarriesNoCredential(AiConfiguredModelDto model, IReadOnlyCollection<string> secrets)
    {
        // Every string the family controls on this model, not the two an operator happens to read first. A
        // credential echoed into the tokenizer name or a protocol mode reached the page the same way.
        var provided = new List<string?> { model.RemoteModelId, model.DisplayName, model.TokenizerName };
        provided.AddRange(model.SupportedProtocolModes ?? []);

        return !secrets.Any(secret =>
            !string.IsNullOrWhiteSpace(secret)
            && provided.Any(value => value is not null && value.Contains(secret, StringComparison.Ordinal)));
    }

    /// <summary>The credential values a saved connection holds, as the host reads them back.</summary>
    /// <param name="connection">The connection being verified.</param>
    public static IReadOnlyCollection<string> SecretsOf(AiConnectionDto connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return
        [
            .. ProviderSecretEnvelope.Decode(connection.Secret, connection.AuthMode).Fields.Values,
            .. connection.DeclaredSecrets.Values,
        ];
    }

    /// <summary>The credential values an unsaved profile carries, as the operator entered them.</summary>
    /// <remarks>
    ///     Both halves, for the reason the saved overload takes both. A family declares secret-marked settings of
    ///     its own beside the authentication envelope, and an operator probing an unsaved profile has just typed
    ///     them. A family that quotes one back in its answer would otherwise have it pass through unscrubbed,
    ///     into the response and into any log line rendering it.
    /// </remarks>
    /// <param name="options">The profile being probed.</param>
    /// <param name="declaredSecrets">The secret-marked declared values this submission carried.</param>
    public static IReadOnlyCollection<string> SecretsOf(
        AiConnectionProbeOptionsDto options,
        IReadOnlyDictionary<string, string>? declaredSecrets = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        return
        [
            .. ProviderSecretEnvelope.Decode(options.Secret, options.AuthMode).Fields.Values,
            .. declaredSecrets?.Values ?? [],
        ];
    }

    private static string? Message(string? value, IReadOnlyCollection<string> secrets)
    {
        return value is null ? null : ProviderMessageGuard.Sanitize(value, secrets);
    }

    private static IReadOnlyList<string>? Texts(IReadOnlyList<string>? values, IReadOnlyCollection<string> secrets)
    {
        return values is null
            ? null
            : [.. values.Take(MaximumEntries).Select(value => ProviderMessageGuard.Sanitize(value, secrets))];
    }

    private static IReadOnlyDictionary<string, string>? Map(
        IReadOnlyDictionary<string, string>? values,
        IReadOnlyCollection<string> secrets)
    {
        if (values is null)
        {
            return null;
        }

        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in values.Take(MaximumEntries))
        {
            kept[ProviderMessageGuard.Sanitize(name, secrets, ProviderHostLimits.MaximumFieldTextLength)] =
                ProviderMessageGuard.Sanitize(value, secrets, ProviderHostLimits.MaximumFieldTextLength);
        }

        return kept;
    }
}
