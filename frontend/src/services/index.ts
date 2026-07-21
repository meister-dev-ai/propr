// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

// Central service-adapter seam.
// View models import services from this barrel so the mock/live split (US3, T034–T037)
// can swap adapters in one place without churning view-model imports.
export * as adoDiscoveryService from './adoDiscoveryService'
export * as aiConnectionsService from './aiConnectionsService'
export * as authOptionsService from './authOptionsService'
export * as clientTokenUsageService from './clientTokenUsageService'
export * as findingDismissalsService from './findingDismissalsService'
export * as jobsService from './jobsService'
export * as licensingService from './licensingService'
export * as proCursorService from './proCursorService'
export * as promptOverridesService from './promptOverridesService'
export * as providerActivationService from './providerActivationService'
export * as providerConnectionsService from './providerConnectionsService'
export * as providerOperationsService from './providerOperationsService'
export * as tenantAdminService from './tenantAdminService'
export * as tenantAuthService from './tenantAuthService'
export * as tenantMembershipService from './tenantMembershipService'
export * as tenantSsoProvidersService from './tenantSsoProvidersService'
export * as threadMemoryService from './threadMemoryService'
export * as userSecurityService from './userSecurityService'
export * as webhookConfigurationService from './webhookConfigurationService'
