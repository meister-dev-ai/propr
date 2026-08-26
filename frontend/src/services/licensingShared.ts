// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

import type { components } from '@/types'

type PremiumCapabilityDto = components['schemas']['PremiumCapabilityDto']

export type InstallationEdition = components['schemas']['InstallationEdition']
export type PremiumCapabilityOverrideState = components['schemas']['PremiumCapabilityOverrideState']
export type PremiumCapabilityUnavailableReason = components['schemas']['PremiumCapabilityUnavailableReason']

export interface PremiumCapability {
  key: string
  displayName: string
  requiresCommercial: boolean
  overrideState: PremiumCapabilityOverrideState
  isAvailable: boolean
  message: string | null
  /** Why the capability is unavailable. Null while it is available, and null on a backend that does not send it. */
  reason: PremiumCapabilityUnavailableReason | null
}

export function normalizeCapability(capability: PremiumCapabilityDto | null | undefined): PremiumCapability {
  return {
    key: capability?.key ?? '',
    displayName: capability?.displayName ?? capability?.key ?? 'Capability',
    requiresCommercial: capability?.requiresCommercial ?? false,
    overrideState: capability?.overrideState ?? 'default',
    isAvailable: capability?.isAvailable ?? false,
    message: capability?.message ?? null,
    reason: capability?.reason ?? null,
  }
}
