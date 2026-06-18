<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="trace-workspace">
        <div class="trace-filter-toolbar trace-filter-toolbar--global">
            <div class="trace-filter-heading-row">
                <div class="trace-filter-title-group">
                    <h4>Trace Search</h4>
                    <p class="trace-filter-subtitle">
                        Search across all loaded passes in this review. Matching passes stay visible in the tree while the detail pane focuses the active match set.
                    </p>
                </div>
                <div class="trace-filter-heading-actions">
                    <p class="trace-filter-summary">
                        {{ vm.visibleTraceRows.length }} visible row{{ vm.visibleTraceRows.length === 1 ? '' : 's' }} across this review.
                    </p>
                    <button
                        type="button"
                        class="btn-ghost btn-sm trace-filter-toggle"
                        :class="{ 'trace-filter-toggle--active': vm.traceFindingsOnly }"
                        :aria-pressed="vm.traceFindingsOnly"
                        data-testid="trace-findings-only-toggle"
                        @click="vm.traceFindingsOnly = !vm.traceFindingsOnly"
                    >
                        Final findings only
                    </button>
                    <button
                        type="button"
                        class="btn-ghost btn-sm trace-filter-collapse"
                        :aria-expanded="!vm.isTraceSearchCollapsed"
                        data-testid="trace-search-toggle"
                        @click="vm.isTraceSearchCollapsed = !vm.isTraceSearchCollapsed"
                    >
                        <span>{{ vm.traceSearchToggleLabel }}</span>
                        <i class="mdi" :class="vm.traceSearchToggleIcon" aria-hidden="true"></i>
                    </button>
                </div>
            </div>
            <div v-show="!vm.isTraceSearchCollapsed" class="trace-filter-grid" data-testid="trace-search-panel">
                <v-text-field
                    v-model="vm.traceFilters.queryText"
                    type="search"
                    class="trace-filter-input trace-filter-query"
                    data-testid="trace-filter-query"
                    hide-details
                    placeholder="Search prompts, outputs, tool calls, memory, errors..."
                    prepend-inner-icon="mdi-magnify"
                />
                <v-autocomplete
                    :model-value="vm.traceAutocompleteValue(vm.traceFilters.filePath)"
                    v-model:search="vm.traceFilters.filePath"
                    class="trace-filter-input"
                    data-testid="trace-filter-file-path"
                    :items="vm.traceSuggestions.filePaths"
                    hide-details
                    clearable
                    menu-icon="mdi-chevron-down"
                    label="File path"
                    placeholder="Start typing a file path"
                    no-data-text="No matching file paths"
                    @update:model-value="vm.setTraceFilterValue('filePath', $event)"
                />
                <v-autocomplete
                    :model-value="vm.traceAutocompleteValue(vm.traceFilters.modelId)"
                    v-model:search="vm.traceFilters.modelId"
                    class="trace-filter-input"
                    data-testid="trace-filter-model"
                    :items="vm.traceSuggestions.modelIds"
                    hide-details
                    clearable
                    menu-icon="mdi-chevron-down"
                    label="Model"
                    placeholder="Start typing a model"
                    no-data-text="No matching models"
                    @update:model-value="vm.setTraceFilterValue('modelId', $event)"
                />
                <button class="btn-secondary btn-sm trace-filter-clear" :disabled="!vm.hasActiveTraceFilters" @click="vm.clearTraceFilters">
                    Clear filters
                </button>
            </div>
        </div>

        <div class="protocol-content">
            <PassSelector
                :items="vm.sidebarItems"
                :model-value="vm.activePassId"
                @update:model-value="vm.activePassId = $event"
            />
            <div v-if="vm.activePass" class="pass-main">
                    <div class="pass-detail-header">
                        <div class="pass-detail-filepath">
                            <span v-if="vm.parseFilePath(vm.activePass.label).directory" class="pass-detail-dir">{{ vm.parseFilePath(vm.activePass.label).directory }}/</span>
                            <span class="pass-detail-filename">{{ vm.parseFilePath(vm.activePass.label).filename }}</span>
                        </div>
                        <span v-if="vm.activePass.isInherited" class="chip chip-inherited">Inherited trace</span>
                    </div>

                    <div class="pass-detail-tabs" role="tablist" aria-label="Reviewed file detail">
                        <button
                            type="button"
                            role="tab"
                            :aria-selected="vm.detailTab === 'events'"
                            class="pass-detail-tab"
                            :class="{ 'is-active': vm.detailTab === 'events' }"
                            data-testid="trace-tab-events"
                            @click="vm.clearDiff()"
                        >
                            <i class="fi fi-rr-list" aria-hidden="true"></i>
                            Events
                            <span class="pass-detail-tab-count">({{ vm.activePassEventRows.length }})</span>
                        </button>
                        <button
                            type="button"
                            role="tab"
                            :aria-selected="vm.detailTab === 'diff'"
                            class="pass-detail-tab"
                            :class="{ 'is-active': vm.detailTab === 'diff' }"
                            data-testid="trace-tab-diff"
                            @click="selectDiffTab"
                        >
                            <i class="fi fi-rr-document-signed" aria-hidden="true"></i>
                            Diff
                        </button>
                    </div>

                    <dl class="summary-grid pass-summary">
                        <div><dt>Attempt</dt><dd>{{ vm.activePass.attemptNumber ?? '—' }}</dd></div>
                        <div><dt>Started</dt><dd>{{ vm.formatDate(vm.activePass.startedAt) }}</dd></div>
                        <div><dt>Completed</dt><dd>{{ vm.formatDate(vm.activePass.completedAt) }}</dd></div>
                        <div><dt>Duration</dt><dd>{{ vm.computePassDuration(vm.activePass) }}</dd></div>
                        <div><dt>Iterations</dt><dd>{{ vm.activePass.iterationCount ?? '—' }}</dd></div>
                        <div><dt>Tool Calls</dt><dd>{{ vm.activePass.toolCallCount ?? '—' }}</dd></div>
                        <div><dt>In Tokens</dt><dd class="fat-tokens">{{ vm.formatTokens(vm.activePass.totalInputTokens) }}</dd></div>
                        <div><dt>Out Tokens</dt><dd class="fat-tokens">{{ vm.formatTokens(vm.activePass.totalOutputTokens) }}</dd></div>
                        <div><dt>Strategy</dt><dd>{{ vm.activePassReviewStrategyDisplay }}</dd></div>
                    </dl>

                    <section v-if="vm.detailTab === 'events' && vm.traceTimingInsights.length" class="pass-file-outcome-section">
                        <div class="pass-final-result-header">
                            <h4>Timing Insights</h4>
                            <span class="chip chip-muted">Current pass</span>
                        </div>
                        <ol class="timing-insights-list" aria-label="Slowest visible tool calls">
                            <li v-for="insight in vm.traceTimingInsights" :key="insight.eventId">
                                <button type="button" class="timing-insight-row" @click="vm.openTimingInsight(insight)">
                                    <span class="timing-insight-rank">#{{ insight.rank }}</span>
                                    <span class="timing-insight-tool">{{ insight.toolName }}</span>
                                    <span class="timing-insight-context">{{ insight.passLabel }}</span>
                                    <span class="timing-insight-duration">{{ vm.formatDurationWithMs(insight.durationMs) }}</span>
                                    <span class="timing-insight-meta">
                                        <template v-if="insight.waitDurationMs != null || insight.activeDurationMs != null">
                                            <span v-if="insight.waitDurationMs != null">Wait {{ vm.formatDurationWithMs(insight.waitDurationMs) }}</span>
                                            <span v-if="insight.activeDurationMs != null">Active {{ vm.formatDurationWithMs(insight.activeDurationMs) }}</span>
                                        </template>
                                        <span v-else-if="insight.hasPhaseDetail">Phase detail available</span>
                                    </span>
                                </button>
                            </li>
                        </ol>
                    </section>

                    <section v-if="vm.detailTab === 'events' && vm.activePassInheritance" class="pass-file-outcome-section">
                        <div class="pass-final-result-header">
                            <h4>Inheritance</h4>
                            <span class="chip chip-muted">Same revision retry reuse</span>
                        </div>
                        <dl class="summary-grid pass-summary pass-file-outcome-grid">
                            <div><dt>Source Job</dt><dd class="inheritance-job-cell"><RouterLink class="inheritance-link monospace-value" :to="vm.sourceJobProtocolLink(vm.activePassInheritance.sourceJobId)">{{ vm.shortGuid(vm.activePassInheritance.sourceJobId) }}</RouterLink></dd></div>
                            <div><dt>Source Protocol</dt><dd class="monospace-value">{{ vm.shortGuid(vm.activePassInheritance.sourceProtocolId) }}</dd></div>
                            <div v-if="vm.activePassInheritance.sourceFileResultId"><dt>Source File Result</dt><dd class="monospace-value">{{ vm.shortGuid(vm.activePassInheritance.sourceFileResultId) }}</dd></div>
                            <div><dt>Source Completed</dt><dd>{{ vm.formatDate(vm.activePassInheritance.sourceCompletedAt) }}</dd></div>
                        </dl>
                        <p class="pass-file-outcome-note">
                            This file pass was inherited from a previously completed same-revision run and is included here so protocol totals and trace review reflect reused work.
                        </p>
                    </section>

                    <section v-if="vm.detailTab === 'events' && vm.activePassFileOutcome" class="pass-file-outcome-section">
                        <div class="pass-final-result-header">
                            <h4>File Outcome</h4>
                            <span class="chip chip-muted">{{ vm.formatFileOutcomeStatus(vm.activePassFileOutcome) }}</span>
                        </div>
                        <dl class="summary-grid pass-summary pass-file-outcome-grid">
                            <div><dt>Path</dt><dd>{{ vm.activePassFileOutcome.filePath }}</dd></div>
                            <div><dt>Status</dt><dd>{{ vm.formatFileOutcomeStatus(vm.activePassFileOutcome) }}</dd></div>
                            <div v-if="vm.activePassFileOutcome.exclusionReason"><dt>Exclusion</dt><dd>{{ vm.activePassFileOutcome.exclusionReason }}</dd></div>
                            <div v-if="vm.activePassFileOutcome.errorMessage"><dt>Error</dt><dd>{{ vm.activePassFileOutcome.errorMessage }}</dd></div>
                        </dl>
                        <p v-if="vm.activePassFileOutcome.isDegraded" class="pass-file-outcome-note">
                            Agentic file investigation recorded a degraded intermediate outcome for this pass. It remained non-validated unless later verification and final gate kept it.
                        </p>
                    </section>

                    <section v-if="vm.detailTab === 'events' && vm.activePassFollowUp" class="pass-file-outcome-section">
                        <div class="pass-final-result-header">
                            <h4>Follow-up</h4>
                            <span class="chip chip-muted">{{ vm.formatFollowUpStatus(vm.activePassFollowUp) }}</span>
                        </div>
                        <dl class="summary-grid pass-summary pass-file-outcome-grid">
                            <div><dt>Used</dt><dd>{{ vm.activePassFollowUp.used ? 'Yes' : 'No' }}</dd></div>
                            <div v-if="vm.activePassFollowUp.triggerFamily"><dt>Trigger</dt><dd>{{ vm.activePassFollowUp.triggerFamily }}</dd></div>
                            <div><dt>Completion</dt><dd>{{ vm.activePassFollowUp.completedSuccessfully ? 'Completed successfully' : 'Not completed successfully' }}</dd></div>
                            <div><dt>Dependency</dt><dd>{{ vm.activePassFollowUp.dependencyRecorded ? 'Dependent finding recorded' : 'No surviving dependency recorded' }}</dd></div>
                        </dl>
                    </section>

                    <section v-if="vm.detailTab === 'events' && vm.activePassRepeatedJudgment" class="pass-file-outcome-section">
                        <div class="pass-final-result-header">
                            <h4>Repeated Judgment</h4>
                            <span class="chip chip-muted">{{ vm.formatRepeatedJudgmentStatus(vm.activePassRepeatedJudgment) }}</span>
                        </div>
                        <dl class="summary-grid pass-summary pass-file-outcome-grid">
                            <div><dt>Finding</dt><dd>{{ vm.activePassRepeatedJudgment.findingId }}</dd></div>
                            <div v-if="vm.activePassRepeatedJudgment.evidenceSetId"><dt>Evidence Set</dt><dd>{{ vm.activePassRepeatedJudgment.evidenceSetId }}</dd></div>
                            <div><dt>Agreement</dt><dd>{{ vm.activePassRepeatedJudgment.agreementState ?? 'Not recorded' }}</dd></div>
                            <div><dt>Disposition</dt><dd>{{ vm.activePassRepeatedJudgment.recommendedDisposition ?? 'Not recorded' }}</dd></div>
                            <div><dt>Evidence Reused</dt><dd>{{ vm.activePassRepeatedJudgment.usedSameEvidenceSet ? 'Yes' : 'No' }}</dd></div>
                            <div v-if="vm.activePassRepeatedJudgment.reasonCodes?.length"><dt>Reason Codes</dt><dd>{{ vm.activePassRepeatedJudgment.reasonCodes.join(', ') }}</dd></div>
                        </dl>
                    </section>

                    <section v-if="vm.detailTab === 'events' && vm.activePassProRvPrefilter" class="pass-file-outcome-section">
                        <div class="pass-final-result-header">
                            <h4>ProRV Prefilter</h4>
                            <span class="chip chip-muted">{{ vm.activePassProRvPrefilter.executionState ?? 'Not recorded' }}</span>
                        </div>
                        <dl class="summary-grid pass-summary pass-file-outcome-grid">
                            <div><dt>Selected</dt><dd>{{ vm.activePassProRvPrefilter.selected ? 'Yes' : 'No' }}</dd></div>
                            <div><dt>Execution</dt><dd>{{ vm.activePassProRvPrefilter.executionState ?? 'Not recorded' }}</dd></div>
                            <div><dt>Guidance Applied</dt><dd>{{ vm.activePassProRvPrefilter.guidanceApplied ? 'Yes' : 'No' }}</dd></div>
                            <div><dt>Prompt Kind</dt><dd>{{ vm.activePassProRvPrefilter.appliedPromptKind ?? 'Not recorded' }}</dd></div>
                            <div><dt>Guidance Count</dt><dd>{{ vm.activePassProRvPrefilter.guidanceCount ?? 0 }}</dd></div>
                            <div><dt>AI Call Recorded</dt><dd>{{ vm.activePassProRvPrefilter.aiCallRecorded ? 'Yes' : 'No' }}</dd></div>
                            <div v-if="vm.activePassProRvPrefilter.stageId"><dt>Stage</dt><dd>{{ vm.activePassProRvPrefilter.stageId }}</dd></div>
                            <div v-if="vm.activePassProRvPrefilter.runtimeSource"><dt>Runtime</dt><dd>{{ vm.activePassProRvPrefilter.runtimeSource }}</dd></div>
                            <div v-if="vm.activePassProRvPrefilter.modelId"><dt>Model</dt><dd>{{ vm.activePassProRvPrefilter.modelId }}</dd></div>
                            <div v-if="vm.activePassProRvPrefilter.language"><dt>Language</dt><dd>{{ vm.activePassProRvPrefilter.language }}</dd></div>
                            <div v-if="vm.activePassProRvPrefilter.prefilterStatus"><dt>Prefilter Status</dt><dd>{{ vm.activePassProRvPrefilter.prefilterStatus }}</dd></div>
                            <div v-if="vm.activePassProRvPrefilter.reason"><dt>Reason</dt><dd>{{ vm.activePassProRvPrefilter.reason }}</dd></div>
                            <div v-if="vm.activePassProRvPrefilter.appliedGuidanceIds?.length"><dt>Guidance IDs</dt><dd>{{ vm.activePassProRvPrefilter.appliedGuidanceIds.join(', ') }}</dd></div>
                        </dl>
                    </section>

                    <section v-if="vm.detailTab === 'events' && (vm.activePass.finalSummary || vm.activePassFinalComments.length)" class="pass-final-result-section">
                        <div class="pass-final-result-header">
                            <h4>Final File Result</h4>
                            <span v-if="vm.activePassFinalComments.length" class="chip chip-muted">{{ vm.activePassFinalComments.length }} final comment{{ vm.activePassFinalComments.length === 1 ? '' : 's' }}</span>
                        </div>
                        <div v-if="vm.activePass.finalSummary" class="markdown-content pass-final-summary-text" v-html="vm.renderMarkdown(vm.activePass.finalSummary)"></div>
                        <p v-else class="pass-final-summary-empty">No final per-file summary was captured for this pass.</p>
                        <JobProtocolCommentGroups
                            v-if="vm.activePassFinalComments.length"
                            :vm="vm"
                            :groups="[{ directory: 'Root', comments: vm.activePassFinalComments }]"
                            empty-message=""
                        />
                    </section>

                    <section v-if="vm.detailTab === 'events'" class="events-section">
                        <div class="events-section-header">
                            <h4>Events ({{ vm.activePassEventRows.length }})</h4>
                            <p v-if="vm.hasActiveTraceFilters" class="events-section-context">Global trace search is active for this review.</p>
                        </div>
                        <p v-if="!vm.activePass.events?.length && !vm.hasActiveTraceFilters" class="empty-state">{{ vm.emptyPassMessage(vm.activePass) }}</p>
                        <p v-else-if="vm.activePassEventRows.length === 0" class="empty-state trace-empty-state">
                            {{ vm.visibleTraceRows.length === 0 ? 'No trace rows in this review match the current filters.' : 'No trace rows in this pass match the current filters.' }}
                        </p>
                        <TransitionGroup v-else name="list" tag="div" class="events-list">
                            <article
                                v-for="row in vm.activePassEventRows"
                                :key="row.id"
                                class="event-card row-clickable"
                                :class="{
                                    'row-error': !!row.merged.callDetails.error || !!row.merged.resultDetails?.error,
                                    'row-processing': vm.isMergedEventProcessing(row.merged),
                                    'row-child': row.depth > 0,
                                    'row-focused': vm.focusedEventId === row.id,
                                }"
                                :data-event-id="row.id"
                                :data-event-name="row.merged.name"
                                :data-event-depth="row.depth"
                                :data-parent-event-id="row.parentId ?? ''"
                                @click="vm.openMergedModal(row.merged)"
                            >
                                <div v-if="row.isToolChild" class="event-child-gutter" aria-hidden="true">
                                    <span class="event-child-rail"></span>
                                </div>
                                <div class="event-card-main">
                                    <div class="event-card-header">
                                        <div class="event-card-meta-row">
                                            <div class="event-card-kind-group" :class="{ 'kind-cell--child': row.isToolChild }">
                                                <span class="kind-badge" :class="vm.kindBadgeClass(row.merged.callDetails.kind)">{{ row.merged.callDetails.kind ?? 'unknown' }}</span>
                                                <span v-if="row.isToolChild && row.parentName" class="kind-parent-pill">{{ vm.parentIterationLabel(row.parentName) }}</span>
                                                <span v-if="vm.isMergedEventProcessing(row.merged)" class="status-badge status-processing">Executing...</span>
                                            </div>
                                            <span class="date-cell">{{ vm.formatDate(row.merged.time) }}</span>
                                        </div>

                                        <div class="event-card-title-row" :class="{ 'event-card-title-row--child': row.depth > 0 }">
                                            <button
                                                v-if="row.childCount > 0"
                                                class="event-toggle"
                                                :aria-label="row.isExpanded ? 'Collapse child tool calls' : 'Expand child tool calls'"
                                                @click.stop="vm.toggleEventParent(row.id)"
                                            >
                                                <span class="event-toggle-icon" aria-hidden="true">
                                                    <i class="fi fi-rr-angle-small-down event-toggle-chevron" :class="{ 'event-toggle-chevron--collapsed': !row.isExpanded }"></i>
                                                </span>
                                                <span class="event-toggle-count">{{ row.childCount }}</span>
                                            </button>

                                            <div class="event-title-stack" :class="{ 'tool-name': row.merged.callDetails.kind === 'toolCall', 'ai-name': row.merged.callDetails.kind === 'aiCall', 'memory-name': row.merged.callDetails.kind === 'memoryOperation', 'operational-name': row.merged.callDetails.kind === 'operational' }">
                                                <span class="event-name-label">{{ row.merged.name }}</span>
                                                <div v-if="vm.hasToolTiming(row.merged.callDetails)" class="timing-inline-group">
                                                    <span class="timing-duration">{{ row.timingSummary }}</span>
                                                    <span v-if="row.timingDetail" class="timing-detail">{{ row.timingDetail }}</span>
                                                </div>
                                            </div>
                                        </div>

                                        <div class="event-meta-line">
                                            <span v-if="row.traceFilePath" class="event-meta-pill event-meta-pill--path" :title="row.traceFilePath">{{ row.traceFilePath }}</span>
                                            <span class="event-meta-pill">{{ row.traceEventCategory }}</span>
                                            <span v-if="row.traceModelId" class="event-meta-pill event-meta-pill--model" :title="row.traceModelId">{{ row.traceModelId }}</span>
                                            <span v-if="row.traceIsRedacted" class="event-meta-pill event-meta-pill--flag">Redacted</span>
                                            <span v-if="row.traceHasLimitedMetadata" class="event-meta-pill event-meta-pill--flag">Limited metadata</span>
                                        </div>
                                        <div v-if="row.traceMatchSnippet" class="event-match-snippet"><strong>{{ row.traceMatchedField }}:</strong> {{ row.traceMatchSnippet }}</div>
                                        <div v-if="row.traceContextSnippet" class="event-context-snippet">{{ row.traceContextSnippet }}</div>
                                        <div v-else-if="row.traceHasLimitedMetadata" class="event-context-limitation">Supporting metadata or nearby trace context was not captured for this row.</div>

                                        <div v-if="vm.hasToolTiming(row.merged.callDetails)" class="timing-badges">
                                            <span v-if="row.merged.callDetails.timingAvailability" class="status-badge" :class="vm.statusBadgeClass(row.merged.callDetails.timingAvailability)">{{ vm.formatTimingAvailability(row.merged.callDetails.timingAvailability) }}</span>
                                            <span v-if="row.merged.callDetails.toolOutcome" class="status-badge" :class="vm.statusBadgeClass(row.merged.callDetails.toolOutcome)">{{ vm.formatToolOutcome(row.merged.callDetails.toolOutcome) }}</span>
                                        </div>
                                        <span v-else class="timing-empty">No timing captured</span>
                                    </div>

                                    <div v-if="vm.hasEventTokens(row.merged.callDetails) || vm.hasEventError(row.merged.callDetails)" class="event-card-secondary">
                                        <div v-if="vm.hasEventTokens(row.merged.callDetails)" class="event-metric-card event-metric-card--tokens">
                                            <span class="event-card-label">Input Tokens</span>
                                            <div class="tokens-cell fat-tokens">
                                                <div>{{ vm.formatTokens(row.merged.callDetails.inputTokens) }}</div>
                                                <small v-if="row.merged.callDetails.kind === 'aiCall'" class="cache-token-detail">
                                                    Cached {{ vm.formatTokens(row.merged.callDetails.cachedInputTokens) }} · {{ vm.formatCacheStatus(row.merged.callDetails.cacheStatus) }}
                                                </small>
                                            </div>
                                        </div>

                                        <div v-if="vm.hasEventTokens(row.merged.callDetails)" class="event-metric-card event-metric-card--tokens">
                                            <span class="event-card-label">Output Tokens</span>
                                            <div class="tokens-cell fat-tokens">{{ vm.formatTokens(row.merged.callDetails.outputTokens) }}</div>
                                        </div>

                                        <div v-if="vm.hasEventError(row.merged.callDetails)" class="event-metric-card event-metric-card--error">
                                            <span class="event-card-label">Error</span>
                                            <div class="error-cell">
                                                <span v-if="row.merged.callDetails.error">{{ row.merged.callDetails.error }}</span>
                                                <span v-if="row.merged.callDetails.finalizationAttemptKind" class="status-badge status-processing">{{ row.merged.callDetails.finalizationAttemptKind }}: {{ row.merged.callDetails.finalizationOutcome ?? row.merged.callDetails.finalizationReason }}</span>
                                                <span v-if="row.merged.callDetails.toolEvidence" class="status-badge status-processing">Evidence {{ row.merged.callDetails.toolEvidence.action }}</span>
                                            </div>
                                        </div>
                                    </div>
                                </div>
                            </article>
                        </TransitionGroup>
                    </section>

                    <section v-if="vm.detailTab === 'diff'" class="diff-section" data-testid="trace-diff-section">
                        <JobProtocolDiffViewer
                            :file-result-id="activeDiffFileResultId"
                            :diff="vm.fileDiff"
                            :loading="vm.diffLoading"
                            :diff-error="vm.diffError"
                            :on-retry="retryFileDiff"
                        />
                                        </section>
                </div>
            </div>
        </div>
