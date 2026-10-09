// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing;

/// <summary>Contains the captured iteration comparison used to recreate the reviewed diff.</summary>
public sealed record AzureDevOpsPublicationContext(int? CompareToIterationId);
