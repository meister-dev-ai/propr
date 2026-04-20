// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type {
  ProviderConnectionReadinessLevel,
  ScmAuthenticationKind,
  ScmProviderFamily,
} from '@/services/providerConnectionsService'

export interface ProviderActivationStatusDto {
  providerFamily: ScmProviderFamily
  isEnabled: boolean
  baselineAdapterSetRegistered: boolean
  registeredCapabilities: string[]
  supportClaimReadiness: ProviderConnectionReadinessLevel
  supportClaimReason: string
  updatedAt: string
}

export interface ProviderOption {
  value: ScmProviderFamily
  label: string
}

const providerOrder: readonly ScmProviderFamily[] = ['azureDevOps', 'github', 'gitLab', 'forgejo']

function getClient() {
  return createAdminClient() as any
}

export async function listProviderActivationStatuses(): Promise<ProviderActivationStatusDto[]> {
  const { data, error, response } = await getClient().GET('/admin/providers', {})

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to load provider activation settings.'))
  }

  return sortProviderActivationStatuses((data as ProviderActivationStatusDto[]) ?? [])
}

export async function updateProviderActivationStatus(
  providerFamily: ScmProviderFamily,
  isEnabled: boolean,
): Promise<ProviderActivationStatusDto> {
  const { data, error, response } = await getClient().PATCH('/admin/providers/{provider}', {
    params: { path: { provider: providerFamily } },
    body: { isEnabled },
  })

  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, 'Failed to update provider activation setting.'))
  }

  return data as ProviderActivationStatusDto
}

export function sortProviderActivationStatuses(
  statuses: readonly ProviderActivationStatusDto[],
): ProviderActivationStatusDto[] {
  return [...statuses].sort(
    (left, right) => providerOrder.indexOf(left.providerFamily) - providerOrder.indexOf(right.providerFamily),
  )
}

export function getEnabledProviderOptions(statuses: readonly ProviderActivationStatusDto[]): ProviderOption[] {
  const enabled = new Set(
    statuses
      .filter((status) => status.isEnabled)
      .map((status) => status.providerFamily),
  )

  return providerOrder
    .filter((providerFamily) => enabled.has(providerFamily))
    .map((providerFamily) => ({ value: providerFamily, label: formatProviderFamily(providerFamily) }))
}

export function formatProviderFamily(providerFamily: ScmProviderFamily): string {
  switch (providerFamily) {
    case 'gitLab':
      return 'GitLab'
    case 'forgejo':
      return 'Forgejo'
    case 'github':
      return 'GitHub'
    default:
      return 'Azure DevOps'
  }
}

export function getProviderDefaultHostBaseUrl(providerFamily: ScmProviderFamily): string {
  switch (providerFamily) {
    case 'azureDevOps':
      return 'https://dev.azure.com'
    case 'gitLab':
      return 'https://gitlab.com'
    case 'forgejo':
      return 'https://codeberg.org'
    default:
      return 'https://github.com'
  }
}

export function getSupportedAuthenticationKind(providerFamily: ScmProviderFamily): ScmAuthenticationKind {
  return providerFamily === 'azureDevOps' ? 'oauthClientCredentials' : 'personalAccessToken'
}