</template>
<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink } from 'vue-router'
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'
import JobProtocolCommentGroups from './JobProtocolCommentGroups.vue'
import JobProtocolDiffViewer from './JobProtocolDiffViewer.vue'
import PassSelector from './PassSelector.vue'

const props = defineProps<{ vm: JobProtocolViewModel }>()

const activeDiffFileResultId = computed<string | null>(() => {
    const pass = props.vm.activePass
    if (!pass) return null
    const fileResultId = pass.fileResultId
    return fileResultId ?? null
})

function selectDiffTab() {
    props.vm.detailTab = 'diff'
    const pass = props.vm.activePass
    if (!pass) return
    const fileResultId = pass.fileResultId
    if (!fileResultId) {
        props.vm.fileDiff = null
        props.vm.diffError = null
        return
    }
    const jobId = pass.jobId ?? ''
    if (!jobId) return
    void props.vm.loadFileDiff(jobId, fileResultId)
}

function retryFileDiff() {
    const pass = props.vm.activePass
    if (!pass) return
    const fileResultId = pass.fileResultId
    const jobId = pass.jobId ?? ''
    if (!jobId || !fileResultId) return
    void props.vm.loadFileDiff(jobId, fileResultId)
}
</script>

<style scoped>
.trace-workspace {
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.trace-filter-toolbar {
    display: flex;
    flex-direction: column;
    gap: 0.9rem;
    padding: 1rem 1.1rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-xl);
    background: linear-gradient(180deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.015));
}

