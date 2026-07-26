<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
    <div class="tenant-spend-section">
        <section class="section-card">
            <div class="section-card-header">
                <div>
                    <h2>Spend</h2>
                    <p class="section-subtitle">
                        Aggregate USD spend across every client in this tenant for the current period, with a
                        trailing per-month trend. Spend resets each month.
                    </p>
                </div>
            </div>

            <div class="section-card-body">
                <p v-if="!isBudgetingAvailable" class="muted-hint">
                    {{ budgetingUpgradeMessage || 'Budgeting requires a commercial license.' }}
                </p>

                <p v-else-if="vm.loading.value" class="muted-hint">Loading tenant spend…</p>

                <div v-else-if="vm.error.value">
                    <p class="error">{{ vm.error.value }}</p>
                    <button class="btn-secondary btn-sm" @click="vm.loadSpend()">Try again</button>
                </div>

                <template v-else-if="vm.spend.value">
                    <p class="period-line">
                        Period {{ formatDate(vm.spend.value.periodStart) }} – {{ formatDate(vm.spend.value.periodEnd) }}
                        · as of {{ formatDate(vm.spend.value.asOf) }}
                        <span v-if="vm.hasResets.value" class="reset-chip" data-testid="reset-marker">
                            <i class="fi fi-rr-refresh"></i>
                            Reset ×{{ vm.resetCount.value }}
                        </span>
                    </p>
                    <p v-if="vm.hasResets.value" class="reset-note" data-testid="reset-note">
                        <i class="fi fi-rr-info"></i>
                        This period was manually reset {{ vm.resetCount.value }}
                        {{ vm.resetCount.value === 1 ? 'time' : 'times' }}<template
                            v-if="lastResetLabel">, most recently {{ lastResetLabel }}</template>;
                        the summed caps shown include the granted allowance.
                        Reset a client from its Spend tab or the Budget Overview.
                    </p>

                    <div class="spend-summary">
                        <div class="spend-card">
                            <span class="spend-label">Spent to date</span>
                            <span class="spend-value">{{ formatUsd(vm.spentToDateUsd.value) }}</span>
                        </div>
                        <div class="spend-card">
                            <span class="spend-label">Summed soft cap</span>
                            <span class="spend-value">{{ capLabel(vm.softCapUsd.value) }}</span>
                        </div>
                        <div class="spend-card">
                            <span class="spend-label">Summed hard cap</span>
                            <span class="spend-value">{{ capLabel(vm.hardCapUsd.value) }}</span>
                        </div>
                        <div class="spend-card" :class="{ 'is-forecast-over': vm.projectedToExceedHardCap.value }">
                            <span class="spend-label">Projected (period)</span>
                            <span class="spend-value">{{ formatUsd(vm.projectedPeriodSpendUsd.value) }}</span>
                        </div>
                    </div>

                    <div v-if="vm.hasBudget.value" class="meter-block">
                        <BudgetMeter :percent="vm.meterPercent.value" :status="vm.status.value" />
                        <p class="meter-caption">
                            {{ formatUsd(vm.spentToDateUsd.value) }} of {{ formatUsd(vm.meterCapUsd.value) }}
                            ({{ Math.round(vm.meterPercent.value) }}%)
                            <template v-if="vm.remainingUsd.value !== null">
                                ·
                                <span :class="{ 'over-budget': vm.remainingUsd.value < 0 }">{{ remainingLabel }}</span>
                            </template>
                        </p>
                        <p v-if="vm.projectedToExceedHardCap.value" class="warn danger">
                            Projected to exceed the summed hard cap this period.
                        </p>
                        <p v-else-if="vm.projectedToExceedSoftCap.value" class="warn">
                            Projected to exceed the summed soft cap this period.
                        </p>
                    </div>
                    <p v-else class="muted-hint no-budget">
                        No client in this tenant has a monthly budget configured. Caps are summed across clients once set.
                    </p>

                    <div class="chart-wrap">
                        <Line :data="vm.trendChartData.value" :options="vm.chartOptions.value" />
                    </div>
                    <p class="history-note">The latest point is the current month to date; earlier months are complete.</p>
                </template>
            </div>
        </section>
    </div>
