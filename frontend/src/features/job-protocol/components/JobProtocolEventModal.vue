<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <ModalDialog v-model:isOpen="vm.isEventModalOpen" :title="vm.selectedMergedEvent?.name ?? 'Event Protocol'">
        <div v-if="vm.selectedMergedEvent" class="merged-modal-layout">
            <section v-if="vm.selectedTriageDecision" class="drawer-section">
                <h4>Triage decision</h4>
                <div class="parsed-json-block">
                    <div class="json-field"><span class="json-key">Tier:</span><pre class="json-content">{{ vm.selectedTriageDecision.tier }}</pre></div>
                    <div class="json-field"><span class="json-key">Security:</span><pre class="json-content">{{ vm.selectedTriageDecision.security }}</pre></div>
                    <div class="json-field"><span class="json-key">Blast radius:</span><pre class="json-content">{{ vm.selectedTriageDecision.blastRadius }}</pre></div>
                    <div class="json-field"><span class="json-key">Why:</span><pre class="json-content">{{ vm.selectedTriageDecision.why }}</pre></div>
                </div>
            </section>
            <section class="drawer-section">
                <h4>Input</h4>
                <template v-if="vm.parsedInputResult">
                    <div class="parsed-json-block">
                        <template v-if="vm.selectedCommentRelevanceInput">
                            <div class="json-field">
                                <span class="json-key">Implementation:</span>
                                <pre class="json-content">{{ vm.formatCommentRelevanceImplementation(vm.selectedCommentRelevanceInput) }}</pre>
                            </div>
                            <div class="json-field">
                                <span class="json-key">File:</span>
                                <pre class="json-content">{{ vm.selectedCommentRelevanceInput.filePath ?? 'Unknown' }}</pre>
                            </div>
                            <div class="json-field">
                                <span class="json-key">Counts:</span>
                                <pre class="json-content">{{ vm.formatCommentRelevanceCounts(vm.selectedCommentRelevanceInput) }}</pre>
                            </div>
                            <div class="json-field">
                                <span class="json-key">Degraded Components:</span>
                                <pre class="json-content">{{ vm.formatStringList(vm.selectedCommentRelevanceInput.degradedComponents) }}</pre>
                            </div>
                            <div class="json-field">
                                <span class="json-key">Fallback Checks:</span>
                                <pre class="json-content">{{ vm.formatStringList(vm.selectedCommentRelevanceInput.fallbackChecks) }}</pre>
                            </div>
                            <div class="json-field">
                                <span class="json-key">Degraded Cause:</span>
                                <pre class="json-content">{{ vm.selectedCommentRelevanceInput.degradedCause ?? 'None' }}</pre>
                            </div>
                        </template>
                        <template v-else-if="vm.selectedFinalGateInput">
                            <div class="json-field">
                                <span class="json-key">Counts:</span>
                                <pre class="json-content">{{ vm.formatFinalGateCounts(vm.selectedFinalGateInput) }}</pre>
                            </div>
                            <div class="json-field">
                                <span class="json-key">Category Counts:</span>
                                <pre class="json-content">{{ vm.formatNamedCounts(vm.selectedFinalGateInput.categoryCounts) }}</pre>
                            </div>
                            <div class="json-field">
                                <span class="json-key">Invariant-blocked Findings:</span>
                                <pre class="json-content">{{ vm.formatTokens(vm.selectedFinalGateInput.invariantBlockedCount) }}</pre>
                            </div>
                        </template>
                        <template v-else-if="vm.selectedVerificationInput">
                            <div class="json-field"><span class="json-key">Finding ID:</span><pre class="json-content">{{ vm.selectedVerificationInput.findingId ?? 'Unknown' }}</pre></div>
                            <div class="json-field"><span class="json-key">Claim ID:</span><pre class="json-content">{{ vm.selectedVerificationInput.claimId ?? 'None' }}</pre></div>
                            <div class="json-field"><span class="json-key">File:</span><pre class="json-content">{{ vm.selectedVerificationInput.filePath ?? 'None' }}</pre></div>
                            <div class="json-field"><span class="json-key">Stage:</span><pre class="json-content">{{ vm.selectedVerificationInput.stage ?? 'None' }}</pre></div>
                            <div class="json-field"><span class="json-key">Coverage / Counts:</span><pre class="json-content">{{ vm.selectedVerificationInput.coverageState ?? 'n/a' }} / claims={{ vm.selectedVerificationInput.claimCount ?? 0 }} / dropped={{ vm.selectedVerificationInput.droppedCount ?? 0 }} / summary-only={{ vm.selectedVerificationInput.summaryOnlyCount ?? 0 }}</pre></div>
                            <div class="json-field"><span class="json-key">Degraded Component:</span><pre class="json-content">{{ vm.selectedVerificationInput.degradedComponent ?? 'None' }}</pre></div>
                        </template>
                        <template v-else-if="vm.selectedMergedEvent.callDetails.kind === 'memoryOperation' && vm.selectedMergedEvent.callDetails.name === 'memory_reconsideration_completed' && typeof vm.parsedInputResult === 'object'">
                            <div class="json-field"><span class="json-key">File:</span><pre class="json-content">{{ vm.parsedInputResult.filePath }}</pre></div>
                            <div class="json-field"><span class="json-key">Comment counts:</span><pre class="json-content">{{ vm.parsedInputResult.originalCommentCount }} original → {{ vm.parsedInputResult.finalCommentCount }} final ({{ vm.parsedInputResult.retainedCount }} retained, {{ vm.parsedInputResult.discardedCount }} discarded, {{ vm.parsedInputResult.downgradedCount }} downgraded)</pre></div>
                            <div v-if="vm.parsedInputResult.contributingMemoryIds?.length" class="json-field">
                                <span class="json-key">Memory IDs used ({{ vm.parsedInputResult.contributingMemoryIds.length }}):</span>
                                <pre class="json-content">{{ vm.parsedInputResult.contributingMemoryIds.join('\n') }}</pre>
                            </div>
                            <div v-if="vm.parsedInputResult.discarded?.length" class="json-field">
                                <span class="json-key">Discarded ({{ vm.parsedInputResult.discarded.length }}):</span>
                                <div class="memory-comment-list">
                                    <div v-for="(c, i) in vm.parsedInputResult.discarded" :key="i" class="memory-comment-row">
                                        <div class="memory-comment-header">
                                            <span class="memory-sev-chip" :class="`memory-sev-chip--${c.severity}`">{{ (c.severity ?? 'note').toUpperCase() }}</span>
                                            <span class="monospace-value memory-comment-loc">{{ c.filePath }}:L{{ c.lineNumber }}</span>
                                        </div>
                                        <p class="memory-comment-msg">{{ c.message }}</p>
                                    </div>
                                </div>
                            </div>
                            <div v-if="vm.parsedInputResult.downgraded?.length" class="json-field">
                                <span class="json-key">Downgraded ({{ vm.parsedInputResult.downgraded.length }}):</span>
                                <div class="memory-comment-list">
                                    <div v-for="(c, i) in vm.parsedInputResult.downgraded" :key="i" class="memory-comment-row">
                                        <div class="memory-comment-header">
                                            <span class="memory-sev-chip" :class="`memory-sev-chip--${c.originalSeverity}`">{{ (c.originalSeverity ?? '').toUpperCase() }}</span>
                                            <i class="fi fi-rr-arrow-right memory-downgrade-arrow"></i>
                                            <span class="memory-sev-chip" :class="`memory-sev-chip--${c.newSeverity}`">{{ (c.newSeverity ?? '').toUpperCase() }}</span>
                                            <span class="monospace-value memory-comment-loc">{{ c.filePath }}:L{{ c.lineNumber }}</span>
                                        </div>
                                        <p class="memory-comment-msg">{{ c.message }}</p>
                                    </div>
                                </div>
                            </div>
                            <div v-if="!vm.parsedInputResult.discarded?.length && !vm.parsedInputResult.downgraded?.length" class="json-field">
                                <span class="json-key">Changes:</span>
                                <pre class="json-content">All comments retained unchanged.</pre>
                            </div>
                        </template>
                        <template v-else-if="vm.selectedMergedEvent.callDetails.kind === 'toolCall' && typeof vm.parsedInputResult === 'object'">
                            <div v-for="(val, key) in vm.parsedInputResult" :key="key" class="json-field">
                                <span class="json-key">{{ key }}:</span>
                                <pre class="json-content">{{ typeof val === 'string' ? val : JSON.stringify(val, null, 2) }}</pre>
                            </div>
                        </template>
                        <pre v-else class="content-block">{{ JSON.stringify(vm.parsedInputResult, null, 2) }}</pre>
                    </div>
                </template>
                <pre v-else-if="vm.selectedMergedEvent.callDetails.inputTextSample" class="content-block">{{ vm.selectedMergedEvent.callDetails.inputTextSample }}</pre>
                <p v-else class="no-content">No input captured.</p>
            </section>

            <section class="drawer-section">
                <div class="drawer-section-header">
                    <h4>Output</h4>
                    <span v-if="vm.selectedMergedEvent.callDetails.kind === 'aiCall'" class="ai-disclaimer"><i class="fi fi-rr-magic-wand"></i> AI-generated content</span>
                </div>
                <div v-if="vm.selectedAiCallProviderManagedNote" class="provider-managed-note">
                    <i class="fi fi-rr-info"></i>
                    <span>{{ vm.selectedAiCallProviderManagedNote }}</span>
                </div>
                <template v-if="vm.selectedMergedEvent.resultDetails">
                    <div v-if="vm.parsedOutputResult" class="parsed-json-block">
                        <template v-if="vm.selectedCommentRelevanceOutput">
                            <div class="json-field"><span class="json-key">Implementation:</span><pre class="json-content">{{ vm.formatCommentRelevanceImplementation(vm.selectedCommentRelevanceOutput) }}</pre></div>
                            <div class="json-field"><span class="json-key">Counts:</span><pre class="json-content">{{ vm.formatCommentRelevanceCounts(vm.selectedCommentRelevanceOutput) }}</pre></div>
                            <div class="json-field"><span class="json-key">Reason Buckets:</span><pre class="json-content">{{ vm.formatNamedCounts(vm.selectedCommentRelevanceOutput.reasonBuckets) }}</pre></div>
                            <div class="json-field"><span class="json-key">Decision Sources:</span><pre class="json-content">{{ vm.formatNamedCounts(vm.selectedCommentRelevanceOutput.decisionSources) }}</pre></div>
                            <div class="json-field"><span class="json-key">Degraded Components:</span><pre class="json-content">{{ vm.formatStringList(vm.selectedCommentRelevanceOutput.degradedComponents) }}</pre></div>
                            <div class="json-field"><span class="json-key">Fallback Checks:</span><pre class="json-content">{{ vm.formatStringList(vm.selectedCommentRelevanceOutput.fallbackChecks) }}</pre></div>
                            <div class="json-field"><span class="json-key">Degraded Cause:</span><pre class="json-content">{{ vm.selectedCommentRelevanceOutput.degradedCause ?? 'None' }}</pre></div>
                            <div v-if="vm.selectedCommentRelevanceOutput.aiTokenUsage" class="json-field"><span class="json-key">AI Token Usage:</span><pre class="json-content">{{ vm.formatCommentRelevanceAiUsage(vm.selectedCommentRelevanceOutput.aiTokenUsage) }}</pre></div>
                            <div class="json-field">
                                <span class="json-key">Discarded ({{ vm.selectedCommentRelevanceOutput.discarded?.length ?? 0 }}):</span>
                                <div v-if="vm.selectedCommentRelevanceOutput.discarded?.length" class="memory-comment-list">
                                    <div v-for="(comment, idx) in vm.selectedCommentRelevanceOutput.discarded" :key="idx" class="memory-comment-row">
                                        <div class="memory-comment-header">
                                            <span class="memory-sev-chip" :class="`memory-sev-chip--${vm.severityVariant(comment.severity)}`">{{ (comment.severity ?? 'note').toUpperCase() }}</span>
                                            <span class="monospace-value memory-comment-loc">{{ vm.commentLocation(comment.filePath, comment.lineNumber) }}</span>
                                        </div>
                                        <p class="memory-comment-msg">{{ comment.message }}</p>
                                        <div class="comment-relevance-meta">
                                            <span v-if="comment.decisionSource" class="monospace-badge">{{ comment.decisionSource }}</span>
                                            <span v-for="reasonCode in comment.reasonCodes ?? []" :key="reasonCode" class="monospace-badge">{{ reasonCode }}</span>
                                        </div>
                                    </div>
                                </div>
                                <pre v-else class="json-content">No comments discarded.</pre>
                            </div>
                        </template>
                        <template v-else-if="vm.selectedFinalGateSummaryOutput">
                            <div class="json-field"><span class="json-key">Counts:</span><pre class="json-content">{{ vm.formatFinalGateCounts(vm.selectedFinalGateSummaryOutput) }}</pre></div>
                            <div class="json-field"><span class="json-key">Category Counts:</span><pre class="json-content">{{ vm.formatNamedCounts(vm.selectedFinalGateSummaryOutput.categoryCounts) }}</pre></div>
                            <div class="json-field"><span class="json-key">Invariant-blocked Findings:</span><pre class="json-content">{{ vm.formatTokens(vm.selectedFinalGateSummaryOutput.invariantBlockedCount) }}</pre></div>
                            <div class="json-field"><span class="json-key">Original Summary:</span><pre class="json-content">{{ vm.selectedFinalGateSummaryOutput.originalSummary ?? 'None' }}</pre></div>
                            <div class="json-field"><span class="json-key">Final Summary:</span><pre class="json-content">{{ vm.selectedFinalGateSummaryOutput.finalSummary ?? 'None' }}</pre></div>
                            <div class="json-field"><span class="json-key">Summary Rewrite:</span><pre class="json-content">{{ vm.selectedFinalGateSummaryOutput.summaryRewritePerformed ? 'Yes' : 'No' }}</pre></div>
                            <div class="json-field"><span class="json-key">Dropped Finding IDs:</span><pre class="json-content">{{ vm.formatStringList(vm.selectedFinalGateSummaryOutput.droppedFindingIds) }}</pre></div>
                            <div class="json-field"><span class="json-key">Summary-only Finding IDs:</span><pre class="json-content">{{ vm.formatStringList(vm.selectedFinalGateSummaryOutput.summaryOnlyFindingIds) }}</pre></div>
                            <div class="json-field"><span class="json-key">Summary Rule Source:</span><pre class="json-content">{{ vm.selectedFinalGateSummaryOutput.summaryRuleSource ?? 'None' }}</pre></div>
                        </template>
                        <template v-else-if="vm.selectedFinalGateDecisionOutput">
                            <div class="json-field"><span class="json-key">Finding ID:</span><pre class="json-content">{{ vm.selectedFinalGateDecisionOutput.findingId ?? 'Unknown' }}</pre></div>
                            <div class="json-field"><span class="json-key">Disposition:</span><pre class="json-content">{{ vm.selectedFinalGateDecisionOutput.disposition ?? 'Unknown' }}</pre></div>
                            <div class="json-field"><span class="json-key">Category:</span><pre class="json-content">{{ vm.selectedFinalGateDecisionOutput.category ?? 'Unknown' }}</pre></div>
                            <div class="json-field"><span class="json-key">Rule Source:</span><pre class="json-content">{{ vm.selectedFinalGateDecisionOutput.ruleSource ?? 'Unknown' }}</pre></div>
                            <div class="json-field"><span class="json-key">Provenance:</span><pre class="json-content">{{ vm.formatFinalGateProvenance(vm.selectedFinalGateDecisionOutput.provenance) }}</pre></div>
                            <div class="json-field"><span class="json-key">Evidence:</span><pre class="json-content">{{ vm.formatFinalGateEvidence(vm.selectedFinalGateDecisionOutput.evidence) }}</pre></div>
                            <div class="json-field"><span class="json-key">Reason Codes:</span><pre class="json-content">{{ vm.formatStringList(vm.selectedFinalGateDecisionOutput.reasonCodes) }}</pre></div>
                            <div class="json-field"><span class="json-key">Blocked Invariants:</span><pre class="json-content">{{ vm.formatStringList(vm.selectedFinalGateDecisionOutput.blockedInvariantIds) }}</pre></div>
                            <div class="json-field"><span class="json-key">Summary Text:</span><pre class="json-content">{{ vm.selectedFinalGateDecisionOutput.summaryText ?? 'None' }}</pre></div>
                            <div class="json-field"><span class="json-key">Included In Final Summary:</span><pre class="json-content">{{ vm.selectedFinalGateDecisionOutput.includedInFinalSummary ? 'Yes' : 'No' }}</pre></div>
                        </template>
                        <template v-else-if="vm.selectedVerificationEvidenceOutput">
                            <div class="json-field"><span class="json-key">Coverage State:</span><pre class="json-content">{{ vm.selectedVerificationEvidenceOutput.coverageState ?? 'Unknown' }}</pre></div>
                            <div class="json-field"><span class="json-key">ProCursor Result:</span><pre class="json-content">{{ vm.formatVerificationProCursorStatus(vm.selectedVerificationEvidenceOutput) }}</pre></div>
                            <div class="json-field"><span class="json-key">Evidence Attempts:</span><pre class="json-content">{{ vm.formatEvidenceAttempts(vm.selectedVerificationEvidenceOutput.evidenceAttempts) }}</pre></div>
                            <div class="json-field"><span class="json-key">Evidence Items:</span><pre class="json-content">{{ vm.formatEvidenceItems(vm.selectedVerificationEvidenceOutput.evidenceItems) }}</pre></div>
                            <div class="json-field"><span class="json-key">Retrieval Notes:</span><pre class="json-content">{{ vm.selectedVerificationEvidenceOutput.retrievalNotes ?? 'None' }}</pre></div>
                        </template>
                        <template v-else-if="vm.selectedVerificationOutput">
                            <div v-for="(val, key) in vm.selectedVerificationOutput" :key="key" class="json-field">
                                <span class="json-key">{{ key }}:</span>
                                <pre class="json-content">{{ typeof val === 'string' ? val : JSON.stringify(val, null, 2) }}</pre>
                            </div>
                        </template>
                        <template v-else-if="vm.selectedAgenticInvestigationOutput">
                            <div class="json-field"><span class="json-key">Stage B status:</span><pre class="json-content">{{ vm.formatAgenticInvestigationStatus(vm.selectedAgenticInvestigationOutput) }}</pre></div>
                            <div class="json-field"><span class="json-key">Runtime tool attempts:</span><pre class="json-content">{{ vm.formatAgenticToolUsage(vm.selectedAgenticInvestigationOutput) }}</pre></div>
                            <div v-if="vm.agenticInvestigationCandidateCount(vm.selectedAgenticInvestigationOutput) !== null" class="json-field"><span class="json-key">Candidate count:</span><pre class="json-content">{{ vm.agenticInvestigationCandidateCount(vm.selectedAgenticInvestigationOutput) }}</pre></div>
                            <div v-if="vm.agenticInvestigationEvidenceCount(vm.selectedAgenticInvestigationOutput) !== null" class="json-field"><span class="json-key">Evidence count:</span><pre class="json-content">{{ vm.agenticInvestigationEvidenceCount(vm.selectedAgenticInvestigationOutput) }}</pre></div>
                            <div v-if="vm.isAgenticDegradedEvent(vm.selectedMergedEvent?.callDetails.name)" class="json-field"><span class="json-key">Degraded outcome note:</span><pre class="json-content">This degraded Stage B result was a non-validated intermediate outcome. It only affected the final review if later verification and final gate kept supporting findings.</pre></div>
                        </template>
                        <pre v-else-if="typeof vm.parsedOutputResult === 'string'" class="content-block">{{ vm.decodeHtmlEntities(vm.parsedOutputResult) }}</pre>
                        <template v-else>
                            <div v-if="vm.parsedOutputResult.summary" class="json-field">
                                <span class="json-key">Summary:</span>
                                <p class="json-summary-text">{{ vm.parsedOutputResult.summary }}</p>
                            </div>
                            <div v-if="vm.parsedOutputResult.comments?.length" class="json-field">
                                <span class="json-key">Comments ({{ vm.parsedOutputResult.comments.length }}):</span>
                                <ul class="json-comments-list">
                                    <li v-for="(comment, idx) in vm.parsedOutputResult.comments" :key="idx" class="json-comment-item" :class="`severity-${comment.severity}`">
                                        <strong>{{ (comment.severity ?? 'note').toUpperCase() }}</strong> at <span class="monospace-value">{{ comment.file_path }}:L{{ comment.line_number }}</span><br />
                                        {{ comment.message }}
                                    </li>
                                </ul>
                            </div>
                            <template v-if="!vm.parsedOutputResult.summary && !vm.parsedOutputResult.comments">
                                <pre class="content-block">{{ JSON.stringify(vm.parsedOutputResult, null, 2) }}</pre>
                            </template>
                        </template>
                    </div>
                    <pre v-else-if="vm.selectedMergedEvent.resultDetails.outputSummary !== null" class="content-block">{{ vm.renderMergedEventText(vm.selectedMergedEvent.resultDetails.outputSummary) }}</pre>
                    <template v-else-if="vm.selectedMergedEvent.resultDetails.error !== null">
                        <pre class="content-block error-block">{{ vm.selectedMergedEvent.resultDetails.error }}</pre>
                    </template>
                    <div v-else-if="vm.selectedMergedEvent.callDetails.kind === 'memoryOperation'" class="memory-no-output">
                        <i class="fi fi-rr-check-circle memory-no-output-icon"></i>
                        <span>No output recorded for this memory operation.</span>
                    </div>
                    <p v-else-if="vm.isMergedEventProcessing(vm.selectedMergedEvent)" class="no-content no-content-processing">Currently Executing...</p>
                    <p v-else class="no-content">No output captured for this completed step.</p>
                </template>
            </section>

            <section v-if="vm.hasToolTiming(vm.selectedMergedEvent.callDetails)" class="tool-timing-panel">
                <div class="tool-timing-header">
                    <h4>Tool Timing</h4>
                    <span v-if="vm.selectedToolPhaseTimings.length" class="tool-phase-overview-chip">
                        {{ vm.formatPhaseCountSummary(vm.selectedToolPhaseTimings.length, vm.modalPhaseGroups.length || vm.getEventTimingPresentation(vm.selectedMergedEvent.callDetails).phaseGroupCount, true) }}
                    </span>
                </div>
                <dl class="tool-timing-grid">
                    <div><dt>Availability</dt><dd>{{ vm.formatTimingAvailability(vm.selectedMergedEvent.callDetails.timingAvailability) }}</dd></div>
                    <div v-if="vm.selectedMergedEvent.callDetails.toolOutcome"><dt>Outcome</dt><dd>{{ vm.formatToolOutcome(vm.selectedMergedEvent.callDetails.toolOutcome) }}</dd></div>
                    <div v-if="vm.selectedMergedEvent.callDetails.startedAt"><dt>Started</dt><dd>{{ vm.formatDate(vm.selectedMergedEvent.callDetails.startedAt) }}</dd></div>
                    <div v-if="vm.selectedMergedEvent.callDetails.completedAt"><dt>Completed</dt><dd>{{ vm.formatDate(vm.selectedMergedEvent.callDetails.completedAt) }}</dd></div>
                    <div v-if="vm.selectedMergedEvent.callDetails.durationMs != null"><dt>Duration</dt><dd>{{ vm.formatDurationWithMs(vm.selectedMergedEvent.callDetails.durationMs) }}</dd></div>
                    <div v-if="vm.selectedMergedEvent.callDetails.activeDurationMs != null"><dt>Active</dt><dd>{{ vm.formatDurationWithMs(vm.selectedMergedEvent.callDetails.activeDurationMs) }}</dd></div>
                    <div v-if="vm.selectedMergedEvent.callDetails.waitDurationMs != null"><dt>Wait</dt><dd>{{ vm.formatDurationWithMs(vm.selectedMergedEvent.callDetails.waitDurationMs) }}</dd></div>
                    <div v-if="vm.selectedToolPhaseTimings.length"><dt>Phases</dt><dd>{{ vm.formatPhaseCountSummary(vm.selectedToolPhaseTimings.length, vm.modalPhaseGroups.length || vm.getEventTimingPresentation(vm.selectedMergedEvent.callDetails).phaseGroupCount) }}</dd></div>
                </dl>
                <div v-if="vm.modalPhaseGroupsPending" class="tool-phase-section">
                    <h5>Phase Breakdown</h5>
                    <p class="tool-phase-loading">Preparing phase breakdown…</p>
                </div>
                <div v-else-if="vm.modalPhaseGroups.length" class="tool-phase-section">
                    <div class="tool-phase-section-header"><h5>Phase Breakdown</h5></div>
                    <ol class="tool-phase-list">
                        <li v-for="group in vm.modalPhaseGroups" :key="group.key" class="tool-phase-item">
                            <div class="tool-phase-header">
                                <div class="tool-phase-title-group">
                                    <span class="tool-phase-title">{{ group.title }}</span>
                                    <span v-if="group.count > 1" class="tool-phase-count-pill">{{ group.count }} occurrences</span>
                                </div>
                                <span class="tool-phase-duration">{{ vm.formatToolPhaseGroupDuration(group) }}</span>
                            </div>
                            <div class="tool-phase-meta">
                                <span>{{ vm.formatTimingAvailability(group.availability) }}</span>
                                <span v-if="group.outcome">{{ vm.formatToolOutcome(group.outcome) }}</span>
                                <span v-if="group.startedAt">First started {{ vm.formatDate(group.startedAt) }}</span>
                                <span v-if="group.completedAt">Last completed {{ vm.formatDate(group.completedAt) }}</span>
                            </div>
                            <p v-if="group.summary" class="tool-phase-summary">{{ group.summary }}</p>
                            <button v-if="group.count > 1" type="button" class="tool-phase-toggle" @click="vm.togglePhaseGroup(group.key)">
                                {{ vm.isPhaseGroupExpanded(group.key) ? 'Hide raw occurrences' : 'Show raw occurrences' }}
                            </button>
                            <ol v-if="group.count > 1 && vm.isPhaseGroupExpanded(group.key)" class="tool-phase-occurrence-list">
                                <li v-for="phase in group.phases" :key="`${group.key}:${phase.sequence ?? phase.occurrence ?? vm.formatPhaseTitle(phase)}`" class="tool-phase-occurrence-item">
                                    <div class="tool-phase-header">
                                        <span class="tool-phase-title">{{ vm.formatPhaseTitle(phase) }}</span>
                                        <span class="tool-phase-duration">{{ vm.formatPhaseDuration(phase) }}</span>
                                    </div>
                                    <div class="tool-phase-meta">
                                        <span>{{ vm.formatTimingAvailability(phase.availability) }}</span>
                                        <span v-if="phase.outcome">{{ vm.formatToolOutcome(phase.outcome) }}</span>
                                        <span v-if="phase.startedAt">Started {{ vm.formatDate(phase.startedAt) }}</span>
                                        <span v-if="phase.completedAt">Completed {{ vm.formatDate(phase.completedAt) }}</span>
                                    </div>
                                    <p v-if="phase.summary" class="tool-phase-summary">{{ phase.summary }}</p>
                                </li>
                            </ol>
                        </li>
                    </ol>
                </div>
            </section>
        </div>
    </ModalDialog>
