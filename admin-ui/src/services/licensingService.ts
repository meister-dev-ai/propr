// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/services/generated/openapi'
import {
  normalizeCapability,
  type InstallationEdition,
  type PremiumCapability,
  type PremiumCapabilityOverrideState,
} from '@/services/licensingShared'

type LicensingSummaryDto = components['schemas']['LicensingSummaryDto']
type PatchAdminLicensingRequest = components['schemas']['PatchAdminLicensingRequest']

export { getAuthOptions } from '@/services/authOptionsService'
export type { AuthOptions } from '@/services/authOptionsService'
export type {
  InstallationEdition,
  PremiumCapability,
  PremiumCapabilityOverrideState,
} from '@/services/licensingShared'

export interface LicensingSummary {
  edition: InstallationEdition
  activatedAt: string | null
  capabilities: PremiumCapability[]
}

export interface CapabilityOverrideInput {
  key: string
  overrideState: PremiumCapabilityOverrideState
}

export interface UpdateLicensingRequest {
  edition: InstallationEdition
  capabilityOverrides?: CapabilityOverrideInput[]
}

function getClient() {
  return createAdminClient() as any
}

function normalizeLicensingSummary(summary: LicensingSummaryDto | null | undefined): LicensingSummary {
  return {
    edition: summary?.edition ?? 'community',
    activatedAt: summary?.activatedAt ?? null,
    capabilities: (summary?.capabilities ?? []).map((capability) => normalizeCapability(capability)),
  }
}

export async function getLicensingSummary(): Promise<LicensingSummary> {
  const { data, error, response } = await getClient().GET('/admin/licensing', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load licensing settings.'))
  }

  return normalizeLicensingSummary((data as LicensingSummaryDto | undefined) ?? null)
}

export async function updateLicensing(request: UpdateLicensingRequest): Promise<LicensingSummary> {
  const body: PatchAdminLicensingRequest = {
    edition: request.edition,
    capabilityOverrides: request.capabilityOverrides ?? [],
  }

  const { data, error, response } = await getClient().PATCH('/admin/licensing', {
    body,
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to update licensing settings.'))
  }

  return normalizeLicensingSummary((data as LicensingSummaryDto | undefined) ?? null)
}
