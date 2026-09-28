<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
    <div class="tenant-budget-section">
        <section class="section-card">
            <div class="section-card-header">
                <div>
                    <h2>Tenant caps</h2>
                    <p class="section-subtitle">
                        Monthly USD caps on the spend of every client in this tenant. The soft cap holds new
                        reviews once the tenant's month-to-date spend reaches it; the hard cap cuts further model
                        calls. Leave a field blank for no limit. A client cap that is tighter binds first, and
                        raising a cap is how a tenant is given more room this month.
                    </p>
                </div>
            </div>

            <div class="section-card-body">
                <p v-if="!isBudgetingAvailable" class="muted-hint">
                    {{ budgetingUpgradeMessage || 'Budgeting requires a commercial license.' }}
                </p>

                <template v-else>
                    <p v-if="caps.error.value" class="error" data-testid="tenant-caps-error">{{ caps.error.value }}</p>

                    <p v-if="caps.loading.value && caps.tenant.value === null" class="muted-hint">
                        Loading tenant caps…
                    </p>

                    <!-- No tenant was loaded, so the stored caps are unknown. The edit form would show blank
                         fields, which read as "no limit", and the readout would show "No limit" for both. -->
                    <template v-else-if="caps.tenant.value === null">
                        <p class="muted-hint" data-testid="tenant-caps-unavailable">
                            The tenant caps could not be loaded, so they cannot be shown or changed.
                        </p>
                        <div class="caps-actions">
                            <button class="btn-secondary btn-sm" type="button" data-testid="tenant-caps-retry"
                                :disabled="caps.loading.value" @click="caps.loadCaps()">
                                Try again
                            </button>
                        </div>
                    </template>

                    <template v-else-if="canEditCaps">
                        <fieldset class="caps-grid" :disabled="caps.loading.value || caps.saving.value">
                            <div class="form-field">
                                <label for="tenantMonthlySoftCapUsd">Monthly soft cap (USD)</label>
                                <input id="tenantMonthlySoftCapUsd" v-model="caps.editedSoftCapUsd.value"
                                    data-testid="tenant-soft-cap" name="tenantMonthlySoftCapUsd" type="number" min="0"
                                    step="0.01" placeholder="No limit" />
                            </div>
                            <div class="form-field">
                                <label for="tenantMonthlyHardCapUsd">Monthly hard cap (USD)</label>
                                <input id="tenantMonthlyHardCapUsd" v-model="caps.editedHardCapUsd.value"
                                    data-testid="tenant-hard-cap" name="tenantMonthlyHardCapUsd" type="number" min="0"
                                    step="0.01" placeholder="No limit" />
                            </div>
                        </fieldset>

                        <p v-if="caps.validationError.value" class="error" data-testid="tenant-caps-validation">
                            {{ caps.validationError.value }}
                        </p>

                        <div class="caps-actions">
                            <button class="btn-primary btn-sm" type="button" data-testid="tenant-caps-save"
                                :disabled="!caps.isSaveEnabled()" @click="caps.saveCaps()">
                                {{ caps.saving.value ? 'Saving…' : 'Save' }}
                            </button>
                        </div>
                    </template>

                    <template v-else>
                        <p class="muted-hint" data-testid="tenant-caps-readonly">
                            {{ capsReadOnlyReason }}
                        </p>
                        <dl class="caps-readout">
                            <div>
                                <dt>Monthly soft cap (USD)</dt>
                                <dd data-testid="tenant-soft-cap-readout">{{ softCapReadout }}</dd>
                            </div>
                            <div>
                                <dt>Monthly hard cap (USD)</dt>
                                <dd data-testid="tenant-hard-cap-readout">{{ hardCapReadout }}</dd>
                            </div>
                        </dl>
                    </template>
                </template>
            </div>
        </section>

        <section class="section-card">
            <div class="section-card-header">
                <div>
                    <h2>Budget</h2>
                    <p class="section-subtitle">
                        Current-period USD spend against budget for every client in this tenant. Spend resets each month.
                    </p>
                </div>
            </div>

            <div class="section-card-body">
                <p v-if="!isBudgetingAvailable" class="muted-hint">
                    {{ budgetingUpgradeMessage || 'Budgeting requires a commercial license.' }}
                </p>

                <p v-else-if="vm.loading.value" class="muted-hint">Loading budget overview…</p>

                <div v-else-if="vm.error.value">
                    <p class="error">{{ vm.error.value }}</p>
                    <button class="btn-secondary btn-sm" @click="vm.loadOverview()">Try again</button>
                </div>

                <template v-else-if="vm.overview.value">
                    <p class="period-line">
                        Period {{ formatDate(vm.overview.value.periodStart) }} – {{ formatDate(vm.overview.value.periodEnd) }}
                        · as of {{ formatDate(vm.overview.value.asOf) }}
                    </p>

                    <div class="overview-controls">
                        <input v-model="vm.search.value" class="overview-search" type="search"
                            placeholder="Filter clients…" aria-label="Filter clients" />
                        <label class="overview-sort">
                            Sort
                            <select v-model="vm.sortBy.value">
                                <option value="spend">Spend</option>
                                <option value="utilization">Utilization</option>
                                <option value="name">Name</option>
                            </select>
                        </label>
                    </div>

                    <p v-if="vm.resetError.value" class="error">{{ vm.resetError.value }}</p>

                    <p v-if="vm.rows.value.length === 0" class="muted-hint">
                        {{ (vm.overview.value.clients?.length ?? 0) === 0 ? 'No clients in this tenant yet.' : 'No clients match your filter.' }}
                    </p>

                    <ul v-else class="overview-list">
                        <li v-for="row in vm.rows.value" :key="row.clientId" class="overview-row">
                            <RouterLink class="overview-client" :to="{ name: 'client-detail', params: { id: row.clientId }, query: { tab: 'spend' } }">
                                {{ row.displayName }}
                            </RouterLink>
                            <div class="overview-meter">
                                <BudgetMeter v-if="row.hasBudget" :percent="row.meterPercent" :status="row.status" />
                                <span v-else class="muted-hint">No budget</span>
                            </div>
                            <span class="overview-amount">
                                {{ formatUsd(row.spentToDateUsd) }}
                                <template v-if="row.meterCapUsd !== null"> / {{ formatUsd(row.meterCapUsd) }}</template>
                            </span>
                            <span class="overview-util" :class="`is-${row.status}`">
                                {{ row.hasBudget ? `${Math.round(row.meterPercent)}%` : '—' }}
                            </span>
                            <div class="overview-actions">
                                <span v-if="row.resetCount > 0" class="reset-chip" data-testid="reset-marker">
                                    <i class="fi fi-rr-refresh"></i>
                                    Reset ×{{ row.resetCount }}
                                </span>
                                <button v-if="row.hasBudget" class="btn-secondary btn-sm" type="button"
                                    data-testid="reset-spend-button"
                                    :disabled="vm.resettingClientId.value !== null"
                                    @click="askReset(row)">
                                    {{ vm.resettingClientId.value === row.clientId ? 'Resetting…' : 'Reset spend' }}
                                </button>
                            </div>
                        </li>
                    </ul>
                </template>
            </div>
        </section>

        <ConfirmDialog :open="pendingReset !== null" :message="resetConfirmMessage" @confirm="onConfirmReset"
            @cancel="pendingReset = null" />
    </div>
