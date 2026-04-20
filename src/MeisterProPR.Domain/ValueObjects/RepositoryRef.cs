// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Stable provider-neutral repository identity.</summary>
public sealed record RepositoryRef
{
    /// <summary>Initializes a new instance of the <see cref="RepositoryRef"/> class.</summary>
    /// <param name="host">The provider host reference.</param>
    /// <param name="externalRepositoryId">The external repository identifier.</param>
    /// <param name="ownerOrNamespace">The owner or namespace.</param>
    /// <param name="projectPath">The project path.</param>
    public RepositoryRef(ProviderHostRef host, string externalRepositoryId, string ownerOrNamespace, string projectPath)
    {
        this.Host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalRepositoryId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerOrNamespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);

        this.ExternalRepositoryId = externalRepositoryId.Trim();
        this.OwnerOrNamespace = ownerOrNamespace.Trim();
        this.ProjectPath = projectPath.Trim();
    }

    /// <summary>Gets the provider host reference.</summary>
    public ProviderHostRef Host { get; }

    /// <summary>Gets the external repository identifier.</summary>
    public string ExternalRepositoryId { get; }

    /// <summary>Gets the owner or namespace.</summary>
    public string OwnerOrNamespace { get; }

    /// <summary>Gets the project path.</summary>
    public string ProjectPath { get; }
}
