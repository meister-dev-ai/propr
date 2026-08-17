// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Crawling.Webhooks.Ports;

/// <summary>
///     A delivery the provider is entitled to send and this product does not act on.
/// </summary>
/// <remarks>
///     Distinct from a malformed payload, because the two deserve opposite answers. A provider whose webhook
///     is configured for every event sends comment, push and pipeline deliveries alongside the pull-request
///     ones, and answering those with a client error tells the provider its request was wrong. Providers count
///     those: GitLab, GitHub and Forgejo all disable a hook that keeps failing, so treating an ordinary
///     unhandled event as a fault eventually switches off the deliveries that do matter.
/// </remarks>
public sealed class UnsupportedWebhookEventException : InvalidOperationException
{
    public UnsupportedWebhookEventException()
        : base("The webhook event is not one this product acts on.")
    {
    }

    public UnsupportedWebhookEventException(string message)
        : base(message)
    {
    }

    public UnsupportedWebhookEventException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
