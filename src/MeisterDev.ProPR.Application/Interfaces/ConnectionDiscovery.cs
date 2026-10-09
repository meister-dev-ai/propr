// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;
using MeisterDev.ProPR.ProCursor.Contracts.Sources;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Identifies the active client-owned connection used throughout guided discovery.</summary>
public sealed record ConnectionDiscoveryContext(Guid ClientId, Guid ConnectionId, ProviderHostRef Host);

/// <summary>Describes the native hierarchy and the operations registered for a connection.</summary>
public sealed record ConnectionDiscoveryDescriptor(
    ScmProvider Provider,
    string ScopeLabel,
    string? ProjectLabel,
    IReadOnlyList<ConnectionDiscoverySourceKind> SourceKinds,
    bool SupportsBranches,
    bool SupportsKnowledgeSources);

public sealed record ConnectionDiscoverySourceKind(ProCursorSourceKind Kind, string Label);

public sealed record ConnectionDiscoveryCoordinates(Guid? OrganizationScopeId, string ProviderScopePath, string ProviderProjectKey);

public sealed record ConnectionDiscoverySelection(
    ScmProvider Provider,
    Guid ConnectionId,
    string ScopeKey,
    Guid? OrganizationScopeId,
    string ProviderScopePath,
    string ProviderProjectKey);

/// <summary>One native scope with its saved identity when the provider uses saved scopes.</summary>
public sealed record ConnectionDiscoveryScope(string ScopeKey, string DisplayName, Guid? SavedScopeId = null);

/// <summary>Native source identity and persistent configuration coordinates.</summary>
public sealed record ConnectionDiscoverySource(
    Guid? OrganizationScopeId,
    string ProviderScopePath,
    string ProviderProjectKey,
    string RepositoryId,
    ProCursorSourceKind SourceKind,
    CanonicalSourceReferenceDto CanonicalSourceRef,
    string DisplayName,
    string? DefaultBranch);
