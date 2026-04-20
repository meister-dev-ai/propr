// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Client-scoped catalog record for one git-backed ProCursor knowledge source.
/// </summary>
public sealed class ProCursorKnowledgeSource
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ProCursorKnowledgeSource"/> class.
    /// </summary>
    public ProCursorKnowledgeSource(
        Guid id,
        Guid clientId,
        string displayName,
        ProCursorSourceKind sourceKind,
        string organizationUrl,
        string projectId,
        string repositoryId,
        string defaultBranch,
        string? rootPath,
        bool isEnabled,
        string symbolMode,
        Guid? organizationScopeId = null,
        string? canonicalSourceProvider = null,
        string? canonicalSourceValue = null,
        string? sourceDisplayName = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Id must not be empty.", nameof(id));
        }

        if (clientId == Guid.Empty)
        {
            throw new ArgumentException("ClientId must not be empty.", nameof(clientId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);

        this.Id = id;
        this.ClientId = clientId;
        this.DisplayName = displayName.Trim();
        this.SourceKind = sourceKind;
        this.ProviderScopePath = organizationUrl.Trim();
        this.ProviderProjectKey = projectId.Trim();
        this.RepositoryId = repositoryId.Trim();
        this.DefaultBranch = defaultBranch.Trim();
        this.RootPath = NormalizeOptional(rootPath);
        this.IsEnabled = isEnabled;
        this.SymbolMode = NormalizeSymbolMode(symbolMode);
        this.OrganizationScopeId = organizationScopeId;
        (this.CanonicalSourceProvider, this.CanonicalSourceValue) =
            NormalizeCanonicalSource(canonicalSourceProvider, canonicalSourceValue);
        this.SourceDisplayName = NormalizeOptional(sourceDisplayName);
        this.CreatedAt = DateTimeOffset.UtcNow;
        this.UpdatedAt = this.CreatedAt;
    }

    private ProCursorKnowledgeSource()
    {
    }

    /// <summary>
    ///     Gets the unique identifier of this knowledge source.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    ///     Gets or sets the client identifier that owns this knowledge source.
    /// </summary>
    public Guid ClientId { get; private set; }

    /// <summary>
    ///     Gets or sets the display name of this knowledge source.
    /// </summary>
    public string DisplayName { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the source kind indicating the type of ProCursor source.
    /// </summary>
    public ProCursorSourceKind SourceKind { get; private set; }

    /// <summary>
    ///     Gets or sets the provider scope path (organization URL).
    /// </summary>
    public string ProviderScopePath { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the provider project key (project ID).
    /// </summary>
    public string ProviderProjectKey { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the repository identifier.
    /// </summary>
    public string RepositoryId { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the default branch name.
    /// </summary>
    public string DefaultBranch { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the optional root path within the repository.
    /// </summary>
    public string? RootPath { get; private set; }

    /// <summary>
    ///     Gets or sets the optional organization scope identifier.
    /// </summary>
    public Guid? OrganizationScopeId { get; private set; }

    /// <summary>
    ///     Gets or sets the canonical source provider.
    /// </summary>
    public string? CanonicalSourceProvider { get; private set; }

    /// <summary>
    ///     Gets or sets the canonical source value.
    /// </summary>
    public string? CanonicalSourceValue { get; private set; }

    /// <summary>
    ///     Gets or sets the optional display name for the source.
    /// </summary>
    public string? SourceDisplayName { get; private set; }

    /// <summary>
    ///     Gets or sets a value indicating whether this knowledge source is enabled.
    /// </summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    ///     Gets or sets the symbol mode for indexing.
    /// </summary>
    public string SymbolMode { get; private set; } = string.Empty;

    /// <summary>
    ///     Gets the date and time when this knowledge source was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    ///     Gets or sets the date and time when this knowledge source was last updated.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    ///     Gets the collection of tracked branches for this knowledge source.
    /// </summary>
    public ICollection<ProCursorTrackedBranch> TrackedBranches { get; } = [];

    /// <summary>
    ///     Updates the definition of this knowledge source with new values.
    /// </summary>
    public void UpdateDefinition(
        string displayName,
        string organizationUrl,
        string projectId,
        string repositoryId,
        string defaultBranch,
        string? rootPath,
        bool isEnabled,
        string symbolMode,
        Guid? organizationScopeId = null,
        string? canonicalSourceProvider = null,
        string? canonicalSourceValue = null,
        string? sourceDisplayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultBranch);

        this.DisplayName = displayName.Trim();
        this.ProviderScopePath = organizationUrl.Trim();
        this.ProviderProjectKey = projectId.Trim();
        this.RepositoryId = repositoryId.Trim();
        this.DefaultBranch = defaultBranch.Trim();
        this.RootPath = NormalizeOptional(rootPath);
        this.OrganizationScopeId = organizationScopeId;
        (this.CanonicalSourceProvider, this.CanonicalSourceValue) =
            NormalizeCanonicalSource(canonicalSourceProvider, canonicalSourceValue);
        this.SourceDisplayName = NormalizeOptional(sourceDisplayName);
        this.IsEnabled = isEnabled;
        this.SymbolMode = NormalizeSymbolMode(symbolMode);
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    ///     Adds a tracked branch to this knowledge source.
    /// </summary>
    public ProCursorTrackedBranch AddTrackedBranch(
        Guid branchId,
        string branchName,
        ProCursorRefreshTriggerMode refreshTriggerMode,
        bool miniIndexEnabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);

        if (this.TrackedBranches.Any(existing =>
                string.Equals(existing.BranchName, branchName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Tracked branch '{branchName}' already exists for this source.");
        }

        var trackedBranch = new ProCursorTrackedBranch(
            branchId,
            this.Id,
            branchName,
            refreshTriggerMode,
            miniIndexEnabled);

        this.TrackedBranches.Add(trackedBranch);
        this.UpdatedAt = DateTimeOffset.UtcNow;
        return trackedBranch;
    }

    /// <summary>
    ///     Sets the enabled state of this knowledge source.
    /// </summary>
    public void SetEnabled(bool isEnabled)
    {
        this.IsEnabled = isEnabled;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static (string? Provider, string? Value) NormalizeCanonicalSource(string? provider, string? value)
    {
        var normalizedProvider = NormalizeOptional(provider);
        var normalizedValue = NormalizeOptional(value);

        if (normalizedProvider is null != normalizedValue is null)
        {
            throw new ArgumentException("Canonical source provider and value must both be provided or both be omitted.");
        }

        return (normalizedProvider, normalizedValue);
    }

    private static string NormalizeSymbolMode(string symbolMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbolMode);
        return symbolMode.Trim();
    }
}
