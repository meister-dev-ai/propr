// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     The identifier this installation reports itself under.
///     <para>
///         It has its own table so that deleting it starts a new identifier without touching the activated
///         license or anything else the licensing tables hold. Deleting the row changes what the installation
///         reports itself as and nothing else: it is not what any license is issued against, and no license
///         check reads it.
///     </para>
/// </summary>
public sealed class LicensingIdentityRecord
{
    /// <summary>Fixed key. The identifier is installation-wide.</summary>
    public int Id { get; set; }

    /// <summary>
    ///     A locally generated random value. It is not derived from the machine, the network, the database or
    ///     the license.
    /// </summary>
    public Guid Identifier { get; set; }

    /// <summary>When this identifier was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