.trace-filter-toolbar--global {
    position: sticky;
    top: 0.5rem;
    z-index: 4;
    backdrop-filter: blur(10px);
}

.trace-filter-heading-row {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.75rem 1rem;
    flex-wrap: wrap;
}

.trace-filter-heading-actions {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 0.75rem;
    flex-wrap: wrap;
}

.trace-filter-title-group {
    display: flex;
    flex-direction: column;
    gap: 0.2rem;
    min-width: 0;
}

.trace-filter-title-group h4,
.events-section-header h4,
.pass-final-result-header h4 {
    margin: 0;
}

.trace-filter-subtitle,
.trace-filter-summary,
.events-section-context,
.event-context-limitation {
    margin: 0;
    color: var(--color-text-muted);
    font-size: 0.85rem;
    line-height: 1.45;
}

.trace-filter-grid {
    display: grid;
    grid-template-columns: minmax(16rem, 2.2fr) repeat(2, minmax(10rem, 1fr)) auto;
    gap: 0.65rem;
    align-items: start;
}

@media (max-width: 1400px) {
    .trace-filter-grid {
        grid-template-columns: repeat(3, minmax(0, 1fr));
    }
}

@media (max-width: 900px) {
    .trace-filter-grid {
        grid-template-columns: minmax(0, 1fr);
    }
}

