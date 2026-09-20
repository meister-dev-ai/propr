// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Declaration;
using MeisterDev.Ai.Providers.Resilience;
using MeisterDev.Ai.Providers.Usage;
using Microsoft.Extensions.AI;

namespace MeisterDev.Ai.Providers.Drivers;

/// <summary>
///     Provider-specific driver for discovery, verification, and runtime creation.
/// </summary>
public interface IAiProviderDriver
{
    /// <summary>
    ///     Everything a host needs to know about this family before it reaches the driver: its identity, what it
    ///     needs configured, how it authenticates, what it speaks, where it goes and what it needs licensed.
    /// </summary>
    /// <remarks>
    ///     A pure function of the family, holding nothing about any one connection, so a host can read it before
    ///     anything has been configured. The vocabulary members below are projections of it rather than separate
    ///     statements, and that keeps a driver from declaring one thing and answering with another.
    ///     <see cref="ProviderDeclaration.Key" /> is the driver's identity: it is what a connection is stored
    ///     against and what every host lookup takes, so a driver states its family once and in one place.
    /// </remarks>
    ProviderDeclaration Declaration { get; }

    /// <summary>
    ///     The protocol shapes this driver can speak, as the qualified names they persist under. Declared rather
    ///     than assumed, because a stored binding can name a shape this driver does not implement: a driver that
    ///     stayed silent about this would fall through to whatever shape it does speak and put a request on the
    ///     wire in the wrong format.
    /// </summary>
    IReadOnlyList<string> SupportedProtocolModes => this.Declaration.ProtocolModes.Supported;

    /// <summary>
    ///     The authentication modes this driver can authenticate with, as the qualified names they persist under.
    ///     Declared for the same reason as the protocol shapes: a stored profile can name a shape this family
    ///     cannot read, so a family that declared nothing would be offered credentials it has nowhere to put. The
    ///     registry refuses a driver whose set is empty, because a family with no authentication mode cannot be
    ///     configured.
    /// </summary>
    IReadOnlyList<string> SupportedAuthModes => this.Declaration.SupportedAuthModes;

    /// <summary>
    ///     The credential fields each declared authentication mode needs, keyed by mode. A mode that needs no
    ///     stored credential, such as an ambient identity, maps to an empty list.
    /// </summary>
    /// <remarks>
    ///     Declared per mode rather than per family, because one family can take more than one authentication mode:
    ///     Google's Gemini surface reads a key string while Vertex reads a service-account document. The
    ///     declaration is a pure function of family and mode and knows nothing about the profile being
    ///     configured, so a host can ask for it before anything has been filled in. The host then collects those
    ///     fields, checks the required ones are present and stores them by name, and the driver reads them back
    ///     by the same names; what each one is for stays with the driver. The registry refuses a driver that
    ///     declares a mode without declaring its fields, because nothing would then be collected for it.
    /// </remarks>
    IReadOnlyDictionary<string, IReadOnlyList<ProviderCredentialField>> CredentialFields =>
        this.Declaration.CredentialFields;

    /// <summary>
    ///     Validates a probe/verify target against this provider's base-URL, SSRF-egress, and auth-shape rules.
    ///     Returns a user-facing error message when the target is rejected, or <c>null</c> when it is acceptable.
    /// </summary>
    string? ValidateProbeTarget(AiProbeTarget target);

