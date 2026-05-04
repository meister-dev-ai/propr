// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { createAdminClient, getApiErrorMessage } from '@/services/api'
import type { components } from '@/services/generated/openapi'

export type WebhookProviderType = components['schemas']['WebhookProviderType']
export type WebhookEventType = components['schemas']['WebhookEventType']
export type WebhookDeliveryOutcome = 'accepted' | 'ignored' | 'rejected' | 'failed'

export type WebhookRepoFilterRequest = components['schemas']['WebhookRepoFilterRequest']

export type CreateWebhookConfigurationRequest = components['schemas']['CreateAdminWebhookConfigRequest']

export type UpdateWebhookConfigurationRequest = components['schemas']['PatchAdminWebhookConfigRequest']

export type WebhookRepoFilterResponse = components['schemas']['WebhookRepoFilterResponse']

export type WebhookConfigurationResponse = components['schemas']['WebhookConfigurationResponse']

export interface WebhookDeliveryLogEntryResponse {
  id: string
  webhookConfigurationId: string
  receivedAt: string
  eventType: string
  deliveryOutcome: WebhookDeliveryOutcome
  httpStatusCode: number
  repositoryId?: string | null
  pullRequestId?: number | null
  sourceBranch?: string | null
  targetBranch?: string | null
  actionSummaries: string[]
  failureReason?: string | null
  failureCategory?: string | null
}

export interface WebhookDeliveryHistoryResponse {
  items: WebhookDeliveryLogEntryResponse[]
}

function ensureOk(response: Response, error: unknown, fallback: string): void {
  if (!response.ok) {
    throw new Error(getApiErrorMessage(error, fallback))
  }
}

export async function listWebhookConfigurations(): Promise<WebhookConfigurationResponse[]> {
  const { data, error, response } = await createAdminClient().GET('/admin/webhook-configurations', {})
  ensureOk(response, error, 'Failed to load webhook configurations.')
  return (data as WebhookConfigurationResponse[]) ?? []
}

export async function createWebhookConfiguration(
  clientId: string,
  request: CreateWebhookConfigurationRequest,
): Promise<WebhookConfigurationResponse> {
  const { data, error, response } = await createAdminClient().POST('/admin/webhook-configurations', {
    body: {
      ...request,
      clientId,
    },
  })

  ensureOk(response, error, 'Failed to create webhook configuration.')
  return data as WebhookConfigurationResponse
}

export async function updateWebhookConfiguration(
  configId: string,
  request: UpdateWebhookConfigurationRequest,
): Promise<WebhookConfigurationResponse> {
  const { data, error, response } = await createAdminClient().PATCH('/admin/webhook-configurations/{configId}', {
    params: { path: { configId } },
    body: request,
  })

  ensureOk(response, error, 'Failed to update webhook configuration.')
  return data as WebhookConfigurationResponse
}

export async function deleteWebhookConfiguration(configId: string): Promise<void> {
  const { error, response } = await createAdminClient().DELETE('/admin/webhook-configurations/{configId}', {
    params: { path: { configId } },
  })

  ensureOk(response, error, 'Failed to delete webhook configuration.')
}

export async function listWebhookDeliveries(
  configId: string,
  take = 50,
): Promise<WebhookDeliveryHistoryResponse> {
  const { data, error, response } = await createAdminClient().GET('/admin/webhook-configurations/{configId}/deliveries', {
    params: {
      path: { configId },
      query: { take },
    },
  })

  ensureOk(response, error, 'Failed to load webhook delivery history.')
  return (data as WebhookDeliveryHistoryResponse) ?? { items: [] }
}