.trace-filter-input {
    min-width: 0;
}

.trace-filter-input :deep(.v-field) {
    background: rgba(15, 17, 22, 0.72);
    box-shadow: none;
}

.trace-filter-input :deep(.v-field__input) {
    min-height: 2.75rem;
}

.trace-filter-input :deep(input) {
    border: none;
    box-shadow: none;
    background: transparent;
    padding: 0;
    font-size: 0.95rem;
}

.trace-filter-input :deep(input:focus) {
    border: none;
    box-shadow: none;
}

.trace-filter-input :deep(.v-label) {
    color: var(--color-text-muted);
}

.trace-filter-query {
    width: 100%;
}

.trace-filter-collapse {
    display: inline-flex;
    align-items: center;
    gap: 0.45rem;
    white-space: nowrap;
}

.trace-filter-toggle--active {
    color: var(--color-text);
    background: rgba(34, 211, 238, 0.12);
    border-color: rgba(34, 211, 238, 0.28);
}

.protocol-content {
    min-width: 0;
}

@media (max-width: 1024px) {
    .events-section {
        overflow-x: visible;
    }

    .event-card {
        padding: 0.9rem;
    }

    .row-child {
        margin-left: 0.85rem;
    }
}

.protocol-content {
    background: var(--color-surface);
    border-radius: var(--radius-lg);
    padding: 0;
    border: 1px solid var(--color-border);
    overflow: hidden;
    display: flex;
    flex-direction: column;
}

