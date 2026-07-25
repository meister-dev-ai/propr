// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the model-catalog endpoints. Browsing requires the client-administrator role, override
 * maintenance the tenant-administrator role, and snapshot import a platform administrator — all enforced
 * server-side, since an import writes the global rows every tenant reads.
 */

import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import { authedFetch, createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/services/generated/openapi'

export type AiModelCatalogEntryDto = components['schemas']['AiModelCatalogEntryDto']
export type AiModelCatalogOverrideDto = components['schemas']['AiModelCatalogOverrideDto']
export type AiModelCatalogDefinitionDto = components['schemas']['AiModelCatalogDefinitionDto']
export type AiModelCatalogLayer = components['schemas']['AiModelCatalogLayer']
export type ModelCatalogProviderResponse = components['schemas']['ModelCatalogProviderResponse']

/** The catalog providers available to browse. */
export async function listProviders(clientId: string): Promise<ModelCatalogProviderResponse[]> {
  const { data } = await createAdminClient().GET('/clients/{clientId}/model-catalog/providers', {
    params: { path: { clientId } },
  })
  return (data as ModelCatalogProviderResponse[]) ?? []
}

/**
 * Catalog models for a client with tenant overrides already applied. Each entry reports which layer supplied
 * its pricing, so a negotiated rate can be labelled rather than shown as a bare number.
 */
export async function listModels(clientId: string, providerId?: string): Promise<AiModelCatalogEntryDto[]> {
  const { data } = await createAdminClient().GET('/clients/{clientId}/model-catalog/models', {
    params: { path: { clientId }, query: providerId ? { providerId } : {} },
  })
  return (data as AiModelCatalogEntryDto[]) ?? []
}

/** The catalog providers a tenant administrator can browse. */
export async function listTenantProviders(tenantId: string): Promise<ModelCatalogProviderResponse[]> {
  const { data } = await createAdminClient().GET('/tenants/{tenantId}/model-catalog/providers', {
    params: { path: { tenantId } },
  })
  return (data as ModelCatalogProviderResponse[]) ?? []
}

/**
 * Catalog models as they apply to a tenant: global entries with that tenant's own overrides applied. A client
 * override is excluded, since it is narrower than the scope being edited and would misreport what the tenant set.
 */
export async function listTenantModels(
  tenantId: string,
  providerId?: string,
): Promise<AiModelCatalogEntryDto[]> {
  const { data } = await createAdminClient().GET('/tenants/{tenantId}/model-catalog/models', {
    params: { path: { tenantId }, query: providerId ? { providerId } : {} },
  })
  return (data as AiModelCatalogEntryDto[]) ?? []
}

export type ModelCatalogImportResponse = components['schemas']['ModelCatalogImportResponse']

/**
 * Uploads a catalog snapshot, replacing the global entries it describes. Platform-admin only: the import writes
 * the rows every tenant reads. Sent as multipart form data, which the generated JSON client does not cover, so
 * this one goes through the shared authenticated fetch instead.
 */
export async function importSnapshot(snapshot: File): Promise<ModelCatalogImportResponse> {
  const body = new FormData()
  body.append('snapshot', snapshot)

  const response = await authedFetch(`${getActiveRuntime().apiBaseUrl}/admin/model-catalog/snapshot`, {
    method: 'POST',
    body,
  })

  if (!response.ok) {
    throw new Error(await readProblemDetail(response))
  }

  return (await response.json()) as ModelCatalogImportResponse
}

/** Surfaces the server's stated cause, so a malformed upload is actionable rather than merely rejected. */
async function readProblemDetail(response: Response): Promise<string> {
  try {
    const problem = (await response.json()) as {
      errors?: Record<string, string[]>
      detail?: string
      title?: string
    }
    const firstError = problem.errors ? Object.values(problem.errors).flat()[0] : undefined
    return firstError ?? problem.detail ?? problem.title ?? 'The snapshot could not be imported.'
  } catch {
    return 'The snapshot could not be imported.'
  }
}

/** A tenant's own catalog overrides. */
export async function listTenantOverrides(tenantId: string): Promise<AiModelCatalogOverrideDto[]> {
  const { data } = await createAdminClient().GET('/tenants/{tenantId}/model-catalog/overrides', {
    params: { path: { tenantId } },
  })
  return (data as AiModelCatalogOverrideDto[]) ?? []
}

/** Records or replaces a tenant override. A price left undefined is inherited, not treated as zero. */
export async function upsertTenantOverride(
  tenantId: string,
  body: AiModelCatalogOverrideDto,
): Promise<void> {
  const { error } = await createAdminClient().PUT('/tenants/{tenantId}/model-catalog/overrides', {
    params: { path: { tenantId } },
    body,
  })
  if (error) {
    throw new Error('Failed to save the model override.')
  }
}

/**
 * Defines a model the catalog does not list, so a private fine-tune, a newer release, or a self-hosted model
 * becomes selectable and budgeted. Refused when the catalog already describes the model, since its capabilities
 * would then come from the snapshot and these values would be ignored — a pricing override is the instrument for
 * that case.
 */
export async function defineTenantModel(
  tenantId: string,
  body: AiModelCatalogDefinitionDto,
): Promise<void> {
  const { error } = await createAdminClient().PUT('/tenants/{tenantId}/model-catalog/models', {
    params: { path: { tenantId } },
    body,
  })
  if (error) {
    throw new Error(getApiErrorMessage(error, 'The model could not be defined.'))
  }
}

/** Removes a tenant override, returning the model to the snapshot's pricing. */
export async function deleteTenantOverride(
  tenantId: string,
  providerId: string,
  remoteModelId: string,
): Promise<void> {
  const { error } = await createAdminClient().DELETE(
    '/tenants/{tenantId}/model-catalog/overrides/{providerId}/{remoteModelId}',
    { params: { path: { tenantId, providerId, remoteModelId } } },
  )
  if (error) {
    throw new Error('Failed to remove the model override.')
  }
}