</template>

<script setup lang="ts">
import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import type { JobProtocolViewModel } from '@/features/job-protocol/composables/useJobProtocolViewModel'

defineProps<{ vm: JobProtocolViewModel }>()
</script>

<style scoped>
.merged-modal-layout {
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
}

.drawer-section {
    margin-bottom: 2rem;
}

.drawer-section h4 {
    margin: 0 0 0.75rem;
    font-size: 0.85rem;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.drawer-section-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 0.75rem;
}

.drawer-section-header h4 {
    margin-bottom: 0;
}

.content-block {
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    padding: 1rem;
    font-size: 0.85rem;
    overflow-x: auto;
    white-space: pre-wrap;
    word-break: break-word;
    max-height: 40vh;
    overflow-y: auto;
    margin: 0;
    color: var(--color-text-muted);
}

.no-content {
    color: var(--color-text-muted);
    font-style: italic;
    font-size: 0.9rem;
    margin: 0;
}

.no-content-processing {
    color: var(--color-accent);
    font-weight: bold;
}

.parsed-json-block {
    display: flex;
    flex-direction: column;
    gap: 1rem;
    font-size: 0.85rem;
}

.json-field {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}

.json-key {
    font-weight: 600;
    color: var(--color-text-muted);
    text-transform: uppercase;
    font-size: 0.75rem;
    letter-spacing: 0.05em;
}

