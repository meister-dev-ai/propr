// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { RuntimeMode } from '@/app/runtime/createRuntime'

export type ScmProviderFamily = 'github' | 'gitLab' | 'forgejo' | 'azureDevOps'
export type ScmAuthenticationKind = 'personalAccessToken' | 'oauthClientCredentials' | 'appInstallation' | 'windowsUserAccount'
export type ProviderConnectionReadinessLevel = 'unknown' | 'configured' | 'degraded' | 'onboardingReady' | 'workflowComplete'

export interface ClientScmConnectionDto {
  id: string
  clientId: string
  providerFamily: ScmProviderFamily
  hostBaseUrl: string
  authenticationKind: ScmAuthenticationKind
  userName?: string | null
  oAuthTenantId?: string | null
  oAuthClientId?: string | null
  gitHubAppId?: number | null
  gitHubAppInstallationId?: number | null
  storeThreads?: boolean | null
  storeDiffs?: boolean | null
  retentionDays?: number | null
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
  userName?: string | null
  oAuthTenantId?: string | null
  oAuthClientId?: string | null
  gitHubAppId?: number | null
  gitHubAppInstallationId?: number | null
  storeThreads?: boolean
  storeDiffs?: boolean
  retentionDays?: number | null
  displayName: string
  secret: string
  isActive: boolean
}

export interface PatchClientProviderConnectionRequest {
  hostBaseUrl?: string
  authenticationKind?: ScmAuthenticationKind
  userName?: string | null
  oAuthTenantId?: string | null
  oAuthClientId?: string | null
  gitHubAppId?: number | null
  gitHubAppInstallationId?: number | null
  storeThreads?: boolean | null
  storeDiffs?: boolean | null
  retentionDays?: number | null
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
  return createAdminClient({ baseUrl: getActiveRuntime().apiBaseUrl })
}