</template>

<script lang="ts" setup>
import { computed, onMounted, ref, watch } from 'vue'
import { useRoute } from 'vue-router'
import ConfirmDialog from '@/components/dialogs/ConfirmDialog.vue'
import { formatUsd } from '@/components/usageDashboardFormatters'
import { RoleLevel } from '@/composables/roles'
import { useSession } from '@/composables/useSession'
import BudgetMeter from '@/features/clients/components/BudgetMeter.vue'
import {
    useTenantBudgetOverview,
    type OverviewRow,
} from '@/features/tenants/view-models/useTenantBudgetOverview'
import { useTenantBudgetCaps } from '@/features/tenants/view-models/useTenantBudgetCaps'

const route = useRoute()
const tenantId = String(route.params.tenantId ?? '')

const { getCapability, hasTenantRole, isCapabilityAvailable } = useSession()
const isBudgetingAvailable = computed(() => isCapabilityAvailable('budgeting'))
const budgetingUpgradeMessage = computed(() => getCapability('budgeting')?.message ?? '')

const vm = useTenantBudgetOverview(tenantId)
const caps = useTenantBudgetCaps(tenantId)

// The patch endpoint requires the tenant-administrator role and refuses the System tenant, so a viewer who
// cannot pass either check reads the caps instead of filling in a form whose save would be rejected.
const isTenantAdministrator = computed(() => hasTenantRole(tenantId, RoleLevel.Administrator))
const isTenantEditable = computed(() => caps.tenant.value?.isEditable !== false)
const canEditCaps = computed(() => isTenantAdministrator.value && isTenantEditable.value)

const capsReadOnlyReason = computed(() =>
    isTenantAdministrator.value
        ? 'The System tenant is managed internally, so its caps cannot be changed.'
        : 'Changing the tenant caps requires the tenant administrator role.',
)

const softCapReadout = computed(() => formatCap(caps.tenant.value?.budget?.monthlySoftCapUsd))
const hardCapReadout = computed(() => formatCap(caps.tenant.value?.budget?.monthlyHardCapUsd))