.json-content {
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-sm);
    padding: 0.75rem;
    margin: 0;
    white-space: pre-wrap;
    word-break: break-all;
    font-family: monospace;
    color: var(--color-text);
}

.json-summary-text {
    margin: 0;
    line-height: 1.5;
    color: var(--color-text);
}

.json-comments-list {
    margin: 0;
    padding: 0 0 0 1.25rem;
    color: var(--color-text);
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
}

.json-comment-item.severity-error { border-left: 2px solid var(--color-danger); padding-left: 0.5rem; list-style-type: none; margin-left: -1.25rem; }
.json-comment-item.severity-warning { border-left: 2px solid var(--color-warning); padding-left: 0.5rem; list-style-type: none; margin-left: -1.25rem; }
.json-comment-item.severity-suggestion { border-left: 2px solid var(--color-accent); padding-left: 0.5rem; list-style-type: none; margin-left: -1.25rem; }
.json-comment-item.severity-info, .json-comment-item.severity-note { border-left: 2px solid var(--color-info); padding-left: 0.5rem; list-style-type: none; margin-left: -1.25rem; }

.monospace-value {
    font-family: var(--font-mono, monospace);
}

.monospace-badge {
    font-family: var(--font-mono, monospace);
    background: rgba(255, 255, 255, 0.05);
    padding: 0.1rem 0.4rem;
    border-radius: var(--radius-xs);
    font-size: 0.8rem;
}

