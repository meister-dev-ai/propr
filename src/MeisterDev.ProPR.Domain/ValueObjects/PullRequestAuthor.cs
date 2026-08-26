// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     The account that opened a pull request, as identified by the host that issued it.
/// </summary>
public sealed record PullRequestAuthor
{
    /// <summary>Initializes a new instance of the <see cref="PullRequestAuthor" /> class.</summary>
    /// <param name="host">The provider host that issued the identifier.</param>
    /// <param name="externalUserId">The provider-native user identifier.</param>
    /// <param name="login">The provider login, where the payload states one.</param>
    /// <param name="displayName">The display name, where the payload states one.</param>
    /// <param name="isBot">
    ///     Whether the provider states the account is a bot. Null where the payload states nothing about it.
    /// </param>
    public PullRequestAuthor(
        ProviderHostRef host,
        string externalUserId,
        string? login = null,
        string? displayName = null,
        bool? isBot = null)
    {
        this.Host = host ?? throw new ArgumentNullException(nameof(host));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalUserId);

        this.ExternalUserId = externalUserId.Trim();
        this.Login = string.IsNullOrWhiteSpace(login) ? null : login.Trim();
        this.DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        this.IsBot = isBot;
    }

    /// <summary>Gets the provider host that issued the identifier.</summary>
    public ProviderHostRef Host { get; }

    /// <summary>Gets the provider-native user identifier.</summary>
    public string ExternalUserId { get; }

    /// <summary>Gets the provider login, or null where the payload states none.</summary>
    /// <remarks>
    ///     Azure DevOps has no login separate from the account's unique name; where the payload omits it, the
    ///     adapter carries the display name here, as the reviewer-identity mapping does.
    /// </remarks>
    public string? Login { get; }

    /// <summary>Gets the display name, or null where the payload states none.</summary>
    public string? DisplayName { get; }

    /// <summary>Gets whether the provider states the account is a bot, or null where it states nothing.</summary>
    public bool? IsBot { get; }

    /// <summary>Gets the host-scoped identity: the provider, the host and the provider-native user id.</summary>
    /// <remarks>
    ///     A provider-native user id is unique only within one host, so two hosts can issue the same id for
    ///     different people. The login and the display name are excluded because a provider lets an account
    ///     change either one. <see cref="IsBot" /> is excluded as well, because it is a property of the
    ///     account and not part of its identity. Two instances of one account with differing signals
    ///     therefore collapse in a set, and a reader of the signal takes it from the instance the set kept.
    /// </remarks>
    public string AuthorKey => this.Host.ScopedKey(this.ExternalUserId);

    /// <summary>Compares this author with another by host-scoped identity.</summary>
    /// <param name="other">The author to compare against.</param>
    /// <returns>True when both name the same account on the same host.</returns>
    public bool Equals(PullRequestAuthor? other)
    {
        return other is not null && string.Equals(this.AuthorKey, other.AuthorKey, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(this.AuthorKey);
    }
}
