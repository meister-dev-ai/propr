<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="usage-dashboard">
    <section class="dashboard-surface">
      <div class="panel-header">
        <div>
          <p class="panel-kicker">Reviewer Usage</p>
          <h3>Tokens over time</h3>
          <p class="panel-copy">Review-model traffic for the selected calendar window.</p>
        </div>

        <div class="review-controls">
          <div class="date-inputs-wrapper">
            <div class="date-range-group">
              <label class="label-muted" for="usage-from">From</label>
              <input id="usage-from" v-model="fromDate" class="date-input" type="date" :max="toDate" @change="markCustomRange" />
            </div>
            <span class="date-separator">—</span>
            <div class="date-range-group">
              <label class="label-muted" for="usage-to">To</label>
              <input id="usage-to" v-model="toDate" class="date-input" type="date" :min="fromDate" @change="markCustomRange" />
            </div>
          </div>

          <button class="btn-slide" :disabled="loadingReview || loadingProCursor || exportingProCursor" @click="handleRefresh">
            <div class="sign"><i class="fi fi-rr-refresh"></i></div>
            <span class="text">{{ loadingReview || loadingProCursor ? 'Refreshing' : exportingProCursor ? 'Exporting' : 'Refresh' }}</span>
          </button>
        </div>
      </div>

      <div v-if="loadingReview" class="loading-state">
        <ProgressOrb class="state-orb" />
        <span>Loading reviewer usage...</span>
      </div>

      <div v-else-if="reviewError" class="error-state">
        <i class="fi fi-rr-warning error-icon"></i>
        <p>{{ reviewError }}</p>
        <button class="btn-slide" @click="loadReviewUsage">
          <div class="sign"><i class="fi fi-rr-refresh"></i></div>
          <span class="text">Try Again</span>
        </button>
      </div>

      <template v-else>
        <div class="usage-summary">
          <div class="summary-card usage-input">
            <span class="summary-label">Input Tokens</span>
            <span class="summary-value">{{ formatNumber(totalInputTokens) }}</span>
            <i class="fi fi-rr-arrow-down-to-bracket summary-icon"></i>
          </div>

          <div class="summary-card usage-output">
            <span class="summary-label">Output Tokens</span>
            <span class="summary-value">{{ formatNumber(totalOutputTokens) }}</span>
            <i class="fi fi-rr-arrow-up-from-bracket summary-icon"></i>
          </div>

          <div class="summary-card usage-cached">
            <span class="summary-label">Cached Input Tokens</span>
            <span class="summary-value">{{ formatNumber(totalCachedInputTokens) }}</span>
            <i class="fi fi-rr-database summary-icon"></i>
          </div>

          <div class="summary-card usage-reasoning">
            <span class="summary-label">Reasoning Tokens</span>
            <span class="summary-value">{{ formatNumber(totalReasoningTokens) }}</span>
            <i class="fi fi-rr-brain summary-icon"></i>
          </div>

          <div class="summary-card usage-cost">
            <span class="summary-label">Estimated Cost</span>
            <span class="summary-value">{{ formatUsd(totalEstimatedCostUsd) }}</span>
            <i class="fi fi-rr-badge-dollar summary-icon"></i>
          </div>
        </div>

        <div v-if="!hasReviewSamples" class="empty-state empty-state--compact">
          <i class="fi fi-rr-chart-line-up empty-icon"></i>
          <h3>No reviewer usage data</h3>
          <p>No token consumption was recorded for this client in the selected date range.</p>
        </div>

        <div v-else class="chart-card">
          <div class="chart-card-header">
            <div>
              <h4>Trend</h4>
              <p>Daily token consumption by model and effort tier.</p>
            </div>
          </div>

          <div class="chart-container">
            <Line :data="reviewChartData" :options="reviewChartOptions" />
          </div>
        </div>
      </template>
    </section>

    <section class="dashboard-surface dashboard-surface--procursor">
      <div class="panel-header panel-header--procursor">
        <div>
          <p class="panel-kicker">ProCursor Usage</p>
          <h3>Knowledge indexing and retrieval</h3>
          <p class="panel-copy">Track ProCursor-owned AI calls across sources, models, and safe export rows.</p>
        </div>

        <div class="procursor-controls">
          <div class="period-pills" role="tablist" aria-label="ProCursor usage periods">
            <button
              v-for="preset in periodPresets"
              :key="preset.value"
              class="period-pill"
              :class="{ 'period-pill--active': selectedProCursorPreset === preset.value }"
              type="button"
              @click="applyProCursorPeriod(preset.value)"
            >
              {{ preset.label }}
            </button>
          </div>

          <label class="select-chip">
            <span>Granularity</span>
            <select v-model="proCursorGranularity" @change="loadProCursorUsage">
              <option value="daily">Daily</option>
              <option value="monthly">Monthly</option>
            </select>
          </label>

          <label class="select-chip">
            <span>Group by</span>
            <select v-model="proCursorGroupBy" @change="loadProCursorUsage">
              <option value="source">Source</option>
              <option value="model">Model</option>
            </select>
          </label>

          <button class="btn-slide btn-export" :disabled="exportingProCursor || loadingProCursor || !hasProCursorData" @click="handleExportProCursor">
            <div class="sign"><i class="fi fi-rr-download"></i></div>
            <span class="text">{{ exportingProCursor ? 'Exporting' : 'Export CSV' }}</span>
          </button>

        </div>
      </div>

      <div v-if="loadingProCursor" class="loading-state">
        <ProgressOrb class="state-orb" />
        <span>Loading ProCursor usage...</span>
      </div>

      <div v-else-if="proCursorError" class="error-state">
        <i class="fi fi-rr-warning error-icon"></i>
        <p>{{ proCursorError }}</p>
        <button class="btn-slide" @click="loadProCursorUsage">
          <div class="sign"><i class="fi fi-rr-refresh"></i></div>
          <span class="text">Try Again</span>
        </button>
      </div>

      <template v-else>
        <div class="usage-summary usage-summary--procursor">
          <div class="summary-card usage-total">
            <span class="summary-label">Total Tokens</span>
            <span class="summary-value">{{ formatNumber(proCursorTotalTokens) }}</span>
            <i class="fi fi-rr-layer-plus summary-icon"></i>
          </div>

          <div class="summary-card usage-cost">
            <span class="summary-label">Estimated Cost</span>
            <span class="summary-value">{{ formatUsd(proCursorEstimatedCost) }}</span>
            <i class="fi fi-rr-badge-dollar summary-icon"></i>
          </div>

          <div class="summary-card usage-estimated">
            <span class="summary-label">Estimated Events</span>
            <span class="summary-value">{{ formatNumber(proCursorEstimatedEvents) }}</span>
            <i class="fi fi-rr-calculator summary-icon"></i>
          </div>
        </div>

        <div v-if="showEstimatedBanner" class="usage-callout">
          <i class="fi fi-rr-info"></i>
          <p>{{ formatNumber(proCursorEstimatedEvents) }} events used estimated token counts because provider usage metadata was unavailable.</p>
        </div>

        <div v-if="showGapFillBanner" class="usage-callout usage-callout-subtle">
          <i class="fi fi-rr-history"></i>
          <p>Recent activity newer than the last completed rollup is merged directly from captured event rows.</p>
        </div>

        <p v-if="lastRollupCompletedLabel" class="panel-caption">Last completed rollup: {{ lastRollupCompletedLabel }}</p>

        <div v-if="!hasProCursorData" class="empty-state empty-state--procursor">
          <i class="fi fi-rr-books empty-icon"></i>
          <h3>No ProCursor usage for this period</h3>
          <p>Run indexing or source refresh work to begin collecting ProCursor token usage.</p>
        </div>

        <div v-else class="procursor-grid">
          <div class="chart-card chart-card--procursor">
            <div class="chart-card-header">
              <div>
                <h4>Trend</h4>
                <p>{{ currentPeriodLabel }} · grouped by {{ proCursorGroupBy }}</p>
              </div>
            </div>

            <div class="chart-container chart-container--procursor">
              <Line :data="proCursorChartData" :options="proCursorChartOptions" />
            </div>
          </div>

          <aside class="top-sources-card">
            <div class="top-sources-header">
              <div>
                <h4>Top Sources</h4>
                <p>Highest-consuming knowledge sources in {{ currentPeriodLabel }}.</p>
              </div>
              <span class="top-sources-meta">{{ resolvedTopSources.length }} ranked</span>
            </div>

            <ol class="top-sources-list">
              <li v-for="(item, index) in resolvedTopSources" :key="`${item.sourceId ?? item.sourceDisplayName ?? 'unknown'}-${index}`" class="top-source-row">
                <div class="top-source-main">
                  <span class="top-source-rank">#{{ item.rank ?? index + 1 }}</span>
                  <div>
                    <strong>{{ item.sourceDisplayName || 'Unknown source' }}</strong>
                    <p>{{ formatNumber(item.totalTokens) }} tokens</p>
                  </div>
                </div>

                <div class="top-source-side">
                  <strong>{{ formatUsd(item.estimatedCostUsd) }}</strong>
                  <span v-if="(item.estimatedEventCount ?? 0) > 0">{{ formatNumber(item.estimatedEventCount) }} est.</span>
                </div>
              </li>
            </ol>
          </aside>
        </div>
      </template>
    </section>
  </div>
