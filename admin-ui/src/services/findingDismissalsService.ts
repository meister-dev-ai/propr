// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrapper for the dismiss-finding endpoint.
 * Dismisses a finding by storing it as an admin-dismissed memory record.
 * The memory reconsideration pipeline will suppress similar future findings.
 */

import { createAdminClient } from '@/services/api'

export interface DismissFindingRequest {
  findingMessage: string
  filePath?: string | null
  label?: string | null
}

/** Dismisses a finding for the given client. Returns the created memory record. */
export async function dismissFinding(
  clientId: string,
  request: DismissFindingRequest,
): Promise<unknown> {
  const { data } = await createAdminClient().POST('/clients/{clientId}/dismiss-finding', {
    params: { path: { clientId } },
    body: request as any,
  })
  return data
}

/**
 * @deprecated Use dismissFinding() instead.
 *             Kept for backward compatibility with JobProtocolView dismiss button.
 */
export async function createDismissal(
  clientId: string,
  request: { originalMessage: string; label?: string | null },
): Promise<unknown> {
  return dismissFinding(clientId, {
    findingMessage: request.originalMessage,
    label: request.label,
  })
}
