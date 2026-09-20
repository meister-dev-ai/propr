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
/**
 * A provider family's identity, as that family declares it — `meisterdev/openAi`, say. Every string the server
 * sends is one: which families an installation has is decided by the add-ins it loaded, so the console holds no
 * list of its own and offers whatever the server describes.
 */
export type AiProviderKind = string
/**
 * A authentication mode, as it is submitted and stored: the declaring family's key joined to the mode name —
 * `meisterdev/anthropic:XApiKey`, say. Every string the server sends is one, so the console holds no list of
 * its own and offers whatever the selected family describes.
 */
export type AiAuthMode = string
export type AiDiscoveryMode = components['schemas']['AiDiscoveryMode']
export type AiPurpose = components['schemas']['AiPurpose']
/**
 * A protocol mode, written the same way as a authentication mode. The two the host reserves, `Auto` and `Embeddings`,
 * belong to no family and carry no qualifier.
 */
export type AiProtocolMode = string
export type AiOperationKind = components['schemas']['AiOperationKind']
export type AiVerificationStatus = components['schemas']['AiVerificationStatus']
export type AiConnectionAvailabilityDto = components['schemas']['AiConnectionAvailabilityDto']
export type AiConnectionUnavailableReason = components['schemas']['AiConnectionUnavailableReason']
export type AiConnectionVocabularyField = components['schemas']['AiConnectionVocabularyField']

function readStringField(value: unknown): string | null {
  return typeof value === 'string' && value ? value : null
}

function readFirstFieldError(errors: unknown): string | null {
  if (!errors || typeof errors !== 'object') {
    return null
  }

  return Object.values(errors as Record<string, string[]>).flat()[0] ?? null
}

/**
 * A refusal the server keyed to the fields it is about.
 *
 * The flattened message alone cannot be attached to an input, and a form that renders fields it has never seen
 * has no other way to say which one is wrong. The keys are the server's: a declared configuration field is
 * reported as `providerSettings.<field name>`.
 */
export class ApiFieldValidationError extends Error {
  constructor(message: string, readonly fieldErrors: Record<string, string[]>) {
    super(message)
    this.name = 'ApiFieldValidationError'
  }
}

function readFieldErrors(error: unknown): Record<string, string[]> | null {
  if (!error || typeof error !== 'object') {
    return null
  }

  const errors = (error as { errors?: unknown }).errors
  return errors && typeof errors === 'object' ? (errors as Record<string, string[]>) : null
}

