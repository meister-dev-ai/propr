<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
    <div class="client-spend-tab">
        <div class="section-card">
            <div class="section-card-header">
                <h3>Spend &amp; Budget</h3>
                <p class="section-card-subtitle">
                    USD spend against this client's monthly budget for the current period. Spend resets each month.
                </p>
            </div>
            <div class="section-card-body">
                <p v-if="!isBudgetingAvailable" class="muted">
                    {{ budgetingUpgradeMessage || 'Budgeting requires a commercial license.' }}
                </p>

                <div v-else-if="spend.loading.value" class="spend-state">
                    <ProgressOrb class="state-orb" />
                    <span>Loading spend…</span>
                </div>

                <div v-else-if="spend.error.value" class="spend-state">
                    <i class="fi fi-rr-warning spend-error-icon"></i>
                    <p class="error">{{ spend.error.value }}</p>
                    <button class="btn-slide" @click="spend.loadConsumption()">
                        <div class="sign"><i class="fi fi-rr-refresh"></i></div>
                        <span class="text">Try Again</span>
                    </button>
                </div>

                <template v-else-if="spend.consumption.value">
                    <p class="period-line">
                        Current period <strong>{{ formatDate(spend.consumption.value.periodStart) }}</strong>
                        – <strong>{{ formatDate(spend.consumption.value.periodEnd) }}</strong>
                        · resets {{ formatDate(spend.consumption.value.nextResetOn) }}
                        · as of {{ formatDate(spend.consumption.value.asOf) }}
                    </p>
                    <p v-if="spend.spendIsApproximate.value" class="approx-note">
                        <i class="fi fi-rr-info"></i>
                        Some usage this period is unpriced, so the spend shown is a lower bound.
                    </p>

                    <div class="spend-summary">
                        <div class="spend-card">
                            <span class="spend-label">Spent to date</span>
                            <span class="spend-value">{{ formatUsd(spend.spentToDateUsd.value) }}</span>
                            <i class="fi fi-rr-coins spend-icon"></i>
                        </div>
                        <div class="spend-card">
                            <span class="spend-label">Monthly soft cap</span>
                            <span class="spend-value">{{ capLabel(spend.softCapUsd.value) }}</span>
                            <i class="fi fi-rr-flag spend-icon"></i>
                        </div>
                        <div class="spend-card">
                            <span class="spend-label">Monthly hard cap</span>
                            <span class="spend-value">{{ capLabel(spend.hardCapUsd.value) }}</span>
                            <i class="fi fi-rr-octagon-exclamation spend-icon"></i>
                        </div>
                        <div class="spend-card" :class="{ 'is-forecast-over': spend.projectedToExceedHardCap.value }">
                            <span class="spend-label">Projected (period)</span>
                            <span class="spend-value">{{ formatUsd(spend.projectedPeriodSpendUsd.value) }}</span>
                            <i class="fi fi-rr-chart-line-up spend-icon"></i>
                        </div>
                    </div>

                    <div v-if="spend.hasBudget.value" class="meter-block">
                        <div class="meter" :class="`is-${spend.status.value}`">
                            <div class="meter-fill" :style="{ width: `${spend.meterPercent.value}%` }"></div>
                        </div>
                        <p class="meter-caption">
                            {{ formatUsd(spend.spentToDateUsd.value) }} of {{ formatUsd(spend.meterCapUsd.value) }}
                            ({{ Math.round(spend.meterPercent.value) }}%)
                            <template v-if="spend.remainingUsd.value !== null">
                                ·
                                <span :class="{ 'over-budget': spend.remainingUsd.value < 0 }">
                                    {{ remainingLabel }}
                                </span>
                            </template>
                        </p>
                        <p v-if="spend.projectedToExceedHardCap.value" class="warn danger">
                            <i class="fi fi-rr-exclamation"></i> Projected to exceed the hard cap this period.
                        </p>
                        <p v-else-if="spend.projectedToExceedSoftCap.value" class="warn">
                            <i class="fi fi-rr-exclamation"></i> Projected to exceed the soft cap this period.
                        </p>
                    </div>
                    <p v-else class="muted no-budget">
                        No monthly budget configured. Set caps on the <strong>Budget</strong> tab to track spend against a limit.
                    </p>

                    <div class="chart-wrap">
                        <Line :data="spend.spendChartData.value" :options="spend.spendChartOptions.value" />
                    </div>
                </template>
            </div>
        </div>
    </div>
