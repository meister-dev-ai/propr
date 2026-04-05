// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>Per-client AI endpoint configuration. Exactly one connection per client may be active at a time.</summary>
public sealed class AiConnection
{
    /// <summary>Creates a new <see cref="AiConnection"/>.</summary>
    public AiConnection(Guid id, Guid clientId, string displayName, string endpointUrl, IReadOnlyList<string> models)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(displayName))
        {
            throw new ArgumentException("DisplayName required.", nameof(displayName));
        }

        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            throw new ArgumentException("EndpointUrl required.", nameof(endpointUrl));
        }

        if (models is null || models.Count == 0)
        {
            throw new ArgumentException("At least one model is required.", nameof(models));
        }

        this.Id = id;
        this.ClientId = clientId;
        this.DisplayName = displayName;
        this.EndpointUrl = endpointUrl;
        this.Models = models;
        this.CreatedAt = DateTimeOffset.UtcNow;
        this.IsActive = false;
    }

    /// <summary>Unique identifier.</summary>
    public Guid Id { get; private set; }

    /// <summary>Owning client.</summary>
    public Guid ClientId { get; private set; }

    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; private set; }

    /// <summary>Azure OpenAI or AI Foundry endpoint URL.</summary>
    public string EndpointUrl { get; private set; }

    /// <summary>Available model deployment names at this endpoint.</summary>
    public IReadOnlyList<string> Models { get; private set; }

    /// <summary>Whether this connection is the active one for its client.</summary>
    public bool IsActive { get; private set; }

    /// <summary>The selected model deployment when this connection is active.</summary>
    public string? ActiveModel { get; private set; }

    /// <summary>Optional API key. When null, the runtime falls back to the ambient Azure credential chain.</summary>
    public string? ApiKey { get; private set; }

    /// <summary>When this connection was created.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    ///     Optional model category tag. When non-null, this connection is selected as the tier-specific
    ///     AI client for reviews on files classified into the corresponding <see cref="FileComplexityTier" />.
    /// </summary>
    public AiConnectionModelCategory? ModelCategory { get; private set; }

    /// <summary>Sets or clears the model category for this connection.</summary>
    public void SetModelCategory(AiConnectionModelCategory? category)
    {
        this.ModelCategory = category;
    }

    /// <summary>Marks this connection as active with the specified model. Caller must deactivate others first.</summary>
    public void Activate(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("Model is required.", nameof(model));
        }

        if (!this.Models.Contains(model))
        {
            throw new ArgumentException($"Model '{model}' is not in the connection's model list.", nameof(model));
        }

        this.IsActive = true;
        this.ActiveModel = model;
    }

    /// <summary>Marks this connection as inactive.</summary>
    public void Deactivate()
    {
        this.IsActive = false;
        this.ActiveModel = null;
    }

    /// <summary>Updates non-null fields. Null values leave the existing value unchanged.</summary>
    public void Update(string? displayName, string? endpointUrl, IReadOnlyList<string>? models, string? apiKey)
    {
        if (displayName is not null)
        {
            this.DisplayName = displayName;
        }

        if (endpointUrl is not null)
        {
            this.EndpointUrl = endpointUrl;
        }

        if (models is not null)
        {
            this.Models = models;
        }

        if (apiKey is not null)
        {
            this.ApiKey = apiKey;
        }
    }
}
