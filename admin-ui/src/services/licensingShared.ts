// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { components } from '@/services/generated/openapi'

type PremiumCapabilityDto = components['schemas']['PremiumCapabilityDto']

export type InstallationEdition = components['schemas']['InstallationEdition']
export type PremiumCapabilityOverrideState = components['schemas']['PremiumCapabilityOverrideState']

export interface PremiumCapability {
  key: string
  displayName: string
  requiresCommercial: boolean
  defaultWhenCommercial: boolean
  overrideState: PremiumCapabilityOverrideState
  isAvailable: boolean
  message: string | null
}

export function normalizeCapability(capability: PremiumCapabilityDto | null | undefined): PremiumCapability {
  return {
    key: capability?.key ?? '',
    displayName: capability?.displayName ?? capability?.key ?? 'Capability',
    requiresCommercial: capability?.requiresCommercial ?? false,
    defaultWhenCommercial: capability?.defaultWhenCommercial ?? false,
    overrideState: capability?.overrideState ?? 'default',
    isAvailable: capability?.isAvailable ?? false,
    message: capability?.message ?? null,
  }
}
