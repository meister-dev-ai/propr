/**
 * Typed wrapper functions for the finding dismissals endpoints.
 * All functions use the shared admin client from api.ts.
 */

import { createAdminClient } from '@/services/api'
import type {
  CreateFindingDismissalRequest,
  FindingDismissalDto,
  UpdateFindingDismissalRequest,
} from '@/types'

/** Lists all finding dismissals for the given client. */
export async function listDismissals(clientId: string): Promise<FindingDismissalDto[]> {
  const { data } = await createAdminClient().GET('/clients/{clientId}/finding-dismissals', {
    params: { path: { clientId } },
  })
  return (data as FindingDismissalDto[]) ?? []
}

/** Creates a new finding dismissal for the given client. Returns the new dismissal DTO. */
export async function createDismissal(
  clientId: string,
  request: CreateFindingDismissalRequest,
): Promise<FindingDismissalDto> {
  const { data } = await createAdminClient().POST('/clients/{clientId}/finding-dismissals', {
    params: { path: { clientId } },
    body: request,
  })
  return data as FindingDismissalDto
}

/** Updates the label of an existing finding dismissal. Returns the updated dismissal DTO. */
export async function updateDismissal(
  clientId: string,
  id: string,
  request: UpdateFindingDismissalRequest,
): Promise<FindingDismissalDto> {
  const { data } = await createAdminClient().PATCH(
    '/clients/{clientId}/finding-dismissals/{id}',
    {
      params: { path: { clientId, id } },
      body: request,
    },
  )
  return data as FindingDismissalDto
}

/** Deletes a finding dismissal. */
export async function deleteDismissal(clientId: string, id: string): Promise<void> {
  await createAdminClient().DELETE('/clients/{clientId}/finding-dismissals/{id}', {
    params: { path: { clientId, id } },
  })
}
