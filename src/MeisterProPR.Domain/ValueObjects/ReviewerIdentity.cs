// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Normalized reviewer or bot identity for provider interactions.</summary>
public sealed record ReviewerIdentity
{
    /// <summary>Initializes a new instance of the <see cref="ReviewerIdentity" /> class.</summary>
    /// <param name="host">The provider host reference.</param>
    /// <param name="externalUserId">The external user identifier.</param>
    /// <param name="login">The login name.</param>
    /// <param name="displayName">The display name.</param>
    /// <param name="isBot">Whether the reviewer is a bot.</param>
    public ReviewerIdentity(ProviderHostRef host, string externalUserId, string login, string displayName, bool isBot)
    {
        this.Host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(login);

        this.ExternalUserId = externalUserId.Trim();
        this.Login = login.Trim();
        this.DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? this.Login
            : displayName.Trim();
        this.IsBot = isBot;
    }

    /// <summary>Gets the provider host reference.</summary>
    public ProviderHostRef Host { get; }

    /// <summary>Gets the external user identifier.</summary>
    public string ExternalUserId { get; }

    /// <summary>Gets the login name.</summary>
    public string Login { get; }

    /// <summary>Gets the display name.</summary>
    public string DisplayName { get; }

    /// <summary>Gets a value indicating whether the reviewer is a bot.</summary>
    public bool IsBot { get; }
}
