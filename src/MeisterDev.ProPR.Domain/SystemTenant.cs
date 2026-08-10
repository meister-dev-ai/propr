// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain;

/// <summary>
///     The tenant that belongs to the installation rather than to a customer.
///     <para>
///         The identifier is defined in the domain because rules depend on it. A runner enrolled in this
///         tenant is offered every tenant's work, and its credential is given a shorter lifetime. Those
///         rules are applied above the layer that stores the value.
///     </para>
/// </summary>
public static class SystemTenant
{
    /// <summary>The System tenant's fixed identifier.</summary>
    public static readonly Guid Id = new("11111111-1111-1111-1111-111111111111");

    /// <summary>Whether the given tenant is the installation's own.</summary>
    /// <param name="tenantId">The tenant to test.</param>
    public static bool Is(Guid tenantId)
    {
        return tenantId == Id;
    }
}