function formatCap(value: number | null | undefined): string {
    return value == null ? 'No limit' : formatUsd(value)
}

const pendingReset = ref<OverviewRow | null>(null)

/**
 * The grant equals the client's configured monthly cap, which this aggregate row does not carry, so the prompt names
 * the client and the ceiling in force rather than inventing a figure.
 */
const resetConfirmMessage = computed(() => {
    const row = pendingReset.value
    if (row === null) {
        return ''
    }
    const cap = row.meterCapUsd
    return `Grant ${row.displayName} a fresh allowance equal to its configured monthly cap? `
        + (cap === null ? '' : `The effective cap rises above the current ${formatUsd(cap)}. `)
        + `Spend to date (${formatUsd(row.spentToDateUsd)}) is preserved.`
})

function askReset(row: OverviewRow): void {
    pendingReset.value = row
}

async function onConfirmReset(): Promise<void> {
    const row = pendingReset.value
    pendingReset.value = null
    if (row !== null) {
        await vm.resetClientSpend(row.clientId)
    }
}

function formatDate(value: string | null | undefined): string {
    if (!value) {
        return ''
    }
    const date = new Date(`${value}T00:00:00Z`)
    return Number.isNaN(date.valueOf())
        ? value
        : date.toLocaleDateString(undefined, { month: 'short', day: 'numeric', timeZone: 'UTC' })
}

// Both cards on this page are gated on the same capability, so they are read together wherever that gate
// opens.
function loadBudgetSections(): void {
    vm.loadOverview().catch(console.error)
    caps.loadCaps().catch(console.error)
}

onMounted(() => {
    if (isBudgetingAvailable.value) {
        loadBudgetSections()
    }
})

// The capabilities are read from /auth/me after this section mounts, so on a cold load budgeting is still
// unavailable at the mount and neither card is read. Reading both when the capability turns available shows
// the budget overview and the stored caps without a reload.
watch(isBudgetingAvailable, (available, wasAvailable) => {
    if (available && !wasAvailable) {
        loadBudgetSections()
    }
})
</script>

<style scoped>
.caps-grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 0.85rem 1rem;
    /* Reset the fieldset defaults so the wrapper keeps behaving as a plain grid. */
    border: 0;
    margin: 0;
    padding: 0;
    min-inline-size: 0;
}

.caps-readout {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 0.85rem 1rem;
    margin: 0;
}

.caps-readout dt {
    color: var(--color-text-muted);
    font-size: 0.85rem;
}

.caps-readout dd {
    margin: 0.15rem 0 0;
    font-variant-numeric: tabular-nums;
}

.caps-actions {
    display: flex;
    justify-content: flex-end;
    margin-top: 1rem;
}

.period-line {
    color: var(--color-text-muted);
    font-size: 0.9rem;
    margin-bottom: 1rem;
}

.overview-controls {
    display: flex;
    gap: 1rem;
    align-items: center;
    margin-bottom: 1rem;
}

.overview-search {
    flex: 1;
    max-width: 20rem;
    padding: 0.45rem 0.75rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: var(--color-surface-raised);
    color: var(--color-text);
}

.overview-sort {
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
    color: var(--color-text-muted);
    font-size: 0.85rem;
}

.overview-sort select {
    padding: 0.4rem 0.5rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    background: var(--color-surface-raised);
    color: var(--color-text);
}

.overview-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
}

.overview-row {
    display: grid;
    /* The actions column keeps a floor wide enough for the reset marker, so a marked row does not shift the
       meter and amount columns out of line with the rows around it. */
    grid-template-columns: minmax(8rem, 1.5fr) minmax(6rem, 2fr) minmax(7rem, auto) 3.5rem minmax(13rem, auto);
    align-items: center;
    gap: 1rem;
    padding: 0.7rem 0;
    border-bottom: 1px solid var(--color-border);
}

.overview-actions {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 0.5rem;
}

.reset-chip {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    padding: 0.15rem 0.6rem;
    border: 1px solid var(--color-warning);
    border-radius: var(--radius-pill);
    color: var(--color-warning);
    font-size: 0.75rem;
    white-space: nowrap;
}

.overview-client {
    font-weight: 600;
    color: var(--color-accent);
    text-decoration: none;
}

.overview-client:hover {
    text-decoration: underline;
}

.overview-amount {
    color: var(--color-text-muted);
    font-size: 0.9rem;
    text-align: right;
}

.overview-util {
    text-align: right;
    font-variant-numeric: tabular-nums;
}

.overview-util.is-warning {
    color: var(--color-warning);
}

.overview-util.is-danger {
    color: var(--color-danger);
    font-weight: 600;
}

.muted-hint {
    color: var(--color-text-muted);
    font-style: italic;
}
</style>
