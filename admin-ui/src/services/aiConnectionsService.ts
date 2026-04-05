// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Typed wrapper functions for the AI connections endpoints.
 * All functions use the shared admin client from api.ts.
 */

import { createAdminClient } from '@/services/api'
import type { components } from '@/services/generated/openapi'

export type AiConnectionDto = components['schemas']['AiConnectionDto']
export type AiConnectionModelCapabilityDto = components['schemas']['AiConnectionModelCapabilityDto']
export type AiConnectionModelCapabilityRequest = components['schemas']['AiConnectionModelCapabilityRequest']
export type CreateAiConnectionRequest = components['schemas']['CreateAiConnectionRequest']
export type UpdateAiConnectionRequest = components['schemas']['UpdateAiConnectionRequest']
export type ActivateAiConnectionRequest = components['schemas']['ActivateAiConnectionRequest']

function getErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object') {
    const apiError = error as {
      error?: string
      detail?: string
      title?: string
      errors?: Record<string, string[]>
    }

    if (typeof apiError.error === 'string' && apiError.error) {
      return apiError.error
    }

    if (typeof apiError.detail === 'string' && apiError.detail) {
      return apiError.detail
    }

    if (typeof apiError.title === 'string' && apiError.title) {
      return apiError.title
    }

    if (apiError.errors && typeof apiError.errors === 'object') {
      const firstError = Object.values(apiError.errors).flat()[0]
      if (firstError) {
        return firstError
      }
    }
  }

  return fallback
}

/** Lists all AI connections for the given client. */
export async function listAiConnections(clientId: string): Promise<AiConnectionDto[]> {
  const { data, error, response } = await createAdminClient().GET('/clients/{clientId}/ai-connections', {
    params: { path: { clientId } },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to load AI connections.'))
  }

  return (data as AiConnectionDto[]) ?? []
}

/** Creates a new AI connection for the given client. Returns the new connection DTO. */
export async function createAiConnection(
  clientId: string,
  request: CreateAiConnectionRequest,
): Promise<AiConnectionDto> {
  const { data, error, response } = await createAdminClient().POST('/clients/{clientId}/ai-connections', {
    params: { path: { clientId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to create AI connection.'))
  }

  return data as AiConnectionDto
}

/** Partially updates an AI connection. Returns the updated connection DTO. */
export async function updateAiConnection(
  clientId: string,
  connectionId: string,
  request: UpdateAiConnectionRequest,
): Promise<AiConnectionDto> {
  const { data, error, response } = await createAdminClient().PATCH(
    '/clients/{clientId}/ai-connections/{connectionId}',
    {
      params: { path: { clientId, connectionId } },
      body: request,
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to update AI connection.'))
  }

  return data as AiConnectionDto
}

/** Deletes an AI connection. */
export async function deleteAiConnection(clientId: string, connectionId: string): Promise<void> {
  const { error, response } = await createAdminClient().DELETE('/clients/{clientId}/ai-connections/{connectionId}', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to delete AI connection.'))
  }
}

/** Activates an AI connection with the given model. Returns the updated connection DTO. */
export async function activateAiConnection(
  clientId: string,
  connectionId: string,
  model: string,
): Promise<AiConnectionDto> {
  const { data, error, response } = await createAdminClient().POST(
    '/clients/{clientId}/ai-connections/{connectionId}/activate',
    {
      params: { path: { clientId, connectionId } },
      body: { model },
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to activate AI connection.'))
  }

  return data as AiConnectionDto
}

/** Deactivates an AI connection. Returns the updated connection DTO. */
export async function deactivateAiConnection(
  clientId: string,
  connectionId: string,
): Promise<AiConnectionDto> {
  const { data, error, response } = await createAdminClient().POST(
    '/clients/{clientId}/ai-connections/{connectionId}/deactivate',
    {
      params: { path: { clientId, connectionId } },
    },
  )

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to deactivate AI connection.'))
  }

  return data as AiConnectionDto
}
