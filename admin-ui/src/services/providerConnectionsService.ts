// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'

export type ScmProviderFamily = 'github' | 'gitLab' | 'forgejo' | 'azureDevOps'
export type ScmAuthenticationKind = 'personalAccessToken' | 'oauthClientCredentials' | 'appInstallation'
export type ProviderConnectionReadinessLevel = 'unknown' | 'configured' | 'degraded' | 'onboardingReady' | 'workflowComplete'

export interface ClientScmConnectionDto {
  id: string
  clientId: string
  providerFamily: ScmProviderFamily
  hostBaseUrl: string
  authenticationKind: ScmAuthenticationKind
  oAuthTenantId?: string | null
  oAuthClientId?: string | null
  displayName: string
  isActive: boolean
  verificationStatus: string
  readinessLevel?: ProviderConnectionReadinessLevel
  readinessReason?: string | null
  hostVariant?: string
  missingReadinessCriteria?: string[] | null
  lastVerifiedAt?: string | null
  lastVerificationError?: string | null
  lastVerificationFailureCategory?: string | null
  createdAt: string
  updatedAt: string
}

export interface ProviderConnectionAuditEntryDto {
  id: string
  clientId: string
  connectionId: string
  providerFamily: ScmProviderFamily
  displayName: string
  hostBaseUrl: string
  eventType: string
  summary: string
  occurredAt: string
  status: string
  failureCategory?: string | null
  detail?: string | null
}

export interface CreateClientProviderConnectionRequest {
  providerFamily: ScmProviderFamily
  hostBaseUrl: string
  authenticationKind: ScmAuthenticationKind
  oAuthTenantId?: string | null
  oAuthClientId?: string | null
  displayName: string
  secret: string
  isActive: boolean
}

export interface PatchClientProviderConnectionRequest {
  hostBaseUrl?: string
  authenticationKind?: ScmAuthenticationKind
  oAuthTenantId?: string | null
  oAuthClientId?: string | null
  displayName?: string
  secret?: string
  isActive?: boolean
}

export interface ClientScmScopeDto {
  id: string
  clientId: string
  connectionId: string
  scopeType: string
  externalScopeId: string
  scopePath: string
  displayName: string
  verificationStatus: string
  isEnabled: boolean
  lastVerifiedAt?: string | null
  lastVerificationError?: string | null
  createdAt: string
  updatedAt: string
}

export interface CreateClientProviderScopeRequest {
  scopeType: string
  externalScopeId: string
  scopePath: string
  displayName: string
  isEnabled: boolean
}

export interface PatchClientProviderScopeRequest {
  displayName?: string
  isEnabled?: boolean
}

export interface ClientReviewerIdentityDto {
  id: string
  clientId: string
  connectionId: string
  providerFamily: ScmProviderFamily
  externalUserId: string
  login: string
  displayName: string
  isBot: boolean
  updatedAt: string
}

export interface ResolvedReviewerIdentityResponse {
  clientId: string
  connectionId: string
  providerFamily: ScmProviderFamily
  externalUserId: string
  login: string
  displayName: string
  isBot: boolean
}

export interface SetClientReviewerIdentityRequest {
  externalUserId: string
  login: string
  displayName: string
  isBot: boolean
}

function getClient() {
  return createAdminClient() as any
}