.pass-main {
    flex: 1;
    min-width: 0;
}

.pass-detail-header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 1rem 1.5rem;
    background: rgba(255, 255, 255, 0.02);
    border-bottom: 1px solid var(--color-border);
    min-width: 0;
}

.pass-detail-tabs {
    display: flex;
    align-items: center;
    gap: 0.4rem;
    padding: 0.6rem 1.5rem 0;
    border-bottom: 1px solid var(--color-border);
    background: rgba(255, 255, 255, 0.015);
}

.pass-detail-tab {
    appearance: none;
    background: transparent;
    border: none;
    color: var(--color-text-muted);
    display: inline-flex;
    align-items: center;
    gap: 0.4rem;
    padding: 0.45rem 0.85rem;
    border-radius: var(--radius-md) var(--radius-md) 0 0;
    font: inherit;
    font-size: 0.78rem;
    font-weight: 600;
    letter-spacing: 0.03em;
    text-transform: uppercase;
    cursor: pointer;
    border-bottom: 2px solid transparent;
    transition: color 0.15s, border-color 0.15s, background 0.15s;
}

.pass-detail-tab:hover {
    color: var(--color-text);
}

.pass-detail-tab.is-active {
    color: var(--color-accent);
    border-bottom-color: var(--color-accent);
    background: rgba(34, 211, 238, 0.06);
}

