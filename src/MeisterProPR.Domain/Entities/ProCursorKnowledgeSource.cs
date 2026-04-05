// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.Entities;

/// <summary>
///     Client-scoped catalog record for one git-backed ProCursor knowledge source.
/// </summary>
public sealed class ProCursorKnowledgeSource
{
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
        this.OrganizationUrl = organizationUrl.Trim();
        this.ProjectId = projectId.Trim();
        this.RepositoryId = repositoryId.Trim();
        this.DefaultBranch = defaultBranch.Trim();
        this.RootPath = NormalizeOptional(rootPath);
        this.IsEnabled = isEnabled;
        this.SymbolMode = NormalizeSymbolMode(symbolMode);
        this.OrganizationScopeId = organizationScopeId;
        (this.CanonicalSourceProvider, this.CanonicalSourceValue) = NormalizeCanonicalSource(canonicalSourceProvider, canonicalSourceValue);
        this.SourceDisplayName = NormalizeOptional(sourceDisplayName);
        this.CreatedAt = DateTimeOffset.UtcNow;
        this.UpdatedAt = this.CreatedAt;
    }

    private ProCursorKnowledgeSource()
    {
    }

    public Guid Id { get; private set; }

    public Guid ClientId { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public ProCursorSourceKind SourceKind { get; private set; }

    public string OrganizationUrl { get; private set; } = string.Empty;

    public string ProjectId { get; private set; } = string.Empty;

    public string RepositoryId { get; private set; } = string.Empty;

    public string DefaultBranch { get; private set; } = string.Empty;

    public string? RootPath { get; private set; }

    public Guid? OrganizationScopeId { get; private set; }

    public string? CanonicalSourceProvider { get; private set; }

    public string? CanonicalSourceValue { get; private set; }

    public string? SourceDisplayName { get; private set; }

    public bool IsEnabled { get; private set; }

    public string SymbolMode { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public ICollection<ProCursorTrackedBranch> TrackedBranches { get; } = [];

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
        this.OrganizationUrl = organizationUrl.Trim();
        this.ProjectId = projectId.Trim();
        this.RepositoryId = repositoryId.Trim();
        this.DefaultBranch = defaultBranch.Trim();
        this.RootPath = NormalizeOptional(rootPath);
        this.OrganizationScopeId = organizationScopeId;
        (this.CanonicalSourceProvider, this.CanonicalSourceValue) = NormalizeCanonicalSource(canonicalSourceProvider, canonicalSourceValue);
        this.SourceDisplayName = NormalizeOptional(sourceDisplayName);
        this.IsEnabled = isEnabled;
        this.SymbolMode = NormalizeSymbolMode(symbolMode);
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

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

    public void SetEnabled(bool isEnabled)
    {
        this.IsEnabled = isEnabled;
        this.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static (string? Provider, string? Value) NormalizeCanonicalSource(string? provider, string? value)
    {
        var normalizedProvider = NormalizeOptional(provider);
        var normalizedValue = NormalizeOptional(value);

        if ((normalizedProvider is null) != (normalizedValue is null))
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
