// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { tenantApiRequest } from '@/services/tenantApiClient'
import type { AiProviderKind } from '@/services/aiConnectionsService'

/**
 * Whether a tenant's reviews capture the model's reasoning into the trace. `installationDefault` states no
 * policy and leaves the installation switch in charge.
 */
export type ReasoningCapturePolicy = 'installationDefault' | 'enabled' | 'disabled'

/** A tenant's per-file byte limits; a null value leaves the installation value in force. */
export interface TenantReviewLimits {
  maxFileSizeBytes?: number | null
  maxStructuralParseBytes?: number | null
}

/**
 * The per-file byte limits a patch writes. The backend stores the group as a whole, so both properties are
 * required here and one sent as null puts the installation value back.
 */
export interface TenantReviewLimitsUpdate {
  maxFileSizeBytes: number | null
  maxStructuralParseBytes: number | null
}

/** A tenant's monthly USD caps; a null cap means no tenant-level limit. */
export interface TenantBudgetConfig {
  monthlySoftCapUsd?: number | null
  monthlyHardCapUsd?: number | null
}

/**
 * The monthly USD caps a patch writes. The backend stores the group as a whole, so both properties are required
 * here and a cap sent as null clears it. A partial object would leave the omitted cap's fate undefined.
 */
export interface TenantBudgetConfigUpdate {
  monthlySoftCapUsd: number | null
  monthlyHardCapUsd: number | null
}

export interface TenantDto {
  id: string
  slug: string
  displayName: string
  isActive: boolean
  localLoginEnabled: boolean
  isEditable: boolean
  createdAt: string
  updatedAt: string
  /** Provider families this tenant's clients may use; empty or absent means unrestricted. */
  allowedAiProviderKinds?: AiProviderKind[]
  /** Endpoint hosts this tenant's clients may reach; empty or absent means unrestricted. */
  allowedAiEndpointHosts?: string[]
  /**
   * Allow-list entries no installed provider claims. They restrict like any other entry, and they are reported
   * apart from `allowedAiProviderKinds`, which carries only the entries a loaded family claims.
   */
  unresolvedAiProviderKinds?: string[]
  /** Whether this tenant's reviews capture the model's reasoning into the trace. */
  reasoningCapturePolicy?: ReasoningCapturePolicy
  /** The current value of the installation-wide reasoning-capture switch, so the default can be named. */
  installationDefaultCapturesReasoning?: boolean
  /** The tenant's monthly USD caps. Both values are absent until a tenant administrator sets them. */
  budget?: TenantBudgetConfig
  /** Per-file byte limits for this tenant's reviews; an absent value leaves the installation value in force. */
  reviewLimits?: TenantReviewLimits
}

export interface CreateTenantRequest {
  slug: string
  displayName: string
}

export interface UpdateTenantRequest {
  displayName?: string
  isActive?: boolean
  localLoginEnabled?: boolean
  /** Provider families to permit; an empty array clears the restriction rather than forbidding everything. */
  allowedAiProviderKinds?: AiProviderKind[]
  /** Endpoint hosts to permit; an empty array clears the restriction rather than forbidding everything. */
  allowedAiEndpointHosts?: string[]
  /**
   * Entries from `unresolvedAiProviderKinds` to remove. The server refuses an `allowedAiProviderKinds` entry no
   * installed provider claims, so naming one here is how it leaves the policy; anything not named survives the
   * save.
   */
  removedUnresolvedAiProviderKinds?: string[]
  /**
   * The reasoning-capture policy to store. `installationDefault` clears the tenant's override and hands the
   * decision back to the installation switch.
   */
  reasoningCapturePolicy?: ReasoningCapturePolicy
  /**
   * The monthly USD caps to store. Both caps are written from this value, so a cap sent as null clears it;
   * omitting `budget` leaves both unchanged.
   */
  budget?: TenantBudgetConfigUpdate
  /**
   * The per-file byte limits to store. Both are written from this value, so one sent as null puts the
   * installation value back; omitting `reviewLimits` leaves both unchanged.
   */
  reviewLimits?: TenantReviewLimitsUpdate
}

function buildTenantsPath(): string {
  return '/admin/tenants'
}

function buildTenantPath(tenantId: string): string {
  return `${buildTenantsPath()}/${encodeURIComponent(tenantId)}`
}

export async function listTenants(): Promise<TenantDto[]> {
  return tenantApiRequest<TenantDto[]>(buildTenantsPath(), {
    requireAuth: true,
  })
}

export async function createTenant(request: CreateTenantRequest): Promise<TenantDto> {
  return tenantApiRequest<TenantDto>(buildTenantsPath(), {
    method: 'POST',
    requireAuth: true,
    body: JSON.stringify(request),
  })
}

export async function getTenant(tenantId: string): Promise<TenantDto> {
  return tenantApiRequest<TenantDto>(buildTenantPath(tenantId), {
    requireAuth: true,
  })
}

export async function updateTenant(tenantId: string, request: UpdateTenantRequest): Promise<TenantDto> {
  return tenantApiRequest<TenantDto>(buildTenantPath(tenantId), {
    method: 'PATCH',
    requireAuth: true,
    body: JSON.stringify(request),
  })
}