.pass-detail-tab-count {
    font-size: 0.7rem;
    font-weight: 500;
    color: var(--color-text-muted);
    letter-spacing: 0;
    text-transform: none;
}

.diff-section {
    padding: 1.25rem 1.5rem 1.5rem;
    overflow: hidden;
    min-width: 0;
}

.pass-detail-filepath {
    flex: 1;
    min-width: 0;
    font-family: 'JetBrains Mono', 'Fira Code', monospace;
    font-size: 0.9rem;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
}

.pass-detail-dir {
    color: var(--color-text-muted);
}

.pass-detail-filename {
    color: var(--color-text);
    font-weight: 600;
}

.chip {
    display: inline-flex;
    align-items: center;
    padding: 0.2rem 0.65rem;
    border-radius: var(--radius-pill);
    font-size: 0.72rem;
    font-weight: 700;
    line-height: 1.2;
    white-space: nowrap;
}

.chip-muted {
    background: rgba(255, 255, 255, 0.08);
    color: var(--color-text-muted);
    border: 1px solid rgba(255, 255, 255, 0.08);
}

.chip-inherited {
    background: rgba(59, 130, 246, 0.14);
    color: var(--color-info);
    border: 1px solid rgba(59, 130, 246, 0.28);
}

.summary-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: 1.5rem;
    margin: 0;
    padding: 1.5rem;
    background: rgba(255, 255, 255, 0.02);
    border-bottom: 1px solid var(--color-border);
}

.summary-grid div {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
}

.summary-grid dt {
    font-size: 0.8rem;
    color: var(--color-text-muted);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.summary-grid dd {
    margin: 0;
    font-size: 1rem;
    color: var(--color-text);
}

.pass-summary {
    margin-bottom: 0;
}

.pass-file-outcome-section {
    margin-top: 0;
    padding: 0 1.5rem 1.25rem;
}

.pass-file-outcome-section .pass-final-result-header {
    padding: 1.35rem 0 0.35rem;
    margin-bottom: 0;
}

.pass-file-outcome-grid {
    margin-bottom: 0.75rem;
}

.inheritance-job-cell {
    display: flex;
    align-items: center;
}

.inheritance-link {
    color: var(--color-info);
    text-decoration: none;
}

.inheritance-link:hover {
    color: var(--color-info);
    text-decoration: underline;
}

.pass-file-outcome-note {
    margin: 0;
    padding: 0 1.5rem 1.35rem;
    color: var(--color-text-muted);
}

.pass-final-result-section {
    padding: 1.25rem 1.5rem 0;
}

.pass-final-result-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    margin-bottom: 0.9rem;
}

.pass-final-result-header h4 {
    font-size: 0.9rem;
    font-weight: 600;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.pass-final-summary-text,
.pass-final-summary-empty {
    margin-bottom: 1rem;
}

.timing-insights-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
}