export async function listProviderConnections(clientId: string): Promise<ClientScmConnectionDto[]> {
  const { data, error, response } = await getClient().GET('/clients/{clientId}/provider-connections', {
    params: { path: { clientId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load provider connections.'))
  }

  return (data as ClientScmConnectionDto[]) ?? []
}

export async function listProviderConnectionAuditTrail(
  clientId: string,
  take = 20,
): Promise<ProviderConnectionAuditEntryDto[]> {
  const { data, error, response } = await getClient().GET('/clients/{clientId}/provider-operations/audit-trail', {
    params: {
      path: { clientId },
      query: { take },
    },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load provider audit trail.'))
  }

  return (data as ProviderConnectionAuditEntryDto[]) ?? []
}

export async function createProviderConnection(
  clientId: string,
  request: CreateClientProviderConnectionRequest,
): Promise<ClientScmConnectionDto> {
  const { data, error, response } = await getClient().POST('/clients/{clientId}/provider-connections', {
    params: { path: { clientId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to create provider connection.'))
  }

  return data as ClientScmConnectionDto
}

export async function updateProviderConnection(
  clientId: string,
  connectionId: string,
  request: PatchClientProviderConnectionRequest,
): Promise<ClientScmConnectionDto> {
  const { data, error, response } = await getClient().PATCH('/clients/{clientId}/provider-connections/{connectionId}', {
    params: { path: { clientId, connectionId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to update provider connection.'))
  }

  return data as ClientScmConnectionDto
}

export async function verifyProviderConnection(clientId: string, connectionId: string): Promise<ClientScmConnectionDto> {
  const { data, error, response } = await getClient().POST('/clients/{clientId}/provider-connections/{connectionId}/verify', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to verify provider connection.'))
  }

  return data as ClientScmConnectionDto
}

export async function deleteProviderConnection(clientId: string, connectionId: string): Promise<void> {
  const { error, response } = await getClient().DELETE('/clients/{clientId}/provider-connections/{connectionId}', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to delete provider connection.'))
  }
}

export async function listProviderScopes(clientId: string, connectionId: string): Promise<ClientScmScopeDto[]> {
  const { data, error, response } = await getClient().GET('/clients/{clientId}/provider-connections/{connectionId}/scopes', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load provider scopes.'))
  }

  return (data as ClientScmScopeDto[]) ?? []
}

export async function createProviderScope(
  clientId: string,
  connectionId: string,
  request: CreateClientProviderScopeRequest,
): Promise<ClientScmScopeDto> {
  const { data, error, response } = await getClient().POST('/clients/{clientId}/provider-connections/{connectionId}/scopes', {
    params: { path: { clientId, connectionId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to create provider scope.'))
  }

  return data as ClientScmScopeDto
}

export async function updateProviderScope(
  clientId: string,
  connectionId: string,
  scopeId: string,
  request: PatchClientProviderScopeRequest,
): Promise<ClientScmScopeDto> {
  const { data, error, response } = await getClient().PATCH('/clients/{clientId}/provider-connections/{connectionId}/scopes/{scopeId}', {
    params: { path: { clientId, connectionId, scopeId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to update provider scope.'))
  }

  return data as ClientScmScopeDto
}

export async function deleteProviderScope(clientId: string, connectionId: string, scopeId: string): Promise<void> {
  const { error, response } = await getClient().DELETE('/clients/{clientId}/provider-connections/{connectionId}/scopes/{scopeId}', {
    params: { path: { clientId, connectionId, scopeId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to delete provider scope.'))
  }
}

export async function resolveReviewerIdentityCandidates(
  clientId: string,
  connectionId: string,
  search: string,
): Promise<ResolvedReviewerIdentityResponse[]> {
  const { data, error, response } = await getClient().GET('/clients/{clientId}/provider-connections/{connectionId}/reviewer-identities/resolve', {
    params: {
      path: { clientId, connectionId },
      query: { search },
    },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to resolve reviewer identities.'))
  }

  return (data as ResolvedReviewerIdentityResponse[]) ?? []
}

export async function getReviewerIdentity(clientId: string, connectionId: string): Promise<ClientReviewerIdentityDto | null> {
  const { data, error, response } = await getClient().GET('/clients/{clientId}/provider-connections/{connectionId}/reviewer-identity', {
    params: { path: { clientId, connectionId } },
  })

  if (response.status === 404) {
    return null
  }

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load reviewer identity.'))
  }

  return (data as ClientReviewerIdentityDto) ?? null
}

export async function setReviewerIdentity(
  clientId: string,
  connectionId: string,
  request: SetClientReviewerIdentityRequest,
): Promise<ClientReviewerIdentityDto> {
  const { data, error, response } = await getClient().PUT('/clients/{clientId}/provider-connections/{connectionId}/reviewer-identity', {
    params: { path: { clientId, connectionId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to save reviewer identity.'))
  }

  return data as ClientReviewerIdentityDto
}

export async function deleteReviewerIdentity(clientId: string, connectionId: string): Promise<void> {
  const { error, response } = await getClient().DELETE('/clients/{clientId}/provider-connections/{connectionId}/reviewer-identity', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to clear reviewer identity.'))
  }
}
