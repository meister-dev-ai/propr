// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import type { components } from '@/types'
import {
  normalizeCapability,
  type InstallationEdition,
  type PremiumCapability,
} from '@/services/licensingShared'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import type { RuntimeMode } from '@/app/runtime/createRuntime'

type AuthOptionsDto = components['schemas']['AuthOptionsDto']

export interface AuthOptions {
  edition: InstallationEdition
  availableSignInMethods: string[]
  capabilities: PremiumCapability[]
  publicBaseUrl: string | null
}

export function supportsTenantSignIn(options: AuthOptions | null | undefined): boolean {
  if (options?.edition !== 'commercial' || !options?.availableSignInMethods.includes('sso')) {
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

export interface AuthOptionsService {
  runtimeMode: RuntimeMode
  getAuthOptions: () => Promise<AuthOptions>
}

async function fetchAuthOptions(): Promise<AuthOptions> {
  const response = await fetch(getActiveRuntime().apiBaseUrl + '/auth/options')
  if (!response.ok) {
    throw new Error('Failed to load authentication options.')
  }

  const data = (await response.json()) as AuthOptionsDto
  return normalizeAuthOptions(data)
}

const liveAuthOptionsService: AuthOptionsService = {
  runtimeMode: 'live',
  getAuthOptions: fetchAuthOptions,
}

const mockAuthOptionsService: AuthOptionsService = {
  runtimeMode: 'mock',
  getAuthOptions: fetchAuthOptions,
}

export function resolveAuthOptionsService(): AuthOptionsService {
  return getActiveRuntime().mode === 'mock'
    ? mockAuthOptionsService
    : liveAuthOptionsService
}

export async function getAuthOptions(): Promise<AuthOptions> {
  return resolveAuthOptionsService().getAuthOptions()
}
