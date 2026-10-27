// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Infrastructure.Data.Models;

/// <summary>
///     The account of a provisioned user: a person whom a hosting platform provisions by the issuer of the identity
///     provider for provisioned users and the subject of the person at that provider. The provisioned sign-in reaches the
///     account through this record and through nothing else.
/// </summary>
public sealed class ProvisionedUserRecord
{
    /// <summary>The account, which the provisioning created.</summary>
    public Guid UserId { get; set; }

    /// <summary>The issuer of the identity provider for provisioned users, without a trailing slash.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>The subject of the person at the provider, as the platform sent it.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The email address the platform sent. ProPR keeps it for the platform and never looks an account up by it.</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>The display name the platform sent, or <see langword="null" />.</summary>
    public string? DisplayName { get; set; }

    /// <summary>When the platform provisioned the user.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the platform last changed the email address or the display name.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When the user last signed in through the identity provider for provisioned users.</summary>
    public DateTimeOffset? LastSignInAt { get; set; }

    /// <summary>
    ///     Since when ProPR keeps the account inactive because it holds no membership, or <see langword="null" />. ProPR
    ///     enables such an account when the platform sets a membership. A platform administrator who enables or disables
    ///     the account ends this state, so ProPR does not enable an account that an administrator disabled.
    /// </summary>
    public DateTimeOffset? InactiveWithoutMembershipSince { get; set; }

    /// <summary>The account.</summary>
    public AppUserRecord? User { get; set; }
}
