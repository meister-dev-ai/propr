using System.Security.Cryptography;
using System.Text;
using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using Microsoft.Extensions.Configuration;

namespace MeisterProPR.Infrastructure.Configuration;

/// <summary>
///     In-memory client registry seeded from the <c>MEISTER_CLIENT_KEYS</c> configuration value.
///     Used in non-DB mode; deterministic GUIDs are derived from each key string.
/// </summary>
public sealed class EnvVarClientRegistry : IClientRegistry
{
    private readonly Dictionary<string, Guid> _keys;

    /// <summary>Initialises the registry by parsing <c>MEISTER_CLIENT_KEYS</c> from configuration.</summary>
    /// <param name="configuration">The application configuration.</param>
    // Use IConfiguration so values come from env vars, user-secrets, appsettings, etc.
    public EnvVarClientRegistry(IConfiguration configuration)
    {
        if (configuration is null)
        {
            throw new ArgumentNullException(nameof(configuration));
        }

        var raw = configuration["MEISTER_CLIENT_KEYS"]; // reads from env, user-secrets, appsettings
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new InvalidOperationException("MEISTER_CLIENT_KEYS is not set or empty in configuration.");
        }

        var parsed = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parsed.Length == 0)
        {
            throw new InvalidOperationException("MEISTER_CLIENT_KEYS contains no valid keys.");
        }

        this._keys = parsed.ToDictionary(k => k, DeterministicGuid, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public bool IsValidKey(string clientKey)
    {
        return this._keys.ContainsKey(clientKey);
    }

    /// <inheritdoc />
    public Task<Guid?> GetClientIdByKeyAsync(string key, CancellationToken ct = default)
    {
        var id = this._keys.TryGetValue(key, out var guid) ? (Guid?)guid : null;
        return Task.FromResult(id);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Env-var mode has no database storage; reviewer identity is always null.
    ///     Crawl-based jobs are not triggered in this mode, so the null return is safe.
    /// </remarks>
    public Task<Guid?> GetReviewerIdAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult<Guid?>(null);
    }

    /// <inheritdoc />
    /// <remarks>Env-var mode has no database storage; defaults to <see cref="CommentResolutionBehavior.Silent" />.</remarks>
    public Task<CommentResolutionBehavior> GetCommentResolutionBehaviorAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult(CommentResolutionBehavior.Silent);
    }

    /// <inheritdoc />
    /// <remarks>Env-var mode has no database storage; custom system messages are not supported.</remarks>
    public Task<string?> GetCustomSystemMessageAsync(Guid clientId, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }

    /// <summary>Derives a stable, deterministic UUID from the key string using MD5.</summary>
    private static Guid DeterministicGuid(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash);
    }
}
