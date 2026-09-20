// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { adminApiBaseUrl, authedFetch, createAdminClient, getApiErrorMessage } from '@/services/api'

/**
 * Hand-written response shapes, as the other service wrappers do.
 *
 * The generated schema marks every property optional, because the API's OpenAPI document does not emit
 * `required`. This endpoint always answers with the whole object, so restating the shape here keeps the
 * optional chaining out of the view.
 */
export interface LoadedProviderAddIn {
  key: string
  label: string
  version: string
  contractVersion: string
  reachedHostPatterns: string[]
  requiredCapabilityKey: string | null
  filePath: string
  contentHash: string | null
  origin: string
}

export interface RejectedProviderAddIn {
  category: string
  reason: string
  filePath: string
  contentHash: string | null
  key: string | null
  origin: string
}

/**
 * One add-in found in the external directory that nobody has activated, so none of it has run.
 *
 * Every value here was read out of the file with none of it executed. `key`, `label`, `version`,
 * `contractVersion`, `reachedHosts` and `requiredCapability` come from the assembly attribute the add-in
 * states; they are null when it states none, which `refusal` then says.
 */
export interface AwaitingProviderAddIn {
  filePath: string
  contentHash: string | null
  key: string | null
  label: string | null
  version: string | null
  contractVersion: string | null
  reachedHosts: string[]
  requiredCapability: string | null
  assemblyName: string
  assemblyVersion: string
  refusal: string | null
  canBeActivated: boolean
}

/** One decision an administrator made to let this host run an add-in binary. */
export interface ProviderAddInActivation {
  contentHash: string
  key: string
  label: string
  version: string
  filePath: string
  activatedByDisplayName: string
  activatedAt: string
  /** Whether that add-in is one this host is running now. */
  isServing: boolean
}

export interface ProviderAddInInventory {
  loaded: LoadedProviderAddIn[]
  rejected: RejectedProviderAddIn[]
  awaiting: AwaitingProviderAddIn[]
}

function getClient() {
  return createAdminClient()
}

export async function getProviderAddInInventory(): Promise<ProviderAddInInventory> {
  const { data, error, response } = await getClient().GET('/admin/ai-provider-add-ins', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the provider add-in inventory.'))
  }

  const inventory = data as ProviderAddInInventory | undefined

  return {
    loaded: inventory?.loaded ?? [],
    rejected: inventory?.rejected ?? [],
    awaiting: inventory?.awaiting ?? [],
  }
}

/*
 * The three calls below build their own URL. They reach paths added after `openapi.json` was last regenerated,
 * so the generated client does not type them yet. They go through `authedFetch`, which is the session handling
 * the generated client gets, and they move to the generated client when the document is next regenerated.
 */

export async function listProviderAddInActivations(): Promise<ProviderAddInActivation[]> {
  const response = await authedFetch(`${adminApiBaseUrl()}/admin/ai-provider-add-ins/activations`)

  if (!response.ok) {
    throw new Error('Failed to load the add-in activations.')
  }

  return (await response.json()) as ProviderAddInActivation[]
}

/** Lets this host run the add-in with these bytes, loading it now. */
export async function activateProviderAddIn(contentHash: string): Promise<LoadedProviderAddIn> {
  const response = await authedFetch(
    `${adminApiBaseUrl()}/admin/ai-provider-add-ins/${encodeURIComponent(contentHash)}/activate`,
    { method: 'POST' },
  )

  if (!response.ok) {
    throw new Error(await refusalIn(response, 'The add-in was not activated.'))
  }

  return (await response.json()) as LoadedProviderAddIn
}

/** Withdraws an activation. The add-in stays loaded until the host restarts. */
export async function revokeProviderAddIn(contentHash: string): Promise<void> {
  const response = await authedFetch(
    `${adminApiBaseUrl()}/admin/ai-provider-add-ins/${encodeURIComponent(contentHash)}/activate`,
    { method: 'DELETE' },
  )

  if (!response.ok) {
    throw new Error('The activation was not withdrawn.')
  }
}

// The host words a refusal against the field it is about, so the message the operator needs is inside the
// problem document rather than in its title.
async function refusalIn(response: Response, fallback: string): Promise<string> {
  try {
    const problem = (await response.json()) as { errors?: Record<string, string[]>; detail?: string }
    const stated = Object.values(problem.errors ?? {}).flat()

    return stated[0] ?? problem.detail ?? fallback
  } catch {
    return fallback
  }
}
