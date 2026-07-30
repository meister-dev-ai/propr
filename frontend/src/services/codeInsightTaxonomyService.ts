// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * Typed wrapper for the Code Insights finding-type taxonomy endpoints.
 *
 * The core set is read-only installation vocabulary: it is what makes numbers comparable across
 * clients, so it is served rather than edited. Custom tags are per-client and editable by a client
 * administrator. Retired tags stay in the listing so a historical finding always resolves to a name.
 */

import { createAdminClient, getApiErrorMessage } from '@/services/api'

export type CodeInsightQualityCharacteristic =
  | 'reliability'
  | 'security'
  | 'performanceEfficiency'
  | 'maintainability'

export interface CodeInsightCoreTag {
  slug: string
  displayName: string
  definition: string
  characteristic: CodeInsightQualityCharacteristic
  behaviourChanging: boolean
}

export interface CodeInsightCustomTag {
  id: string
  slug: string
  displayName: string
  definition: string
  retiredAt: string | null
  createdAt: string
  updatedAt: string
}

export interface CodeInsightTaxonomy {
  version: number
  coreTags: CodeInsightCoreTag[]
  customTags: CodeInsightCustomTag[]
}

export interface CodeInsightCustomTagWrite {
  slug: string
  displayName: string
  definition: string
}

/**
 * Loads the client's full finding-type vocabulary, retired custom tags included.
 *
 * The generated contract types both collections as nullable, so they are normalised to arrays here:
 * every consumer iterates them, and a null reaching a computed property would fail the whole render
 * rather than showing an empty list.
 */
export async function fetchTaxonomy(clientId: string): Promise<CodeInsightTaxonomy> {
  const { data, error } = await createAdminClient().GET(
    '/clients/{clientId}/code-insights/taxonomy',
    { params: { path: { clientId } } },
  )
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to load the finding-type taxonomy.'))
  }

  const taxonomy = data as unknown as Partial<CodeInsightTaxonomy>
  return {
    version: taxonomy.version ?? 0,
    coreTags: taxonomy.coreTags ?? [],
    customTags: taxonomy.customTags ?? [],
  }
}

/** Creates a custom finding type. Rejects a slug that shadows a core type or is already used. */
export async function createCustomTag(
  clientId: string,
  request: CodeInsightCustomTagWrite,
): Promise<CodeInsightCustomTag> {
  const { data, error } = await createAdminClient().POST(
    '/clients/{clientId}/code-insights/taxonomy/custom-tags',
    { params: { path: { clientId } }, body: request },
  )
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to create the custom tag.'))
  }
  return data as unknown as CodeInsightCustomTag
}

/** Updates a custom finding type. Findings already tagged with it are unaffected. */
export async function updateCustomTag(
  clientId: string,
  tagId: string,
  request: CodeInsightCustomTagWrite,
): Promise<CodeInsightCustomTag> {
  const { data, error } = await createAdminClient().PUT(
    '/clients/{clientId}/code-insights/taxonomy/custom-tags/{tagId}',
    { params: { path: { clientId, tagId } }, body: request },
  )
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to update the custom tag.'))
  }
  return data as unknown as CodeInsightCustomTag
}

/** Retires a custom finding type: no longer offered for new findings, still resolves for old ones. */
export async function retireCustomTag(
  clientId: string,
  tagId: string,
): Promise<CodeInsightCustomTag> {
  const { data, error } = await createAdminClient().POST(
    '/clients/{clientId}/code-insights/taxonomy/custom-tags/{tagId}/retire',
    { params: { path: { clientId, tagId } } },
  )
  if (error || !data) {
    throw new Error(getApiErrorMessage(error, 'Failed to retire the custom tag.'))
  }
  return data as unknown as CodeInsightCustomTag
}