</template>

<script lang="ts" setup>
import { computed, onMounted } from 'vue'
import { useRoute } from 'vue-router'
import { Line } from 'vue-chartjs'
import {
    CategoryScale,
    Chart as ChartJS,
    Filler,
    Legend,
    LinearScale,
    LineElement,
    PointElement,
    Title,
    Tooltip,
} from 'chart.js'
import { formatUsd } from '@/components/usageDashboardFormatters'
import { useSession } from '@/composables/useSession'
import BudgetMeter from '@/features/clients/components/BudgetMeter.vue'
import { useTenantBudgetSpend } from '@/features/tenants/view-models/useTenantBudgetSpend'

ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Title, Tooltip, Legend, Filler)

const route = useRoute()
const tenantId = String(route.params.tenantId ?? '')

const { getCapability, isCapabilityAvailable } = useSession()
const isBudgetingAvailable = computed(() => isCapabilityAvailable('budgeting'))
const budgetingUpgradeMessage = computed(() => getCapability('budgeting')?.message ?? '')

const vm = useTenantBudgetSpend(tenantId)

const remainingLabel = computed(() => {
    const remaining = vm.remainingUsd.value
    if (remaining === null) {
        return ''
    }
    return remaining < 0 ? `${formatUsd(Math.abs(remaining))} over` : `${formatUsd(remaining)} remaining`
})

function capLabel(value: number | null | undefined): string {
    return value == null ? 'No limit' : formatUsd(value)
}

/** The most recent reset in the period, stamped in UTC so the audit trail reads the same everywhere. */
const lastResetLabel = computed(() => {
    const value = vm.lastResetAt.value
    if (!value) {
        return ''
    }
    const date = new Date(value)
    return Number.isNaN(date.valueOf())
        ? ''
        : `${date.toLocaleString(undefined, {
            year: 'numeric',
            month: 'short',
            day: 'numeric',
            hour: '2-digit',
            minute: '2-digit',
            timeZone: 'UTC',
        })} UTC`
})

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
        void vm.loadSpend()
    }
})
</script>

<style scoped>
.reset-chip {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
    margin-left: 0.5rem;
    padding: 0.15rem 0.6rem;
    border: 1px solid var(--color-warning);
    border-radius: var(--radius-pill);
    color: var(--color-warning);
    font-size: 0.75rem;
    white-space: nowrap;
}

.reset-note {
    margin-bottom: 1rem;
    color: var(--color-text-muted);
    font-size: 0.85rem;
}

.period-line {
    color: var(--color-text-muted);
    font-size: 0.9rem;
    margin-bottom: 1rem;
}

.spend-summary {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    gap: 0.75rem;
    margin-bottom: 1.25rem;
}

.spend-card {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    padding: 1rem 1.1rem;
    border: 1px solid var(--color-border);
    border-radius: 0.6rem;
    background: var(--color-muted-soft);
}

.spend-card.is-forecast-over {
    border-color: var(--color-danger);
}

.spend-label {
    font-size: 0.8rem;
    color: var(--color-text-muted);
}

.spend-value {
    font-size: 1.35rem;
    font-weight: 600;
}

.meter-block {
    margin-bottom: 1.25rem;
}

.meter-caption {
    margin-top: 0.5rem;
    font-size: 0.85rem;
    color: var(--color-text-muted);
}

.over-budget {
    color: var(--color-danger);
    font-weight: 600;
}

.warn {
    margin-top: 0.4rem;
    font-size: 0.85rem;
    color: var(--color-warning);
}

.warn.danger {
    color: var(--color-danger);
}

.no-budget {
    margin-bottom: 1.25rem;
}

.chart-wrap {
    height: 300px;
}

.history-note {
    margin-top: 0.5rem;
    font-size: 0.8rem;
    color: var(--color-text-muted);
}

.muted-hint {
    color: var(--color-text-muted);
    font-style: italic;
}
</style>