.timing-insight-row {
    width: 100%;
    display: grid;
    grid-template-columns: auto minmax(0, 1.4fr) minmax(0, 1.6fr) auto minmax(0, 1.2fr);
    gap: 0.75rem;
    align-items: center;
    padding: 0.7rem 0.85rem;
    border-radius: var(--radius-lg);
    border: 1px solid var(--color-border);
    background: rgba(255, 255, 255, 0.02);
    color: var(--color-text);
    text-align: left;
    cursor: pointer;
    box-sizing: border-box;
}

.timing-insight-row:hover {
    border-color: rgba(255, 255, 255, 0.12);
    background: rgba(255, 255, 255, 0.04);
}

.timing-insight-rank,
.timing-insight-duration,
.timing-insight-tool,
.tool-phase-title,
.tool-phase-duration {
    font-weight: 600;
}

.timing-insight-context,
.timing-insight-meta {
    color: var(--color-text-muted);
    font-size: 0.85rem;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

@media (max-width: 1024px) {
    .timing-insight-row {
        grid-template-columns: auto 1fr auto;
    }

    .timing-insight-context,
    .timing-insight-meta {
        grid-column: 2 / 4;
    }
}

.events-section {
    padding: 1.5rem;
    flex: 1;
    overflow-y: auto;
    max-width: 1000px;
    margin: 0 auto;
    width: 100%;
}

.events-section-header {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 0.75rem;
    flex-wrap: wrap;
    margin-bottom: 0.75rem;
}

.events-list {
    display: flex;
    flex-direction: column;
    gap: 0.9rem;
}

.event-card {
    position: relative;
    display: flex;
    align-items: stretch;
    gap: 0.8rem;
    padding: 1rem 1.1rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-xl);
    background: linear-gradient(180deg, rgba(255, 255, 255, 0.028), rgba(255, 255, 255, 0.018));
    box-shadow: 0 10px 30px rgba(0, 0, 0, 0.12);
}

.event-card-main {
    flex: 1 1 auto;
    width: 100%;
    display: flex;
    flex-direction: column;
    gap: 0.85rem;
    min-width: 0;
}

.event-card-header,
.event-card-secondary {
    min-width: 0;
}

.event-card-header {
    display: flex;
    flex-direction: column;
    gap: 0.65rem;
}

.event-card-secondary {
    grid-template-columns: repeat(3, minmax(0, 1fr));
    display: grid;
    gap: 0.75rem;
    padding-top: 0.85rem;
    border-top: 1px solid rgba(255, 255, 255, 0.05);
}

@media (max-width: 1200px) {
    .event-card-secondary {
        grid-template-columns: repeat(2, minmax(0, 1fr));
    }
}

@media (max-width: 900px) {
    .event-card-secondary {
        grid-template-columns: minmax(0, 1fr);
    }

    .event-title-stack {
        flex-direction: column;
        align-items: flex-start;
    }
}

.event-metric-card {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    padding: 0.75rem 0.85rem;
    border-radius: var(--radius-lg);
    background: rgba(255, 255, 255, 0.025);
    border: 1px solid rgba(255, 255, 255, 0.04);
    min-width: 0;
}

.event-card-meta-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.75rem;
    flex-wrap: wrap;
}

