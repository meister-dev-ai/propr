// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Web;

/// <summary>
///     What an endpoint returns of the data of a tenant. The value decides what a platform administrator reads at
///     the endpoint: metadata always, content only during an elevation that covers the client.
/// </summary>
public enum TenantDataAccess
{
    /// <summary>The endpoint returns no data of a tenant: installation settings, the caller's own account, sign-in.</summary>
    None,

    /// <summary>
    ///     The endpoint serves a runner, the ProCursor service or a source-control webhook and authenticates that caller
    ///     with its own credential. The credential of a platform administrator is not accepted there.
    /// </summary>
    ServiceChannel,

    /// <summary>
    ///     The endpoint returns or changes metadata of tenants, clients and reviews: identifiers, states, times, counts,
    ///     sizes, token and cost figures, and configuration without secrets.
    /// </summary>
    Metadata,

    /// <summary>
    ///     The endpoint returns metadata of several reviews or clients, and their content only where the caller may read
    ///     the content of the client. The action withholds the content fields of the other clients.
    /// </summary>
    Mixed,

    /// <summary>
    ///     The endpoint returns content: text that comes from a customer's repository or pull-request conversation, or
    ///     the answer of the AI about it. A platform administrator reads it only during an elevation that covers the
    ///     client, unless the client belongs to the System tenant. A role the administrator holds on the client grants
    ///     no content.
    /// </summary>
    Content,

    /// <summary>
    ///     The endpoint carries out a data request of the customer: the export or deletion of a tenant, or the erasure
    ///     of an identity. A platform administrator calls it without an elevation.
    /// </summary>
    DataRequest,
}

/// <summary>Where ProPR finds the client whose content an endpoint returns.</summary>
public enum ContentScopeSource
{
    /// <summary>The route value <c>clientId</c>.</summary>
    ClientRoute,

    /// <summary>The query parameter <c>clientId</c>.</summary>
    ClientQuery,

    /// <summary>
    ///     The route value named by <see cref="TenantDataAttribute.RouteValue" />, which identifies a review job or a
    ///     thread pass. The client that owns the job owns the content.
    /// </summary>
    ReviewJob,

    /// <summary>
    ///     The action limits its answer to the clients whose content the caller may read, because the answer spans
    ///     several clients.
    /// </summary>
    Action,
}

/// <summary>
///     Classifies the data an endpoint returns. Every controller action of the API carries a classification, on the
///     action or on its controller; the one on the action takes precedence.
/// </summary>
/// <param name="access">What the endpoint returns of the data of a tenant.</param>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class TenantDataAttribute(TenantDataAccess access) : Attribute
{
    /// <summary>What the endpoint returns of the data of a tenant.</summary>
    public TenantDataAccess Access { get; } = access;

    /// <summary>Where ProPR finds the client of a <see cref="TenantDataAccess.Content" /> endpoint.</summary>
    public ContentScopeSource Scope { get; init; } = ContentScopeSource.ClientRoute;

    /// <summary>The route value that identifies the review job, for <see cref="ContentScopeSource.ReviewJob" />.</summary>
    public string RouteValue { get; init; } = "id";

    /// <summary>
    ///     What the endpoint returns, as a noun phrase for the audit record of a read, for example
    ///     <c>the protocol of a review job</c>.
    /// </summary>
    public string Resource { get; init; } = "content";
}
