// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { API_BASE_URL } from '@/services/apiBase'
import type { components } from '@/services/generated/openapi'
import {
  normalizeCapability,
  type InstallationEdition,
  type PremiumCapability,
} from '@/services/licensingShared'

type AuthOptionsDto = components['schemas']['AuthOptionsDto']

export interface AuthOptions {
  edition: InstallationEdition
  availableSignInMethods: string[]
  capabilities: PremiumCapability[]
  publicBaseUrl: string | null
}

export function supportsTenantSignIn(options: AuthOptions | null | undefined): boolean {
  if (!options || options.edition !== 'commercial' || !options.availableSignInMethods.includes('sso')) {
    return false
  }

  return options.capabilities.find((capability) => capability.key === 'sso-authentication')?.isAvailable === true
}

function normalizeAuthOptions(options: AuthOptionsDto | null | undefined): AuthOptions {
  return {
    edition: options?.edition ?? 'community',
    availableSignInMethods: options?.availableSignInMethods ?? ['password'],
    capabilities: (options?.capabilities ?? []).map((capability) => normalizeCapability(capability)),
    publicBaseUrl: options?.publicBaseUrl ?? null,
  }
}

export async function getAuthOptions(): Promise<AuthOptions> {
  const response = await fetch(API_BASE_URL + '/auth/options')
  if (!response.ok) {
    throw new Error('Failed to load authentication options.')
  }

  const data = (await response.json()) as AuthOptionsDto
  return normalizeAuthOptions(data)
}
