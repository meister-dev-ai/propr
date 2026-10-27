// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Web;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace MeisterDev.ProPR.Api.Tests.Features.PlatformAdminElevation;

/// <summary>
///     The rule for platform administrators holds only if every endpoint carries its classification. These tests read
///     the classification of every controller action the API host registers, so an action added without one fails here.
/// </summary>
public sealed class TenantDataClassificationTests(ElevationApiFactory factory) : IClassFixture<ElevationApiFactory>
{
    [Fact]
    public void EveryControllerActionOfTheApiIsClassified()
    {
        var unclassified = this.Actions()
            .Where(action => Classification(action) is null)
            .Select(Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unclassified.Count == 0,
            "These actions carry no TenantData classification: " + string.Join(", ", unclassified));
    }

    [Fact]
    public void EveryContentActionNamesASourceItsRouteOrQueryProvides()
    {
        var inconsistent = new List<string>();
        foreach (var action in this.Actions())
        {
            if (Classification(action) is not { Access: TenantDataAccess.Content } classification)
            {
                continue;
            }

            var template = action.AttributeRouteInfo?.Template ?? string.Empty;
            var consistent = classification.Scope switch
            {
                ContentScopeSource.ClientRoute => HasRouteParameter(template, "clientId"),
                ContentScopeSource.ReviewJob => HasRouteParameter(template, classification.RouteValue),
                ContentScopeSource.ClientQuery => action.Parameters.Any(parameter =>
                    parameter.Name == "clientId" && parameter.BindingInfo?.BindingSource == BindingSource.Query),
                ContentScopeSource.Action => true,
                _ => false,
            };
            if (!consistent || classification.Resource == "content")
            {
                inconsistent.Add($"{Name(action)} ({classification.Scope}, {template})");
            }
        }

        Assert.True(
            inconsistent.Count == 0,
            "These content actions name a client source their route does not provide, or no resource: " + string.Join(", ", inconsistent));
    }

    [Fact]
    public void TheDataRequestsOfACustomerAreTheExportTheDeletionAndTheIdentityErasureOfATenant()
    {
        var dataRequests = this.Actions()
            .Where(action => Classification(action)?.Access == TenantDataAccess.DataRequest)
            .Select(Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["TenantData.DeleteTenant", "TenantData.EraseAuthor", "TenantData.ExportTenant"], dataRequests);
    }

    private static TenantDataAttribute? Classification(ControllerActionDescriptor action) =>
        action.EndpointMetadata.OfType<TenantDataAttribute>().LastOrDefault();

    private static string Name(ControllerActionDescriptor action) => $"{action.ControllerName}.{action.ActionName}";

    // A parameter counts with its exact name, with or without a constraint: {clientIdentifier} does not provide
    // clientId.
    private static bool HasRouteParameter(string template, string name) =>
        template.Length > 0
        && RoutePatternFactory.Parse(template).Parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.Ordinal));

    private IReadOnlyList<ControllerActionDescriptor> Actions()
    {
        var actions = factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
            .ToList();
        Assert.NotEmpty(actions);
        return actions;
    }
}