    /// <summary>
    ///     Validates the values an operator entered into this family's declared fields, returning a message per
    ///     field it refuses, keyed by declared field name. An empty result accepts them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the family's own rule, and it runs above the host's. A family may refuse more than the host
    ///         does — an endpoint that is not its vendor's, a port outside a range it can bind, a combination of
    ///         values that cannot work together — and its message reaches the operator against the field it names.
    ///     </para>
    ///     <para>
    ///         It cannot admit what the host refuses. Every value whose declared shape is an address is checked
    ///         against the installation's egress rules before this runs and again before it is stored, so a family
    ///         that accepted one of those cannot turn an operator-entered value into egress the installation never
    ///         permitted.
    ///     </para>
    ///     <para>
    ///         The default accepts everything, which is right for a family whose fields need nothing beyond the
    ///         shapes it declared them with.
    ///     </para>
    /// </remarks>
    /// <param name="values">The values as the operator entered them, by declared field name.</param>
    IReadOnlyDictionary<string, string> ValidateDeclaredValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return new Dictionary<string, string>();
    }

    /// <summary>
    ///     Computes the values of this family's read-only declared fields from the connection's other declared
    ///     values, keyed by declared field name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Called wherever a computed value is shown, and the result is never stored. The value that needs
    ///         this is composed from another field — a redirect address over a port an operator sets — so a stored
    ///         copy stops matching the moment that field changes, and it would occupy a persisted field no
    ///         operator can correct. Validating such a value when it is stored is meaningless for the same reason,
    ///         so the host checks it when it renders it.
    ///     </para>
    ///     <para>
    ///         Only the non-secret values are passed in, so a computed value cannot be composed from credential
    ///         material and then shown on a form. The host caps and scrubs what comes back regardless.
    ///     </para>
    ///     <para>
    ///         The default computes nothing, which is right for a family that declares no computed field.
    ///     </para>
    /// </remarks>
    /// <param name="values">The connection's non-secret declared values, by declared field name.</param>
    IReadOnlyDictionary<string, string> ComputeDeclaredValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        return new Dictionary<string, string>();
    }

    /// <summary>Discovers provider models using the supplied connection settings.</summary>
    Task<ProviderModelDiscoveryResult> DiscoverModelsAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default);

    /// <summary>Verifies the provider connection using the supplied settings.</summary>
    Task<ProviderVerificationResult> VerifyAsync(
        ProviderEndpoint endpoint,
        CancellationToken ct = default);

    /// <summary>Creates a chat client for one resolved model binding.</summary>
    IChatClient CreateChatClient(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode);

    /// <summary>
    ///     Maps the usage one completed call reported onto the host's normalized counters.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A family's vendor vocabulary is read here and nowhere above it. The host prices the counters
    ///         <see cref="ProviderTokenUsage" /> names and holds no table of vendor field names, so a family whose
    ///         vendor spells a bucket its own way reads that spelling here and returns the counters under the
    ///         host's names.
    ///     </para>
    ///     <para>
    ///         The relationship between the counters is part of what is returned:
    ///         <see cref="ProviderTokenUsage.InputTokens" /> is inclusive of both cache buckets and
    ///         <see cref="ProviderTokenUsage.OutputTokens" /> is inclusive of the reasoning portion. A vendor that
    ///         reports input exclusive of its cache buckets is normalized here, because the host bills the input
    ///         total less the buckets and would otherwise bill the prompt at nothing. The conformance kit replays
    ///         the family's recorded usage payload through this member and refuses a mapping that returns
    ///         exclusive counts.
    ///     </para>
    ///     <para>
    ///         The default reads the counters a payload already states in normalized terms, which is right for a
    ///         family whose client library reports them that way.
    ///     </para>
    /// </remarks>
    /// <param name="usage">The usage the call reported, or <see langword="null" /> when it reported none.</param>
    ProviderTokenUsage ReadUsage(UsageDetails? usage)
    {
        return ProviderTokenUsage.FromUsageDetails(usage);
    }

    /// <summary>Gets session-related chat runtime capabilities for one resolved model binding.</summary>
    ProviderRuntimeCapabilities GetChatRuntimeCapabilities(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode);

    /// <summary>Creates an embedding generator for one resolved model binding.</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(
        ProviderEndpoint endpoint,
        ProviderModelDescriptor model,
        string protocolMode,
        int dimensions);

    /// <summary>
    ///     Decides whether a failed call is worth repeating. This is the driver's own judgement because only the
    ///     driver knows how its transport signals throttling — a provider SDK that reports a rate limit as its own
    ///     exception type rather than an HTTP status can only be understood here.
    /// </summary>
    /// <remarks>
    ///     The default reads the HTTP status, or the absence of a response, and is right for anything speaking
    ///     HTTP with conventional status codes. Override it to recognise SDK-specific signals first, then defer to
    ///     <see cref="DriverFailureMapper.ClassifyRuntimeFailure" /> for the rest; a driver that classifies
    ///     everything itself will get the common cases subtly wrong.
    /// </remarks>
    /// <param name="exception">The exception the call threw.</param>
    ProviderFailureVerdict ClassifyRuntimeFailure(Exception exception)
    {
        return DriverFailureMapper.ClassifyRuntimeFailure(exception);
    }
}
