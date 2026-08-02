<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
    <div class="tenant-budget-section">
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
import { computed, onMounted, ref } from 'vue'
import { useRoute } from 'vue-router'
import ConfirmDialog from '@/components/dialogs/ConfirmDialog.vue'
import { formatUsd } from '@/components/usageDashboardFormatters'
import { useSession } from '@/composables/useSession'
import BudgetMeter from '@/features/clients/components/BudgetMeter.vue'
import {
    useTenantBudgetOverview,
    type OverviewRow,
} from '@/features/tenants/view-models/useTenantBudgetOverview'

const route = useRoute()
const tenantId = String(route.params.tenantId ?? '')

const { getCapability, isCapabilityAvailable } = useSession()
const isBudgetingAvailable = computed(() => isCapabilityAvailable('budgeting'))
const budgetingUpgradeMessage = computed(() => getCapability('budgeting')?.message ?? '')

const vm = useTenantBudgetOverview(tenantId)

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

onMounted(() => {
    if (isBudgetingAvailable.value) {
        vm.loadOverview().catch(console.error)
    }
})
</script>

<style scoped>
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
