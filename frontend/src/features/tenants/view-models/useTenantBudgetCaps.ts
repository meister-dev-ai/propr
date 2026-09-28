// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { computed, ref } from 'vue'
import { capFromInput, capToInput } from '@/features/clients/view-models/useClientDetailViewModel'
import { getTenant, updateTenant, type TenantDto } from '@/services/tenantAdminService'

export interface UseTenantBudgetCapsOptions {
  /** Overridable tenant read for tests; defaults to the live tenant endpoint. */
  loader?: (tenantId: string) => Promise<TenantDto>
  /** Overridable tenant write for tests; defaults to the live tenant patch endpoint. */
  saver?: (tenantId: string, softCapUsd: number | null, hardCapUsd: number | null) => Promise<TenantDto>
}

/**
 * Edits the tenant's monthly USD caps. The caps are patched as a whole, so a field left blank clears that cap
 * back to no limit, which is how the client caps behave.
 */
export function useTenantBudgetCaps(tenantId: string, options: UseTenantBudgetCapsOptions = {}) {
  const load = options.loader ?? getTenant
  const save =
    options.saver ??
    ((id: string, softCapUsd: number | null, hardCapUsd: number | null) =>
      updateTenant(id, { budget: { monthlySoftCapUsd: softCapUsd, monthlyHardCapUsd: hardCapUsd } }))

  const tenant = ref<TenantDto | null>(null)
  const loading = ref(false)
  const saving = ref(false)
  const error = ref('')
  const saved = ref(false)
  const editedSoftCapUsd = ref('')
  const editedHardCapUsd = ref('')
  // Two loads can be in flight at once, because the section loads on mount and again when the budgeting
  // capability arrives. Only the response of the latest load describes what the form should show.
  let loadGeneration = 0

  function apply(next: TenantDto): void {
    tenant.value = next
    editedSoftCapUsd.value = capToInput(next.budget?.monthlySoftCapUsd)
    editedHardCapUsd.value = capToInput(next.budget?.monthlyHardCapUsd)
  }

  async function loadCaps(): Promise<void> {
    const generation = ++loadGeneration
    loading.value = true
    error.value = ''
    // A load replaces what the form shows, so the success indicator of an earlier save no longer describes
    // the fields in front of the operator.
    saved.value = false
    try {
      const loaded = await load(tenantId)
      if (generation === loadGeneration) {
        apply(loaded)
      }
    } catch {
      if (generation === loadGeneration) {
        error.value = 'Failed to load the tenant budget caps. Please try again.'
      }
    } finally {
      if (generation === loadGeneration) {
        loading.value = false
      }
    }
  }

  /**
   * The cross-field rule the backend enforces, checked here so a contradictory pair is reported on the form
   * instead of after a round trip. A blank field means no limit, which cannot contradict the other cap.
   */
  const validationError = computed(() => {
    const soft = capFromInput(editedSoftCapUsd.value)
    const hard = capFromInput(editedHardCapUsd.value)
    // A field that parses to NaN or Infinity is not a cap and is reported as such. Without this the save
    // stays enabled and the request body serializes the value as null, which clears the cap the operator
    // was editing.
    if ((soft !== null && !Number.isFinite(soft)) || (hard !== null && !Number.isFinite(hard))) {
      return 'A cap must be a number in USD, or blank for no limit.'
    }
    return soft !== null && hard !== null && soft > hard
      ? 'The soft cap must not exceed the hard cap.'
      : ''
  })

  function isDirty(): boolean {
    if (tenant.value === null) {
      return false
    }
    return (
      capFromInput(editedSoftCapUsd.value) !== (tenant.value.budget?.monthlySoftCapUsd ?? null) ||
      capFromInput(editedHardCapUsd.value) !== (tenant.value.budget?.monthlyHardCapUsd ?? null)
    )
  }

  function isSaveEnabled(): boolean {
    return !saving.value && tenant.value !== null && isDirty() && validationError.value === ''
  }

  async function saveCaps(): Promise<void> {
    if (!isSaveEnabled()) {
      return
    }
    saving.value = true
    error.value = ''
    saved.value = false
    try {
      apply(await save(tenantId, capFromInput(editedSoftCapUsd.value), capFromInput(editedHardCapUsd.value)))
      saved.value = true
    } catch (caught) {
      error.value = caught instanceof Error && caught.message ? caught.message : 'Failed to save the tenant budget caps.'
    } finally {
      saving.value = false
    }
  }

  return {
    tenant,
    loading,
    saving,
    error,
    saved,
    editedSoftCapUsd,
    editedHardCapUsd,
    validationError,
    loadCaps,
    saveCaps,
    isSaveEnabled,
  }
}