.memory-comment-list {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    margin-top: 0.25rem;
}

.memory-comment-row {
    background: rgba(255, 255, 255, 0.025);
    border: 1px solid var(--color-border);
    border-radius: var(--radius-md);
    padding: 0.6rem 0.875rem;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}

.memory-comment-header {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
}

.memory-comment-loc {
    color: var(--color-text-muted);
    font-size: 0.78rem;
    margin-left: auto;
}

.memory-comment-msg {
    margin: 0;
    font-size: 0.82rem;
    color: var(--color-text-muted);
    line-height: 1.5;
}

.comment-relevance-meta {
    display: flex;
    gap: 0.35rem;
    flex-wrap: wrap;
}

.memory-downgrade-arrow {
    color: var(--color-text-muted);
    font-size: 0.65rem;
}

.memory-sev-chip {
    display: inline-flex;
    align-items: center;
    padding: 0.1rem 0.5rem;
    border-radius: var(--radius-pill);
    font-size: 0.68rem;
    font-weight: 700;
    letter-spacing: 0.04em;
    flex-shrink: 0;
}

.memory-sev-chip--error { background: rgba(239, 68, 68, 0.15); color: var(--color-danger); }
.memory-sev-chip--warning { background: rgba(234, 179, 8, 0.15); color: var(--color-warning); }
.memory-sev-chip--info { background: rgba(34, 211, 238, 0.12); color: var(--color-accent); }
.memory-sev-chip--suggestion { background: rgba(168, 85, 247, 0.12); color: var(--color-suggestion); }
.memory-sev-chip--note { background: rgba(255, 255, 255, 0.08); color: var(--color-text-muted); }

