// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

global using IAdoCommentPoster = MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Reviewing.IAdoCommentPoster;
global using IAdoWebhookBasicAuthVerifier =
    MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Security.IAdoWebhookBasicAuthVerifier;
global using IAdoWebhookPayloadParser =
    MeisterDev.ProPR.Infrastructure.Features.Providers.AzureDevOps.Parsing.IAdoWebhookPayloadParser;
