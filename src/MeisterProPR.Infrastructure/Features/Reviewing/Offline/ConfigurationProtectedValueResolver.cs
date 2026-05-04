// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Reviewing.Execution.Models;
using MeisterProPR.Application.Features.Reviewing.Execution.Ports;
using Microsoft.Extensions.Configuration;

namespace MeisterProPR.Infrastructure.Features.Reviewing.Offline;

/// <summary>
///     Resolves protected values from the active configuration stack.
/// </summary>
public sealed class ConfigurationProtectedValueResolver(IConfiguration configuration) : IProtectedValueResolver
{
    public Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IReadOnlyList<ProtectedValueReference> references,
        CancellationToken cancellationToken = default)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var reference in references)
        {
            var value = configuration[reference.ConfigurationKey];
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(
                    $"Required protected value '{reference.ReferenceName}' was not found at configuration key '{reference.ConfigurationKey}'.");
            }

            resolved[reference.ReferenceName] = value;
        }

        return Task.FromResult<IReadOnlyDictionary<string, string>>(resolved);
    }
}