.memory-no-output {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    color: var(--color-text-muted);
    font-size: 0.85rem;
    padding: 0.75rem 0;
}

.memory-no-output-icon {
    color: var(--color-success);
    font-size: 1rem;
    flex-shrink: 0;
}

.ai-disclaimer {
    font-size: 0.75rem;
    color: var(--color-accent);
    font-weight: 500;
}

.provider-managed-note {
    display: flex;
    gap: 0.5rem;
    align-items: flex-start;
    margin-bottom: 0.75rem;
    padding: 0.65rem 0.8rem;
    border-radius: var(--radius-md);
    border: 1px solid rgba(59, 130, 246, 0.22);
    background: rgba(59, 130, 246, 0.08);
    color: var(--color-text-muted);
    font-size: 0.78rem;
    line-height: 1.45;
}

.provider-managed-note i {
    color: var(--color-info);
    margin-top: 0.1rem;
    flex-shrink: 0;
}

.tool-timing-panel {
    margin-bottom: 1rem;
    padding: 1rem;
    border: 1px solid var(--color-border);
    border-radius: var(--radius-lg);
    background: rgba(255, 255, 255, 0.02);
}

.tool-timing-header,
.tool-phase-section-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 0.75rem;
    flex-wrap: wrap;
}

.tool-timing-header {
    margin-bottom: 0.75rem;
}

