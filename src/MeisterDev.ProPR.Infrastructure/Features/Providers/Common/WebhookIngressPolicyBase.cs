// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Application.Interfaces;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Common;

/// <summary>Reads the first case-insensitive header match without altering the stored value.</summary>
internal abstract class WebhookIngressPolicyBase : IWebhookIngressPolicy
{
    public abstract ScmProvider Provider { get; }
    protected virtual string? EventHeaderName => null;
    protected virtual string? DeliveryHeaderName => null;
    public string? ReadEventType(IReadOnlyDictionary<string, string> headers) => ReadHeader(headers, this.EventHeaderName);
    public string? ReadDeliveryKey(IReadOnlyDictionary<string, string> headers) => ReadHeader(headers, this.DeliveryHeaderName);

    private static string? ReadHeader(IReadOnlyDictionary<string, string> headers, string? name)
    {
        if (name is null)
        {
            return null;
        }

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }
}

/// <summary>Retains absent diagnostic and retry headers for undefined saved provider values.</summary>
internal sealed class UnregisteredWebhookIngressPolicy(ScmProvider provider) : WebhookIngressPolicyBase
{
    public override ScmProvider Provider => provider;
}