</template>

<script setup lang="ts">
import { Line } from 'vue-chartjs'
import {
  Chart as ChartJS,
  CategoryScale,
  Filler,
  Legend,
  LineElement,
  LinearScale,
  PointElement,
  Title,
  Tooltip,
} from 'chart.js'
import ProgressOrb from '@/components/ProgressOrb.vue'
import { formatNumber, formatUsd } from '@/components/usageDashboardFormatters'
import { useUsageDashboard } from '@/components/useUsageDashboard'

ChartJS.register(CategoryScale, LinearScale, PointElement, LineElement, Title, Tooltip, Legend, Filler)

const props = defineProps<{
  clientId: string
}>()

const {
  fromDate,
  toDate,
  loadingReview,
  reviewError,
  loadingProCursor,
  exportingProCursor,
  proCursorError,
  proCursorGranularity,
  proCursorGroupBy,
  selectedProCursorPreset,
  periodPresets,
  totalInputTokens,
  totalOutputTokens,
  totalCachedInputTokens,
  totalReasoningTokens,
  totalEstimatedCostUsd,
  hasReviewSamples,
  proCursorTotalTokens,
  proCursorEstimatedCost,
  proCursorEstimatedEvents,
  resolvedTopSources,
  hasProCursorData,
  showEstimatedBanner,
  showGapFillBanner,
  currentPeriodLabel,
  lastRollupCompletedLabel,
  reviewChartData,
  reviewChartOptions,
  proCursorChartData,
  proCursorChartOptions,
  markCustomRange,
  applyProCursorPeriod,
  loadReviewUsage,
  loadProCursorUsage,
  handleRefresh,
  handleExportProCursor,
} = useUsageDashboard(props)
</script>