.tool-timing-header h4,
.tool-phase-section-header h5,
.tool-phase-section h5 {
    margin: 0;
}

.tool-phase-overview-chip,
.tool-phase-count-pill {
    display: inline-flex;
    align-items: center;
    padding: 0.2rem 0.55rem;
    border-radius: var(--radius-pill);
    background: rgba(34, 211, 238, 0.12);
    color: var(--color-accent);
    font-size: 0.75rem;
    font-weight: 600;
}

.tool-phase-loading {
    margin: 0;
    color: var(--color-text-muted);
    font-size: 0.85rem;
}

.tool-timing-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
    gap: 0.75rem 1rem;
    margin: 0 0 1rem;
}

.tool-timing-grid div {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}

.tool-timing-grid dt {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.tool-timing-grid dd {
    margin: 0;
}

.tool-phase-section h5 {
    font-size: 0.8rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--color-text-muted);
}

.tool-phase-list {
    list-style: none;
    padding: 0;
    margin: 0;
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
}

.tool-phase-item {
    padding: 0.85rem 1rem;
    border-radius: var(--radius-lg);
    background: rgba(255, 255, 255, 0.03);
    border: 1px solid rgba(255, 255, 255, 0.04);
}

.tool-phase-title-group {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-wrap: wrap;
}

