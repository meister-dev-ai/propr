// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/types'

export type DiscoveryDescriptor = components['schemas']['ConnectionDiscoveryDescriptor']
export type DiscoveryScope = components['schemas']['ConnectionDiscoveryScope']
export type DiscoveryProject = components['schemas']['ScmDiscoveryProjectOption']
export type DiscoverySource = components['schemas']['ConnectionDiscoverySource']
export type DiscoverySelection = components['schemas']['ConnectionDiscoverySelection']
export type DiscoveryBranch = components['schemas']['ScmDiscoveryBranchOption']
export type DiscoveryFilter = components['schemas']['ScmDiscoveryCrawlFilterOption']
export type CanonicalSourceReferenceDto = components['schemas']['CanonicalSourceReferenceDto']
export type DiscoverySourceKind = components['schemas']['ProCursorSourceKind']
export type DiscoveryPurpose = 'crawl' | 'webhook' | 'mention' | 'procursor'

export async function listConnectionDescriptor(clientId: string, connectionId: string, purpose: DiscoveryPurpose): Promise<DiscoveryDescriptor> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/connections/{connectionId}/discovery/descriptor', {
    params: { path: { clientId, connectionId }, query: { purpose } },
  })
  if (!response.ok || !data) throw new Error(getApiErrorMessage(error, 'Failed to load connection capabilities.'))
  return data
}

export async function listConnectionScopes(clientId: string, connectionId: string, purpose: DiscoveryPurpose): Promise<DiscoveryScope[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/connections/{connectionId}/discovery/scopes', {
    params: { path: { clientId, connectionId }, query: { purpose } },
  })
  if (!response.ok) throw new Error(getApiErrorMessage(error, 'Failed to load connection scopes.'))
  return data ?? []
}

export async function listConnectionProjects(clientId: string, connectionId: string, purpose: DiscoveryPurpose, scopeKey: string): Promise<DiscoveryProject[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/connections/{connectionId}/discovery/projects', {
    params: { path: { clientId, connectionId }, query: { purpose, scopeKey } },
  })
  if (!response.ok) throw new Error(getApiErrorMessage(error, 'Failed to load projects.'))
  return data ?? []
}

export async function listConnectionSources(clientId: string, connectionId: string, purpose: DiscoveryPurpose, scopeKey: string, projectId: string | undefined, sourceKind: DiscoverySourceKind): Promise<DiscoverySource[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/connections/{connectionId}/discovery/sources', {
    params: { path: { clientId, connectionId }, query: { purpose, scopeKey, projectId, sourceKind } },
  })
  if (!response.ok) throw new Error(getApiErrorMessage(error, 'Failed to load sources.'))
  return data ?? []
}

export async function listConnectionBranches(clientId: string, connectionId: string, purpose: DiscoveryPurpose, scopeKey: string, projectId: string, sourceKind: DiscoverySourceKind, reference: CanonicalSourceReferenceDto): Promise<DiscoveryBranch[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/connections/{connectionId}/discovery/branches', {
    params: { path: { clientId, connectionId }, query: { purpose, scopeKey, projectId, sourceKind,
      canonicalSourceProvider: reference.provider ?? '', canonicalSourceValue: reference.value ?? '' } },
  })
  if (!response.ok) throw new Error(getApiErrorMessage(error, 'Failed to load branches.'))
  return data ?? []
}

export async function listConnectionFilters(clientId: string, connectionId: string, purpose: DiscoveryPurpose, scopeKey: string, projectId?: string): Promise<DiscoveryFilter[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/connections/{connectionId}/discovery/filters', {
    params: { path: { clientId, connectionId }, query: { purpose, scopeKey, projectId } },
  })
  if (!response.ok) throw new Error(getApiErrorMessage(error, 'Failed to load repository filters.'))
  return data ?? []
}

export async function resolveConnectionSelection(clientId: string, connectionId: string, purpose: DiscoveryPurpose, scopeKey: string, projectId?: string): Promise<DiscoverySelection> {
  const { data, error, response } = await createAdminClient().GET('/admin/clients/{clientId}/connections/{connectionId}/discovery/selection', {
    params: { path: { clientId, connectionId }, query: { purpose, scopeKey, projectId } },
  })
  if (!response.ok || !data) throw new Error(getApiErrorMessage(error, 'Failed to resolve the selected coordinates.'))
  return data
}