.event-card-label {
    font-size: 0.72rem;
    font-weight: 600;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.row-error {
    border-color: rgba(239, 68, 68, 0.28);
    background: linear-gradient(180deg, rgba(239, 68, 68, 0.08), rgba(255, 255, 255, 0.018));
}

.row-clickable {
    cursor: pointer;
    transition: background 0.15s, border-color 0.15s, transform 0.15s;
}

.row-clickable:hover {
    background: linear-gradient(180deg, rgba(255, 255, 255, 0.04), rgba(255, 255, 255, 0.024));
    border-color: rgba(255, 255, 255, 0.1);
    transform: translateY(-1px);
}

.row-child {
    background: linear-gradient(180deg, rgba(34, 211, 238, 0.05), rgba(255, 255, 255, 0.018));
    margin-left: 1.35rem;
}

.row-focused {
    border-color: rgba(59, 130, 246, 0.45);
}

.row-processing td {
    background: rgba(34, 211, 238, 0.05);
}

.event-card-kind-group {
    display: flex;
    align-items: center;
    gap: 0.45rem;
    flex-wrap: wrap;
    min-width: 0;
}

.kind-cell--child {
    padding-left: 0;
}

.row-child .date-cell,
.row-child .tokens-cell,
.row-child .error-cell {
    color: var(--color-text-muted);
}

.event-card-title-row {
    display: flex;
    align-items: flex-start;
    gap: 0.7rem;
    min-width: 0;
}

.event-card-title-row--child {
    padding-left: 0;
}

.event-title-stack {
    display: flex;
    flex-direction: column;
    align-items: stretch;
    justify-content: flex-start;
    gap: 0.45rem;
    min-width: 0;
    width: 100%;
}

.date-cell {
    font-size: 0.83rem;
    color: var(--color-text-muted);
    min-width: 0;
}

.tokens-cell {
    font-family: monospace;
    color: var(--color-text);
    min-width: 0;
}

.timing-inline-group {
    display: inline-flex;
    align-items: center;
    gap: 0.45rem;
    flex-wrap: wrap;
    min-width: 0;
}

.event-meta-line {
    display: flex;
    flex-wrap: wrap;
    gap: 0.45rem;
    min-width: 0;
}

.event-meta-pill {
    display: inline-flex;
    align-items: center;
    min-width: 0;
    max-width: 100%;
    padding: 0.16rem 0.5rem;
    border-radius: var(--radius-pill);
    background: rgba(255, 255, 255, 0.06);
    color: var(--color-text-muted);
    font-size: 0.76rem;
    line-height: 1.25;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.event-meta-pill--path,
.event-meta-pill--model {
    max-width: min(100%, 26rem);
}

.event-meta-pill--flag {
    color: var(--color-text);
    background: rgba(34, 211, 238, 0.09);
}

.event-match-snippet,
.event-context-snippet,
.event-context-limitation {
    max-width: 100%;
    overflow-wrap: anywhere;
    word-break: break-word;
}

.event-match-snippet {
    color: var(--color-text);
    font-size: 0.88rem;
    line-height: 1.5;
}

.event-context-snippet {
    color: var(--color-text-muted);
    font-size: 0.84rem;
    line-height: 1.5;
}

.timing-duration {
    font-weight: 600;
    white-space: nowrap;
    color: var(--color-text);
}

.timing-detail,
.timing-empty {
    font-size: 0.82rem;
    color: var(--color-text-muted);
}

.timing-badges {
    display: flex;
    flex-wrap: wrap;
    gap: 0.25rem;
}

.status-badge {
    display: inline-block;
    padding: 0.15rem 0.6rem;
    border-radius: var(--radius-pill);
    font-size: 0.75rem;
    font-weight: 600;
    text-transform: capitalize;
}

.status-processing {
    background: rgba(34, 211, 238, 0.15);
    color: var(--color-accent);
    animation: flash 2s cubic-bezier(0.4, 0, 0.6, 1) infinite;
}

.status-completed {
    background: rgba(34, 197, 94, 0.15);
    color: var(--color-success);
}

.status-failed {
    background: rgba(239, 68, 68, 0.16);
    color: var(--color-danger);
}

.status-pending {
    background: rgba(148, 163, 184, 0.16);
    color: var(--color-text-muted);
}

@keyframes flash {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.5; }
}

.kind-badge {
    display: inline-flex;
    align-items: center;
    padding: 0.18rem 0.58rem;
    border-radius: var(--radius-pill);
    font-size: 0.68rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.04em;
}

.kind-badge--suggestion { background: rgba(168, 85, 247, 0.15); color: var(--color-suggestion); }
.kind-badge--accent { background: rgba(34, 211, 238, 0.15); color: var(--color-accent); }
.kind-badge--success { background: rgba(52, 211, 153, 0.15); color: var(--color-success); }
.kind-badge--muted { background: rgba(255, 255, 255, 0.1); color: var(--color-text-muted); }

.tool-name { font-weight: 600; font-family: monospace; }
.ai-name { font-style: italic; color: var(--color-suggestion); }
.memory-name { color: var(--color-success); }
.operational-name { color: var(--color-text-muted); }

.kind-parent-pill {
    display: inline-flex;
    align-items: center;
    padding: 0.12rem 0.48rem;
    border-radius: var(--radius-pill);
    background: rgba(168, 85, 247, 0.14);
    color: var(--color-suggestion);
    font-size: 0.68rem;
    font-weight: 700;
    line-height: 1.1;
    white-space: nowrap;
}

.event-toggle {
    display: inline-flex;
    align-items: center;
    gap: 0.45rem;
    border: 1px solid rgba(168, 85, 247, 0.18);
    background: rgba(168, 85, 247, 0.14);
    color: var(--color-suggestion);
    border-radius: var(--radius-pill);
    padding: 0.14rem 0.48rem 0.14rem 0.22rem;
    cursor: pointer;
    font: inherit;
    min-width: 0;
    line-height: 1;
}

.event-toggle-icon {
    width: 1rem;
    height: 1rem;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    border-radius: var(--radius-pill);
    background: rgba(255, 255, 255, 0.08);
    flex: 0 0 auto;
}

.event-toggle-chevron {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    font-size: 0.8rem;
    line-height: 1;
    transition: transform 0.15s ease;
}

.event-toggle-chevron--collapsed {
    transform: rotate(-90deg);
}

.event-toggle-count {
    font-size: 0.74rem;
    font-weight: 700;
    line-height: 1;
    min-width: 0.9rem;
    text-align: center;
}

.event-child-rail {
    position: relative;
    display: block;
    width: 1rem;
    height: 100%;
    flex: 0 0 auto;
    min-height: 3.5rem;
}

.event-child-rail::before {
    content: '';
    position: absolute;
    right: 0.18rem;
    top: 1.2rem;
    width: 0.78rem;
    height: 1.5px;
    background: rgba(34, 211, 238, 0.42);
    border-radius: var(--radius-pill);
}

.event-child-rail::after {
    content: '';
    position: absolute;
    right: 0;
    top: 1.05rem;
    width: 0.34rem;
    height: 0.34rem;
    border-radius: var(--radius-pill);
    background: rgba(34, 211, 238, 0.78);
    box-shadow: 0 0 0 0.18rem rgba(34, 211, 238, 0.12);
}

.event-child-gutter {
    position: absolute;
    left: -1rem;
    top: 0.7rem;
    bottom: 0.7rem;
    display: flex;
    align-items: stretch;
    justify-content: flex-end;
    width: 1rem;
}

.event-name-label {
    display: inline-block;
    font-size: 0.98rem;
    font-weight: 600;
    line-height: 1.45;
    padding: 0.05rem 0;
    max-width: 100%;
    word-break: break-word;
    overflow-wrap: anywhere;
}

.cache-token-detail {
    color: var(--color-text-muted);
}

.monospace-value {
    font-family: var(--font-mono, monospace);
}

.markdown-content :deep(h1),
.markdown-content :deep(h2),
.markdown-content :deep(h3),
.markdown-content :deep(h4) {
    font-size: 1.1rem;
    margin: 1.25rem 0 0.5rem;
    color: var(--color-text);
    font-weight: 600;
}

.markdown-content :deep(p) {
    margin-bottom: 0.75rem;
    line-height: 1.6;
}
</style>
