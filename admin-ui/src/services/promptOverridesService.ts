// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrapper functions for the prompt-overrides endpoints.
 * All functions use the shared admin client from api.ts.
 */

import { createAdminClient } from '@/services/api'
import type {
  CreatePromptOverrideRequest,
  PromptOverrideDto,
  UpdatePromptOverrideRequest,
} from '@/types'

/** Lists all prompt overrides for the given client. */
export async function listOverrides(clientId: string): Promise<PromptOverrideDto[]> {
  const { data } = await createAdminClient().GET('/clients/{clientId}/prompt-overrides', {
    params: { path: { clientId } },
  })
  return (data as PromptOverrideDto[]) ?? []
}

/** Creates a new prompt override for the given client. Returns the new override DTO. */
export async function createOverride(
  clientId: string,
  request: CreatePromptOverrideRequest,
): Promise<PromptOverrideDto> {
  const { data } = await createAdminClient().POST('/clients/{clientId}/prompt-overrides', {
    params: { path: { clientId } },
    body: request,
  })
  return data as PromptOverrideDto
}

/** Updates the override text of an existing prompt override. Returns the updated DTO. */
export async function updateOverride(
  clientId: string,
  id: string,
  request: UpdatePromptOverrideRequest,
): Promise<PromptOverrideDto> {
  const { data } = await createAdminClient().PUT('/clients/{clientId}/prompt-overrides/{id}', {
    params: { path: { clientId, id } },
    body: request,
  })
  return data as PromptOverrideDto
}

/** Deletes a prompt override by ID. */
export async function deleteOverride(clientId: string, id: string): Promise<void> {
  await createAdminClient().DELETE('/clients/{clientId}/prompt-overrides/{id}', {
    params: { path: { clientId, id } },
  })
}
