// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>Extracts native webhook metadata without verification credentials or provider requests.</summary>
public interface IWebhookIngressPolicy
{
    ScmProvider Provider { get; }
    string? ReadEventType(IReadOnlyDictionary<string, string> headers);
    string? ReadDeliveryKey(IReadOnlyDictionary<string, string> headers);
}