.tool-phase-header,
.tool-phase-meta {
    display: flex;
    flex-wrap: wrap;
    gap: 0.5rem 0.75rem;
    justify-content: space-between;
}

.tool-phase-meta {
    margin-top: 0.35rem;
    color: var(--color-text-muted);
    font-size: 0.8rem;
}

.tool-phase-summary {
    margin: 0.5rem 0 0;
    color: var(--color-text);
}

.tool-phase-toggle {
    appearance: none;
    border: 1px solid var(--color-border);
    background: rgba(255, 255, 255, 0.03);
    color: var(--color-text);
    border-radius: var(--radius-md);
    padding: 0.35rem 0.7rem;
    font-size: 0.8rem;
    cursor: pointer;
}

.tool-phase-toggle:hover {
    background: rgba(255, 255, 255, 0.06);
}

.tool-phase-occurrence-list {
    list-style: decimal;
    margin: 0.85rem 0 0;
    padding-left: 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 0.65rem;
}

.tool-phase-occurrence-item {
    padding: 0.75rem 0.85rem;
    border-radius: var(--radius-md);
    background: rgba(255, 255, 255, 0.025);
    border: 1px solid rgba(255, 255, 255, 0.035);
}

.error-block {
    color: var(--color-danger);
    background: rgba(239, 68, 68, 0.05) !important;
    border-color: rgba(239, 68, 68, 0.2) !important;
}
</style>