</template>

<script lang="ts" setup>
import { computed, inject, onMounted } from 'vue'
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
import ProgressOrb from '@/components/ProgressOrb.vue'
import { formatUsd } from '@/components/usageDashboardFormatters'
import { ClientDetailVmKey } from '@/features/clients/view-models/useClientDetailViewModel'
import {
    useClientSpendConsumption,
    type SpendConsumptionLoadResult,
} from '@/features/clients/view-models/useClientSpendConsumption'

ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Title, Tooltip, Legend, Filler)

const props = defineProps<{
    /** Test seam: overrides the live budget-consumption loader. */
    loader?: (clientId: string) => Promise<SpendConsumptionLoadResult>
}>()

const vm = inject(ClientDetailVmKey)

const isBudgetingAvailable = computed(() => vm?.isBudgetingAvailable.value ?? false)
const budgetingUpgradeMessage = computed(() => vm?.budgetingUpgradeMessage.value ?? '')

const spend = useClientSpendConsumption(vm?.clientId ?? '', { loader: props.loader })

const remainingLabel = computed(() => {
    const remaining = spend.remainingUsd.value
    if (remaining === null) {
        return ''
    }
    return remaining < 0
        ? `${formatUsd(Math.abs(remaining))} over`
        : `${formatUsd(remaining)} remaining`
})

function capLabel(value: number | null | undefined): string {
    return value == null ? 'No limit' : formatUsd(value)
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
        void spend.loadConsumption()
    }
})
</script>

<style scoped>
.client-spend-tab {
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.section-card-subtitle {
    color: var(--color-text-muted);
    margin-top: 0.25rem;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
}

.period-line {
    color: var(--color-text-muted);
    font-size: 0.9rem;
    margin-bottom: 0.5rem;
}

.approx-note {
    color: var(--color-text-muted);
    font-size: 0.85rem;
    margin-bottom: 1rem;
}

.spend-state {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 1.5rem 0;
    color: var(--color-text-muted);
}

.spend-error-icon {
    color: #ef4444;
}

.spend-summary {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
    gap: 0.75rem;
    margin-bottom: 1.25rem;
}

.spend-card {
    position: relative;
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    padding: 1rem 1.1rem;
    border: 1px solid var(--color-border, rgba(148, 163, 184, 0.3));
    border-radius: 0.6rem;
    background: var(--color-surface-alt, rgba(148, 163, 184, 0.06));
}

.spend-card.is-forecast-over {
    border-color: #ef4444;
}

.spend-label {
    font-size: 0.8rem;
    color: var(--color-text-muted);
}

.spend-value {
    font-size: 1.35rem;
    font-weight: 600;
}

.spend-icon {
    position: absolute;
    top: 0.9rem;
    right: 0.9rem;
    color: var(--color-text-muted);
    opacity: 0.5;
}

.meter-block {
    margin-bottom: 1.25rem;
}

.meter {
    height: 0.7rem;
    border-radius: 999px;
    background: rgba(148, 163, 184, 0.2);
    overflow: hidden;
}

.meter-fill {
    height: 100%;
    border-radius: 999px;
    transition: width 0.3s ease;
}

.meter.is-ok .meter-fill {
    background: #22c55e;
}

.meter.is-warning .meter-fill {
    background: #f59e0b;
}

.meter.is-danger .meter-fill {
    background: #ef4444;
}

.meter-caption {
    margin-top: 0.5rem;
    font-size: 0.85rem;
    color: var(--color-text-muted);
}

.over-budget {
    color: #ef4444;
    font-weight: 600;
}

.warn {
    margin-top: 0.4rem;
    font-size: 0.85rem;
    color: #b45309;
}

.warn.danger {
    color: #ef4444;
}

.no-budget {
    margin-bottom: 1.25rem;
}

.chart-wrap {
    height: 280px;
}
</style>
