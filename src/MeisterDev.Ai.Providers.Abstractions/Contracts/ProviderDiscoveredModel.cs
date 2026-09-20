// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Enums;

namespace MeisterDev.Ai.Providers.Contracts;

/// <summary>
///     A model as the provider itself reports it during discovery. Carries only what the provider can be asked
///     about; where the entry came from and what it costs the host to use are the host's provenance to record.
/// </summary>
/// <param name="RemoteModelId">Model identifier as the provider knows it.</param>
/// <param name="DisplayName">Human-readable name, falling back to the remote identifier.</param>
/// <param name="OperationKinds">Operations the model can serve.</param>
/// <param name="SupportedProtocolModes">Protocol modes the model can serve.</param>
/// <param name="TokenizerName">Tokenizer the provider associates with the model, when known.</param>
/// <param name="MaxInputTokens">Maximum input tokens the provider reports, when known.</param>
/// <param name="MaxContextTokens">Maximum total context tokens the provider reports, when known.</param>
/// <param name="EmbeddingDimensions">Embedding dimensionality for an embedding model, when known.</param>
/// <param name="SupportsStructuredOutput">Whether the model accepts a response schema.</param>
/// <param name="SupportsToolUse">Whether the model supports tool or function calling.</param>
/// <exception cref="ArgumentException">
///     A dimensionality is stated for a model that serves no embedding operation.
/// </exception>
/// <exception cref="ArgumentOutOfRangeException">A stated bound or dimensionality is not positive.</exception>
public sealed record ProviderDiscoveredModel(
    string RemoteModelId,
    string DisplayName,
    IReadOnlyList<AiOperationKind> OperationKinds,
    IReadOnlyList<string> SupportedProtocolModes,
    string? TokenizerName = null,
    int? MaxInputTokens = null,
    int? MaxContextTokens = null,
    int? EmbeddingDimensions = null,
    bool SupportsStructuredOutput = false,
    bool SupportsToolUse = false)
{
    // Held in fields so a value is checked wherever it is set: a positional argument, an object initializer and
    // a `with` expression all reach the property, and a property with an accessor body cannot carry a field
    // initializer of its own. Every positional member is declared here, in the order of the parameters, because
    // a record that declares only some of them emits the rest first.
    private readonly string _remoteModelId = RemoteModelId;
    private readonly string _displayName = Named(DisplayName, RemoteModelId);
    private readonly IReadOnlyList<AiOperationKind> _operationKinds = OperationKinds;
    private readonly IReadOnlyList<string> _supportedProtocolModes = SupportedProtocolModes;
    private readonly string? _tokenizerName = TokenizerName;
    private readonly int? _maxInputTokens = Stated(MaxInputTokens, nameof(MaxInputTokens));
    private readonly int? _maxContextTokens = Stated(MaxContextTokens, nameof(MaxContextTokens));
    private readonly int? _embeddingDimensions = Embedding(EmbeddingDimensions, OperationKinds);
    private readonly bool _supportsStructuredOutput = SupportsStructuredOutput;
    private readonly bool _supportsToolUse = SupportsToolUse;

    /// <summary>Model identifier as the provider knows it.</summary>
    public string RemoteModelId
    {
        get => this._remoteModelId;
        init => this._remoteModelId = value;
    }

    /// <summary>Human-readable name, falling back to the remote identifier.</summary>
    /// <remarks>
    ///     The fallback is applied here rather than documented and left to each driver. Several providers list a
    ///     model with no name at all, and a console rendering a blank offers a row an operator cannot pick.
    /// </remarks>
    public string DisplayName
    {
        get => this._displayName;
        init => this._displayName = Named(value, this.RemoteModelId);
    }

    /// <summary>Operations the model can serve.</summary>
    public IReadOnlyList<AiOperationKind> OperationKinds
    {
        get => this._operationKinds;
        init => this._operationKinds = value;
    }

    /// <summary>Protocol modes the model can serve.</summary>
    public IReadOnlyList<string> SupportedProtocolModes
    {
        get => this._supportedProtocolModes;
        init => this._supportedProtocolModes = value;
    }

    /// <summary>Tokenizer the provider associates with the model, when known.</summary>
    public string? TokenizerName
    {
        get => this._tokenizerName;
        init => this._tokenizerName = value;
    }

    /// <summary>Maximum input tokens the provider reports, when known.</summary>
    public int? MaxInputTokens
    {
        get => this._maxInputTokens;
        init => this._maxInputTokens = Stated(value, nameof(ProviderDiscoveredModel.MaxInputTokens));
    }

    /// <summary>Maximum total context tokens the provider reports, when known.</summary>
    public int? MaxContextTokens
    {
        get => this._maxContextTokens;
        init => this._maxContextTokens = Stated(value, nameof(ProviderDiscoveredModel.MaxContextTokens));
    }

    /// <summary>Embedding dimensionality for an embedding model, when known.</summary>
    /// <remarks>
    ///     The operation kinds are read on the way in, so a width stated for a model that serves no embedding
    ///     operation is refused. A <c>with</c> expression that changes only the kinds is not re-checked, because
    ///     an accessor cannot read a sibling that may not be assigned yet.
    /// </remarks>
    public int? EmbeddingDimensions
    {
        get => this._embeddingDimensions;
        init => this._embeddingDimensions = Embedding(value, this.OperationKinds);
    }

    /// <summary>Whether the model accepts a response schema.</summary>
    public bool SupportsStructuredOutput
    {
        get => this._supportsStructuredOutput;
        init => this._supportsStructuredOutput = value;
    }

    /// <summary>Whether the model supports tool or function calling.</summary>
    public bool SupportsToolUse
    {
        get => this._supportsToolUse;
        init => this._supportsToolUse = value;
    }

    private static string Named(string displayName, string remoteModelId)
    {
        return string.IsNullOrWhiteSpace(displayName) ? remoteModelId : displayName;
    }

    /// <summary>
    ///     One reported bound, refused when it is stated and not positive.
    /// </summary>
    /// <remarks>
    ///     The field is nullable, so a provider that states nothing is already expressible. A zero is a provider
    ///     saying the model takes no tokens, which is not a fact about any model, and it reaches the context
    ///     budget as a model nothing fits in.
    /// </remarks>
    /// <param name="value">The bound as the driver read it.</param>
    /// <param name="parameter">The bound's name, for the refusal.</param>
    private static int? Stated(int? value, string parameter)
    {
        return value is not { } stated || stated > 0
            ? value
            : throw new ArgumentOutOfRangeException(
                parameter,
                stated,
                "A stated token bound is a number of tokens the model takes, so it is positive. Leave it unset "
                + "for a provider that states none.");
    }

    private static int? Embedding(int? value, IReadOnlyList<AiOperationKind>? operationKinds)
    {
        if (value is not { } dimensions)
        {
            return null;
        }

        if (dimensions <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ProviderDiscoveredModel.EmbeddingDimensions),
                dimensions,
                "An embedding width is the length of the vector the model returns, so it is positive.");
        }

        // A width on a model that embeds nothing is read by nothing and configures a connection whose vectors
        // never arrive. Refused rather than dropped, because it says the driver has the model wrong.
        return operationKinds is null || operationKinds.Contains(AiOperationKind.Embedding)
            ? dimensions
            : throw new ArgumentException(
                "An embedding width belongs to a model that serves the embedding operation, and this model does "
                + "not declare it.",
                nameof(ProviderDiscoveredModel.EmbeddingDimensions));
    }
}
