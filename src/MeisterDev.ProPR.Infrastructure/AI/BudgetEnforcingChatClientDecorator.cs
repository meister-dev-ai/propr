// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

using MeisterDev.Ai.Providers.Contracts;
using MeisterDev.Ai.Providers.Runtime;
using MeisterDev.ProPR.Application.DTOs;
using MeisterDev.ProPR.Application.Features.Budgeting;
using MeisterDev.ProPR.Domain.ValueObjects;
using Microsoft.Extensions.AI;

namespace MeisterDev.ProPR.Infrastructure.AI;

/// <summary>
///     Contributes spend metering and hard-cap gating as the provider pipeline's budget stage. The provider library
///     has no notion of cost or entitlement, so this is supplied from the product side; it occupies the stage the
///     library reserves for it rather than choosing its own position in the chain.
/// </summary>
/// <remarks>
///     Pricing comes from the configured model the runtime was resolved for, which the endpoint and model
///     descriptors deliberately do not carry, so it is supplied per resolution rather than looked up here.
/// </remarks>
public sealed class BudgetEnforcingChatClientDecorator(
    IBudgetScopeAccessor budgetScopeAccessor,
    Func<ProviderModelDescriptor, ModelPricing> pricingFor) : IProviderChatClientDecorator
{
    public ProviderRuntimeStage Stage => ProviderRuntimeStage.Budget;

    public IChatClient Decorate(IChatClient inner, ProviderEndpoint endpoint, ProviderModelDescriptor model)
    {
        _ = endpoint;

        return new BudgetEnforcingChatClient(inner, budgetScopeAccessor, pricingFor(model));
    }
}
