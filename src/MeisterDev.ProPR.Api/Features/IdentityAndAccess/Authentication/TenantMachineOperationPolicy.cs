// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Collections.Frozen;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Api.Controllers;
using MeisterDev.ProPR.Api.Features.Clients.Controllers;
using MeisterDev.ProPR.Api.Features.Crawling.Configuration.Controllers;
using MeisterDev.ProPR.Api.Features.Reviewing.Intake.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;


namespace MeisterDev.ProPR.Api.Features.IdentityAndAccess.Authentication;

/// <summary>Explicit controller action permission boundary for tenant machine callers.</summary>
public static class TenantMachineOperationPolicy
{
    public const string TenantItemKey = "TenantMachineTenantId";
    public const string AuthorizedItemKey = "TenantMachineAuthorized";

    private static readonly FrozenSet<(string Controller, string Action)> Allowed =
        new (string Controller, string Action)[]
        {
            Action<ClientsController>(nameof(ClientsController.GetClient)), Action<ClientsController>(nameof(ClientsController.GetClients)),
            Action<ReviewsController>(nameof(ReviewsController.ListReviews)), Action<ReviewsController>(nameof(ReviewsController.GetDashboard)),
            Action<ReviewsController>(nameof(ReviewsController.GetHistory)), Action<ReviewJobsController>(nameof(ReviewJobsController.GetClientReview)),
            Action<ReviewJobsController>(nameof(ReviewJobsController.GetReview)),
            Action<ReviewJobsController>(nameof(ReviewJobsController.SubmitReviewByCoordinates)),
            Action<ClientReviewTargetsController>(nameof(ClientReviewTargetsController.GetTargets)),
            Action<ClientReviewTargetsController>(nameof(ClientReviewTargetsController.CreateTarget)),
            Action<ClientReviewTargetsController>(nameof(ClientReviewTargetsController.GetRepositories)),
            Action<ClientReviewTargetsController>(nameof(ClientReviewTargetsController.GetOpenReviews)),
            Action<ClientProviderConnectionsController>(nameof(ClientProviderConnectionsController.GetProviderConnections)),
            Action<ClientProviderConnectionsController>(nameof(ClientProviderConnectionsController.GetProviderConnection)),
            Action<ClientProviderConnectionsController>(nameof(ClientProviderConnectionsController.GetProviderOperationalStatus)),
            Action<ClientProviderConnectionsController>(nameof(ClientProviderConnectionsController.GetProviderConnectionAuditTrail)),
            Action<ClientProviderConnectionsController>(nameof(ClientProviderConnectionsController.CreateProviderConnection)),
            Action<ClientProviderConnectionsController>(nameof(ClientProviderConnectionsController.PatchProviderConnection)),
            Action<ClientProviderConnectionsController>(nameof(ClientProviderConnectionsController.DeleteProviderConnection)),
            Action<ClientProviderConnectionsController>(nameof(ClientProviderConnectionsController.VerifyProviderConnection)),
            Action<ClientProviderScopesController>(nameof(ClientProviderScopesController.GetProviderScopes)),
            Action<ClientProviderScopesController>(nameof(ClientProviderScopesController.GetProviderScope)),
            Action<ClientProviderScopesController>(nameof(ClientProviderScopesController.CreateProviderScope)),
            Action<ClientProviderScopesController>(nameof(ClientProviderScopesController.PatchProviderScope)),
            Action<ClientProviderScopesController>(nameof(ClientProviderScopesController.DeleteProviderScope)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.GetAiConnections)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.GetPermittedProviders)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.CreateAiConnection)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.UpdateAiConnection)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.DeleteAiConnection)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.ActivateAiConnection)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.DeactivateAiConnection)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.VerifyAiConnection)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.VerifyUpdateAiConnection)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.SelectAiPurposes)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.DiscoverModels)),
            Action<ClientAiConnectionsController>(nameof(ClientAiConnectionsController.ProbeAiConnection)),
            Action<ClientLogicalModelsController>(nameof(ClientLogicalModelsController.ListEffective)),
            Action<ClientLogicalModelsController>(nameof(ClientLogicalModelsController.ListPurposeRoles)),
        }.ToFrozenSet();

    /// <summary>The registered actions available to tenant machine callers.</summary>
    public static IReadOnlyCollection<(string Controller, string Action)> AllowedActions => Allowed;

    private static (string Controller, string Action) Action<TController>(string action)
        where TController : Microsoft.AspNetCore.Mvc.ControllerBase
    {
        var type = typeof(TController);
        const string suffix = "Controller";
        if (!type.Name.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Controller {type.Name} does not use the MVC controller naming convention.");
        }

        return (type.Name[..^suffix.Length], action);
    }

    /// <summary>Checks that each permission matches an action registered by MVC.</summary>
    public static void ValidateRegisteredActions(IEnumerable<ControllerActionDescriptor> descriptors)
    {
        var registered = descriptors.Select(descriptor => (descriptor.ControllerName, descriptor.ActionName)).ToHashSet();
        if (!Allowed.IsSubsetOf(registered))
        {
            throw new InvalidOperationException("A tenant machine permission has no matching MVC action.");
        }
    }

    public static bool IsAllowed(string controller, string action) => Allowed.Contains((controller, action));

    /// <summary>Checks the resolved action and current database ownership before granting a request.</summary>
    public static async Task<bool> AuthorizeAsync(HttpContext context)
    {
        if (context.Items[TenantItemKey] is not Guid tenantId ||
            context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } descriptor ||
            !IsAllowed(descriptor.ControllerName, descriptor.ActionName))
        {
            return false;
        }

        var service = context.RequestServices.GetRequiredService<TenantMachineCredentialService>();
        var roles = new Dictionary<Guid, ClientRole>();
        if (context.Request.RouteValues.TryGetValue("clientId", out var rawClientId))
        {
            if (!Guid.TryParse(rawClientId?.ToString(), out var clientId) ||
                !await service.OwnsClientAsync(tenantId, clientId, context.RequestAborted))
            {
                return false;
            }

            roles.Add(clientId, ClientRole.ClientAdministrator);
        }
        else if ((descriptor.ControllerName, descriptor.ActionName) == Action<ReviewJobsController>(nameof(ReviewJobsController.GetReview)))
        {
            var rawJobId = context.Request.RouteValues["jobId"];
            if (!Guid.TryParse(rawJobId?.ToString(), out var jobId))
            {
                return false;
            }

            var jobClientId = await service.GetJobClientIdAsync(jobId, context.RequestAborted);
            if (jobClientId is not Guid id || !await service.OwnsClientAsync(tenantId, id, context.RequestAborted))
            {
                return false;
            }

            roles.Add(id, ClientRole.ClientUser);
        }
        else if ((descriptor.ControllerName, descriptor.ActionName) == Action<ClientsController>(nameof(ClientsController.GetClients)))
        {
            foreach (var id in await service.GetClientIdsAsync(tenantId, context.RequestAborted))
            {
                roles.Add(id, ClientRole.ClientAdministrator);
            }
        }
        else
        {
            return false;
        }

        context.Items["ClientRoles"] = roles;
        context.Items[AuthorizedItemKey] = true;
        return true;
    }
}
