// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrappers for the logical-model endpoints. All functions use the shared
 * admin client from api.ts. Per-client override + purpose-map operations require the client-administrator
 * role; tenant-catalog operations require the tenant-administrator role (enforced server-side).
 */

import { createAdminClient } from '@/services/api'
import type { components } from '@/services/generated/openapi'

export type LogicalModelResponse = components['schemas']['LogicalModelResponse']
export type LogicalModelWriteRequest = components['schemas']['LogicalModelWriteRequest']
export type PurposeRoleResponse = components['schemas']['PurposeRoleResponse']
export type SetPurposeRoleRequest = components['schemas']['SetPurposeRoleRequest']
export type AiPurpose = components['schemas']['AiPurpose']
export type AiConnectionDto = components['schemas']['AiConnectionDto']
export type CreateAiConnectionRequest = components['schemas']['CreateAiConnectionRequest']
export type AiVerificationResultDto = components['schemas']['AiVerificationResultDto']

/** The logical models effective for a client: its overrides plus the inherited (non-shadowed) tenant catalog. */
export async function listEffectiveForClient(clientId: string): Promise<LogicalModelResponse[]> {
  const { data } = await createAdminClient().GET('/clients/{clientId}/logical-models', {
    params: { path: { clientId } },
  })
  return (data as LogicalModelResponse[]) ?? []
}

/** The client's own override logical models (not the inherited tenant catalog). */
export async function listClientOverrides(clientId: string): Promise<LogicalModelResponse[]> {
  const { data } = await createAdminClient().GET('/clients/{clientId}/logical-models/overrides', {
    params: { path: { clientId } },
  })
  return (data as LogicalModelResponse[]) ?? []
}

/** Creates a per-client override logical model. */
export async function createClientOverride(
  clientId: string,
  request: LogicalModelWriteRequest,
): Promise<LogicalModelResponse> {
  const { data } = await createAdminClient().POST('/clients/{clientId}/logical-models/overrides', {
    params: { path: { clientId } },
    body: request,
  })
  return data as LogicalModelResponse
}

/** Updates a per-client override's mapping (connection/model/reasoning/protocol) by name. */
export async function updateClientOverride(
  clientId: string,
  name: string,
  request: LogicalModelWriteRequest,
): Promise<void> {
  await createAdminClient().PUT('/clients/{clientId}/logical-models/overrides/{name}', {
    params: { path: { clientId, name } },
    body: request,
  })
}

/** Deletes a per-client override by name. */
export async function deleteClientOverride(clientId: string, name: string): Promise<void> {
  await createAdminClient().DELETE('/clients/{clientId}/logical-models/overrides/{name}', {
    params: { path: { clientId, name } },
  })
}

/** The client's purpose → logical-model map. */
export async function listPurposeRoles(clientId: string): Promise<PurposeRoleResponse[]> {
  const { data } = await createAdminClient().GET('/clients/{clientId}/logical-models/purposes', {
    params: { path: { clientId } },
  })
  return (data as PurposeRoleResponse[]) ?? []
}

/** Maps an internal AI purpose to a logical model for the client. */
export async function setPurposeRole(clientId: string, purpose: AiPurpose, logicalModelName: string): Promise<void> {
  await createAdminClient().PUT('/clients/{clientId}/logical-models/purposes/{purpose}', {
    params: { path: { clientId, purpose } },
    body: { logicalModelName },
  })
}

/** Removes a purpose mapping (the purpose then resolves through the client's AI purpose bindings again). */
export async function removePurposeRole(clientId: string, purpose: AiPurpose): Promise<void> {
  await createAdminClient().DELETE('/clients/{clientId}/logical-models/purposes/{purpose}', {
    params: { path: { clientId, purpose } },
  })
}

// ---- Tenant catalog (tenant-administrator role) ----

/** The tenant-catalog logical models a tenant's clients inherit. */
export async function listTenantCatalog(tenantId: string): Promise<LogicalModelResponse[]> {
  const { data } = await createAdminClient().GET('/tenants/{tenantId}/logical-models', {
    params: { path: { tenantId } },
  })
  return (data as LogicalModelResponse[]) ?? []
}

/** Creates a tenant-catalog logical model. */
export async function createTenantEntry(
  tenantId: string,
  request: LogicalModelWriteRequest,
): Promise<LogicalModelResponse> {
  const { data } = await createAdminClient().POST('/tenants/{tenantId}/logical-models', {
    params: { path: { tenantId } },
    body: request,
  })
  return data as LogicalModelResponse
}

/** Updates a tenant-catalog entry's mapping (connection/model/reasoning/protocol) by name. */
export async function updateTenantEntry(
  tenantId: string,
  name: string,
  request: LogicalModelWriteRequest,
): Promise<void> {
  await createAdminClient().PUT('/tenants/{tenantId}/logical-models/{name}', {
    params: { path: { tenantId, name } },
    body: request,
  })
}

/** Renames a tenant-catalog entry. */
export async function renameTenantEntry(tenantId: string, name: string, newName: string): Promise<void> {
  await createAdminClient().POST('/tenants/{tenantId}/logical-models/{name}/rename', {
    params: { path: { tenantId, name } },
    body: { newName },
  })
}

/** Deletes a tenant-catalog entry by name. */
export async function deleteTenantEntry(tenantId: string, name: string): Promise<void> {
  await createAdminClient().DELETE('/tenants/{tenantId}/logical-models/{name}', {
    params: { path: { tenantId, name } },
  })
}

/** The tenant's own connection profiles (with their configured models), for the tenant-catalog picker. */
export async function listTenantConnections(tenantId: string): Promise<AiConnectionDto[]> {
  const { data } = await createAdminClient().GET('/tenants/{tenantId}/ai-connections', {
    params: { path: { tenantId } },
  })
  return (data as AiConnectionDto[]) ?? []
}

/** Creates a tenant-scoped connection profile. */
export async function createTenantConnection(
  tenantId: string,
  request: CreateAiConnectionRequest,
): Promise<AiConnectionDto> {
  const { data } = await createAdminClient().POST('/tenants/{tenantId}/ai-connections', {
    params: { path: { tenantId } },
    body: request,
  })
  return data as AiConnectionDto
}

/** Deletes a tenant connection profile. */
export async function deleteTenantConnection(tenantId: string, connectionId: string): Promise<void> {
  await createAdminClient().DELETE('/tenants/{tenantId}/ai-connections/{connectionId}', {
    params: { path: { tenantId, connectionId } },
  })
}

/** Verifies a tenant connection against its provider. */
export async function verifyTenantConnection(tenantId: string, connectionId: string): Promise<AiVerificationResultDto> {
  const { data } = await createAdminClient().POST('/tenants/{tenantId}/ai-connections/{connectionId}/verify', {
    params: { path: { tenantId, connectionId } },
  })
  return data as AiVerificationResultDto
}