async function listProviderConnectionsInternal(clientId: string): Promise<ClientScmConnectionDto[]> {
  const { data, error, response } = await getClient().GET('/clients/{clientId}/provider-connections', {
    params: { path: { clientId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load provider connections.'))
  }

  return (data as ClientScmConnectionDto[]) ?? []
}

export async function listProviderConnections(clientId: string): Promise<ClientScmConnectionDto[]> {
  return resolveProviderConnectionsService().listProviderConnections(clientId)
}

async function listProviderConnectionAuditTrailInternal(
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

export async function listProviderConnectionAuditTrail(
  clientId: string,
  take = 20,
): Promise<ProviderConnectionAuditEntryDto[]> {
  return resolveProviderConnectionsService().listProviderConnectionAuditTrail(clientId, take)
}

async function createProviderConnectionInternal(
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

export async function createProviderConnection(
  clientId: string,
  request: CreateClientProviderConnectionRequest,
): Promise<ClientScmConnectionDto> {
  return resolveProviderConnectionsService().createProviderConnection(clientId, request)
}

async function updateProviderConnectionInternal(
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

export async function updateProviderConnection(
  clientId: string,
  connectionId: string,
  request: PatchClientProviderConnectionRequest,
): Promise<ClientScmConnectionDto> {
  return resolveProviderConnectionsService().updateProviderConnection(clientId, connectionId, request)
}

async function verifyProviderConnectionInternal(clientId: string, connectionId: string): Promise<ClientScmConnectionDto> {
  const { data, error, response } = await getClient().POST('/clients/{clientId}/provider-connections/{connectionId}/verify', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to verify provider connection.'))
  }

  return data as ClientScmConnectionDto
}

export async function verifyProviderConnection(clientId: string, connectionId: string): Promise<ClientScmConnectionDto> {
  return resolveProviderConnectionsService().verifyProviderConnection(clientId, connectionId)
}

async function deleteProviderConnectionInternal(clientId: string, connectionId: string): Promise<void> {
  const { error, response } = await getClient().DELETE('/clients/{clientId}/provider-connections/{connectionId}', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to delete provider connection.'))
  }
}

export async function deleteProviderConnection(clientId: string, connectionId: string): Promise<void> {
  return resolveProviderConnectionsService().deleteProviderConnection(clientId, connectionId)
}

async function listProviderScopesInternal(clientId: string, connectionId: string): Promise<ClientScmScopeDto[]> {
  const { data, error, response } = await getClient().GET('/clients/{clientId}/provider-connections/{connectionId}/scopes', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load provider scopes.'))
  }

  return (data as ClientScmScopeDto[]) ?? []
}

export async function listProviderScopes(clientId: string, connectionId: string): Promise<ClientScmScopeDto[]> {
  return resolveProviderConnectionsService().listProviderScopes(clientId, connectionId)
}

async function createProviderScopeInternal(
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

export async function createProviderScope(
  clientId: string,
  connectionId: string,
  request: CreateClientProviderScopeRequest,
): Promise<ClientScmScopeDto> {
  return resolveProviderConnectionsService().createProviderScope(clientId, connectionId, request)
}

async function updateProviderScopeInternal(
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

export async function updateProviderScope(
  clientId: string,
  connectionId: string,
  scopeId: string,
  request: PatchClientProviderScopeRequest,
): Promise<ClientScmScopeDto> {
  return resolveProviderConnectionsService().updateProviderScope(clientId, connectionId, scopeId, request)
}

async function deleteProviderScopeInternal(clientId: string, connectionId: string, scopeId: string): Promise<void> {
  const { error, response } = await getClient().DELETE('/clients/{clientId}/provider-connections/{connectionId}/scopes/{scopeId}', {
    params: { path: { clientId, connectionId, scopeId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to delete provider scope.'))
  }
}

export async function deleteProviderScope(clientId: string, connectionId: string, scopeId: string): Promise<void> {
  return resolveProviderConnectionsService().deleteProviderScope(clientId, connectionId, scopeId)
}

async function resolveReviewerIdentityCandidatesInternal(
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

export async function resolveReviewerIdentityCandidates(
  clientId: string,
  connectionId: string,
  search: string,
): Promise<ResolvedReviewerIdentityResponse[]> {
  return resolveProviderConnectionsService().resolveReviewerIdentityCandidates(clientId, connectionId, search)
}

async function getReviewerIdentityInternal(clientId: string, connectionId: string): Promise<ClientReviewerIdentityDto | null> {
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

export async function getReviewerIdentity(clientId: string, connectionId: string): Promise<ClientReviewerIdentityDto | null> {
  return resolveProviderConnectionsService().getReviewerIdentity(clientId, connectionId)
}

async function setReviewerIdentityInternal(
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

export async function setReviewerIdentity(
  clientId: string,
  connectionId: string,
  request: SetClientReviewerIdentityRequest,
): Promise<ClientReviewerIdentityDto> {
  return resolveProviderConnectionsService().setReviewerIdentity(clientId, connectionId, request)
}

async function deleteReviewerIdentityInternal(clientId: string, connectionId: string): Promise<void> {
  const { error, response } = await getClient().DELETE('/clients/{clientId}/provider-connections/{connectionId}/reviewer-identity', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to clear reviewer identity.'))
  }
}

export async function deleteReviewerIdentity(clientId: string, connectionId: string): Promise<void> {
  return resolveProviderConnectionsService().deleteReviewerIdentity(clientId, connectionId)
}

export interface ProviderConnectionsService {
  runtimeMode: RuntimeMode
  listProviderConnections: typeof listProviderConnections
  listProviderConnectionAuditTrail: typeof listProviderConnectionAuditTrail
  createProviderConnection: typeof createProviderConnection
  updateProviderConnection: typeof updateProviderConnection
  verifyProviderConnection: typeof verifyProviderConnection
  deleteProviderConnection: typeof deleteProviderConnection
  listProviderScopes: typeof listProviderScopes
  createProviderScope: typeof createProviderScope
  updateProviderScope: typeof updateProviderScope
  deleteProviderScope: typeof deleteProviderScope
  resolveReviewerIdentityCandidates: typeof resolveReviewerIdentityCandidates
  getReviewerIdentity: typeof getReviewerIdentity
  setReviewerIdentity: typeof setReviewerIdentity
  deleteReviewerIdentity: typeof deleteReviewerIdentity
}

function createProviderConnectionsService(runtimeMode: RuntimeMode): ProviderConnectionsService {
  return {
    runtimeMode,
    listProviderConnections: listProviderConnectionsInternal,
    listProviderConnectionAuditTrail: listProviderConnectionAuditTrailInternal,
    createProviderConnection: createProviderConnectionInternal,
    updateProviderConnection: updateProviderConnectionInternal,
    verifyProviderConnection: verifyProviderConnectionInternal,
    deleteProviderConnection: deleteProviderConnectionInternal,
    listProviderScopes: listProviderScopesInternal,
    createProviderScope: createProviderScopeInternal,
    updateProviderScope: updateProviderScopeInternal,
    deleteProviderScope: deleteProviderScopeInternal,
    resolveReviewerIdentityCandidates: resolveReviewerIdentityCandidatesInternal,
    getReviewerIdentity: getReviewerIdentityInternal,
    setReviewerIdentity: setReviewerIdentityInternal,
    deleteReviewerIdentity: deleteReviewerIdentityInternal,
  }
}

const liveProviderConnectionsService = createProviderConnectionsService('live')
const mockProviderConnectionsService = createProviderConnectionsService('mock')

export function resolveProviderConnectionsService(): ProviderConnectionsService {
  return getActiveRuntime().mode === 'mock'
    ? mockProviderConnectionsService
    : liveProviderConnectionsService
}
