// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.Features.Crawling.Webhooks.Ports;
using MeisterProPR.Application.Interfaces;

namespace MeisterProPR.Application.Tests.Architecture;

public sealed class AzureDevOpsApplicationBoundaryTests
{
    [Fact]
    public void ApplicationAssembly_DoesNotExposePublicAzureDevOpsSpecificInterfaces()
    {
        var exportedAzureSpecificInterfaces = typeof(IReviewAssignmentService).Assembly
            .GetExportedTypes()
            .Where(type => type.IsInterface)
            .Where(type => type.Namespace is not null)
            .Where(type =>
                type.Namespace == typeof(IReviewAssignmentService).Namespace ||
                type.Namespace == typeof(IWebhookSecretGenerator).Namespace)
            .Where(type => type.Name.StartsWith("IAdo", StringComparison.Ordinal))
            .Select(type => type.FullName)
            .OrderBy(static typeName => typeName, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(exportedAzureSpecificInterfaces);
    }
}
