// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/types'
import type { ScmProviderFamily } from '@/services/providerConnectionsService'

export type ProviderScopeOption = components['schemas']['ProviderScopeOptionResponse']
export type ProviderRepositoryOption = components['schemas']['ProviderRepositoryOptionResponse']

/**
 * Lists the owners, organizations or groups one of a client's connections can reach.
 *
 * The connection is what a caller names, because every provider other than Azure DevOps is reached at the
 * connection's own host, and that host is what a mention configuration stores as its scope path.
 */
export async function listProviderScopeOptions(
  clientId: string,
  provider: ScmProviderFamily,
  connectionId: string,
): Promise<ProviderScopeOption[]> {
  const { data, error, response } = await createAdminClient()
    .GET('/admin/clients/{clientId}/providers/{provider}/discovery/scopes', {
      params: { path: { clientId, provider }, query: { connectionId } },
    })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load owners for this connection.'))
  }

  return ((data as ProviderScopeOption[]) ?? []).filter((scope) => Boolean(scope.scopePath))
}

/** Lists the repositories one of a client's connections can reach within a scope. */
export async function listProviderRepositoryOptions(
  clientId: string,
  provider: ScmProviderFamily,
  connectionId: string,
  scopePath: string,
): Promise<ProviderRepositoryOption[]> {
  const { data, error, response } = await createAdminClient()
    .GET('/admin/clients/{clientId}/providers/{provider}/discovery/repositories', {
      params: { path: { clientId, provider }, query: { connectionId, scopePath } },
    })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load repositories for this owner.'))
  }

  return ((data as ProviderRepositoryOption[]) ?? []).filter((repository) => Boolean(repository.repositoryId))
}