function toApiError(error: unknown, fallback: string): Error {
  const message = getErrorMessage(error, fallback)
  const fieldErrors = readFieldErrors(error)
  return fieldErrors ? new ApiFieldValidationError(message, fieldErrors) : new Error(message)
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

  // A field error is read before the title: a validation ProblemDetails always titles itself "One or more
  // validation errors occurred.", which tells the operator nothing, while the field entry names what is wrong.
  return (
    readStringField(apiError.error) ??
    readStringField(apiError.detail) ??
    readFirstFieldError(apiError.errors) ??
    readStringField(apiError.title) ??
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

/** One value a credential is made of, as the provider's driver declared it. */
export type ProviderCredentialField = {
  /** The name the value is sent and stored under. */
  name: string
  /** What to call the field on the form. */
  label: string
  /** Whether the input is masked. */
  isSecret: boolean
  /** Whether the profile can be saved without it. */
  isRequired: boolean
  /** Guidance shown under the input, when the label is not enough on its own. */
  hint?: string | null
}

/** The value shapes a declared configuration field can take. */
export type ProviderFieldKind = components['schemas']['ProviderFieldKind']

/** What has to hold for a declared field to be shown. */
export type AiDeclaredFieldVisibilityDto = components['schemas']['AiDeclaredFieldVisibilityDto']

/** One configuration value a provider family asks for, described so the form can render it. */
export type AiDeclaredFieldDto = components['schemas']['AiDeclaredFieldDto']

/** The current value of one read-only computed field, as of the read that returned it. */
export type AiComputedFieldDto = components['schemas']['AiComputedFieldDto']

/** One operation a provider family declares against a connection, as a console offers it. */
export type AiDeclaredActionDto = components['schemas']['AiDeclaredActionDto']

/** Which of the four answers an action gave. */
export type AiProviderActionResultKind = components['schemas']['AiProviderActionResultKind']

/** What a provider family answered a dispatch with, after the host checked it. */
export type AiProviderActionResultDto = components['schemas']['AiProviderActionResultDto']

/** One run of a declared action, as the console reads it while it is open and once it is not. */
export type AiProviderActionInvocationDto = components['schemas']['AiProviderActionInvocationDto']

/** What starting or continuing an action answered with: the run, and the result where one arrived in time. */
export type AiProviderActionDispatchDto = components['schemas']['AiProviderActionDispatchDto']

/**
 * Starts one declared action against one connection.
 *
 * The family key travels in the body rather than the path because it carries a separator. The call returns as
 * soon as the host has an answer or its wait is over, whichever comes first, so a flow that takes minutes is
 * followed by reading the run it opened.
 */
export async function dispatchProviderAction(
  connectionId: string,
  addInKey: string,
  actionId: string,
): Promise<AiProviderActionDispatchDto> {
  const { data, error, response } = await createAdminClient().POST('/ai-provider-actions/dispatch', {
    body: { connectionId, addInKey, actionId },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to start the provider action.'))
  }

  return data as AiProviderActionDispatchDto
}

/**
 * Submits the values an action asked for, continuing the run that asked rather than opening a second one.
 *
 * The values belong to the run and reach no column, and that keeps a pasted callback address carrying a
 * live authorization code out of the database.
 */
export async function submitProviderActionValues(
  invocationId: string,
  values: Record<string, string>,
): Promise<AiProviderActionDispatchDto> {
  const { data, error, response } = await createAdminClient().POST('/ai-provider-actions/{invocationId}/values', {
    params: { path: { invocationId } },
    body: { values },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to submit the values the provider action asked for.'))
  }

  return data as AiProviderActionDispatchDto
}

/** Reads where one run stands, which is how the console follows a flow that outlives the request that began it. */
export async function readProviderActionInvocation(
  invocationId: string,
): Promise<AiProviderActionInvocationDto> {
  const { data, error, response } = await createAdminClient().GET('/ai-provider-actions/{invocationId}', {
    params: { path: { invocationId } },
  })

  if (!response.ok) {
    throw new Error(getErrorMessage(error, 'Failed to read the provider action run.'))
  }

  return data as AiProviderActionInvocationDto
}

/** One protocol mode a family speaks, with what an operator sees where it is offered. */
export type AiProtocolModeOptionDto = { value: AiProtocolMode; label: string }

/**
 * One authentication mode a family authenticates with, with what an operator sees where it is offered.
 *
 * `isSuperseded` marks a shape the family still reads but no longer offers for a new connection. It is reported
 * rather than left out because a profile saved under it holds its credential in that shape, so the form needs
 * the shape's name and its fields to open that profile.
 */
export type AiAuthModeOptionDto = { value: AiAuthMode; label: string; isSuperseded?: boolean }

/** What a family says about the connection boxes the host keeps for every family. */
export type AiProviderConnectionFormDto = components['schemas']['AiProviderConnectionFormDto']

/** One provider family this build can call, and what a client or tenant may do with it. */
export interface PermittedProviderDescriptor {
  providerKind: AiProviderKind
  /** Whether the tenant permits it. */
  isPermitted: boolean
  /**
   * What an operator sees where the family is offered. It comes from the family's own declaration, which is
   * how a family installed after this build shipped is named rather than shown as a key.
   */
  label: string
  /** The protocol modes this provider's driver can speak, named; the UI offers no others. */
  protocolModes: AiProtocolModeOptionDto[]
  /** The authentication modes this provider's driver can authenticate with, named; the UI offers no others. */
  authModes: AiAuthModeOptionDto[]
  /**
   * The fields each of those authentication modes needs, keyed by mode. A credential is one key for most families
   * and several values for some, so the form renders what the selected mode declares instead of one key box.
   */
  credentialFields: Partial<Record<AiAuthMode, ProviderCredentialField[]>>
  /**
   * The configuration fields this family declares, which is how a family the frontend has never seen gets a
   * usable form. Empty for a family that declares none.
   */
  declaredFields: AiDeclaredFieldDto[]
  /**
   * What this family says about the display-name, base-URL and query-parameter boxes. Absent for a family that
   * says nothing, which leaves the form showing its family-neutral text.
   */
  connectionForm?: AiProviderConnectionFormDto | null
}

/** What a client or tenant may configure, plus enough to explain anything it may not. */
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

/**
 * The same answer for a tenant, read against the tenant's own policy.
 *
 * The tenant screens need it for the same reason the client screens do: what a family is called, which shapes
 * it offers and what its connection boxes take are the installation's answer, and a screen that kept its own
 * copy could only name the families that existed when it shipped.
 */
export async function listTenantPermittedProviders(tenantId: string): Promise<PermittedProvidersResponse> {
  const { data, error, response } = await createAdminClient().GET('/tenants/{tenantId}/ai-connections/permitted-providers', {
    params: { path: { tenantId } },
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
    throw toApiError(error, 'Failed to create AI profile.')
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
    throw toApiError(error, 'Failed to update AI profile.')
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
