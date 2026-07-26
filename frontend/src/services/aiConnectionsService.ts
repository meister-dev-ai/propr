// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient } from '@/services/api'
import type { components } from '@/types'

export type AiConnectionDto = components['schemas']['AiConnectionDto']
export type AiConfiguredModelDto = components['schemas']['AiConfiguredModelDto']
export type AiConfiguredModelRequest = components['schemas']['AiConfiguredModelRequest']
export type AiPurposeBindingDto = components['schemas']['AiPurposeBindingDto']
export type AiPurposeBindingRequest = components['schemas']['AiPurposeBindingRequest']
export type AiVerificationResultDto = components['schemas']['AiVerificationResultDto']
export type AiModelDiscoveryResultDto = components['schemas']['AiModelDiscoveryResultDto']
export type CreateAiConnectionRequest = components['schemas']['CreateAiConnectionRequest']
export type UpdateAiConnectionRequest = components['schemas']['UpdateAiConnectionRequest']
export type DiscoverModelsRequest = components['schemas']['DiscoverModelsRequest']
export type AiProviderKind = components['schemas']['AiProviderKind']
export type AiAuthMode = components['schemas']['AiAuthMode']
export type AiDiscoveryMode = components['schemas']['AiDiscoveryMode']
export type AiPurpose = components['schemas']['AiPurpose']
export type AiProtocolMode = components['schemas']['AiProtocolMode']
export type AiOperationKind = components['schemas']['AiOperationKind']
export type AiVerificationStatus = components['schemas']['AiVerificationStatus']

function readStringField(value: unknown): string | null {
  return typeof value === 'string' && value ? value : null
}

function readFirstFieldError(errors: unknown): string | null {
  if (!errors || typeof errors !== 'object') {
    return null
  }

  return Object.values(errors as Record<string, string[]>).flat()[0] ?? null
}

function getErrorMessage(error: unknown, fallback: string): string {
  if (!error || typeof error !== 'object') {
    return fallback
  }

  const apiError = error as {
    error?: string
    detail?: string
    title?: string
    errors?: Record<string, string[]>
  }

  return (
    readStringField(apiError.error) ??
    readStringField(apiError.detail) ??
    readStringField(apiError.title) ??
    readFirstFieldError(apiError.errors) ??
    fallback
  )
}

export async function listAiConnections(clientId: string): Promise<AiConnectionDto[]> {
  const { data, error, response } = await createAdminClient().GET('/clients/{clientId}/ai-connections', {
    params: { path: { clientId } },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to load AI profiles.'))
  }

  return (data as AiConnectionDto[]) ?? []
}

/** One provider family this build can call, and what this client may do with it. */
export interface PermittedProviderDescriptor {
  providerKind: AiProviderKind
  /** Whether the client's tenant permits it. */
  isPermitted: boolean
  /** The wire shapes this provider's driver can speak; the UI offers no others. */
  protocolModes: AiProtocolMode[]
}

/** What this client may configure, plus enough to explain anything it may not. */
export interface PermittedProvidersResponse {
  /** Every family this build has a driver for. A family absent from this list has no driver at all. */
  providers: PermittedProviderDescriptor[]
  /** Whether the tenant has stated a provider policy at all. */
  isRestricted: boolean
}

export async function listPermittedProviders(clientId: string): Promise<PermittedProvidersResponse> {
  const { data, error, response } = await createAdminClient().GET('/clients/{clientId}/ai-connections/permitted-providers', {
    params: { path: { clientId } },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to load the permitted providers.'))
  }

  return (data as PermittedProvidersResponse) ?? { providers: [], isRestricted: false }
}

export async function createAiConnection(clientId: string, request: CreateAiConnectionRequest): Promise<AiConnectionDto> {
  const { data, error, response } = await createAdminClient().POST('/clients/{clientId}/ai-connections', {
    params: { path: { clientId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to create AI profile.'))
  }

  return data as AiConnectionDto
}

export async function updateAiConnection(
  clientId: string,
  connectionId: string,
  request: UpdateAiConnectionRequest,
): Promise<AiConnectionDto> {
  const { data, error, response } = await createAdminClient().PATCH('/clients/{clientId}/ai-connections/{connectionId}', {
    params: { path: { clientId, connectionId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to update AI profile.'))
  }

  return data as AiConnectionDto
}

export async function deleteAiConnection(clientId: string, connectionId: string): Promise<void> {
  const { error, response } = await createAdminClient().DELETE('/clients/{clientId}/ai-connections/{connectionId}', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to delete AI profile.'))
  }
}

export async function activateAiConnection(clientId: string, connectionId: string): Promise<AiConnectionDto> {
  const { data, error, response } = await createAdminClient().POST('/clients/{clientId}/ai-connections/{connectionId}/activate', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to activate AI profile.'))
  }

  return data as AiConnectionDto
}

export async function deactivateAiConnection(clientId: string, connectionId: string): Promise<AiConnectionDto> {
  const { data, error, response } = await createAdminClient().POST('/clients/{clientId}/ai-connections/{connectionId}/deactivate', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to deactivate AI profile.'))
  }

  return data as AiConnectionDto
}

export async function verifyAiConnection(clientId: string, connectionId: string): Promise<AiVerificationResultDto> {
  const { data, error, response } = await createAdminClient().POST('/clients/{clientId}/ai-connections/{connectionId}/verify', {
    params: { path: { clientId, connectionId } },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to verify AI profile.'))
  }

  return data as AiVerificationResultDto
}

/**
 * Probes an unsaved profile. Nothing is stored, so a credential can be tested before it is committed — otherwise
 * finding out a key is wrong requires saving it first.
 */
export async function probeAiConnection(
  clientId: string,
  request: DiscoverModelsRequest,
): Promise<AiVerificationResultDto> {
  const { data, error, response } = await createAdminClient().POST('/clients/{clientId}/ai-connections/probe', {
    params: { path: { clientId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to probe the provider connection.'))
  }

  return data as AiVerificationResultDto
}

export async function discoverAiModels(
  clientId: string,
  request: DiscoverModelsRequest,
): Promise<AiModelDiscoveryResultDto> {
  const { data, error, response } = await createAdminClient().POST('/clients/{clientId}/ai-connections/discover-models', {
    params: { path: { clientId } },
    body: request,
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to discover provider models.'))
  }

  return data as AiModelDiscoveryResultDto
}