<style scoped>
      .usage-dashboard {
        display: flex;
        flex-direction: column;
        gap: 1.5rem;
      }

      .dashboard-surface {
        display: flex;
        flex-direction: column;
        gap: 1.25rem;
        padding: 1.35rem;
        border-radius: var(--radius-xl);
        border: 1px solid var(--color-border);
        background:
          linear-gradient(180deg, rgba(255, 255, 255, 0.02), transparent 30%),
          var(--color-surface);
        box-shadow: 0 18px 36px -32px rgba(15, 23, 42, 0.7);
      }

      .dashboard-surface--procursor {
        border-color: rgba(232, 93, 63, 0.28);
        background:
          linear-gradient(135deg, rgba(232, 93, 63, 0.1), transparent 40%),
          linear-gradient(180deg, rgba(255, 255, 255, 0.02), transparent 30%),
          var(--color-surface);
      }

      .panel-header {
        display: flex;
        justify-content: space-between;
        align-items: flex-start;
        gap: 1rem;
        flex-wrap: wrap;
      }

      .panel-header--procursor {
        align-items: center;
      }

      .panel-kicker {
        margin: 0 0 0.35rem;
        color: var(--color-accent);
        font-size: 0.78rem;
        font-weight: 700;
        letter-spacing: 0.14em;
        text-transform: uppercase;
      }

      .panel-header h3,
      .chart-card-header h4,
      .top-sources-header h4,
      .empty-state h3 {
        margin: 0;
      }

      .panel-copy,
      .chart-card-header p,
      .top-sources-header p,
      .panel-caption,
      .top-source-row p,
      .top-source-side span {
        margin: 0;
        color: var(--color-text-muted);
      }

      .review-controls,
      .procursor-controls {
        display: flex;
        align-items: center;
        gap: 0.85rem;
        flex-wrap: wrap;
      }

      .date-inputs-wrapper {
        display: flex;
        align-items: center;
        gap: 0.9rem;
        flex-wrap: wrap;
      }

      .date-range-group {
        display: flex;
        align-items: center;
        gap: 0.7rem;
      }

      .date-separator,
      .top-sources-meta {
        color: var(--color-text-muted);
        font-weight: 600;
      }

      .label-muted,
      .select-chip span {
        font-size: 0.78rem;
        color: var(--color-text-muted);
        font-weight: 700;
        letter-spacing: 0.06em;
        text-transform: uppercase;
      }

      .date-input,
      .select-chip select {
        width: auto;
      }

      .select-chip {
        display: grid;
        gap: 0.35rem;
      }

      .period-pills {
        display: inline-flex;
        gap: 0.35rem;
        padding: 0.25rem;
        border-radius: var(--radius-pill);
        border: 1px solid var(--color-border);
        background: rgba(15, 23, 42, 0.25);
      }

      .period-pill {
        border: 0;
        background: transparent;
        color: var(--color-text-muted);
        font: inherit;
        font-weight: 700;
        padding: 0.45rem 0.8rem;
        border-radius: var(--radius-pill);
        cursor: pointer;
      }

      .period-pill--active {
        background: linear-gradient(135deg, rgba(232, 93, 63, 0.18), rgba(50, 118, 245, 0.18));
        color: var(--color-text);
      }

      .usage-summary {
        display: grid;
        gap: 1rem;
        grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
      }

      .summary-card {
        position: relative;
        display: grid;
        grid-template-columns: minmax(0, 1fr) auto;
        grid-template-areas:
          'label icon'
          'value value';
        align-items: center;
        column-gap: 0.75rem;
        row-gap: 0.5rem;
        padding: 1.15rem 1.25rem;
        border-radius: var(--radius-xl);
        border: 1px solid rgba(148, 163, 184, 0.16);
        background: rgba(15, 23, 42, 0.24);
        overflow: hidden;
      }

      .summary-card::before {
        content: '';
        position: absolute;
        inset: 0 0 auto;
        height: 4px;
      }

      .usage-input::before { background: var(--chart-1); }
      .usage-output::before { background: var(--chart-3); }
      .usage-total::before { background: var(--chart-2); }
      .usage-cost::before { background: var(--color-warning); }
      .usage-estimated::before { background: var(--color-suggestion); }

      .summary-label {
        grid-area: label;
        color: var(--color-text-muted);
        font-size: 0.76rem;
        font-weight: 700;
        letter-spacing: 0.12em;
        text-transform: uppercase;
      }

      .summary-value {
        grid-area: value;
        font-size: clamp(1.75rem, 4vw, 2.4rem);
        line-height: 1;
        font-weight: 800;
        letter-spacing: -0.04em;
      }

      .summary-icon {
        grid-area: icon;
        align-self: start;
        font-size: 1.2rem;
        opacity: 0.4;
        color: var(--color-text-muted);
      }

      .usage-callout {
        display: flex;
        align-items: flex-start;
        gap: 0.75rem;
        padding: 0.95rem 1rem;
        border-radius: var(--radius-lg);
        border: 1px solid rgba(242, 165, 65, 0.28);
        background: rgba(242, 165, 65, 0.08);
      }

      .usage-callout i {
        font-size: 1rem;
        margin-top: 0.1rem;
        color: var(--color-warning);
      }

      .panel-caption {
        font-size: 0.9rem;
      }

      .chart-card,
      .top-sources-card {
        border: 1px solid rgba(148, 163, 184, 0.16);
        border-radius: var(--radius-xl);
        background: rgba(15, 23, 42, 0.24);
      }

      .chart-card {
        padding: 1rem 1rem 0.75rem;
      }

      .chart-card-header,
      .top-sources-header {
        display: flex;
        justify-content: space-between;
        gap: 1rem;
        align-items: flex-start;
        margin-bottom: 0.85rem;
      }

      .chart-container {
        position: relative;
        height: 320px;
      }

      .chart-container--procursor {
        height: 340px;
      }

      .procursor-grid {
        display: grid;
        gap: 1rem;
        grid-template-columns: minmax(0, 1.8fr) minmax(280px, 0.95fr);
      }

      .top-sources-card {
        padding: 1rem;
      }

      .top-sources-list {
        display: flex;
        flex-direction: column;
        gap: 0.75rem;
        margin: 0;
        padding: 0;
        list-style: none;
      }

      .top-source-row {
        display: flex;
        justify-content: space-between;
        align-items: center;
        gap: 1rem;
        padding: 0.85rem 0.95rem;
        border-radius: var(--radius-lg);
        background: rgba(255, 255, 255, 0.02);
      }

      .top-source-main {
        display: flex;
        align-items: center;
        gap: 0.8rem;
      }

      .top-source-rank {
        display: inline-grid;
        place-items: center;
        width: 2rem;
        height: 2rem;
        border-radius: var(--radius-pill);
        background: rgba(232, 93, 63, 0.16);
        color: var(--color-danger);
        font-size: 0.85rem;
        font-weight: 700;
      }

      .top-source-side {
        text-align: right;
        display: grid;
        gap: 0.2rem;
      }

      .top-source-side strong {
        font-size: 0.95rem;
      }

      .loading-state,
      .error-state,
      .empty-state {
        display: flex;
        flex-direction: column;
        align-items: center;
        gap: 0.65rem;
        padding: 2.4rem 1rem;
        text-align: center;
      }

      .empty-state--compact {
        padding-block: 2rem;
      }

      .empty-state--procursor {
        border: 1px dashed rgba(148, 163, 184, 0.24);
        border-radius: var(--radius-xl);
      }

      .empty-icon,
      .error-icon {
        font-size: 2rem;
        opacity: 0.5;
      }

      .state-orb {
        width: 46px;
        height: 46px;
      }

      @media (max-width: 1080px) {
        .procursor-grid {
          grid-template-columns: 1fr;
        }
      }

      @media (max-width: 720px) {
        .dashboard-surface {
          padding: 1rem;
        }

        .review-controls,
        .procursor-controls,
        .date-inputs-wrapper {
          width: 100%;
        }

        .date-range-group {
          flex: 1 1 100%;
        }

        .date-input,
        .select-chip select {
          width: 100%;
        }

        .top-source-row {
          flex-direction: column;
          align-items: flex-start;
        }

        .top-source-side {
          text-align: left;
        }
      }
    </style>
