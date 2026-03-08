using MeisterProPR.Application.Interfaces;
using Microsoft.Extensions.Configuration;

namespace MeisterProPR.Infrastructure.Configuration;

public sealed class EnvVarClientRegistry : IClientRegistry
{
    private readonly HashSet<string> _keys;

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

        this._keys = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (this._keys.Count == 0)
        {
            throw new InvalidOperationException("MEISTER_CLIENT_KEYS contains no valid keys.");
        }
    }

    public bool IsValidKey(string clientKey)
    {
        return this._keys.Contains(clientKey);
    }
}