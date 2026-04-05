// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/services/generated/openapi'

export type CanonicalSourceReferenceDto = components['schemas']['CanonicalSourceReferenceDto']
export type ClientAdoOrganizationScopeDto = components['schemas']['ClientAdoOrganizationScopeDto']
export type CreateClientAdoOrganizationScopeRequest = components['schemas']['CreateClientAdoOrganizationScopeRequest']
export type PatchClientAdoOrganizationScopeRequest = components['schemas']['PatchClientAdoOrganizationScopeRequest']
export type AdoProjectOptionDto = components['schemas']['AdoProjectOptionDto']
export type AdoSourceOptionDto = components['schemas']['AdoSourceOptionDto']
export type AdoBranchOptionDto = components['schemas']['AdoBranchOptionDto']
export type AdoCrawlFilterOptionDto = components['schemas']['AdoCrawlFilterOptionDto']

export type AdoSourceKind = 'repository' | 'adoWiki'

export async function listAdoOrganizationScopes(clientId: string): Promise<ClientAdoOrganizationScopeDto[]> {
  const { data, error, response } = await createAdminClient().GET('/clients/{clientId}/ado-organization-scopes', {
    params: { path: { clientId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load organization scopes.'))
  }

  return (data as ClientAdoOrganizationScopeDto[]) ?? []
}

export async function createAdoOrganizationScope(
  clientId: string,
  request: CreateClientAdoOrganizationScopeRequest,
): Promise<ClientAdoOrganizationScopeDto> {
  const { data, error, response } = await createAdminClient().POST('/clients/{clientId}/ado-organization-scopes', {
    params: { path: { clientId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to create organization scope.'))
  }

  return data as ClientAdoOrganizationScopeDto
}

export async function updateAdoOrganizationScope(
  clientId: string,
  scopeId: string,
  request: PatchClientAdoOrganizationScopeRequest,
): Promise<ClientAdoOrganizationScopeDto> {
  const { data, error, response } = await createAdminClient().PATCH('/clients/{clientId}/ado-organization-scopes/{scopeId}', {
    params: { path: { clientId, scopeId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to update organization scope.'))
  }

  return data as ClientAdoOrganizationScopeDto
}

export async function deleteAdoOrganizationScope(clientId: string, scopeId: string): Promise<void> {
  const { error, response } = await createAdminClient().DELETE('/clients/{clientId}/ado-organization-scopes/{scopeId}', {
    params: { path: { clientId, scopeId } },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to delete organization scope.'))
  }
}

export async function listAdoProjects(
  clientId: string,
  organizationScopeId: string,
): Promise<AdoProjectOptionDto[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/ado/discovery/projects', {
    params: {
      path: { clientId },
      query: { organizationScopeId },
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
): Promise<AdoCrawlFilterOptionDto[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/ado/discovery/crawl-filters', {
    params: {
      path: { clientId },
      query: { organizationScopeId, projectId },
    },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load crawl filter options.'))
  }

  return (data as AdoCrawlFilterOptionDto[]) ?? []
}
