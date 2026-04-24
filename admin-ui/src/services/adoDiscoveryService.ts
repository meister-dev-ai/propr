// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/services/generated/openapi'
import { listProviderConnections, listProviderScopes, type ScmProviderFamily } from '@/services/providerConnectionsService'

export type CanonicalSourceReferenceDto = components['schemas']['CanonicalSourceReferenceDto']
export type AdoProjectOptionDto = components['schemas']['AdoProjectOptionDto']
export type AdoSourceOptionDto = components['schemas']['AdoSourceOptionDto']
export type AdoBranchOptionDto = components['schemas']['AdoBranchOptionDto']
export type AdoCrawlFilterOptionDto = components['schemas']['AdoCrawlFilterOptionDto']

export type AdoSourceKind = 'repository' | 'adoWiki'
export type AdoDiscoveryPurpose = 'crawl' | 'procursor' | 'webhook'

export interface ClientAdoOrganizationScopeDto {
  id: string
  clientId: string
  organizationUrl: string
  displayName: string
  isEnabled: boolean
  verificationStatus: 'unknown' | 'verified' | 'stale' | 'unauthorized' | 'unreachable'
  lastVerifiedAt?: string | null
  lastVerificationError?: string | null
  createdAt: string
  updatedAt: string
  connectionId: string
}

export async function listAdoOrganizationScopes(clientId: string): Promise<ClientAdoOrganizationScopeDto[]> {
  try {
    const connections = await listProviderConnections(clientId)
    const azureConnections = connections.filter((connection) => connection.providerFamily === 'azureDevOps')
    const scopesByConnection = await Promise.all(
      azureConnections.map(async (connection) => {
        const scopes = await listProviderScopes(clientId, connection.id)
        return scopes
          .filter((scope) => scope.scopeType.toLowerCase() === 'organization')
          .map((scope) => ({
            id: scope.id,
            clientId: scope.clientId,
            organizationUrl: scope.scopePath,
            displayName: scope.displayName,
            isEnabled: scope.isEnabled,
            verificationStatus: mapVerificationStatus(scope.verificationStatus),
            lastVerifiedAt: scope.lastVerifiedAt ?? null,
            lastVerificationError: scope.lastVerificationError ?? null,
            createdAt: scope.createdAt,
            updatedAt: scope.updatedAt,
            connectionId: connection.id,
          }))
      }),
    )

    return scopesByConnection
      .flat()
      .sort((left, right) => left.organizationUrl.localeCompare(right.organizationUrl))
  } catch (error) {
    throw new Error(getApiErrorMessage(error, 'Failed to load organization scopes.'))
  }
}

function mapVerificationStatus(status: string): ClientAdoOrganizationScopeDto['verificationStatus'] {
  switch (status.trim().toLowerCase()) {
    case 'verified':
      return 'verified'
    case 'failed':
      return 'unreachable'
    case 'stale':
      return 'stale'
    default:
      return 'unknown'
  }
}

export async function listAdoProjects(
  clientId: string,
  organizationScopeId: string,
  purpose?: AdoDiscoveryPurpose,
): Promise<AdoProjectOptionDto[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/ado/discovery/projects', {
    params: {
      path: { clientId },
      query: { organizationScopeId, purpose },
    },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load Azure DevOps projects.'))
  }

  return (data as AdoProjectOptionDto[]) ?? []
}

export async function listAdoSources(
  clientId: string,
  organizationScopeId: string,
  projectId: string,
  sourceKind: AdoSourceKind,
): Promise<AdoSourceOptionDto[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/ado/discovery/sources', {
    params: {
      path: { clientId },
      query: { organizationScopeId, projectId, sourceKind },
    },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load Azure DevOps sources.'))
  }

  return (data as AdoSourceOptionDto[]) ?? []
}

export async function listAdoBranches(
  clientId: string,
  organizationScopeId: string,
  projectId: string,
  sourceKind: AdoSourceKind,
  canonicalSourceRef: CanonicalSourceReferenceDto,
): Promise<AdoBranchOptionDto[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/ado/discovery/branches', {
    params: {
      path: { clientId },
      query: {
        organizationScopeId,
        projectId,
        sourceKind,
        canonicalSourceProvider: canonicalSourceRef.provider ?? '',
        canonicalSourceValue: canonicalSourceRef.value ?? '',
      },
    },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load Azure DevOps branches.'))
  }

  return (data as AdoBranchOptionDto[]) ?? []
}

export async function listAdoCrawlFilters(
  clientId: string,
  organizationScopeId: string,
  projectId: string,
  purpose?: AdoDiscoveryPurpose,
): Promise<AdoCrawlFilterOptionDto[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/ado/discovery/crawl-filters', {
    params: {
      path: { clientId },
      query: { organizationScopeId, projectId, purpose },
    },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load crawl filter options.'))
  }

  return (data as AdoCrawlFilterOptionDto[]) ?? []
}
