<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="page-view">
        <AppTopBar class="header-stack">
            <div class="header-nav-links">
                <RouterLink class="back-link" :to="backToReviewsLink">← Back to reviews</RouterLink>
                <RouterLink v-if="prReviewLink" :to="prReviewLink" class="back-link pr-view-link">PR Review ↗</RouterLink>
            </div>
            <h2>Job Protocol</h2>
        </AppTopBar>

        <p v-if="loading" class="loading">Loading…</p>
        <p v-else-if="error" class="error">{{ error }}</p>
        <p v-else-if="protocols.length === 0" class="empty-state">No protocol available for this job.</p>

        <template v-else>
            <!-- Aggregated totals across all passes -->


            <!-- Page Header Stats (Compact) -->
            <div class="job-stat-strip compact-stats">
                <div class="stat-pill"><span class="stat-label">Job</span><span class="stat-value monospace-value" :title="protocols[0].jobId">{{ jobShortId }}</span></div>
                <div class="stat-pill"><span class="stat-label">Duration</span><span class="stat-value">{{ overallDuration }}</span></div>
                <div class="stat-pill"><span class="stat-label">Visible Passes</span><span class="stat-value">{{ protocols.length }}</span></div>
                <div class="stat-pill"><span class="stat-label">Inherited</span><span class="stat-value">{{ inheritedProtocolCount }}</span></div>
                <div class="stat-pill"><span class="stat-label">Total Tokens</span><span class="stat-value fat-tokens">{{ formatTokens(totalInputTokens + totalOutputTokens) }}</span></div>
                <div class="stat-pill"><span class="stat-label">Cached Input</span><span class="stat-value fat-tokens">{{ formatTokens(totalCachedInputTokens) }}</span></div>
                <div class="stat-pill"><span class="stat-label">Effective Input</span><span class="stat-value fat-tokens">{{ formatTokens(totalEffectiveInputTokens) }}</span></div>
            </div>

            <!-- Top Level Tabs -->
            <div class="detail-tabs">
                <button class="tab-btn" :class="{ 'tab-active': activeTab === 'summary' }" @click="activeTab = 'summary'">Review Summary</button>
                <button class="tab-btn" :class="{ 'tab-active': activeTab === 'traces' }" @click="activeTab = 'traces'">Execution Traces</button>
                <button class="tab-btn" :class="{ 'tab-active': activeTab === 'tokens' }" @click="activeTab = 'tokens'">Token Breakdown</button>
            </div>

            <!-- Tab 1: Review Summary (Master-Detail) -->
            <div class="protocol-master-detail summary-master-detail" v-if="activeTab === 'summary'">
                <!-- Left Sidebar: Comment Navigation -->
                <nav class="protocol-sidebar" aria-label="Comment Navigation">
                    <div class="sidebar-search-container">
                        <i class="fi fi-rr-search search-icon"></i>
                        <input
                            v-model="globalSearchQuery"
                            type="text"
                            class="sidebar-search-input"
                            placeholder="Filter paths..."
                        />
                    </div>
                    <div v-for="item in commentSidebarItems" :key="item.path" class="sidebar-tree-node">
                        <!-- Folder -->
                        <button
                            v-if="item.type === 'folder'"
                            class="folder-header tree-folder-btn"
                            :class="{ 'active-folder': selectedCommentPath === item.path || (selectedCommentPath?.startsWith(item.path + '/')) }"
                            :style="{ paddingLeft: (item.depth * 1.5) + 'rem' }"
                            @click="toggleFolder('comments:' + item.path); selectedCommentPath = item.path"
                        >
                            <i class="fi fi-rr-angle-small-down folder-chevron" :class="{ collapsed: item.isCollapsed }"></i>
                            <i class="fi" :class="item.isCollapsed ? 'fi-rr-folder' : 'fi-rr-folder-open'"></i>
                            <span class="folder-name">{{ item.name }}</span>
                        </button>

                        <!-- File -->
                        <button
                            v-else
                            class="pass-nav-item tree-pass-btn comment-node-btn"
                            :class="{ 'active': selectedCommentPath === item.path }"
                            :style="{ paddingLeft: (item.depth * 1.5) + 'rem' }"
                            @click="selectedCommentPath = item.path"
                        >
                            <i class="fi fi-rr-file-code file-icon"></i>
                            <div class="pass-nav-info">
                                <span class="pass-nav-filename">{{ item.name }}</span>
                                <span class="comment-count-pill">{{ item.commentCount }}</span>
                            </div>
                        </button>
                    </div>
                </nav>

                <!-- Right Detail: Summary & Filtered Comments -->
                <div class="protocol-content">
                    <div class="synthesis-main">
                        <div class="ai-disclaimer">
                            <i class="fi fi-rr-magic-wand ai-icon"></i>
                            <span class="ai-text">This review was generated by an AI assistant. It may contain inaccuracies or hallucinations.</span>
                        </div>

                        <!-- Summary Dashboard (shown when no file is selected) -->
                        <div v-if="!selectedCommentPath" class="summary-dashboard">
                            <div class="summary-dashboard-header">
                                <div class="title-group">
                                    <h3>Review Summary</h3>
                                    <p class="subtitle">Aggregated findings and executive summary</p>
                                </div>
                                <button class="ui-button primary-action" @click="isSummaryModalOpen = true">
                                    <i class="fi fi-rr-expand"></i>
                                    View Full Summary & Findings
                                </button>
                            </div>

                            <div class="summary-preview-card">
                                <div class="overview-metadata-grid">
                                    <div class="overview-metadata-item">
                                        <span class="overview-metadata-label">Model</span>
                                        <span class="overview-metadata-value monospace-value">{{ reviewModelDisplay }}</span>
                                    </div>
                                    <div class="overview-metadata-item">
                                        <span class="overview-metadata-label">Strategy</span>
                                        <span class="overview-metadata-value">{{ reviewStrategyDisplay }}</span>
                                    </div>
                                    <div class="overview-metadata-item">
                                        <span class="overview-metadata-label">Temperature</span>
                                        <span class="overview-metadata-value">{{ reviewTemperatureDisplay }}</span>
                                    </div>
                                </div>

                                <div v-if="reviewStatus?.result?.summary"
                                     class="markdown-content preview-markdown"
                                     v-html="renderMarkdown(reviewStatus.result.summary.substring(0, 500) + '...')"
                                ></div>
                                <div v-else-if="reviewStatus?.status === 'processing'" class="synthesis-waiting-state">
                                    <ProgressOrb class="waiting-orb" />
                                    <p>Synthesizing Review...</p>
                                </div>
                                <div v-else class="empty-state">No summary available yet.</div>

                                <div class="dashboard-stats">
                                    <div class="dash-stat">
                                        <span class="dash-stat-value">{{ commentSidebarItems.filter(i => i.type === 'file').length }}</span>
                                        <span class="dash-stat-label">Files with findings</span>
                                    </div>
                                    <div class="dash-stat">
                                        <span class="dash-stat-value">{{ reviewStatus?.result?.comments?.length ?? 0 }}</span>
                                        <span class="dash-stat-label">Total Comments</span>
                                    </div>
                                </div>

                                <div class="severity-summary-row">
                                    <div v-for="(count, sev) in severityCounts" :key="sev"
                                         class="sev-summary-pill" :class="`pill-${sev}`"
                                         v-show="count > 0"
                                    >
                                        <span class="pill-count">{{ count }}</span>
                                        <span class="pill-label">{{ sev }}</span>
                                    </div>
                                </div>
                            </div>
                        </div>

                        <!-- File Specific Comments (shown when a file is selected) -->
                        <section v-else class="events-section selected-file-view">
                            <div class="comments-section has-selection">
                                <div class="comments-toolbar">
                                    <div class="back-to-summary-row">
                                        <button class="btn-ghost back-to-summary-btn" @click="selectedCommentPath = null">
                                            ← Back to Summary
                                        </button>
                                    </div>
                                    <h4 class="comments-main-title">
                                        Comments for {{ selectedCommentPath }}
                                    </h4>
                                    <div class="comments-filter-controls">
                                        <input v-model="localSearchQuery" type="text" class="comment-search-input" placeholder="Search file comments…" />
                                        <div class="severity-pills">
                                            <button v-for="sev in ['error', 'warning', 'info', 'suggestion']" :key="sev"
                                                class="severity-pill" :class="[`severity-pill--${sev}`, { 'severity-pill--active': localSeverities.has(sev) }]"
                                                :data-severity="sev"
                                                @click="toggleSeverity(sev)">{{ sev }}</button>
                                        </div>
                                    </div>
                                </div>

                                <template v-if="groupedReviewComments.length">
                                    <div v-for="group in groupedReviewComments" :key="group.directory" class="comment-group">
                                        <div v-if="group.directory !== 'Root'" class="comment-group-header">{{ group.directory }}</div>
                                        <ul class="json-comments-list synthesis-comments">
                                            <li v-for="(comment, idx) in group.comments" :key="idx" class="json-comment-item synthesis-comment" :class="`severity-${comment.severity}`">
                                                <div class="comment-header">
                                                    <strong class="comment-sev">{{ (comment.severity ?? 'note').toUpperCase() }}</strong>
                                                    <span class="monospace-value">{{ comment.filePath ?? (comment as any).file_path }}:L{{ comment.lineNumber ?? (comment as any).line_number }}</span>
                                                    <button
                                                        v-if="routeClientId"
                                                        class="dismiss-btn"
                                                        :disabled="dismissingIds.has(commentKey(comment))"
                                                        @click.stop="dismissComment(comment)"
                                                        title="Dismiss this finding"
                                                    >{{ dismissingIds.has(commentKey(comment)) ? '…' : 'Dismiss' }}</button>
                                                </div>
                                                <div class="comment-msg-container markdown-content">
                                                    <div v-html="renderMarkdown(comment.message)"></div>
                                                </div>
                                            </li>
                                        </ul>
                                    </div>
                                </template>
                                <p v-else class="comments-empty-state">No comments found for this selection.</p>
                            </div>
                        </section>
                    </div>
                </div>
            </div>

            <!-- Tab 2: Master-Detail Layout -->
            <div class="protocol-master-detail" v-else-if="activeTab === 'traces'">
                <!-- Left Sidebar: Pass Navigation -->
                <nav class="protocol-sidebar" aria-label="Pass Navigation">
                    <div
                        v-for="item in sidebarItems"
                        :key="item.type === 'folder' ? item.path : (item.protocol.id || item.name)"
                        class="sidebar-tree-node"
                        :class="{ 'is-last-in-group': item.isLast }"
                        :style="{ '--depth': item.depth }"
                    >
                        <!-- Folder Toggle -->
                        <button
                            v-if="item.type === 'folder'"
                            class="folder-header tree-folder-btn"
                            :style="{ paddingLeft: (item.depth * 1.5) + 'rem' }"
                            @click="toggleFolder(item.path)"
                            :aria-expanded="!item.isCollapsed"
                        >
                            <i class="fi fi-rr-angle-small-down folder-chevron" :class="{ collapsed: item.isCollapsed }"></i>
                            <i class="fi" :class="item.isCollapsed ? 'fi-rr-folder' : 'fi-rr-folder-open'"></i>
                            <span class="folder-name">{{ item.name }}</span>
                        </button>

                        <!-- Pass Item -->
                        <button
                            v-else
                            class="pass-nav-item tree-pass-btn"
                            :class="{ 'active': activePassId === item.protocol.id || (!activePassId && protocols.indexOf(item.protocol) === 0) }"
                            :style="{ paddingLeft: (item.depth * 1.5) + 'rem' }"
                            @click="activePassId = item.protocol.id || null"
                        >
                            <i class="fi fi-rr-file-code pass-file-icon" :class="statusIconClass(item.protocol.outcome)"></i>
                            <div class="pass-nav-info">
                                <div class="pass-nav-path">
                                    <span class="pass-nav-filename">{{ item.name }}</span>
                                    <span v-if="item.protocol.isInherited" class="pass-nav-badge">Inherited</span>
                                </div>
                                <div class="pass-nav-stats-grid" style="margin-left: auto; padding-right: 0.5rem;">
                                    <div class="stat-item" title="Tokens">
                                        <i class="fi fi-rr-coins stat-icon"></i>
                                        <span class="stat-text">{{ formatTokens((item.protocol.totalInputTokens ?? 0) + (item.protocol.totalOutputTokens ?? 0)) }}</span>
                                    </div>
                                </div>
                            </div>
                        </button>
                    </div>
                </nav>

                <!-- Right Detail: Active Pass -->
                <div class="protocol-content">
                    <div class="pass-main" v-if="activePass">
                        <div class="pass-detail-header">
                            <div class="pass-detail-filepath">
                                <span class="pass-detail-dir" v-if="parseFilePath(activePass.label).directory">{{ parseFilePath(activePass.label).directory }}/</span><span class="pass-detail-filename">{{ parseFilePath(activePass.label).filename }}</span>
                            </div>
                            <span v-if="activePass.isInherited" class="chip chip-inherited">Inherited trace</span>
                        </div>
                        <dl class="summary-grid pass-summary">
                            <div><dt>Attempt</dt><dd>{{ activePass.attemptNumber ?? '—' }}</dd></div>
                            <div><dt>Started</dt><dd>{{ formatDate(activePass.startedAt) }}</dd></div>
                            <div><dt>Completed</dt><dd>{{ formatDate(activePass.completedAt) }}</dd></div>
                            <div><dt>Duration</dt><dd>{{ computePassDuration(activePass) }}</dd></div>
                            <div><dt>Iterations</dt><dd>{{ activePass.iterationCount ?? '—' }}</dd></div>
                            <div><dt>Tool Calls</dt><dd>{{ activePass.toolCallCount ?? '—' }}</dd></div>
                            <div><dt>In Tokens</dt><dd class="fat-tokens">{{ formatTokens(activePass.totalInputTokens) }}</dd></div>
                            <div><dt>Out Tokens</dt><dd class="fat-tokens">{{ formatTokens(activePass.totalOutputTokens) }}</dd></div>
                            <div><dt>Strategy</dt><dd>{{ activePassReviewStrategyDisplay }}</dd></div>
                        </dl>

                        <section v-if="traceTimingInsights.length" class="pass-file-outcome-section">
                            <div class="pass-final-result-header">
                                <h4>Timing Insights</h4>
                                <span class="chip chip-muted">Current pass</span>
                            </div>

                            <ol class="timing-insights-list" aria-label="Slowest visible tool calls">
                                <li
                                    v-for="insight in traceTimingInsights"
                                    :key="insight.eventId"
                                >
                                    <button
                                        type="button"
                                        class="timing-insight-row"
                                        @click="openTimingInsight(insight)"
                                    >
                                        <span class="timing-insight-rank">#{{ insight.rank }}</span>
                                        <span class="timing-insight-tool">{{ insight.toolName }}</span>
                                        <span class="timing-insight-context">{{ insight.passLabel }}</span>
                                        <span class="timing-insight-duration">{{ formatDurationWithMs(insight.durationMs) }}</span>
                                        <span class="timing-insight-meta">
                                            <template v-if="insight.waitDurationMs != null || insight.activeDurationMs != null">
                                                <span v-if="insight.waitDurationMs != null">Wait {{ formatDurationWithMs(insight.waitDurationMs) }}</span>
                                                <span v-if="insight.activeDurationMs != null">Active {{ formatDurationWithMs(insight.activeDurationMs) }}</span>
                                            </template>
                                            <span v-else-if="insight.hasPhaseDetail">Phase detail available</span>
                                        </span>
                                    </button>
                                </li>
                            </ol>
                        </section>

                        <section v-if="activePassInheritance" class="pass-file-outcome-section">
                            <div class="pass-final-result-header">
                                <h4>Inheritance</h4>
                                <span class="chip chip-muted">Same revision retry reuse</span>
                            </div>

                            <dl class="summary-grid pass-summary pass-file-outcome-grid">
                                <div><dt>Source Job</dt><dd class="inheritance-job-cell"><RouterLink class="inheritance-link monospace-value" :to="sourceJobProtocolLink(activePassInheritance.sourceJobId)">{{ shortGuid(activePassInheritance.sourceJobId) }}</RouterLink></dd></div>
                                <div><dt>Source Protocol</dt><dd class="monospace-value">{{ shortGuid(activePassInheritance.sourceProtocolId) }}</dd></div>
                                <div v-if="activePassInheritance.sourceFileResultId"><dt>Source File Result</dt><dd class="monospace-value">{{ shortGuid(activePassInheritance.sourceFileResultId) }}</dd></div>
                                <div><dt>Source Completed</dt><dd>{{ formatDate(activePassInheritance.sourceCompletedAt) }}</dd></div>
                            </dl>

                            <p class="pass-file-outcome-note">
                                This file pass was inherited from a previously completed same-revision run and is included here so protocol totals and trace review reflect reused work.
                            </p>
                        </section>

                        <section v-if="activePassFileOutcome" class="pass-file-outcome-section">
                            <div class="pass-final-result-header">
                                <h4>File Outcome</h4>
                                <span class="chip chip-muted">{{ formatFileOutcomeStatus(activePassFileOutcome) }}</span>
                            </div>

                            <dl class="summary-grid pass-summary pass-file-outcome-grid">
                                <div><dt>Path</dt><dd>{{ activePassFileOutcome.filePath }}</dd></div>
                                <div><dt>Status</dt><dd>{{ formatFileOutcomeStatus(activePassFileOutcome) }}</dd></div>
                                <div v-if="activePassFileOutcome.exclusionReason"><dt>Exclusion</dt><dd>{{ activePassFileOutcome.exclusionReason }}</dd></div>
                                <div v-if="activePassFileOutcome.errorMessage"><dt>Error</dt><dd>{{ activePassFileOutcome.errorMessage }}</dd></div>
                            </dl>

                            <p v-if="activePassFileOutcome.isDegraded" class="pass-file-outcome-note">
                                Agentic file investigation recorded a degraded intermediate outcome for this pass. It remained non-validated unless later verification and final gate kept it.
                            </p>
                        </section>

                        <section v-if="activePassFollowUp" class="pass-file-outcome-section">
                            <div class="pass-final-result-header">
                                <h4>Follow-up</h4>
                                <span class="chip chip-muted">{{ formatFollowUpStatus(activePassFollowUp) }}</span>
                            </div>

                            <dl class="summary-grid pass-summary pass-file-outcome-grid">
                                <div><dt>Used</dt><dd>{{ activePassFollowUp.used ? 'Yes' : 'No' }}</dd></div>
                                <div v-if="activePassFollowUp.triggerFamily"><dt>Trigger</dt><dd>{{ activePassFollowUp.triggerFamily }}</dd></div>
                                <div><dt>Completion</dt><dd>{{ activePassFollowUp.completedSuccessfully ? 'Completed successfully' : 'Not completed successfully' }}</dd></div>
                                <div><dt>Dependency</dt><dd>{{ activePassFollowUp.dependencyRecorded ? 'Dependent finding recorded' : 'No surviving dependency recorded' }}</dd></div>
                            </dl>
                        </section>

                        <section v-if="activePassRepeatedJudgment" class="pass-file-outcome-section">
                            <div class="pass-final-result-header">
                                <h4>Repeated Judgment</h4>
                                <span class="chip chip-muted">{{ formatRepeatedJudgmentStatus(activePassRepeatedJudgment) }}</span>
                            </div>

                            <dl class="summary-grid pass-summary pass-file-outcome-grid">
                                <div><dt>Finding</dt><dd>{{ activePassRepeatedJudgment.findingId }}</dd></div>
                                <div v-if="activePassRepeatedJudgment.evidenceSetId"><dt>Evidence Set</dt><dd>{{ activePassRepeatedJudgment.evidenceSetId }}</dd></div>
                                <div><dt>Agreement</dt><dd>{{ activePassRepeatedJudgment.agreementState ?? 'Not recorded' }}</dd></div>
                                <div><dt>Disposition</dt><dd>{{ activePassRepeatedJudgment.recommendedDisposition ?? 'Not recorded' }}</dd></div>
                                <div><dt>Evidence Reused</dt><dd>{{ activePassRepeatedJudgment.usedSameEvidenceSet ? 'Yes' : 'No' }}</dd></div>
                                <div v-if="activePassRepeatedJudgment.reasonCodes?.length"><dt>Reason Codes</dt><dd>{{ activePassRepeatedJudgment.reasonCodes.join(', ') }}</dd></div>
                            </dl>
                        </section>

                        <section v-if="activePassProRvPrefilter" class="pass-file-outcome-section">
                            <div class="pass-final-result-header">
                                <h4>ProRV Prefilter</h4>
                                <span class="chip chip-muted">{{ activePassProRvPrefilter.executionState ?? 'Not recorded' }}</span>
                            </div>

                            <dl class="summary-grid pass-summary pass-file-outcome-grid">
                                <div><dt>Selected</dt><dd>{{ activePassProRvPrefilter.selected ? 'Yes' : 'No' }}</dd></div>
                                <div><dt>Execution</dt><dd>{{ activePassProRvPrefilter.executionState ?? 'Not recorded' }}</dd></div>
                                <div><dt>Guidance Applied</dt><dd>{{ activePassProRvPrefilter.guidanceApplied ? 'Yes' : 'No' }}</dd></div>
                                <div><dt>Prompt Kind</dt><dd>{{ activePassProRvPrefilter.appliedPromptKind ?? 'Not recorded' }}</dd></div>
                                <div><dt>Guidance Count</dt><dd>{{ activePassProRvPrefilter.guidanceCount ?? 0 }}</dd></div>
                                <div><dt>AI Call Recorded</dt><dd>{{ activePassProRvPrefilter.aiCallRecorded ? 'Yes' : 'No' }}</dd></div>
                                <div v-if="activePassProRvPrefilter.stageId"><dt>Stage</dt><dd>{{ activePassProRvPrefilter.stageId }}</dd></div>
                                <div v-if="activePassProRvPrefilter.runtimeSource"><dt>Runtime</dt><dd>{{ activePassProRvPrefilter.runtimeSource }}</dd></div>
                                <div v-if="activePassProRvPrefilter.modelId"><dt>Model</dt><dd>{{ activePassProRvPrefilter.modelId }}</dd></div>
                                <div v-if="activePassProRvPrefilter.language"><dt>Language</dt><dd>{{ activePassProRvPrefilter.language }}</dd></div>
                                <div v-if="activePassProRvPrefilter.prefilterStatus"><dt>Prefilter Status</dt><dd>{{ activePassProRvPrefilter.prefilterStatus }}</dd></div>
                                <div v-if="activePassProRvPrefilter.reason"><dt>Reason</dt><dd>{{ activePassProRvPrefilter.reason }}</dd></div>
                                <div v-if="activePassProRvPrefilter.appliedGuidanceIds?.length"><dt>Guidance IDs</dt><dd>{{ activePassProRvPrefilter.appliedGuidanceIds.join(', ') }}</dd></div>
                            </dl>
                        </section>

                        <section v-if="activePass.finalSummary || activePassFinalComments.length" class="pass-final-result-section">
                            <div class="pass-final-result-header">
                                <h4>Final File Result</h4>
                                <span v-if="activePassFinalComments.length" class="chip chip-muted">{{ activePassFinalComments.length }} final comment{{ activePassFinalComments.length === 1 ? '' : 's' }}</span>
                            </div>

                            <div v-if="activePass.finalSummary" class="markdown-content pass-final-summary-text" v-html="renderMarkdown(activePass.finalSummary)"></div>
                            <p v-else class="pass-final-summary-empty">No final per-file summary was captured for this pass.</p>

                            <ul v-if="activePassFinalComments.length" class="json-comments-list synthesis-comments pass-final-comments-list">
                                <li v-for="(comment, idx) in activePassFinalComments" :key="`final-${idx}`" class="json-comment-item synthesis-comment" :class="`severity-${comment.severity}`">
                                    <div class="comment-header">
                                        <strong class="comment-sev">{{ (comment.severity ?? 'note').toUpperCase() }}</strong>
                                        <span class="monospace-value">{{ comment.filePath ?? (comment as any).file_path }}:L{{ comment.lineNumber ?? (comment as any).line_number }}</span>
                                    </div>
                                    <div class="comment-msg-container markdown-content">
                                        <div v-html="renderMarkdown(comment.message)"></div>
                                    </div>
                                </li>
                            </ul>
                        </section>

                        <section class="events-section">
                            <h4>Events ({{ activePassEventRows.length }})</h4>
                            <p v-if="!activePass.events?.length" class="empty-state">{{ emptyPassMessage(activePass) }}</p>
                            <TransitionGroup v-else name="list" tag="div" class="events-list">
                                <article
                                    v-for="row in activePassEventRows"
                                    :key="row.id"
                                    class="event-card row-clickable"
                                    :class="{
                                        'row-error': !!row.merged.callDetails.error || !!row.merged.resultDetails?.error,
                                        'row-processing': isMergedEventProcessing(row.merged),
                                        'row-child': row.depth > 0
                                    }"
                                    :data-event-name="row.merged.name"
                                    :data-event-depth="row.depth"
                                    :data-parent-event-id="row.parentId ?? ''"
                                    @click="openMergedModal(row.merged)"
                                >
                                    <div v-if="row.isToolChild" class="event-child-gutter" aria-hidden="true">
                                        <span class="event-child-rail"></span>
                                    </div>
                                    <div class="event-card-main">
                                        <div class="event-card-header">
                                            <div class="event-card-meta-row">
                                                <div class="event-card-kind-group" :class="{ 'kind-cell--child': row.isToolChild }">
                                                    <span class="kind-badge" :class="kindBadgeClass(row.merged.callDetails.kind)">{{ row.merged.callDetails.kind ?? 'unknown' }}</span>
                                                    <span v-if="row.isToolChild && row.parentName" class="kind-parent-pill">{{ parentIterationLabel(row.parentName) }}</span>
                                                    <span v-if="isMergedEventProcessing(row.merged)" class="status-badge status-processing">Executing...</span>
                                                </div>
                                                <span class="date-cell">{{ formatDate(row.merged.time) }}</span>
                                            </div>

                                            <div class="event-card-title-row" :class="{ 'event-card-title-row--child': row.depth > 0 }">
                                                <button
                                                    v-if="row.childCount > 0"
                                                    class="event-toggle"
                                                    :aria-label="row.isExpanded ? 'Collapse child tool calls' : 'Expand child tool calls'"
                                                    @click.stop="toggleEventParent(row.id)"
                                                >
                                                    <span class="event-toggle-icon" aria-hidden="true">
                                                        <i class="fi fi-rr-angle-small-down event-toggle-chevron" :class="{ 'event-toggle-chevron--collapsed': !row.isExpanded }"></i>
                                                    </span>
                                                    <span class="event-toggle-count">{{ row.childCount }}</span>
                                                </button>

                                                <div class="event-title-stack" :class="{ 'tool-name': row.merged.callDetails.kind === 'toolCall', 'ai-name': row.merged.callDetails.kind === 'aiCall', 'memory-name': row.merged.callDetails.kind === 'memoryOperation', 'operational-name': row.merged.callDetails.kind === 'operational' }">
                                                    <span class="event-name-label">{{ row.merged.name }}</span>
                                                    <div v-if="hasToolTiming(row.merged.callDetails)" class="timing-inline-group">
                                                        <span class="timing-duration">{{ row.timingSummary }}</span>
                                                        <span v-if="row.timingDetail" class="timing-detail">{{ row.timingDetail }}</span>
                                                    </div>
                                                </div>
                                            </div>

                                            <div v-if="hasToolTiming(row.merged.callDetails)" class="timing-badges">
                                                <span v-if="row.merged.callDetails.timingAvailability" class="status-badge" :class="statusBadgeClass(row.merged.callDetails.timingAvailability)">
                                                    {{ formatTimingAvailability(row.merged.callDetails.timingAvailability) }}
                                                </span>
                                                <span v-if="row.merged.callDetails.toolOutcome" class="status-badge" :class="statusBadgeClass(row.merged.callDetails.toolOutcome)">
                                                    {{ formatToolOutcome(row.merged.callDetails.toolOutcome) }}
                                                </span>
                                            </div>
                                            <span v-else class="timing-empty">No timing captured</span>
                                        </div>

                                        <div v-if="hasEventTokens(row.merged.callDetails) || hasEventError(row.merged.callDetails)" class="event-card-secondary">
                                            <div v-if="hasEventTokens(row.merged.callDetails)" class="event-metric-card event-metric-card--tokens">
                                                <span class="event-card-label">Input Tokens</span>
                                                <div class="tokens-cell fat-tokens">
                                                    <div>{{ formatTokens(row.merged.callDetails.inputTokens) }}</div>
                                                    <small v-if="row.merged.callDetails.kind === 'aiCall'" class="cache-token-detail">
                                                        Cached {{ formatTokens(row.merged.callDetails.cachedInputTokens) }} · {{ formatCacheStatus(row.merged.callDetails.cacheStatus) }}
                                                    </small>
                                                </div>
                                            </div>

                                            <div v-if="hasEventTokens(row.merged.callDetails)" class="event-metric-card event-metric-card--tokens">
                                                <span class="event-card-label">Output Tokens</span>
                                                <div class="tokens-cell fat-tokens">{{ formatTokens(row.merged.callDetails.outputTokens) }}</div>
                                            </div>

                                            <div v-if="hasEventError(row.merged.callDetails)" class="event-metric-card event-metric-card--error">
                                                <span class="event-card-label">Error</span>
                                                <div class="error-cell">
                                                    <span v-if="row.merged.callDetails.error">{{ row.merged.callDetails.error }}</span>
                                                    <span v-if="row.merged.callDetails.finalizationAttemptKind" class="status-badge status-processing">
                                                        {{ row.merged.callDetails.finalizationAttemptKind }}: {{ row.merged.callDetails.finalizationOutcome ?? row.merged.callDetails.finalizationReason }}
                                                    </span>
                                                    <span v-if="row.merged.callDetails.toolEvidence" class="status-badge status-processing">
                                                        Evidence {{ row.merged.callDetails.toolEvidence.action }}
                                                    </span>
                                                </div>
                                            </div>
                                        </div>
                                    </div>
                                </article>
                            </TransitionGroup>
                        </section>
                    </div>
                </div>
            </div>

            <!-- Tab 3: Token Breakdown -->
            <div class="tokens-tab-view" v-if="activeTab === 'tokens'">
                <section class="section-card">
                    <div class="section-card-header">
                        <h3 class="section-title">Token Usage by Tier</h3>
                    </div>
                    <div class="section-inner">
                        <p class="section-description">
                            Breakdown of token consumption across administrative, memory, and review tiers.
                        </p>
                        <TokenBreakdownTable
                            v-if="protocolTokenBreakdown.length > 0 || jobDetail"
                            :breakdown="protocolTokenBreakdown"
                            :breakdown-consistent="protocolBreakdownConsistent"
                        />
                        <p v-else class="empty-state">No detailed token breakdown available for this job.</p>
                    </div>
                </section>
            </div>

            <ModalDialog v-model:isOpen="isEventModalOpen" :title="selectedMergedEvent?.name ?? 'Event Protocol'">
                <div v-if="selectedMergedEvent" class="merged-modal-layout">
                    <section class="drawer-section">
                        <h4>Input</h4>
                        <template v-if="parsedInputResult">
                        <div class="parsed-json-block">
                            <template v-if="selectedCommentRelevanceInput">
                                <div class="json-field">
                                    <span class="json-key">Implementation:</span>
                                    <pre class="json-content">{{ formatCommentRelevanceImplementation(selectedCommentRelevanceInput) }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">File:</span>
                                    <pre class="json-content">{{ selectedCommentRelevanceInput.filePath ?? 'Unknown' }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Counts:</span>
                                    <pre class="json-content">{{ formatCommentRelevanceCounts(selectedCommentRelevanceInput) }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Degraded Components:</span>
                                    <pre class="json-content">{{ formatStringList(selectedCommentRelevanceInput.degradedComponents) }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Fallback Checks:</span>
                                    <pre class="json-content">{{ formatStringList(selectedCommentRelevanceInput.fallbackChecks) }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Degraded Cause:</span>
                                    <pre class="json-content">{{ selectedCommentRelevanceInput.degradedCause ?? 'None' }}</pre>
                                </div>
                            </template>
                            <template v-else-if="selectedFinalGateInput">
                                <div class="json-field">
                                    <span class="json-key">Counts:</span>
                                    <pre class="json-content">{{ formatFinalGateCounts(selectedFinalGateInput) }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Category Counts:</span>
                                    <pre class="json-content">{{ formatNamedCounts(selectedFinalGateInput.categoryCounts) }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Invariant-blocked Findings:</span>
                                    <pre class="json-content">{{ formatTokens(selectedFinalGateInput.invariantBlockedCount) }}</pre>
                                </div>
                            </template>
                            <template v-else-if="selectedVerificationInput">
                                <div class="json-field">
                                    <span class="json-key">Finding ID:</span>
                                    <pre class="json-content">{{ selectedVerificationInput.findingId ?? 'Unknown' }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Claim ID:</span>
                                    <pre class="json-content">{{ selectedVerificationInput.claimId ?? 'None' }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">File:</span>
                                    <pre class="json-content">{{ selectedVerificationInput.filePath ?? 'None' }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Stage:</span>
                                    <pre class="json-content">{{ selectedVerificationInput.stage ?? 'None' }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Coverage / Counts:</span>
                                    <pre class="json-content">{{ selectedVerificationInput.coverageState ?? 'n/a' }} / claims={{ selectedVerificationInput.claimCount ?? 0 }} / dropped={{ selectedVerificationInput.droppedCount ?? 0 }} / summary-only={{ selectedVerificationInput.summaryOnlyCount ?? 0 }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Degraded Component:</span>
                                    <pre class="json-content">{{ selectedVerificationInput.degradedComponent ?? 'None' }}</pre>
                                </div>
                            </template>
                            <template v-else-if="selectedMergedEvent.callDetails.kind === 'memoryOperation' && selectedMergedEvent.callDetails.name === 'memory_reconsideration_completed' && typeof parsedInputResult === 'object'">
                                <div class="json-field">
                                    <span class="json-key">File:</span>
                                    <pre class="json-content">{{ parsedInputResult.filePath }}</pre>
                                </div>
                                <div class="json-field">
                                    <span class="json-key">Comment counts:</span>
                                    <pre class="json-content">{{ parsedInputResult.originalCommentCount }} original → {{ parsedInputResult.finalCommentCount }} final ({{ parsedInputResult.retainedCount }} retained, {{ parsedInputResult.discardedCount }} discarded, {{ parsedInputResult.downgradedCount }} downgraded)</pre>
                                </div>
                                <div v-if="parsedInputResult.contributingMemoryIds?.length" class="json-field">
                                    <span class="json-key">Memory IDs used ({{ parsedInputResult.contributingMemoryIds.length }}):</span>
                                    <pre class="json-content">{{ parsedInputResult.contributingMemoryIds.join('\n') }}</pre>
                                </div>
                                <div v-if="parsedInputResult.discarded?.length" class="json-field">
                                    <span class="json-key">Discarded ({{ parsedInputResult.discarded.length }}):</span>
                                    <div class="memory-comment-list">
                                        <div v-for="(c, i) in parsedInputResult.discarded" :key="i" class="memory-comment-row">
                                            <div class="memory-comment-header">
                                                <span class="memory-sev-chip" :class="`memory-sev-chip--${c.severity}`">{{ (c.severity ?? 'note').toUpperCase() }}</span>
                                                <span class="monospace-value memory-comment-loc">{{ c.filePath }}:L{{ c.lineNumber }}</span>
                                            </div>
                                            <p class="memory-comment-msg">{{ c.message }}</p>
                                        </div>
                                    </div>
                                </div>
                                <div v-if="parsedInputResult.downgraded?.length" class="json-field">
                                    <span class="json-key">Downgraded ({{ parsedInputResult.downgraded.length }}):</span>
                                    <div class="memory-comment-list">
                                        <div v-for="(c, i) in parsedInputResult.downgraded" :key="i" class="memory-comment-row">
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
                                <div v-if="!parsedInputResult.discarded?.length && !parsedInputResult.downgraded?.length" class="json-field">
                                    <span class="json-key">Changes:</span>
                                    <pre class="json-content">All comments retained unchanged.</pre>
                                </div>
                            </template>
                            <template v-else-if="selectedMergedEvent.callDetails.kind === 'toolCall' && typeof parsedInputResult === 'object'">
                                <div v-for="(val, key) in parsedInputResult" :key="key" class="json-field">
                                    <span class="json-key">{{ key }}:</span>
                                    <pre class="json-content">{{ typeof val === 'string' ? val : JSON.stringify(val, null, 2) }}</pre>
                                </div>
                            </template>
                            <pre v-else class="content-block">{{ JSON.stringify(parsedInputResult, null, 2) }}</pre>
                        </div>
                        </template>
                        <pre v-else-if="selectedMergedEvent.callDetails.inputTextSample" class="content-block">{{ selectedMergedEvent.callDetails.inputTextSample }}</pre>
                        <p v-else class="no-content">No input captured.</p>
                    </section>

                    <section class="drawer-section">
                        <div class="drawer-section-header">
                            <h4>Output</h4>
                            <span v-if="selectedMergedEvent.callDetails.kind === 'aiCall'" class="ai-disclaimer"><i class="fi fi-rr-magic-wand"></i> AI-generated content</span>
                        </div>
                        <div v-if="selectedAiCallProviderManagedNote" class="provider-managed-note">
                            <i class="fi fi-rr-info"></i>
                            <span>{{ selectedAiCallProviderManagedNote }}</span>
                        </div>
                        <template v-if="selectedMergedEvent.resultDetails">
                            <div v-if="parsedOutputResult" class="parsed-json-block">
                                <template v-if="selectedCommentRelevanceOutput">
                                    <div class="json-field">
                                        <span class="json-key">Implementation:</span>
                                        <pre class="json-content">{{ formatCommentRelevanceImplementation(selectedCommentRelevanceOutput) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Counts:</span>
                                        <pre class="json-content">{{ formatCommentRelevanceCounts(selectedCommentRelevanceOutput) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Reason Buckets:</span>
                                        <pre class="json-content">{{ formatNamedCounts(selectedCommentRelevanceOutput.reasonBuckets) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Decision Sources:</span>
                                        <pre class="json-content">{{ formatNamedCounts(selectedCommentRelevanceOutput.decisionSources) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Degraded Components:</span>
                                        <pre class="json-content">{{ formatStringList(selectedCommentRelevanceOutput.degradedComponents) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Fallback Checks:</span>
                                        <pre class="json-content">{{ formatStringList(selectedCommentRelevanceOutput.fallbackChecks) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Degraded Cause:</span>
                                        <pre class="json-content">{{ selectedCommentRelevanceOutput.degradedCause ?? 'None' }}</pre>
                                    </div>
                                    <div v-if="selectedCommentRelevanceOutput.aiTokenUsage" class="json-field">
                                        <span class="json-key">AI Token Usage:</span>
                                        <pre class="json-content">{{ formatCommentRelevanceAiUsage(selectedCommentRelevanceOutput.aiTokenUsage) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Discarded ({{ selectedCommentRelevanceOutput.discarded?.length ?? 0 }}):</span>
                                        <div v-if="selectedCommentRelevanceOutput.discarded?.length" class="memory-comment-list">
                                            <div v-for="(comment, idx) in selectedCommentRelevanceOutput.discarded" :key="idx" class="memory-comment-row">
                                                <div class="memory-comment-header">
                                                    <span class="memory-sev-chip" :class="`memory-sev-chip--${severityVariant(comment.severity)}`">{{ (comment.severity ?? 'note').toUpperCase() }}</span>
                                                    <span class="monospace-value memory-comment-loc">{{ commentLocation(comment.filePath, comment.lineNumber) }}</span>
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
                                <template v-else-if="selectedFinalGateSummaryOutput">
                                    <div class="json-field">
                                        <span class="json-key">Counts:</span>
                                        <pre class="json-content">{{ formatFinalGateCounts(selectedFinalGateSummaryOutput) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Category Counts:</span>
                                        <pre class="json-content">{{ formatNamedCounts(selectedFinalGateSummaryOutput.categoryCounts) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Invariant-blocked Findings:</span>
                                        <pre class="json-content">{{ formatTokens(selectedFinalGateSummaryOutput.invariantBlockedCount) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Original Summary:</span>
                                        <pre class="json-content">{{ selectedFinalGateSummaryOutput.originalSummary ?? 'None' }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Final Summary:</span>
                                        <pre class="json-content">{{ selectedFinalGateSummaryOutput.finalSummary ?? 'None' }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Summary Rewrite:</span>
                                        <pre class="json-content">{{ selectedFinalGateSummaryOutput.summaryRewritePerformed ? 'Yes' : 'No' }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Dropped Finding IDs:</span>
                                        <pre class="json-content">{{ formatStringList(selectedFinalGateSummaryOutput.droppedFindingIds) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Summary-only Finding IDs:</span>
                                        <pre class="json-content">{{ formatStringList(selectedFinalGateSummaryOutput.summaryOnlyFindingIds) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Summary Rule Source:</span>
                                        <pre class="json-content">{{ selectedFinalGateSummaryOutput.summaryRuleSource ?? 'None' }}</pre>
                                    </div>
                                </template>
                                <template v-else-if="selectedFinalGateDecisionOutput">
                                    <div class="json-field">
                                        <span class="json-key">Finding ID:</span>
                                        <pre class="json-content">{{ selectedFinalGateDecisionOutput.findingId ?? 'Unknown' }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Disposition:</span>
                                        <pre class="json-content">{{ selectedFinalGateDecisionOutput.disposition ?? 'Unknown' }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Category:</span>
                                        <pre class="json-content">{{ selectedFinalGateDecisionOutput.category ?? 'Unknown' }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Rule Source:</span>
                                        <pre class="json-content">{{ selectedFinalGateDecisionOutput.ruleSource ?? 'Unknown' }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Provenance:</span>
                                        <pre class="json-content">{{ formatFinalGateProvenance(selectedFinalGateDecisionOutput.provenance) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Evidence:</span>
                                        <pre class="json-content">{{ formatFinalGateEvidence(selectedFinalGateDecisionOutput.evidence) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Reason Codes:</span>
                                        <pre class="json-content">{{ formatStringList(selectedFinalGateDecisionOutput.reasonCodes) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Blocked Invariants:</span>
                                        <pre class="json-content">{{ formatStringList(selectedFinalGateDecisionOutput.blockedInvariantIds) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Summary Text:</span>
                                        <pre class="json-content">{{ selectedFinalGateDecisionOutput.summaryText ?? 'None' }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Included In Final Summary:</span>
                                        <pre class="json-content">{{ selectedFinalGateDecisionOutput.includedInFinalSummary ? 'Yes' : 'No' }}</pre>
                                    </div>
                                </template>
                                <template v-else-if="selectedVerificationEvidenceOutput">
                                    <div class="json-field">
                                        <span class="json-key">Coverage State:</span>
                                        <pre class="json-content">{{ selectedVerificationEvidenceOutput.coverageState ?? 'Unknown' }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">ProCursor Result:</span>
                                        <pre class="json-content">{{ formatVerificationProCursorStatus(selectedVerificationEvidenceOutput) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Evidence Attempts:</span>
                                        <pre class="json-content">{{ formatEvidenceAttempts(selectedVerificationEvidenceOutput.evidenceAttempts) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Evidence Items:</span>
                                        <pre class="json-content">{{ formatEvidenceItems(selectedVerificationEvidenceOutput.evidenceItems) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Retrieval Notes:</span>
                                        <pre class="json-content">{{ selectedVerificationEvidenceOutput.retrievalNotes ?? 'None' }}</pre>
                                    </div>
                                </template>
                                <template v-else-if="selectedVerificationOutput">
                                    <div v-for="(val, key) in selectedVerificationOutput" :key="key" class="json-field">
                                        <span class="json-key">{{ key }}:</span>
                                        <pre class="json-content">{{ typeof val === 'string' ? val : JSON.stringify(val, null, 2) }}</pre>
                                    </div>
                                </template>
                                <template v-else-if="selectedAgenticInvestigationOutput">
                                    <div class="json-field">
                                        <span class="json-key">Stage B status:</span>
                                        <pre class="json-content">{{ formatAgenticInvestigationStatus(selectedAgenticInvestigationOutput) }}</pre>
                                    </div>
                                    <div class="json-field">
                                        <span class="json-key">Runtime tool attempts:</span>
                                        <pre class="json-content">{{ formatAgenticToolUsage(selectedAgenticInvestigationOutput) }}</pre>
                                    </div>
                                    <div v-if="agenticInvestigationCandidateCount(selectedAgenticInvestigationOutput) !== null" class="json-field">
                                        <span class="json-key">Candidate count:</span>
                                        <pre class="json-content">{{ agenticInvestigationCandidateCount(selectedAgenticInvestigationOutput) }}</pre>
                                    </div>
                                    <div v-if="agenticInvestigationEvidenceCount(selectedAgenticInvestigationOutput) !== null" class="json-field">
                                        <span class="json-key">Evidence count:</span>
                                        <pre class="json-content">{{ agenticInvestigationEvidenceCount(selectedAgenticInvestigationOutput) }}</pre>
                                    </div>
                                    <div v-if="isAgenticDegradedEvent(selectedMergedEvent?.callDetails.name)" class="json-field">
                                        <span class="json-key">Degraded outcome note:</span>
                                        <pre class="json-content">This degraded Stage B result was a non-validated intermediate outcome. It only affected the final review if later verification and final gate kept supporting findings.</pre>
                                    </div>
                                </template>
                                <pre v-else-if="typeof parsedOutputResult === 'string'" class="content-block">{{ decodeHtmlEntities(parsedOutputResult) }}</pre>

                                <template v-else>
                                    <div v-if="parsedOutputResult.summary" class="json-field">
                                        <span class="json-key">Summary:</span>
                                        <p class="json-summary-text">{{ parsedOutputResult.summary }}</p>
                                    </div>
                                    <div v-if="parsedOutputResult.comments?.length" class="json-field">
                                        <span class="json-key">Comments ({{ parsedOutputResult.comments.length }}):</span>
                                        <ul class="json-comments-list">
                                            <li v-for="(comment, idx) in parsedOutputResult.comments" :key="idx" class="json-comment-item" :class="`severity-${comment.severity}`">
                                                <strong>{{ (comment.severity ?? 'note').toUpperCase() }}</strong> at <span class="monospace-value">{{ comment.file_path }}:L{{ comment.line_number }}</span><br/>
                                                {{ comment.message }}
                                            </li>
                                        </ul>
                                    </div>

                                    <template v-if="!parsedOutputResult.summary && !parsedOutputResult.comments">
                                        <pre class="content-block">{{ JSON.stringify(parsedOutputResult, null, 2) }}</pre>
                                    </template>
                                </template>
                            </div>
                            <pre v-else-if="selectedMergedEvent.resultDetails.outputSummary !== null" class="content-block">{{ renderMergedEventText(selectedMergedEvent.resultDetails.outputSummary) }}</pre>

                            <template v-else-if="selectedMergedEvent.resultDetails.error !== null">
                                <pre class="content-block error-block">{{ selectedMergedEvent.resultDetails.error }}</pre>
                            </template>
                            <div v-else-if="selectedMergedEvent.callDetails.kind === 'memoryOperation'" class="memory-no-output">
                                <i class="fi fi-rr-check-circle memory-no-output-icon"></i>
                                <span>No output recorded for this memory operation.</span>
                            </div>
                            <p v-else-if="isMergedEventProcessing(selectedMergedEvent)" class="no-content" style="color: var(--color-accent); font-weight: bold;">Currently Executing...</p>
                            <p v-else class="no-content">No output captured for this completed step.</p>
                        </template>
                    </section>

                    <section v-if="hasToolTiming(selectedMergedEvent.callDetails)" class="tool-timing-panel">
                        <div class="tool-timing-header">
                            <h4>Tool Timing</h4>
                            <span v-if="selectedToolPhaseTimings.length" class="tool-phase-overview-chip">
                                {{ formatPhaseCountSummary(selectedToolPhaseTimings.length, modalPhaseGroups.length || getEventTimingPresentation(selectedMergedEvent.callDetails).phaseGroupCount, true) }}
                            </span>
                        </div>
                        <dl class="tool-timing-grid">
                            <div>
                                <dt>Availability</dt>
                                <dd>{{ formatTimingAvailability(selectedMergedEvent.callDetails.timingAvailability) }}</dd>
                            </div>
                            <div v-if="selectedMergedEvent.callDetails.toolOutcome">
                                <dt>Outcome</dt>
                                <dd>{{ formatToolOutcome(selectedMergedEvent.callDetails.toolOutcome) }}</dd>
                            </div>
                            <div v-if="selectedMergedEvent.callDetails.startedAt">
                                <dt>Started</dt>
                                <dd>{{ formatDate(selectedMergedEvent.callDetails.startedAt) }}</dd>
                            </div>
                            <div v-if="selectedMergedEvent.callDetails.completedAt">
                                <dt>Completed</dt>
                                <dd>{{ formatDate(selectedMergedEvent.callDetails.completedAt) }}</dd>
                            </div>
                            <div v-if="selectedMergedEvent.callDetails.durationMs != null">
                                <dt>Duration</dt>
                                <dd>{{ formatDurationWithMs(selectedMergedEvent.callDetails.durationMs) }}</dd>
                            </div>
                            <div v-if="selectedMergedEvent.callDetails.activeDurationMs != null">
                                <dt>Active</dt>
                                <dd>{{ formatDurationWithMs(selectedMergedEvent.callDetails.activeDurationMs) }}</dd>
                            </div>
                            <div v-if="selectedMergedEvent.callDetails.waitDurationMs != null">
                                <dt>Wait</dt>
                                <dd>{{ formatDurationWithMs(selectedMergedEvent.callDetails.waitDurationMs) }}</dd>
                            </div>
                            <div v-if="selectedToolPhaseTimings.length">
                                <dt>Phases</dt>
                                <dd>{{ formatPhaseCountSummary(selectedToolPhaseTimings.length, modalPhaseGroups.length || getEventTimingPresentation(selectedMergedEvent.callDetails).phaseGroupCount) }}</dd>
                            </div>
                        </dl>
                        <div v-if="modalPhaseGroupsPending" class="tool-phase-section">
                            <h5>Phase Breakdown</h5>
                            <p class="tool-phase-loading">Preparing phase breakdown…</p>
                        </div>
                        <div v-else-if="modalPhaseGroups.length" class="tool-phase-section">
                            <div class="tool-phase-section-header">
                                <h5>Phase Breakdown</h5>
                            </div>
                            <ol class="tool-phase-list">
                                <li v-for="group in modalPhaseGroups" :key="group.key" class="tool-phase-item">
                                    <div class="tool-phase-header">
                                        <div class="tool-phase-title-group">
                                            <span class="tool-phase-title">{{ group.title }}</span>
                                            <span v-if="group.count > 1" class="tool-phase-count-pill">{{ group.count }} occurrences</span>
                                        </div>
                                        <span class="tool-phase-duration">{{ formatToolPhaseGroupDuration(group) }}</span>
                                    </div>
                                    <div class="tool-phase-meta">
                                        <span>{{ formatTimingAvailability(group.availability) }}</span>
                                        <span v-if="group.outcome">{{ formatToolOutcome(group.outcome) }}</span>
                                        <span v-if="group.startedAt">First started {{ formatDate(group.startedAt) }}</span>
                                        <span v-if="group.completedAt">Last completed {{ formatDate(group.completedAt) }}</span>
                                    </div>
                                    <p v-if="group.summary" class="tool-phase-summary">{{ group.summary }}</p>
                                    <button
                                        v-if="group.count > 1"
                                        type="button"
                                        class="tool-phase-toggle"
                                        @click="togglePhaseGroup(group.key)"
                                    >
                                        {{ isPhaseGroupExpanded(group.key) ? 'Hide raw occurrences' : 'Show raw occurrences' }}
                                    </button>
                                    <ol v-if="group.count > 1 && isPhaseGroupExpanded(group.key)" class="tool-phase-occurrence-list">
                                        <li
                                            v-for="phase in group.phases"
                                            :key="`${group.key}:${phase.sequence ?? phase.occurrence ?? formatPhaseTitle(phase)}`"
                                            class="tool-phase-occurrence-item"
                                        >
                                            <div class="tool-phase-header">
                                                <span class="tool-phase-title">{{ formatPhaseTitle(phase) }}</span>
                                                <span class="tool-phase-duration">{{ formatPhaseDuration(phase) }}</span>
                                            </div>
                                            <div class="tool-phase-meta">
                                                <span>{{ formatTimingAvailability(phase.availability) }}</span>
                                                <span v-if="phase.outcome">{{ formatToolOutcome(phase.outcome) }}</span>
                                                <span v-if="phase.startedAt">Started {{ formatDate(phase.startedAt) }}</span>
                                                <span v-if="phase.completedAt">Completed {{ formatDate(phase.completedAt) }}</span>
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

            <!-- Global Review Summary Modal -->
            <ModalDialog v-model:isOpen="isSummaryModalOpen" title="Review Summary & Findings" size="large">
                <div class="summary-modal-layout">
                    <!-- Sticky Filter Bar -->
                    <header class="modal-filter-bar">
                        <div class="findings-header">
                            <div class="header-main">
                                <h4>Findings Matrix</h4>
                                <div class="severity-summary-row mini-stats">
                                    <div v-for="(count, sev) in severityCounts" :key="sev"
                                         class="sev-summary-pill" :class="`pill-${sev}`"
                                         v-show="count > 0"
                                    >
                                        <span class="pill-count">{{ count }}</span>
                                    </div>
                                </div>
                            </div>
                            <div class="comments-filter-controls">
                                <i class="fi fi-rr-search filter-icon"></i>
                                <input v-model="localSearchQuery" type="text" class="comment-search-input" placeholder="Search findings…" />
                                <div class="severity-pills">
                                    <button v-for="sev in ['error', 'warning', 'info', 'suggestion']" :key="sev"
                                        class="severity-pill" :class="[`severity-pill--${sev}`, { 'severity-pill--active': localSeverities.has(sev) }]"
                                        :data-severity="sev"
                                        @click="toggleSeverity(sev)">{{ sev }}</button>
                                </div>
                            </div>
                        </div>
                    </header>

                    <div class="modal-body-scroll">
                        <section class="summary-text-section">
                            <details open class="summary-details">
                                <summary>Executive Summary</summary>
                                <div v-if="reviewStatus?.result?.summary"
                                     class="markdown-content summary-full-text"
                                     v-html="renderMarkdown(reviewStatus.result.summary)"
                                ></div>
                                <p v-else class="empty-state">No detailed summary available.</p>
                            </details>
                        </section>

                        <section class="summary-findings-section">
                            <div class="findings-list-container">
                                <template v-if="groupedReviewComments.length">
                                    <div v-for="group in groupedReviewComments" :key="group.directory" class="comment-group">
                                        <div class="comment-group-header">{{ group.directory }}</div>
                                        <ul class="json-comments-list synthesis-comments">
                                            <li v-for="(comment, idx) in group.comments" :key="idx" class="json-comment-item synthesis-comment" :class="`severity-${comment.severity}`">
                                                <div class="comment-header">
                                                    <strong class="comment-sev">{{ (comment.severity ?? 'note').toUpperCase() }}</strong>
                                                    <span class="monospace-value">{{ comment.filePath ?? (comment as any).file_path }}:L{{ comment.lineNumber ?? (comment as any).line_number }}</span>
                                                    <button
                                                        v-if="routeClientId"
                                                        class="dismiss-btn"
                                                        :disabled="dismissingIds.has(commentKey(comment))"
                                                        @click.stop="dismissComment(comment)"
                                                        title="Dismiss this finding"
                                                    >{{ dismissingIds.has(commentKey(comment)) ? '…' : 'Dismiss' }}</button>
                                                </div>
                                                <div class="comment-msg-container markdown-content">
                                                    <div v-html="renderMarkdown(comment.message)"></div>
                                                </div>
                                            </li>
                                        </ul>
                                    </div>
                                </template>
                                <p v-else class="comments-empty-state">No findings match your filters.</p>
                            </div>
                        </section>
                    </div>
                </div>
            </ModalDialog>
        </template>

        <!-- Dismiss toast notification -->
        <Transition name="toast-fade">
            <div v-if="dismissToast" class="dismiss-toast" :class="{ 'dismiss-toast--error': dismissToast.isError }">
                {{ dismissToast.message }}
            </div>
        </Transition>
    </div>
</template>

<script lang="ts" setup>
import { computed, nextTick, onMounted, onUnmounted, ref, shallowRef, watch } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { AppTopBar } from '@/components'
import ModalDialog from '@/components/ModalDialog.vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import TokenBreakdownTable from '@/components/TokenBreakdownTable.vue'
import MarkdownIt from 'markdown-it'
import DOMPurify from 'dompurify'

const md = new MarkdownIt({
    html: false,
    linkify: true,
    breaks: true
})

function renderMarkdown(content: string | null | undefined): string {
    if (!content) return ''
    return DOMPurify.sanitize(md.render(content))
}

const route = useRoute()
import { createAdminClient } from '@/services/api'
import { createDismissal } from '@/services/findingDismissalsService'
import type { components } from '@/services/generated/openapi'

type ReviewJobProtocolDto = components['schemas']['ReviewJobProtocolDto']
type ProtocolEventDto = components['schemas']['ProtocolEventDto']
type ProtocolEventPhaseTimingDto = components['schemas']['ProtocolEventPhaseTimingDto']
type ReviewJobResultDto = components['schemas']['ReviewJobResultDto']
type ReviewStrategy = components['schemas']['ReviewStrategy']
type ReviewJobProtocolPassResponse = components['schemas']['ReviewJobProtocolDto']

interface ProtocolFileOutcome {
    filePath: string
    isComplete?: boolean
    isFailed?: boolean
    isExcluded?: boolean
    isCarriedForward?: boolean
    exclusionReason?: string | null
    errorMessage?: string | null
    isDegraded?: boolean
}

interface ProtocolFollowUp {
    used?: boolean
    triggerFamily?: string | null
    completedSuccessfully?: boolean
    dependencyRecorded?: boolean
}

interface ProtocolRepeatedJudgment {
    findingId?: string | null
    evidenceSetId?: string | null
    agreementState?: string | null
    recommendedDisposition?: string | null
    usedSameEvidenceSet?: boolean
    reasonCodes?: string[] | null
}

interface ProtocolInheritance {
    sourceJobId: string
    sourceFileResultId?: string | null
    sourceProtocolId: string
    sourceCompletedAt?: string | null
}

interface TokenBreakdownEntry {
    connectionCategory: number | string | null
    modelId: string | null
    totalInputTokens: number
    totalOutputTokens: number
    totalCachedInputTokens?: number | null
}

interface JobDetail {
    aiModel: string | null
    reviewTemperature: number | null
    tokenBreakdown: TokenBreakdownEntry[]
    breakdownConsistent: boolean | null
    submittedAt?: string | null
    processingStartedAt?: string | null
    completedAt?: string | null
}

interface CommentRelevanceEventDetails {
    implementationId?: string
    implementationVersion?: string
    filePath?: string
    originalCommentCount?: number
    keptCount?: number
    discardedCount?: number
    degradedComponents?: string[]
    fallbackChecks?: string[]
    degradedCause?: string | null
}

interface CommentRelevanceDiscardedComment {
    filePath?: string
    lineNumber?: number | null
    severity?: string
    message?: string
    reasonCodes?: string[]
    decisionSource?: string
}

interface CommentRelevanceAiTokenUsage {
    implementationId?: string
    filePath?: string
    inputTokens?: number
    outputTokens?: number
    modelCategory?: number | string | null
    modelId?: string | null
}

interface CommentRelevanceOutputRecord extends CommentRelevanceEventDetails {
    reasonBuckets?: Record<string, number>
    decisionSources?: Record<string, number>
    discarded?: CommentRelevanceDiscardedComment[]
    aiTokenUsage?: CommentRelevanceAiTokenUsage | null
}

interface FinalGateSummaryRecord {
    candidateCount?: number
    publishCount?: number
    summaryOnlyCount?: number
    dropCount?: number
    categoryCounts?: Record<string, number>
    invariantBlockedCount?: number
    originalSummary?: string | null
    finalSummary?: string | null
    summaryRewritePerformed?: boolean
    droppedFindingIds?: string[]
    summaryOnlyFindingIds?: string[]
    summaryRuleSource?: string | null
}

interface VerificationRecord {
    findingId?: string
    claimId?: string
    filePath?: string | null
    stage?: string | null
    coverageState?: string | null
    claimCount?: number
    droppedCount?: number
    summaryOnlyCount?: number
    degradedComponent?: string | null
}

interface VerificationEvidenceAttemptRecord {
    attemptId?: string
    claimId?: string
    sourceFamily?: string
    attemptOrder?: number
    status?: string
    scopeSummary?: string
    coverageImpact?: string
    payloadReference?: string | null
    failureReason?: string | null
}

interface VerificationEvidenceItemRecord {
    kind?: string
    sourceId?: string | null
    summary?: string
    payloadReference?: string | null
    freshnessState?: string | null
}

interface VerificationEvidenceOutputRecord {
    claimId?: string
    evidenceItems?: VerificationEvidenceItemRecord[]
    coverageState?: string
    retrievalNotes?: string | null
    evidenceAttempts?: VerificationEvidenceAttemptRecord[]
    hasProCursorAttempt?: boolean
    proCursorResultStatus?: string | null
}

interface FinalGateProvenance {
    originKind?: string
    generatedByStage?: string
    sourceFilePath?: string | null
    sourceFileResultId?: string | null
    sourceCommentOrdinal?: number | null
}

interface FinalGateEvidence {
    supportingFindingIds?: string[]
    supportingFiles?: string[]
    evidenceResolutionState?: string
    evidenceSource?: string
}

interface FinalGateDecisionRecord {
    findingId?: string
    disposition?: string
    category?: string
    provenance?: FinalGateProvenance | null
    evidence?: FinalGateEvidence | null
    reasonCodes?: string[]
    blockedInvariantIds?: string[]
    ruleSource?: string
    summaryText?: string | null
    includedInFinalSummary?: boolean | null
}

interface AgenticToolUsageRecord {
    ToolName?: string
    toolName?: string
    Status?: string
    status?: string
    Target?: string | null
    target?: string | null
}

interface AgenticInvestigationOutputRecord {
    Status?: string
    status?: string
    ToolUsage?: AgenticToolUsageRecord[]
    toolUsage?: AgenticToolUsageRecord[]
    Degraded?: boolean
    degraded?: boolean
    candidateCount?: number | null
    CandidateCount?: number | null
    evidenceCount?: number | null
    EvidenceCount?: number | null
}

type ReviewCommentRecord = {
    filePath?: string | null
    lineNumber?: number | null
    severity?: string | null
    message: string
}

interface MergedEvent {
    id: string
    time: string
    name: string
    callDetails: ProtocolEventDto
    resultDetails: ProtocolEventDto | null
}

interface EventDisplayRow {
    id: string
    merged: MergedEvent
    depth: number
    parentId: string | null
    parentName: string | null
    isToolChild: boolean
    childCount: number
    isExpanded: boolean
    timingSummary: string | null
    timingDetail: string | null
}

interface PendingToolRow {
    merged: MergedEvent
}

interface TimingInsight {
    rank: number
    protocolId: string | null
    eventId: string
    passLabel: string
    toolName: string
    durationMs: number
    waitDurationMs: number | null
    activeDurationMs: number | null
    hasPhaseDetail: boolean
}

interface ToolPhaseGroup {
    key: string
    title: string
    count: number
    totalDurationMs: number | null
    availability: string | null
    outcome: string | null
    startedAt: string | null
    completedAt: string | null
    summary: string | null
    phases: ProtocolEventPhaseTimingDto[]
}

interface EventTimingPresentation {
    phaseTimings: ProtocolEventPhaseTimingDto[]
    phaseGroupCount: number
    summary: string | null
    detail: string | null
}

type ReviewProtocolPass = ReviewJobProtocolDto & {
    id?: string
    attemptNumber?: number
    label?: string | null
    outcome?: string | null
    resolvedReviewStrategy?: ReviewStrategy | null
    strategySelectionSource?: string | null
    fileOutcome?: ProtocolFileOutcome | null
    followUp?: ProtocolFollowUp | null
    repeatedJudgment?: ProtocolRepeatedJudgment | null
    inheritance?: ProtocolInheritance | null
    isInherited?: boolean
    events?: ProtocolEventDto[]
    startedAt?: string
    completedAt?: string | null
    totalInputTokens?: number | null
    totalOutputTokens?: number | null
    totalCachedInputTokens?: number | null
    iterationCount?: number | null
    toolCallCount?: number | null
    finalSummary?: string | null
    finalComments?: ReviewCommentRecord[] | null
}

const loading = ref(false)
const error = ref('')
const activeTab = ref<'summary' | 'traces' | 'tokens'>('summary')
const protocols = shallowRef<ReviewProtocolPass[]>([])
const loadedProtocolIds = ref<Set<string>>(new Set())
const loadingProtocolIds = ref<Set<string>>(new Set())
const activePassId = ref<string | null>(null)
const selectedMergedEvent = ref<MergedEvent | null>(null)
const expandedPhaseGroups = ref<Set<string>>(new Set())
const modalPhaseGroups = shallowRef<ToolPhaseGroup[]>([])
const modalPhaseGroupsPending = ref(false)
const reviewStatus = ref<ReviewJobResultDto | null>(null)
const jobDetail = ref<JobDetail | null>(null)
const collapsedFolders = ref<Set<string>>(new Set())
const collapsedEventParents = ref<Set<string>>(new Set())
const selectedCommentPath = ref<string | null>(null)
const isSummaryModalOpen = ref(false)

// clientId: prefer route query (from ReviewHistorySection navigation), fall back to job result data
const routeClientId = computed(() =>
    (route.query?.clientId as string | undefined) ?? reviewStatus.value?.clientId ?? undefined
)

const jobShortId = computed(() => {
    const id = protocols.value[0]?.jobId
    if (!id) return '—'
    return id.substring(0, 8)
})

const prReviewLink = computed(() => {
    if (!protocols.value.length || !routeClientId.value) return null
    const p = protocols.value[0]
    // Some protocols might not have full PR info yet if it's a very fresh/failed job
    if (!p.providerScopePath || !p.providerProjectKey || !p.repositoryId || !p.pullRequestId) return null

    return {
        name: 'pr-review',
        query: {
            clientId: routeClientId.value,
            providerScopePath: p.providerScopePath,
            providerProjectKey: p.providerProjectKey,
            repositoryId: p.repositoryId,
            pullRequestId: String(p.pullRequestId),
        },
    }
})

const backToReviewsLink = computed(() => {
    return {
        name: 'reviews',
        query: route.query.clientId ? { clientId: route.query.clientId } : {}
    }
})

// One-click dismiss (US1)
const dismissingIds = ref<Set<string>>(new Set())
const dismissToast = ref<{ message: string; isError: boolean } | null>(null)

function commentKey(comment: any): string {
    return `${comment.filePath ?? (comment as any).file_path ?? ''}:${comment.lineNumber ?? (comment as any).line_number ?? 0}:${String(comment.message ?? '').slice(0, 80)}`
}

async function dismissComment(comment: any) {
    const cid = routeClientId.value
    if (!cid) {
        dismissToast.value = { message: 'Cannot dismiss: client context not available.', isError: true }
        setTimeout(() => { dismissToast.value = null }, 3000)
        return
    }
    const key = commentKey(comment)
    dismissingIds.value = new Set([...dismissingIds.value, key])
    try {
        await createDismissal(cid, { originalMessage: comment.message ?? '', label: '' })
        dismissToast.value = { message: 'Finding dismissed.', isError: false }
    } catch {
        dismissToast.value = { message: 'Failed to dismiss finding.', isError: true }
    } finally {
        const next = new Set(dismissingIds.value)
        next.delete(key)
        dismissingIds.value = next
        setTimeout(() => { dismissToast.value = null }, 3000)
    }
}

// Global search (filters the tree)
const globalSearchQuery = ref('')

// Local search (filters comments within a file or the summary modal)
const localSearchQuery = ref('')
const localSeverities = ref<Set<string>>(new Set())

const severityCounts = computed(() => {
    const counts = { error: 0, warning: 0, info: 0, suggestion: 0 }
    const all = reviewStatus.value?.result?.comments || []
    all.forEach(c => {
        const sev = (c.severity ?? '').toLowerCase() as keyof typeof counts
        if (counts[sev] !== undefined) counts[sev]++
    })
    return counts
})

function toggleSeverity(sev: string) {
    if (localSeverities.value.has(sev)) {
        localSeverities.value.delete(sev)
    } else {
        localSeverities.value.add(sev)
    }
    // Trigger reactivity — Sets are not deeply reactive by default
    localSeverities.value = new Set(localSeverities.value)
}

/**
 * Filtered comments for the sidebar tree navigation (Global Search)
 */
const filteredCommentsForTree = computed(() => {
    const all = reviewStatus.value?.result?.comments || []
    const qLabel = globalSearchQuery.value.trim().toLowerCase()

    if (!qLabel) return all

    return all.filter(c => {
        const fp = ((c.filePath ?? (c as any).file_path) ?? '').toLowerCase()
        return fp.includes(qLabel)
    })
})

/**
 * Filtered comments for the detail view / summary modal (Local Search + Severities)
 */
const filteredCommentsForDetail = computed(() => {
    const all = reviewStatus.value?.result?.comments || []
    const q = localSearchQuery.value.trim().toLowerCase()
    const sevs = localSeverities.value

    return all.filter(c => {
        const matchesSev = sevs.size === 0 || sevs.has((c.severity ?? '').toLowerCase())
        if (!matchesSev) return false
        if (!q) return true
        const msg = (c.message ?? '').toLowerCase()
        const fp = ((c.filePath ?? (c as any).file_path) ?? '').toLowerCase()
        return msg.includes(q) || fp.includes(q)
    })
})

function toggleFolder(dir: string) {
    if (collapsedFolders.value.has(dir)) {
        collapsedFolders.value.delete(dir)
    } else {
        collapsedFolders.value.add(dir)
    }
}

function toggleEventParent(eventId: string) {
    const next = new Set(collapsedEventParents.value)
    if (next.has(eventId)) {
        next.delete(eventId)
    } else {
        next.add(eventId)
    }

    collapsedEventParents.value = next
}

function parseFilePath(label: string | null | undefined) {
    if (!label) return { filename: 'Pass', directory: '' };
    // Normalise path separators
    const path = label.replace(/\\/g, '/');
    const parts = path.split('/');
    const filename = parts.pop() || path;
    const directory = parts.join('/') || '';
    return { filename, directory };
}

const sidebarItems = computed(() => {
    // Build tree
    const root: any = { children: {} };

    protocols.value.forEach(p => {
        const { directory } = parseFilePath(p.label);

        // Robust directory detection for root files
        let parts: string[];
        if (!directory || directory === '.' || directory === './') {
            parts = ['./'];
        } else {
            parts = directory.split('/').filter(Boolean);
        }

        let curr = root;
        parts.forEach((part, i) => {
            if (!curr.children[part]) {
                const nodePath = parts.slice(0, i + 1).join('/');
                curr.children[part] = {
                    name: part,
                    path: nodePath,
                    children: {},
                    protocols: []
                };
            }
            curr = curr.children[part];
        });
        curr.protocols.push(p);
    });

    // Flatten tree
    const items: any[] = [];
    const flatten = (node: any, depth: number) => {
        // Sort folders: prioritize ./ at the top, then alphabetically
        const sortedFolderKeys = Object.keys(node.children ?? {}).sort((a, b) => {
            if (a === './') return -1;
            if (b === './') return 1;
            return a.localeCompare(b);
        });

        const passes = node.protocols ?? [];
        const sortedPasses = [...passes].sort((a, b) => {
            const nameA = parseFilePath(a.label).filename;
            const nameB = parseFilePath(b.label).filename;
            return nameA.localeCompare(nameB);
        });

        sortedFolderKeys.forEach((key, idx) => {
            const childNode = node.children[key];
            const isCollapsed = collapsedFolders.value.has(childNode.path);
            const isLast = (idx === sortedFolderKeys.length - 1) && (sortedPasses.length === 0);

            items.push({
                type: 'folder',
                name: childNode.name,
                path: childNode.path,
                depth,
                isCollapsed,
                isLast
            });

            if (!isCollapsed) {
                flatten(childNode, depth + 1);
            }
        });

        sortedPasses.forEach((p: any, idx) => {
            const isLast = (idx === sortedPasses.length - 1);
            items.push({
                type: 'pass',
                name: parseFilePath(p.label).filename,
                depth,
                protocol: p,
                slowestToolLabel: slowestToolDurationLabel(p),
                isLast
            });
        });
    };
    flatten(root, 0);
    return items;
});

const commentSidebarItems = computed(() => {
    const comments = filteredCommentsForTree.value;
    const root: any = { children: {}, files: {} };

    comments.forEach(c => {
        const path = (c.filePath || (c as any).file_path || '');
        const { filename, directory } = parseFilePath(path);

        let parts: string[];
        if (!directory || directory === '.' || directory === './') {
            parts = ['./'];
        } else {
            parts = directory.split('/').filter(Boolean);
        }

        let curr = root;
        parts.forEach((part, i) => {
            if (!curr.children[part]) {
                curr.children[part] = {
                    name: part,
                    path: parts.slice(0, i + 1).join('/'),
                    children: {},
                    files: {}
                };
            }
            curr = curr.children[part];
        });

        if (!curr.files[filename]) {
            curr.files[filename] = { name: filename, path, commentCount: 0 };
        }
        curr.files[filename].commentCount++;
    });

    const items: any[] = [];
    const flatten = (node: any, depth: number) => {
        const sortedFolderKeys = Object.keys(node.children ?? {}).sort((a, b) => {
            if (a === './') return -1;
            if (b === './') return 1;
            return a.localeCompare(b);
        });

        const sortedFileKeys = Object.keys(node.files ?? {}).sort();

        sortedFolderKeys.forEach((key, idx) => {
            const childNode = node.children[key];
            const isCollapsed = collapsedFolders.value.has('comments:' + childNode.path);
            const isLast = (idx === sortedFolderKeys.length - 1) && (sortedFileKeys.length === 0);

            items.push({
                type: 'folder',
                name: childNode.name,
                path: childNode.path,
                depth,
                isCollapsed,
                isLast
            });

            if (!isCollapsed) {
                flatten(childNode, depth + 1);
            }
        });

        sortedFileKeys.forEach((key, idx) => {
            const file = node.files[key];
            const isLast = (idx === sortedFileKeys.length - 1);
            items.push({
                type: 'file',
                name: file.name,
                path: file.path,
                depth,
                commentCount: file.commentCount,
                isLast
            });
        });
    };
    flatten(root, 0);
    return items;
});

const groupedReviewComments = computed(() => {
    let comments = filteredCommentsForDetail.value

    // Filter by sidebar selection
    if (selectedCommentPath.value) {
        comments = comments.filter(c => {
            const path = (c.filePath || (c as any).file_path || '');
            return path === selectedCommentPath.value || path.startsWith(selectedCommentPath.value + '/');
        });
    }

    const groups: Record<string, any[]> = {}

    comments.forEach(c => {
        const path = (c.filePath || (c as any).file_path || '');
        const { directory } = parseFilePath(path)
        const dirKey = directory || 'Root'
        if (!groups[dirKey]) groups[dirKey] = []
        groups[dirKey].push(c)
    })

    // Sort directories: Root first, then alphabetically
    const sortedDirKeys = Object.keys(groups).sort((a, b) => {
        if (a === 'Root') return -1
        if (b === 'Root') return 1
        return a.localeCompare(b)
    })

    return sortedDirKeys.map(dir => {
        // Sort comments within directory by filePath and then lineNumber
        const sortedComments = [...groups[dir]].sort((a, b) => {
            const pathA = a.filePath || (a as any).file_path || ''
            const pathB = b.filePath || (b as any).file_path || ''
            if (pathA !== pathB) return pathA.localeCompare(pathB)
            return (a.lineNumber || (a as any).line_number || 0) - (b.lineNumber || (b as any).line_number || 0)
        })

        return {
            directory: dir,
            comments: sortedComments
        }
    })
})

const activePassFinalComments = computed<ReviewCommentRecord[]>(() => {
    return activePass.value?.finalComments ?? []
})

const activePass = computed<ReviewProtocolPass | null>(() => {
    if (!protocols.value.length || activeTab.value === 'summary') return null
    return protocols.value.find(p => p.id === activePassId.value) ?? protocols.value[0]
})

watch(activePassId, (protocolId) => {
    if (!protocolId) {
        return
    }

    void ensureProtocolPassLoaded(protocolId)
}, { flush: 'post' })

const activePassEventRows = computed<EventDisplayRow[]>(() => {
    return buildEventRows(activePass.value?.events)
})

const traceTimingInsights = computed<TimingInsight[]>(() => {
    const protocol = activePass.value
    if (!protocol) {
        return []
    }

    const protocolId = protocol.id ?? null
    const passLabel = protocol.label ?? 'Unknown pass'

    return (protocol.events ?? [])
        .filter(event => (event.kind ?? '').toLowerCase() === 'toolcall' && event.durationMs != null)
        .map(event => ({
            protocolId,
            eventId: event.id ?? `${protocolId ?? 'protocol'}:${event.name ?? 'tool'}`,
            passLabel,
            toolName: event.name ?? 'Unknown tool',
            durationMs: event.durationMs ?? 0,
            waitDurationMs: event.waitDurationMs ?? null,
            activeDurationMs: event.activeDurationMs ?? null,
            hasPhaseDetail: getPhaseTimings(event).length > 0,
        }))
        .sort((left, right) => right.durationMs - left.durationMs)
        .slice(0, 3)
        .map((insight, index) => ({
            rank: index + 1,
            ...insight,
        }))
})

const activePassSlowestToolSummary = computed(() => {
    const events = (activePass.value?.events ?? [])
        .filter(event => (event.kind ?? '').toLowerCase() === 'toolcall' && event.durationMs != null)
        .sort((left, right) => (right.durationMs ?? 0) - (left.durationMs ?? 0))

    const slowest = events[0]
    if (!slowest || slowest.durationMs == null) {
        return 'No captured tool timing'
    }

    return `${slowest.name ?? 'Unknown tool'} (${formatDurationWithMs(slowest.durationMs)})`
})

const totalInputTokens = computed(() =>
    protocols.value.reduce((sum, p) => sum + (p.totalInputTokens ?? 0), 0),
)
const totalOutputTokens = computed(() =>
    protocols.value.reduce((sum, p) => sum + (p.totalOutputTokens ?? 0), 0),
)
const totalCachedInputTokens = computed(() =>
    protocols.value.reduce((sum, p) => sum + (p.totalCachedInputTokens ?? 0), 0),
)
const totalEffectiveInputTokens = computed(() =>
    Math.max(0, totalInputTokens.value - totalCachedInputTokens.value),
)

const overallDuration = computed(() => {
    const start = jobDetail.value?.processingStartedAt ?? jobDetail.value?.submittedAt
    if (!start) return '—'

    const startMs = new Date(start).getTime()
    if (Number.isNaN(startMs)) return '—'

    const end = jobDetail.value?.completedAt ?? null
    const endMs = end ? new Date(end).getTime() : Date.now()
    if (Number.isNaN(endMs)) return '—'

    return formatDurationMs(endMs - startMs)
})

const reviewModelDisplay = computed(() => jobDetail.value?.aiModel?.trim() || 'Default')

const reviewStrategyDisplay = computed(() =>
    formatReviewStrategy(protocols.value[0]?.resolvedReviewStrategy),
)

const activePassReviewStrategyDisplay = computed(() =>
    formatReviewStrategy(activePass.value?.resolvedReviewStrategy ?? protocols.value[0]?.resolvedReviewStrategy),
)

const activePassFileOutcome = computed<ProtocolFileOutcome | null>(() =>
    activePass.value?.fileOutcome ?? null,
)

const activePassFollowUp = computed<ProtocolFollowUp | null>(() =>
    activePass.value?.followUp ?? null,
)

const activePassRepeatedJudgment = computed<ProtocolRepeatedJudgment | null>(() =>
    activePass.value?.repeatedJudgment ?? null,
)

const activePassProRvPrefilter = computed(() =>
    activePass.value?.proRvPrefilter ?? null,
)

const reviewTemperatureDisplay = computed(() => formatTemperature(jobDetail.value?.reviewTemperature))

const inheritedProtocolCount = computed(() => protocols.value.filter(protocol => protocol.isInherited).length)

const activePassInheritance = computed<ProtocolInheritance | null>(() =>
    activePass.value?.inheritance ?? null,
)

const protocolTokenBreakdown = computed<TokenBreakdownEntry[]>(() => {
    const grouped = new Map<string, TokenBreakdownEntry>()

    protocols.value.forEach(protocol => {
        const input = protocol.totalInputTokens ?? 0
        const output = protocol.totalOutputTokens ?? 0
        const cached = protocol.totalCachedInputTokens ?? 0
        if (input === 0 && output === 0) {
            return
        }

        const connectionCategory = protocol.aiConnectionCategory ?? null
        const modelId = protocol.modelId ?? null
        const key = `${String(connectionCategory ?? 'unknown')}|${modelId ?? '(default)'}`
        const existing = grouped.get(key)
        if (existing) {
            existing.totalInputTokens += input
            existing.totalOutputTokens += output
            existing.totalCachedInputTokens = (existing.totalCachedInputTokens ?? 0) + cached
            return
        }

        grouped.set(key, {
            connectionCategory,
            modelId,
            totalInputTokens: input,
            totalOutputTokens: output,
            totalCachedInputTokens: cached,
        })
    })

    return Array.from(grouped.values()).sort((left, right) => {
        const leftTotal = left.totalInputTokens + left.totalOutputTokens
        const rightTotal = right.totalInputTokens + right.totalOutputTokens
        if (leftTotal !== rightTotal) {
            return rightTotal - leftTotal
        }

        return `${left.connectionCategory ?? ''}|${left.modelId ?? ''}`.localeCompare(`${right.connectionCategory ?? ''}|${right.modelId ?? ''}`)
    })
})

const protocolBreakdownConsistent = computed(() => {
    if (protocolTokenBreakdown.value.length === 0) {
        return jobDetail.value?.breakdownConsistent ?? null
    }

    const breakdownInput = protocolTokenBreakdown.value.reduce((sum, entry) => sum + entry.totalInputTokens, 0)
    const breakdownOutput = protocolTokenBreakdown.value.reduce((sum, entry) => sum + entry.totalOutputTokens, 0)
    return breakdownInput === totalInputTokens.value && breakdownOutput === totalOutputTokens.value
})

const parsedInputResult = computed(() => {
    if (!selectedMergedEvent.value?.callDetails.inputTextSample) return null;
    try {
        const text = selectedMergedEvent.value.callDetails.inputTextSample;
        if (text.startsWith('args=')) {
            return JSON.parse(text.substring(5));
        }
        return JSON.parse(text);
    } catch {
        return null;
    }
});

const parsedOutputResult = computed(() => {
    if (!selectedMergedEvent.value?.resultDetails?.outputSummary) return null;
    try {
        const parsed = JSON.parse(selectedMergedEvent.value.resultDetails.outputSummary);

        if (parsed && typeof parsed === 'object' && parsed.confidence_evaluations) {
            const evals = parsed.confidence_evaluations;
            if (Array.isArray(evals)) {
                parsed.confidence_evaluations = evals.map((e: any) => {
                    if (typeof e === 'object' && e !== null) {
                        return {
                            concern: e.concern || e.category || e.metric || e.type || e.name || 'Unknown',
                            confidence: e.confidence || e.level || e.score || 'N/A'
                        }
                    }
                    return { concern: 'Unknown', confidence: String(e) }
                });
            } else if (typeof evals === 'object') {
                parsed.confidence_evaluations = Object.entries(evals).map(([key, val]: [string, any]) => {
                    return {
                        concern: key,
                        confidence: typeof val === 'object' && val !== null ? (val.confidence || val.level || val.score || 'N/A') : val
                    }
                });
            }
        }
        return parsed;
    } catch {
        return null;
    }
});

function getPhaseTimings(event: ProtocolEventDto | null | undefined): ProtocolEventPhaseTimingDto[] {
    const raw = event?.phaseTimings
    if (Array.isArray(raw)) {
        return raw.filter((phase): phase is ProtocolEventPhaseTimingDto => !!phase && typeof phase === 'object')
    }

    return []
}

const eventTimingPresentationCache = new WeakMap<ProtocolEventDto, EventTimingPresentation>()

function getPhaseTimingDurationMs(phase: ProtocolEventPhaseTimingDto): number | null {
    if (phase.durationMs != null) {
        return phase.durationMs
    }

    if (phase.startedAt && phase.completedAt) {
        const duration = new Date(phase.completedAt).getTime() - new Date(phase.startedAt).getTime()
        if (!Number.isNaN(duration) && duration >= 0) {
            return duration
        }
    }

    return null
}

function summarizeSharedValue(values: Array<string | null | undefined>): string | null {
    const distinct = [...new Set(values.filter((value): value is string => !!value && value.trim().length > 0))]
    if (distinct.length === 0) {
        return null
    }

    return distinct.length === 1 ? distinct[0] : 'mixed'
}

function summarizePhaseGroupSummary(phases: ProtocolEventPhaseTimingDto[]): string | null {
    const summaries = [...new Set(phases
        .map(phase => phase.summary?.trim())
        .filter((summary): summary is string => !!summary))]

    if (summaries.length === 0) {
        return null
    }

    if (summaries.length === 1) {
        return summaries[0]
    }

    return `${summaries.length} distinct summaries recorded`
}

function buildToolPhaseGroups(phases: ProtocolEventPhaseTimingDto[]): ToolPhaseGroup[] {
    const groups = new Map<string, ToolPhaseGroup>()

    for (const phase of phases) {
        const key = (phase.name ?? phase.displayName ?? 'unnamed-phase').trim().toLowerCase() || 'unnamed-phase'
        const existing = groups.get(key)

        if (existing) {
            existing.count += 1
            existing.phases.push(phase)
            const durationMs = getPhaseTimingDurationMs(phase)
            if (durationMs != null) {
                existing.totalDurationMs = (existing.totalDurationMs ?? 0) + durationMs
            }

            if (!existing.startedAt || (phase.startedAt && new Date(phase.startedAt).getTime() < new Date(existing.startedAt).getTime())) {
                existing.startedAt = phase.startedAt ?? existing.startedAt
            }

            if (!existing.completedAt || (phase.completedAt && new Date(phase.completedAt).getTime() > new Date(existing.completedAt).getTime())) {
                existing.completedAt = phase.completedAt ?? existing.completedAt
            }

            existing.availability = summarizeSharedValue(existing.phases.map(entry => entry.availability))
            existing.outcome = summarizeSharedValue(existing.phases.map(entry => entry.outcome))
            existing.summary = summarizePhaseGroupSummary(existing.phases)
            continue
        }

        const durationMs = getPhaseTimingDurationMs(phase)
        groups.set(key, {
            key,
            title: phase.displayName ?? phase.name ?? 'Unnamed phase',
            count: 1,
            totalDurationMs: durationMs,
            availability: phase.availability ?? null,
            outcome: phase.outcome ?? null,
            startedAt: phase.startedAt ?? null,
            completedAt: phase.completedAt ?? null,
            summary: phase.summary?.trim() ?? null,
            phases: [phase],
        })
    }

    return [...groups.values()]
}

function getEventTimingPresentation(event: ProtocolEventDto | null | undefined): EventTimingPresentation {
    if (!event) {
        return {
            phaseTimings: [],
            phaseGroupCount: 0,
            summary: null,
            detail: null,
        }
    }

    const cached = eventTimingPresentationCache.get(event)
    if (cached) {
        return cached
    }

    const phaseTimings = getPhaseTimings(event)
    const phaseGroupCount = phaseTimings.length > 0 ? buildToolPhaseGroups(phaseTimings).length : 0

    let summary: string | null
    if (event.durationMs != null) {
        summary = formatDurationWithMs(event.durationMs)
    } else if (event.startedAt && event.completedAt) {
        const duration = new Date(event.completedAt).getTime() - new Date(event.startedAt).getTime()
        summary = !Number.isNaN(duration) && duration >= 0 ? formatDurationWithMs(duration) : 'Timing recorded'
    } else {
        summary = hasToolTiming(event) ? 'Timing recorded' : null
    }

    const parts: string[] = []
    if (event.activeDurationMs != null) {
        parts.push(`Active ${formatDurationWithMs(event.activeDurationMs)}`)
    }

    if (event.waitDurationMs != null) {
        parts.push(`Wait ${formatDurationWithMs(event.waitDurationMs)}`)
    }

    if (phaseTimings.length > 0) {
        parts.push(formatPhaseCountSummary(phaseTimings.length, phaseGroupCount))
    }

    const presentation: EventTimingPresentation = {
        phaseTimings,
        phaseGroupCount,
        summary,
        detail: parts.length > 0 ? parts.join(' · ') : null,
    }

    eventTimingPresentationCache.set(event, presentation)
    return presentation
}

const selectedToolPhaseTimings = computed(() => getEventTimingPresentation(selectedMergedEvent.value?.callDetails).phaseTimings)

function resetEventModalExpansionState(): void {
    expandedPhaseGroups.value = new Set()
    modalPhaseGroups.value = []
    modalPhaseGroupsPending.value = false
}

async function scheduleModalPhaseGrouping(): Promise<void> {
    const event = selectedMergedEvent.value?.callDetails
    const phaseTimings = getEventTimingPresentation(event).phaseTimings
    if (phaseTimings.length === 0) {
        modalPhaseGroups.value = []
        modalPhaseGroupsPending.value = false
        return
    }

    modalPhaseGroupsPending.value = true
    await nextTick()

    if (selectedMergedEvent.value?.callDetails !== event) {
        return
    }

    modalPhaseGroups.value = buildToolPhaseGroups(phaseTimings)
    modalPhaseGroupsPending.value = false
}

function togglePhaseGroup(key: string): void {
    const next = new Set(expandedPhaseGroups.value)
    if (next.has(key)) {
        next.delete(key)
    } else {
        next.add(key)
    }

    expandedPhaseGroups.value = next
}

function isPhaseGroupExpanded(key: string): boolean {
    return expandedPhaseGroups.value.has(key)
}

const selectedAiCallSessionTurn = computed(() => {
    if (!activePass.value?.events?.length || !selectedMergedEvent.value) {
        return null
    }

    const selectedIndex = activePass.value.events.findIndex(event => event.id === selectedMergedEvent.value?.callDetails.id)
    if (selectedIndex < 0) {
        return null
    }

    for (let i = selectedIndex + 1; i < activePass.value.events.length; i++) {
        const event = activePass.value.events[i]
        if (event.name === 'review_agent_session_turn') {
            return event
        }

        if (event.kind === 'AiCall') {
            break
        }
    }

    return null
})

const selectedAiCallProviderManagedNote = computed(() => {
    if ((selectedMergedEvent.value?.callDetails.kind ?? '').toLowerCase() !== 'aicall') {
        return null
    }

    const sessionTurn = selectedAiCallSessionTurn.value
    if (!sessionTurn?.inputTextSample) {
        return null
    }

    try {
        const parsed = JSON.parse(sessionTurn.inputTextSample)
        if (parsed?.sessionMode !== 'ProviderManagedSession') {
            return null
        }

        return 'Provider-managed session: this input/output panel shows only the local delta sent for this turn. Token counts may be higher because the provider accounts for the full continued conversation it retained server-side.'
    } catch {
        return null
    }
})

const commentRelevanceEventNames = new Set([
    'comment_relevance_filter_output',
    'comment_relevance_filter_degraded',
    'comment_relevance_evaluator_degraded',
    'comment_relevance_filter_selection_fallback',
])

const finalGateEventNames = new Set([
    'review_finding_gate_summary',
    'review_finding_gate_decision',
])

const verificationEventNames = new Set([
    'verification_claims_extracted',
    'verification_local_decision',
    'verification_evidence_collected',
    'verification_pr_decision',
    'verification_degraded',
    'summary_reconciliation',
])

const agenticInvestigationEventNames = new Set([
    'agentic_file_investigation_result',
    'agentic_file_degraded',
    'agentic_file_evidence_collected',
])

const selectedCommentRelevanceInput = computed<CommentRelevanceEventDetails | null>(() => {
    if (!isCommentRelevanceEvent(selectedMergedEvent.value?.callDetails.name)) {
        return null
    }

    return isPlainObject(parsedInputResult.value)
        ? parsedInputResult.value as CommentRelevanceEventDetails
        : null
})

const selectedCommentRelevanceOutput = computed<CommentRelevanceOutputRecord | null>(() => {
    if (!isCommentRelevanceEvent(selectedMergedEvent.value?.callDetails.name)) {
        return null
    }

    return isPlainObject(parsedOutputResult.value)
        ? parsedOutputResult.value as CommentRelevanceOutputRecord
        : null
})

const selectedFinalGateInput = computed<FinalGateSummaryRecord | null>(() => {
    if (!isFinalGateEvent(selectedMergedEvent.value?.callDetails.name)) {
        return null
    }

    return isPlainObject(parsedInputResult.value)
        ? parsedInputResult.value as FinalGateSummaryRecord
        : null
})

const selectedFinalGateSummaryOutput = computed<FinalGateSummaryRecord | null>(() => {
    if (selectedMergedEvent.value?.callDetails.name !== 'review_finding_gate_summary') {
        return null
    }

    return isPlainObject(parsedOutputResult.value)
        ? parsedOutputResult.value as FinalGateSummaryRecord
        : null
})

const selectedFinalGateDecisionOutput = computed<FinalGateDecisionRecord | null>(() => {
    if (selectedMergedEvent.value?.callDetails.name !== 'review_finding_gate_decision') {
        return null
    }

    return isPlainObject(parsedOutputResult.value)
        ? parsedOutputResult.value as FinalGateDecisionRecord
        : null
})

const selectedVerificationInput = computed<VerificationRecord | null>(() => {
    if (!isVerificationEvent(selectedMergedEvent.value?.callDetails.name)) {
        return null
    }

    return isPlainObject(parsedInputResult.value)
        ? parsedInputResult.value as VerificationRecord
        : null
})

const selectedVerificationOutput = computed<Record<string, unknown> | null>(() => {
    if (!isVerificationEvent(selectedMergedEvent.value?.callDetails.name)) {
        return null
    }

    return isPlainObject(parsedOutputResult.value)
        ? parsedOutputResult.value
        : null
})

const selectedVerificationEvidenceOutput = computed<VerificationEvidenceOutputRecord | null>(() => {
    if (selectedMergedEvent.value?.callDetails.name !== 'verification_evidence_collected') {
        return null
    }

    return isPlainObject(parsedOutputResult.value)
        ? parsedOutputResult.value as VerificationEvidenceOutputRecord
        : null
})

const selectedAgenticInvestigationOutput = computed<AgenticInvestigationOutputRecord | null>(() => {
    if (!isAgenticInvestigationEvent(selectedMergedEvent.value?.callDetails.name)) {
        return null
    }

    return isPlainObject(parsedOutputResult.value)
        ? parsedOutputResult.value as AgenticInvestigationOutputRecord
        : null
})

function isCommentRelevanceEvent(name: string | null | undefined): boolean {
    return !!name && commentRelevanceEventNames.has(name)
}

function isFinalGateEvent(name: string | null | undefined): boolean {
    return !!name && finalGateEventNames.has(name)
}

function isVerificationEvent(name: string | null | undefined): boolean {
    return !!name && verificationEventNames.has(name)
}

function isAgenticInvestigationEvent(name: string | null | undefined): boolean {
    return !!name && agenticInvestigationEventNames.has(name)
}

function isAgenticDegradedEvent(name: string | null | undefined): boolean {
    return name === 'agentic_file_degraded'
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
    return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function decodeHtmlEntities(value: string): string {
    if (!value.includes('&') || typeof document === 'undefined') {
        return value
    }

    const textarea = document.createElement('textarea')
    textarea.innerHTML = value
    return textarea.value
}

function decodeMergedEventEscapes(value: string): string {
    return value
        .replace(/\\u([0-9a-fA-F]{4})/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
        .replace(/\\x([0-9a-fA-F]{2})/g, (_, hex: string) => String.fromCharCode(parseInt(hex, 16)))
        .replace(/\\r\\n/g, '\n')
        .replace(/\\n/g, '\n')
        .replace(/\\r/g, '\r')
        .replace(/\\t/g, '\t')
        .replace(/\\f/g, '\f')
        .replace(/\\"/g, '"')
        .replace(/\\'/g, "'")
        .replace(/\\\//g, '/')
        .replace(/\\\\/g, '\\')
}

function renderMergedEventText(value: string | null | undefined): string {
    if (!value) {
        return ''
    }

    const trimmed = value.trim()
    if (trimmed.startsWith('"') && trimmed.endsWith('"')) {
        try {
            const parsed = JSON.parse(trimmed)
            if (typeof parsed === 'string') {
                return decodeHtmlEntities(parsed)
            }
        } catch {
            // Fall through to escape decoding for non-JSON raw strings.
        }
    }

    return decodeHtmlEntities(decodeMergedEventEscapes(value))
}

function formatCommentRelevanceImplementation(details: CommentRelevanceEventDetails): string {
    if (details.implementationId && details.implementationVersion) {
        return `${details.implementationId} @ ${details.implementationVersion}`
    }

    return details.implementationId ?? details.implementationVersion ?? 'Unknown'
}

function formatCommentRelevanceCounts(details: CommentRelevanceEventDetails): string {
    return `${details.originalCommentCount ?? 0} original -> ${details.keptCount ?? 0} kept / ${details.discardedCount ?? 0} discarded`
}

function formatFinalGateCounts(details: FinalGateSummaryRecord): string {
    return `${details.candidateCount ?? 0} candidates -> ${details.publishCount ?? 0} publish / ${details.summaryOnlyCount ?? 0} summary-only / ${details.dropCount ?? 0} drop`
}

function formatStringList(values: string[] | null | undefined): string {
    return values?.length ? values.join('\n') : 'None'
}

function hasEntries(value: Record<string, number> | null | undefined): boolean {
    return !!value && Object.keys(value).length > 0
}

function formatNamedCounts(value: Record<string, number> | null | undefined): string {
    if (!hasEntries(value)) {
        return 'None'
    }

    return Object.entries(value!)
        .sort(([left], [right]) => left.localeCompare(right))
        .map(([key, count]) => `${key}: ${count}`)
        .join('\n')
}

function commentLocation(filePath: string | undefined, lineNumber: number | null | undefined): string {
    if (!filePath) {
        return lineNumber ? `L${lineNumber}` : 'Unknown location'
    }

    return lineNumber ? `${filePath}:L${lineNumber}` : filePath
}

function severityVariant(severity: string | null | undefined): string {
    switch ((severity ?? '').toLowerCase()) {
        case 'error':
        case 'warning':
        case 'info':
        case 'suggestion':
            return severity.toLowerCase()
        default:
            return 'note'
    }
}

function formatCommentRelevanceAiUsage(usage: CommentRelevanceAiTokenUsage): string {
    const lines = [
        `Input tokens: ${formatTokens(usage.inputTokens)}`,
        `Output tokens: ${formatTokens(usage.outputTokens)}`,
    ]

    if (usage.modelId) {
        lines.push(`Model: ${usage.modelId}`)
    }

    if (usage.filePath) {
        lines.push(`File: ${usage.filePath}`)
    }

    return lines.join('\n')
}

function formatFinalGateProvenance(provenance: FinalGateProvenance | null | undefined): string {
    if (!provenance) {
        return 'None'
    }

    const lines = [
        `Origin: ${provenance.originKind ?? 'Unknown'}`,
        `Stage: ${provenance.generatedByStage ?? 'Unknown'}`,
    ]

    if (provenance.sourceFilePath) {
        lines.push(`Source file: ${provenance.sourceFilePath}`)
    }

    if (provenance.sourceFileResultId) {
        lines.push(`Source file result: ${provenance.sourceFileResultId}`)
    }

    if (provenance.sourceCommentOrdinal != null) {
        lines.push(`Source comment ordinal: ${provenance.sourceCommentOrdinal}`)
    }

    return lines.join('\n')
}

function formatFinalGateEvidence(evidence: FinalGateEvidence | null | undefined): string {
    if (!evidence) {
        return 'None'
    }

    const lines = [
        `Resolution: ${evidence.evidenceResolutionState ?? 'Unknown'}`,
        `Source: ${evidence.evidenceSource ?? 'Unknown'}`,
        `Supporting files: ${evidence.supportingFiles?.length ? evidence.supportingFiles.join(', ') : 'None'}`,
        `Supporting finding IDs: ${evidence.supportingFindingIds?.length ? evidence.supportingFindingIds.join(', ') : 'None'}`,
    ]

    return lines.join('\n')
}

function formatVerificationProCursorStatus(output: VerificationEvidenceOutputRecord): string {
    if (!output.hasProCursorAttempt) {
        return 'Not attempted'
    }

    return output.proCursorResultStatus ?? 'Unknown'
}

function formatEvidenceAttempts(attempts: VerificationEvidenceAttemptRecord[] | null | undefined): string {
    if (!attempts?.length) {
        return 'None'
    }

    return attempts
        .map(attempt => {
            const lines = [
                `${attempt.attemptOrder ?? '?'}: ${attempt.sourceFamily ?? 'Unknown'} -> ${attempt.status ?? 'Unknown'}`,
                `Impact: ${attempt.coverageImpact ?? 'Unknown'}`,
                `Scope: ${attempt.scopeSummary ?? 'Unknown'}`,
            ]

            if (attempt.failureReason) {
                lines.push(`Failure: ${attempt.failureReason}`)
            }

            return lines.join('\n')
        })
        .join('\n\n')
}

function formatEvidenceItems(items: VerificationEvidenceItemRecord[] | null | undefined): string {
    if (!items?.length) {
        return 'None'
    }

    return items
        .map(item => {
            const lines = [
                `Kind: ${item.kind ?? 'Unknown'}`,
                `Summary: ${item.summary ?? 'Unknown'}`,
            ]

            if (item.sourceId) {
                lines.push(`Source: ${item.sourceId}`)
            }

            if (item.freshnessState) {
                lines.push(`Freshness: ${item.freshnessState}`)
            }

            return lines.join('\n')
        })
        .join('\n\n')
}

function normalizeAgenticToolUsage(output: AgenticInvestigationOutputRecord | null | undefined): AgenticToolUsageRecord[] {
    if (!output) {
        return []
    }

    return output.ToolUsage ?? output.toolUsage ?? []
}

function describeAgenticToolStatus(status: string | null | undefined): string {
    switch ((status ?? '').toLowerCase()) {
        case 'success':
            return 'Runtime attempt succeeded.'
        case 'blocked_not_allowed':
            return 'Runtime blocked this attempt because the tool was not allowed for the investigation.'
        case 'blocked_budget_exhausted':
            return 'Runtime blocked this attempt because the investigation exhausted its tool budget.'
        case 'blocked_scope_violation':
            return 'Runtime blocked this attempt because the requested target was outside the approved file scope.'
        case 'failed':
            return 'Runtime attempted the lookup, but the repository/provider fetch failed.'
        default:
            return status || 'Unknown runtime status.'
    }
}

function formatAgenticToolUsage(output: AgenticInvestigationOutputRecord | null | undefined): string {
    const usage = normalizeAgenticToolUsage(output)
    if (!usage.length) {
        return 'No runtime tool attempts were recorded.'
    }

    return usage.map(item => {
        const toolName = item.ToolName ?? item.toolName ?? 'unknown_tool'
        const status = item.Status ?? item.status ?? 'unknown'
        const target = item.Target ?? item.target
        const lines = [
            `${toolName} -> ${status}`,
            describeAgenticToolStatus(status),
        ]

        if (target) {
            lines.splice(1, 0, `Target: ${target}`)
        }

        return lines.join('\n')
    }).join('\n\n')
}

function formatAgenticInvestigationStatus(output: AgenticInvestigationOutputRecord | null | undefined): string {
    const status = output?.Status ?? output?.status ?? 'unknown'
    const degraded = output?.Degraded ?? output?.degraded ?? false
    return degraded ? `${status} (non-validated degraded intermediate outcome)` : status
}

function agenticInvestigationCandidateCount(output: AgenticInvestigationOutputRecord | null | undefined): number | null {
    return output?.candidateCount ?? output?.CandidateCount ?? null
}

function agenticInvestigationEvidenceCount(output: AgenticInvestigationOutputRecord | null | undefined): number | null {
    return output?.evidenceCount ?? output?.EvidenceCount ?? null
}

let pollInterval: ReturnType<typeof setInterval> | null = null

function resetProtocolState() {
    error.value = ''
    protocols.value = []
    loadedProtocolIds.value = new Set()
    loadingProtocolIds.value = new Set()
    reviewStatus.value = null
    jobDetail.value = null
    activePassId.value = null

    if (pollInterval) {
        clearInterval(pollInterval)
        pollInterval = null
    }
}

async function loadProtocol(showLoading = false) {
    if (showLoading) loading.value = true
    try {
        const jobId = route.params.id as string
        const [protocolRes, resultRes, detailRes] = await Promise.all([
            createAdminClient().GET('/jobs/{id}/protocol', { params: { path: { id: jobId }, query: { includeEvents: false } } }),
            createAdminClient().GET('/jobs/{id}/result', { params: { path: { id: jobId } } }),
            createAdminClient().GET('/jobs/{id}', { params: { path: { id: jobId } } })
        ])

        const data = protocolRes.data as any
        const fetchError = protocolRes.error

        if (fetchError) {
            if (showLoading) error.value = 'Protocol not found for this job.'
        } else if (Array.isArray(data)) {
            const normalizedProtocols = [...data].sort(compareProtocols)
            protocols.value = normalizedProtocols
            loadedProtocolIds.value = new Set()
            if (resultRes.data) {
                reviewStatus.value = resultRes.data
            }
            if (detailRes.data) {
                const d = detailRes.data as any
                jobDetail.value = {
                    aiModel: d.aiModel ?? null,
                    reviewTemperature: d.reviewTemperature ?? null,
                    tokenBreakdown: d.tokenBreakdown ?? [],
                    breakdownConsistent: d.breakdownConsistent ?? null,
                    submittedAt: d.submittedAt ?? null,
                    processingStartedAt: d.processingStartedAt ?? null,
                    completedAt: d.completedAt ?? null,
                }
            }
            if (!activePassId.value && normalizedProtocols.length > 0 && normalizedProtocols[0].id) {
                activePassId.value = normalizedProtocols[0].id
            }

            const protocolIdToLoad = activePassId.value && normalizedProtocols.some(protocol => protocol.id === activePassId.value)
                ? activePassId.value
                : normalizedProtocols[0]?.id

            if (protocolIdToLoad) {
                if (activePassId.value !== protocolIdToLoad) {
                    activePassId.value = protocolIdToLoad
                }

                await ensureProtocolPassLoaded(protocolIdToLoad)
            }
            const isProcessing = normalizedProtocols.some((p: any) => !p.completedAt) || (resultRes.data?.status === 'processing')
            if (isProcessing && !pollInterval) {
                pollInterval = setInterval(() => loadProtocol(false), 3000)
            } else if (!isProcessing && pollInterval) {
                clearInterval(pollInterval)
                pollInterval = null
            }
        }
    } catch {
        if (showLoading) error.value = 'Failed to load protocol.'
    } finally {
        if (showLoading) loading.value = false
    }
}

async function ensureProtocolPassLoaded(protocolId: string) {
    if (loadedProtocolIds.value.has(protocolId) || loadingProtocolIds.value.has(protocolId)) {
        return
    }

    const jobId = route.params.id as string
    const nextLoading = new Set(loadingProtocolIds.value)
    nextLoading.add(protocolId)
    loadingProtocolIds.value = nextLoading

    try {
        const response = await createAdminClient().GET('/jobs/{id}/protocol/{protocolId}', {
            params: {
                path: {
                    id: jobId,
                    protocolId,
                },
            },
        })

        if (response.error || !response.data) {
            return
        }

        const detailedPass = response.data as ReviewJobProtocolPassResponse
        const index = protocols.value.findIndex(protocol => protocol.id === protocolId)
        if (index < 0) {
            return
        }

        const nextProtocols = [...protocols.value]
        nextProtocols[index] = {
            ...nextProtocols[index],
            ...detailedPass,
        }
        protocols.value = nextProtocols

        const nextLoaded = new Set(loadedProtocolIds.value)
        nextLoaded.add(protocolId)
        loadedProtocolIds.value = nextLoaded
    } catch {
        // Keep the lightweight overview visible when detail loading fails.
    } finally {
        const nextLoadingIds = new Set(loadingProtocolIds.value)
        nextLoadingIds.delete(protocolId)
        loadingProtocolIds.value = nextLoadingIds
    }
}

onMounted(() => {
    loadProtocol(true)
})

watch(
    () => `${String(route.params.id ?? '')}|${String(route.query.clientId ?? '')}`,
    (nextKey, previousKey) => {
        if (nextKey === previousKey) {
            return
        }

        resetProtocolState()
        loadProtocol(true)
    }
)

onUnmounted(() => {
    if (pollInterval) clearInterval(pollInterval)
})

function processEvents(events: ProtocolEventDto[] | undefined | null): MergedEvent[] {
    if (!events) return []
    return events.map((ev, i) => ({
        id: ev.id ?? String(i),
        time: ev.occurredAt ?? '',
        name: ev.name ?? ev.kind ?? 'Unknown',
        callDetails: ev,
        resultDetails: ev
    }))
}

function buildEventRows(events: ProtocolEventDto[] | undefined | null): EventDisplayRow[] {
    const mergedEvents = processEvents(events)
    const rows: EventDisplayRow[] = []
    let activeAiTurnParentId: string | null = null
    let pendingToolRows: PendingToolRow[] = []
    const childEventsByParentId = new Map<string, MergedEvent[]>()
    const standaloneEvents: MergedEvent[] = []

    const appendStandaloneRow = (merged: MergedEvent) => {
            rows.push({
                id: merged.id,
                merged,
                depth: 0,
                parentId: null,
                parentName: null,
                isToolChild: false,
                childCount: 0,
                isExpanded: false,
                timingSummary: getEventTimingPresentation(merged.callDetails).summary,
                timingDetail: getEventTimingPresentation(merged.callDetails).detail,
            })
    }

    const flushPendingToolRows = () => {
        for (const pendingToolRow of pendingToolRows) {
            standaloneEvents.push(pendingToolRow.merged)
        }

        pendingToolRows = []
    }

    for (const merged of mergedEvents) {
        const isToolCall = (merged.callDetails.kind ?? '').toLowerCase() === 'toolcall'

        if (isPrimaryAiTurnEvent(merged)) {
            activeAiTurnParentId = merged.id

            if (pendingToolRows.length > 0) {
                childEventsByParentId.set(
                    merged.id,
                    pendingToolRows.map(pendingToolRow => pendingToolRow.merged),
                )
            }

            pendingToolRows = []
            continue
        }

        const parentId = isToolCall ? activeAiTurnParentId : null

        if (isToolCall && !parentId) {
            pendingToolRows.push({ merged })
            continue
        }

        if (isToolCall && parentId) {
            const existingChildren = childEventsByParentId.get(parentId) ?? []
            existingChildren.push(merged)
            childEventsByParentId.set(parentId, existingChildren)
            continue
        }

        if (!isToolCall) {
            flushPendingToolRows()
            standaloneEvents.push(merged)
        }
    }

    flushPendingToolRows()

    for (const merged of mergedEvents) {
        if (isPrimaryAiTurnEvent(merged)) {
            const children = childEventsByParentId.get(merged.id) ?? []
            const isExpanded = !collapsedEventParents.value.has(merged.id)

            rows.push({
                id: merged.id,
                merged,
                depth: 0,
                parentId: null,
                parentName: null,
                isToolChild: false,
                childCount: children.length,
                isExpanded,
                timingSummary: getEventTimingPresentation(merged.callDetails).summary,
                timingDetail: getEventTimingPresentation(merged.callDetails).detail,
            })

            if (isExpanded) {
                for (const child of children) {
                    rows.push({
                        id: child.id,
                        merged: child,
                        depth: 1,
                        parentId: merged.id,
                        parentName: merged.name,
                        isToolChild: true,
                        childCount: 0,
                        isExpanded: false,
                        timingSummary: getEventTimingPresentation(child.callDetails).summary,
                        timingDetail: getEventTimingPresentation(child.callDetails).detail,
                    })
                }
            }
            continue
        }

        const belongsToParent = Array.from(childEventsByParentId.values()).some(children =>
            children.some(child => child.id === merged.id),
        )

        if (!belongsToParent) {
            appendStandaloneRow(merged)
        }
    }

    return rows
}

function isPrimaryAiTurnEvent(event: MergedEvent): boolean {
    return (event.callDetails.kind ?? '').toLowerCase() === 'aicall'
        && /^ai_call_iter_\d+$/.test(event.name)
}

function parentIterationLabel(name: string): string {
    const match = /^ai_call_iter_(\d+)$/.exec(name)
    if (!match) {
        return 'Child of AI turn'
    }

    return `AI ${match[1]}`
}

function isMergedEventProcessing(event: MergedEvent | null | undefined): boolean {
    return !event?.resultDetails
}

function compareProtocols(left: ReviewProtocolPass, right: ReviewProtocolPass): number {
    const attemptDelta = (right.attemptNumber ?? 0) - (left.attemptNumber ?? 0)
    if (attemptDelta !== 0) return attemptDelta

    const rightStarted = right.startedAt ? new Date(right.startedAt).getTime() : 0
    const leftStarted = left.startedAt ? new Date(left.startedAt).getTime() : 0
    if (rightStarted !== leftStarted) return rightStarted - leftStarted

    return (left.label ?? '').localeCompare(right.label ?? '')
}

function emptyPassMessage(pass: ReviewProtocolPass): string {
    if ((pass.events?.length ?? 0) > 0) return 'No events recorded.'
    if (pass.isInherited) {
        return 'This inherited pass exposes the source run metadata even when no new events were recorded in the current job.'
    }
    if ((pass.outcome ?? '').toLowerCase() === 'failed') {
        return 'This pass failed before any protocol events were captured.'
    }

    return 'No events recorded.'
}

function statusIconClass(status: string | undefined | null): string {
    switch (status?.toLowerCase()) {
        case 'completed':
        case 'success':
            return 'icon-success'
        case 'processing': return 'icon-processing'
        case 'failed': return 'icon-failed'
        default: return 'icon-pending'
    }
}

const isEventModalOpen = ref(false)

async function openMergedModal(event: MergedEvent): Promise<void> {
    resetEventModalExpansionState()

    const protocolId = activePass.value?.id
    if (protocolId && !loadedProtocolIds.value.has(protocolId)) {
        await ensureProtocolPassLoaded(protocolId)
        const refreshedEvent = buildEventRows(activePass.value?.events)
            .find(row => row.id === event.id)?.merged
        selectedMergedEvent.value = refreshedEvent ?? event
        isEventModalOpen.value = true
        void scheduleModalPhaseGrouping()
        return
    }

    selectedMergedEvent.value = event
    isEventModalOpen.value = true
    void scheduleModalPhaseGrouping()
}

async function openTimingInsight(insight: TimingInsight): Promise<void> {
    if (insight.protocolId && activePassId.value !== insight.protocolId) {
        activePassId.value = insight.protocolId
        await ensureProtocolPassLoaded(insight.protocolId)
    }

    const targetEvent = buildEventRows(activePass.value?.events)
        .find(row => row.id === insight.eventId)?.merged

    if (targetEvent) {
        await openMergedModal(targetEvent)
    }
}

function statusBadgeClass(status: string | undefined | null): string {
    switch (status?.toLowerCase()) {
        case 'completed':
        case 'success':
            return 'status-badge status-completed'
        case 'processing': return 'status-badge status-processing'
        case 'failed': return 'status-badge status-failed'
        default: return 'status-badge status-pending'
    }
}

function formatDate(iso: string | null | undefined): string {
    if (!iso) return '—'
    return new Date(iso).toLocaleString()
}

function formatReviewStrategy(strategy: ReviewStrategy | null | undefined): string {
    switch (strategy) {
        case 'fileByFile':
            return 'File-by-File'
        case 'agenticFileByFile':
            return 'Agentic File-by-File'
        case 'prWideAgentic':
            return 'PR-wide Agentic'
        default:
            return strategy ?? 'Not recorded'
    }
}

function formatFileOutcomeStatus(fileOutcome: ProtocolFileOutcome | null | undefined): string {
    if (!fileOutcome) return 'Not recorded'
    if (fileOutcome.isFailed) return 'Failed'
    if (fileOutcome.isExcluded) return 'Excluded'
    if (fileOutcome.isCarriedForward) return 'Carried Forward'
    if (fileOutcome.isDegraded) return 'Degraded'
    if (fileOutcome.isComplete) return 'Completed'
    return 'In Progress'
}

function formatFollowUpStatus(followUp: ProtocolFollowUp | null | undefined): string {
    if (!followUp?.used) return 'Not used'
    if (followUp.completedSuccessfully) return 'Completed successfully'
    return 'Used'
}

function formatRepeatedJudgmentStatus(repeatedJudgment: ProtocolRepeatedJudgment | null | undefined): string {
    if (!repeatedJudgment) return 'Not used'
    if (repeatedJudgment.agreementState === 'Agreed') return 'Agreement reached'
    if (repeatedJudgment.agreementState === 'Disagreed') return 'Disagreed'
    return repeatedJudgment.agreementState ?? 'Recorded'
}

function formatTokens(n: number | null | undefined): string {
    if (n == null) return '—'
    return n.toLocaleString()
}

function formatCacheStatus(status: unknown): string {
    if (status == null || status === 'notApplicable') return 'not captured'
    return String(status).replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase()
}

function shortGuid(value: string | null | undefined): string {
    if (!value) return '—'
    return value.slice(0, 8)
}

function sourceJobProtocolLink(sourceJobId: string) {
    return {
        name: 'job-protocol',
        params: { id: sourceJobId },
        query: route.query.clientId ? { clientId: route.query.clientId } : {},
    }
}

function formatTemperature(value: number | null | undefined): string {
    if (value == null) return 'Default'
    return value.toFixed(2)
}

function formatDurationMs(ms: number): string {
    if (ms < 0) return '0s';
    const s = Math.floor(ms / 1000);
    const m = Math.floor(s / 60);
    const h = Math.floor(m / 60);
    if (h > 0) return `${h}h ${m % 60}m`;
    if (m > 0) return `${m}m ${s % 60}s`;
    return `${s}s`;
}

function hasToolTiming(event: ProtocolEventDto | null | undefined): boolean {
    return event?.durationMs != null
        || event?.waitDurationMs != null
        || event?.activeDurationMs != null
        || !!event?.startedAt
        || !!event?.completedAt
        || !!event?.timingAvailability
        || !!event?.toolOutcome
        || !!event?.phaseTimings?.length
}

function hasEventTokens(event: ProtocolEventDto | null | undefined): boolean {
    return event?.inputTokens != null
        || event?.outputTokens != null
        || event?.cachedInputTokens != null
}

function hasEventError(event: ProtocolEventDto | null | undefined): boolean {
    return !!event?.error
        || !!event?.finalizationAttemptKind
        || !!event?.finalizationOutcome
        || !!event?.finalizationReason
        || !!event?.toolEvidence
}

function formatDurationWithMs(ms: number | null | undefined): string {
    if (ms == null) return '—'
    if (ms < 1000) return `${ms} ms`
    return formatDurationMs(ms)
}

function humanizeStatusValue(value: string): string {
    return value
        .replace(/_/g, ' ')
        .replace(/\b\w/g, character => character.toUpperCase())
}

function formatTimingAvailability(value: string | null | undefined): string {
    if (!value) return 'Not recorded'
    switch (value.toLowerCase()) {
        case 'captured': return 'Captured'
        case 'partial': return 'Partial'
        case 'missing': return 'Missing'
        case 'not_applicable':
        case 'notapplicable':
            return 'Not applicable'
        default:
            return humanizeStatusValue(value)
    }
}

function formatToolOutcome(value: string | null | undefined): string {
    if (!value) return 'Unknown'
    switch (value.toLowerCase()) {
        case 'succeeded': return 'Succeeded'
        case 'failed': return 'Failed'
        case 'degraded': return 'Degraded'
        case 'cancelled': return 'Cancelled'
        default:
            return humanizeStatusValue(value)
    }
}

function formatPhaseCountSummary(phaseCount: number, groupCount: number, compact = false): string {
    if (phaseCount <= 0) {
        return 'No phases'
    }

    if (phaseCount === groupCount) {
        return `${phaseCount} phase${phaseCount === 1 ? '' : 's'}`
    }

    if (compact) {
        return `${groupCount} group${groupCount === 1 ? '' : 's'} / ${phaseCount} occurrence${phaseCount === 1 ? '' : 's'}`
    }

    return `${phaseCount} phase${phaseCount === 1 ? '' : 's'} across ${groupCount} group${groupCount === 1 ? '' : 's'}`
}

function formatPhaseTitle(phase: ProtocolEventPhaseTimingDto): string {
    const baseName = phase.displayName ?? phase.name ?? 'Unnamed phase'
    return phase.occurrence != null ? `${baseName} #${phase.occurrence}` : baseName
}

function formatPhaseDuration(phase: ProtocolEventPhaseTimingDto): string {
    const duration = getPhaseTimingDurationMs(phase)
    return duration == null ? 'Duration unavailable' : formatDurationWithMs(duration)
}

function formatToolPhaseGroupDuration(group: ToolPhaseGroup): string {
    if (group.totalDurationMs == null) {
        return group.count === 1 ? 'Duration unavailable' : `${group.count} occurrences`
    }

    return group.count === 1
        ? formatDurationWithMs(group.totalDurationMs)
        : `${formatDurationWithMs(group.totalDurationMs)} total`
}

function slowestToolDurationLabel(protocol: ReviewProtocolPass): string | null {
    const slowestDuration = (protocol.events ?? [])
        .filter(event => (event.kind ?? '').toLowerCase() === 'toolcall' && event.durationMs != null)
        .reduce<number | null>((current, event) => {
            if (event.durationMs == null) {
                return current
            }

            if (current == null || event.durationMs > current) {
                return event.durationMs
            }

            return current
        }, null)

    return slowestDuration == null ? null : formatDurationWithMs(slowestDuration)
}

function computePassDuration(pass: any): string {
    if (!pass.startedAt) return '—';
    const start = new Date(pass.startedAt).getTime();
    const end = pass.completedAt ? new Date(pass.completedAt).getTime() : Date.now();
    return formatDurationMs(end - start);
}

function kindBadgeClass(kind: string | null | undefined): string {
    if (kind === 'aiCall') return 'badge-purple';
    if (kind === 'toolCall') return 'badge-cyan';
    if (kind === 'memoryOperation') return 'badge-green';
    if (kind === 'operational') return 'badge-gray';
    return 'badge-gray';
}

function statusBorderClass(status: string | null | undefined): string {
    switch (status?.toLowerCase()) {
        case 'completed':
        case 'success':
            return 'border-success';
        case 'processing': return 'border-processing';
        case 'failed': return 'border-failed';
        default: return 'border-default';
    }
}

function confidenceClass(conf: number | string | null | undefined): string {
    if (conf == null) return '';
    const num = typeof conf === 'string' ? parseInt(conf, 10) : conf;
    if (!isNaN(num) && typeof num === 'number') {
        if (num >= 80) return 'conf-high';
        if (num >= 60) return 'conf-medium';
        return 'conf-low';
    }
    if (typeof conf === 'string') {
        const lower = conf.toLowerCase();
        if (lower === 'high') return 'conf-high';
        if (lower === 'medium') return 'conf-medium';
        if (lower === 'low') return 'conf-low';
    }
    return '';
}

function formatConfidence(val: number | string | null | undefined): string {
    if (val == null) return 'N/A';
    if (typeof val === 'number') return `${val}%`;
    const num = parseInt(val, 10);
    if (!isNaN(num)) return `${num}%`;
    return String(val).charAt(0).toUpperCase() + String(val).slice(1);
}
</script>

<style scoped>
.header-stack {
    margin-bottom: 2rem;
}

.header-nav-links {
    display: flex;
    gap: 1rem;
    align-items: center;
    margin-bottom: 0.5rem;
}

.pr-view-link {
    color: var(--color-accent) !important;
}



.loading,
.empty-state {
    color: var(--color-text-muted);
    font-style: italic;
}

.error {
    color: var(--color-danger);
}

/* Totals card */
.totals-card {
    margin-bottom: 2rem;
}

/* Animations */
.list-enter-active,
.list-leave-active {
    transition: all 0.5s ease;
}
.list-enter-from,
.list-leave-to {
    opacity: 0;
    transform: translateY(-10px);
}

.status-badge {
    display: inline-block;
    padding: 0.15rem 0.6rem;
    border-radius: 9999px;
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
    color: #86efac;
}

.status-failed {
    background: rgba(239, 68, 68, 0.16);
    color: #fca5a5;
}

.status-pending {
    background: rgba(148, 163, 184, 0.16);
    color: #cbd5f5;
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

.chip {
    display: inline-flex;
    align-items: center;
    padding: 0.2rem 0.65rem;
    border-radius: 999px;
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
    color: #93c5fd;
    border: 1px solid rgba(59, 130, 246, 0.28);
}

.pass-nav-badge {
    display: inline-flex;
    align-items: center;
    align-self: flex-start;
    margin-top: 0.2rem;
    padding: 0.08rem 0.45rem;
    border-radius: 999px;
    background: rgba(59, 130, 246, 0.12);
    color: #93c5fd;
    font-size: 0.68rem;
    font-weight: 700;
    line-height: 1.1;
}

.inheritance-job-cell {
    display: flex;
    align-items: center;
}

.inheritance-link {
    color: #93c5fd;
    text-decoration: none;
}

.inheritance-link:hover {
    color: #bfdbfe;
    text-decoration: underline;
}

.pass-file-outcome-note {
    margin: 0;
    padding: 0 1.5rem 1.35rem;
    color: var(--color-text-muted);
}

.row-processing td {
    background: rgba(34, 211, 238, 0.05);
}

@keyframes flash {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.5; }
}

.merged-modal-layout {
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
}

/* Master-Detail Architecture */
.protocol-master-detail {
    display: grid;
    grid-template-columns: 320px 1fr;
    gap: 2rem;
    align-items: start;
}

@media (max-width: 1024px) {
    .protocol-master-detail {
        grid-template-columns: 1fr;
        gap: 1rem;
    }
}

.protocol-sidebar {
    display: flex;
    flex-direction: column;
    gap: 0;
    position: sticky;
    top: 1rem;
    max-height: calc(100vh - 4rem);
    overflow-y: auto;
    padding: 0 1rem 1rem 0.5rem;
    scrollbar-gutter: stable;
}

.sidebar-group {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
}

.folder-header {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.5rem 0.25rem;
    background: transparent !important;
    border: none;
    color: var(--color-text-muted);
    font-size: 0.75rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    cursor: pointer;
    width: 100%;
    border-radius: 0 !important;
}

.folder-header:hover {
    color: var(--color-text);
}

.folder-chevron {
    font-size: 0.6rem;
    transition: transform 0.2s cubic-bezier(0.4, 0, 0.2, 1);
    opacity: 0.6;
}

.folder-chevron.collapsed {
    transform: rotate(-90deg);
}

.folder-name {
    flex: 1;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.folder-count {
    font-size: 0.7rem;
    background: rgba(255, 255, 255, 0.05);
    padding: 0.1rem 0.4rem;
    border-radius: 4px;
    opacity: 0.6;
    font-weight: 500;
}

.sidebar-tree-node {
    position: relative;
    display: flex;
    flex-direction: column;
    width: 100%;
}

/* Terminate vertical line for last item in group */
.sidebar-tree-node.is-last-in-group::before {
    height: 0.75rem; /* Stop at the horizontal line */
}

/* Base depth 0 has no vertical line from above */
.sidebar-tree-node[style*="--depth: 0"]::before {
    display: none;
}

.sidebar-tree-node[style*="--depth: 0"]::after {
    display: none;
}

.tree-folder-btn {
    appearance: none;
    background: transparent !important;
    border: none;
    display: flex;
    align-items: center;
    justify-content: flex-start !important;
    text-align: left !important;
    gap: 0.2rem;
    padding: 0.25rem 0.5rem;
    color: var(--color-accent);
    font-size: 0.75rem;
    font-weight: 700;
    text-transform: none;
    cursor: pointer;
    width: 100%;
    transition: all 0.1s ease;
    border-radius: 4px;
    z-index: 2;
    margin: 1px 0;
}

.tree-folder-btn:hover {
    background: rgba(255, 255, 255, 0.06) !important;
}

.folder-chevron {
    font-size: 0.6rem;
    transition: transform 0.15s ease;
    opacity: 0.6;
    width: 0.8rem;
    display: inline-flex;
    justify-content: center;
    margin-right: 0.2rem;
}

.folder-chevron.collapsed { transform: rotate(-90deg); }

.tree-pass-btn {
    appearance: none;
    background: transparent !important;
    border: none !important;
    box-shadow: none !important;
    display: flex;
    align-items: center;
    justify-content: flex-start !important;
    text-align: left !important;
    gap: 0.6rem;
    padding: 0.2rem 0.5rem;
    cursor: pointer;
    width: 100%;
    border-radius: 4px;
    transition: all 0.1s ease;
    margin: 1px 0;
    z-index: 2;
    min-height: auto !important;
}

.tree-pass-btn:hover {
    background: rgba(255, 255, 255, 0.04) !important;
}

.tree-pass-btn.active {
    background: rgba(59, 130, 246, 0.12) !important;
}

.tree-pass-btn .pass-nav-info {
    display: flex;
    flex-direction: row;
    align-items: center;
    gap: 0.5rem;
    flex: 1;
    min-width: 0;
}

.tree-pass-btn .pass-nav-info {
    gap: 0.1rem;
}

.tree-pass-btn .pass-nav-filename {
    font-size: 0.8rem;
    font-weight: 500;
}

.tree-pass-btn .pass-nav-path {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
}

.tree-pass-btn .pass-nav-stats-grid {
    flex-shrink: 0;
    margin-left: auto;
    gap: 0.25rem;
    font-size: 0.6rem;
    opacity: 0.8;
}

.tree-pass-btn .pass-nav-icon {
    width: 18px;
    height: 18px;
    font-size: 0.7rem;
}

.group-content {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}

.group-content.is-nested {
    padding-left: 0.75rem;
    margin-left: 0.35rem;
    border-left: 1px solid rgba(255, 255, 255, 0.08);
}

/* Custom Slim Scrollbar (Global to this view) */
::-webkit-scrollbar {
    width: 6px;
    height: 6px;
}
::-webkit-scrollbar-track {
    background: transparent;
}
::-webkit-scrollbar-thumb {
    background: rgba(255, 255, 255, 0.1);
    border-radius: 10px;
}
::-webkit-scrollbar-thumb:hover {
    background: rgba(255, 255, 255, 0.2);
}

.pass-nav-icon {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 24px;
    height: 24px;
    border-radius: 50%;
    font-size: 0.85rem;
    font-weight: bold;
    flex-shrink: 0;
}

.sidebar-orb {
    transform: scale(0.65);
}

.icon-success { color: var(--color-success); }
.icon-failed { color: var(--color-danger); }
.icon-processing { color: var(--color-accent); }
.icon-pending { color: var(--color-text-muted); }

.pass-nav-info {
    display: flex;
    flex-direction: column;
    justify-content: center;
    gap: 0.5rem;
    flex: 1;
    min-width: 0;
}

.pass-nav-path {
    display: flex;
    flex-direction: column;
    gap: 0.1rem;
    min-width: 0;
}

.pass-nav-filename {
    font-weight: 600;
    font-size: 0.9rem;
    color: var(--color-text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    display: block;
}

.pass-nav-directory {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    display: block;
    opacity: 0.8;
}

.pass-nav-stats-grid {
    display: flex;
    flex-wrap: nowrap; /* Changed: keep on single line */
    gap: 0.5rem;
    font-size: 0.72rem;
    color: var(--color-text-muted);
    overflow: hidden;
    text-overflow: ellipsis;
}

.stat-item {
    display: flex;
    align-items: center;
    gap: 0.25rem;
    background: rgba(255, 255, 255, 0.04);
    padding: 0.15rem 0.4rem;
    border-radius: 4px;
    border: 1px solid rgba(255, 255, 255, 0.03);
    white-space: nowrap;
}

.stat-icon {
    font-size: 0.75rem;
    opacity: 0.7;
}

.stat-text {
    font-weight: 500;
}

.protocol-content {
    background: var(--color-surface);
    border-radius: 12px;
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

/* Summary table shared styles */
.summary-card {
    border: 1px solid var(--color-border);
    border-radius: 12px;
    overflow: hidden;
    background: var(--color-surface);
}

.summary-card h3 {
    margin: 0;
    padding: 1rem 1.5rem;
    background: var(--color-bg);
    border-bottom: 1px solid var(--color-border);
    font-size: 1.1rem;
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

.pass-final-result-section {
    padding: 1.25rem 1.5rem 0;
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
    border-radius: 10px;
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
.timing-insight-tool {
    font-weight: 600;
}

.timing-insight-context,
.timing-insight-meta {
    color: var(--color-text-muted);
    font-size: 0.85rem;
    min-width: 0;
}

.timing-insight-tool,
.timing-insight-context,
.timing-insight-meta {
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

.pass-final-result-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 1rem;
    margin-bottom: 0.9rem;
}

.pass-final-result-header h4 {
    margin: 0;
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

.pass-final-comments-list {
    margin-bottom: 0;
}

.summary-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.95rem;
}

.summary-table th,
.summary-table td {
    padding: 0.75rem 1.5rem;
    text-align: left;
    border-bottom: 1px solid var(--color-border);
}

.summary-table th {
    width: 12rem;
    color: var(--color-text-muted);
    font-weight: 600;
    background: var(--color-bg);
}

/* Detail Sections */
.events-section,
.comments-section {
    padding: 1.5rem;
    flex: 1;
    overflow-y: auto;
    max-width: 1000px;
    margin: 0 auto;
    width: 100%;
}

.events-section h4 {
    margin: 0 0 0.75rem;
    font-size: 0.9rem;
    font-weight: 600;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.05em;
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
    border-radius: 14px;
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

.event-metric-card {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    padding: 0.75rem 0.85rem;
    border-radius: 12px;
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
    align-items: baseline;
    justify-content: space-between;
    gap: 0.75rem 1rem;
    flex-wrap: wrap;
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

.tool-timing-panel {
    margin-bottom: 1rem;
    padding: 1rem;
    border: 1px solid var(--color-border);
    border-radius: 12px;
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
.tool-phase-section-header h5 {
    margin: 0;
}

.tool-phase-overview-chip,
.tool-phase-count-pill {
    display: inline-flex;
    align-items: center;
    padding: 0.2rem 0.55rem;
    border-radius: 9999px;
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
    margin: 0 0 0.75rem;
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
    border-radius: 10px;
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

.tool-phase-title,
.tool-phase-duration {
    font-weight: 600;
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
    border-radius: 8px;
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
    border-radius: 8px;
    background: rgba(255, 255, 255, 0.025);
    border: 1px solid rgba(255, 255, 255, 0.035);
}

/* Markdown Adjustments */
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

/* Sidebar Search */
.sidebar-search-container {
    padding: 0.5rem;
    position: sticky;
    top: 0;
    background: var(--color-bg);
    z-index: 10;
    display: flex;
    align-items: center;
    gap: 0.5rem;
    border-bottom: 1px solid var(--color-border);
    margin-bottom: 0.5rem;
}

.sidebar-search-input {
    background: transparent;
    border: none;
    color: var(--color-text);
    font-size: 0.85rem;
    width: 100%;
    outline: none;
}

.search-icon {
    font-size: 0.8rem;
    opacity: 0.5;
}

/* Summary Dashboard */
.summary-dashboard {
    padding: 2rem;
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
    animation: fadeIn 0.3s ease-out;
}

.summary-dashboard-header {
    display: flex;
    justify-content: space-between;
    align-items: flex-end;
    border-bottom: 1px solid var(--color-border);
    padding-bottom: 1rem;
}

.summary-dashboard-header h3 {
    margin: 0;
    font-size: 1.5rem;
}

.summary-dashboard-header .subtitle {
    margin: 0.25rem 0 0;
    color: var(--color-text-muted);
    font-size: 0.9rem;
}

.summary-preview-card {
    background: rgba(255, 255, 255, 0.03);
    border-radius: 16px;
    padding: 2rem;
    border: 1px solid var(--color-border);
}

.overview-metadata-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr));
    gap: 0.875rem;
    margin-bottom: 1.5rem;
}

.overview-metadata-item {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
    padding: 0.9rem 1rem;
    border-radius: 0.875rem;
    background: rgba(255, 255, 255, 0.025);
    border: 1px solid var(--color-border);
}

.overview-metadata-label {
    font-size: 0.75rem;
    line-height: 1.1;
    letter-spacing: 0.08em;
    text-transform: uppercase;
    color: var(--color-text-muted);
}

.overview-metadata-value {
    font-size: 1rem;
    font-weight: 600;
    color: var(--color-text);
}

.preview-markdown {
    opacity: 0.85;
    mask-image: linear-gradient(to bottom, black 70%, transparent 100%);
    max-height: 200px;
    overflow: hidden;
}

.dashboard-stats {
    display: flex;
    gap: 3rem;
    margin-top: 2rem;
    padding-top: 2rem;
    border-top: 1px solid var(--color-border);
}

.dash-stat {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}

.dash-stat-value {
    font-size: 2rem;
    font-weight: 700;
    color: var(--color-accent);
}

.dash-stat-label {
    font-size: 0.8rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--color-text-muted);
}

.severity-summary-row {
    display: flex;
    gap: 0.75rem;
    margin-top: 1.5rem;
    flex-wrap: wrap;
}

.sev-summary-pill {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    padding: 0.35rem 0.85rem;
    border-radius: 8px;
    font-size: 0.85rem;
    font-weight: 600;
    text-transform: capitalize;
    border: 1px solid var(--color-border);
}

.pill-count {
    font-size: 1rem;
    font-family: monospace;
}

.pill-error { background: rgba(239, 68, 68, 0.1); border-color: rgba(239, 68, 68, 0.3); color: #ef4444; }
.pill-warning { background: rgba(234, 179, 8, 0.1); border-color: rgba(234, 179, 8, 0.3); color: #eab308; }
.pill-info { background: rgba(59, 130, 246, 0.1); border-color: rgba(59, 130, 246, 0.3); color: #3b82f6; }
.pill-suggestion { background: rgba(168, 85, 247, 0.1); border-color: rgba(168, 85, 247, 0.3); color: #a855f7; }

/* Summary Modal Content */
.summary-modal-layout {
    display: flex;
    flex-direction: column;
    height: 70vh; /* Fixed height for modal content to enable internal scrolling */
    overflow: hidden;
}

.modal-filter-bar {
    padding: 1rem 1.5rem;
    border-bottom: 2px solid var(--color-border);
    z-index: 10;
}

.modal-body-scroll {
    flex: 1;
    overflow-y: auto;
    padding: 1.5rem;
    background: var(--color-surface);
}

.findings-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 1.5rem;
}

.header-main {
    display: flex;
    align-items: center;
    gap: 1rem;
}

.mini-stats {
    margin-top: 0 !important;
}

.mini-stats .sev-summary-pill {
    padding: 0.1rem 0.5rem;
    font-size: 0.75rem;
}

.filter-icon {
    font-size: 0.9rem;
    opacity: 0.5;
    margin-right: -0.25rem;
}

.summary-details {
    background: rgba(255, 255, 255, 0.02);
    border-radius: 12px;
    border: 1px solid var(--color-border);
    margin-bottom: 2rem;
}

.summary-details summary {
    padding: 0.75rem 1.25rem;
    font-weight: 600;
    cursor: pointer;
    color: var(--color-accent);
    user-select: none;
    outline: none;
}

.summary-details summary:hover {
    background: rgba(255, 255, 255, 0.04);
}

.summary-full-text {
    padding: 0 1.25rem 1.25rem;
    max-height: 250px;
    overflow-y: auto;
    font-size: 0.95rem;
}

.findings-list-container {
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

@keyframes fadeIn {
    from { opacity: 0; transform: translateY(10px); }
    to { opacity: 1; transform: translateY(0); }
}

.protocol-sidebar {
    width: 100%;
    max-width: 320px;
    flex-shrink: 0;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    height: fit-content;
    position: sticky;
    top: 1rem;
    padding: 0 1rem;
    z-index: 10;
}

@media (max-width: 1024px) {
    .protocol-sidebar {
        max-width: none;
        width: 100%;
        position: static;
        top: auto;
        max-height: none;
        padding: 0;
    }

    .protocol-content {
        min-width: 0;
    }

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

/* Side Drawer (Events) */
.event-drawer {
    width: 380px;
    flex-shrink: 0;
    border: 1px solid var(--color-border);
    border-radius: 12px;
    background: var(--color-surface);
    position: sticky;
    top: 1rem;
    max-height: calc(100vh - 6rem);
    display: flex;
    flex-direction: column;
    overflow: hidden;
}

.drawer-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 1rem 1.5rem;
    background: var(--color-bg);
    border-bottom: 1px solid var(--color-border);
    gap: 0.5rem;
}

.drawer-title {
    font-weight: 600;
    font-size: 1rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.drawer-close {
    background: none;
    border: none;
    font-size: 1.2rem;
    cursor: pointer;
    color: var(--color-text-muted);
    flex-shrink: 0;
    padding: 0 0.25rem;
}

.drawer-close:hover {
    color: var(--color-danger);
}

.drawer-body {
    flex: 1;
    overflow-y: auto;
    padding: 1.5rem;
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

.content-block {
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    border-radius: 8px;
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

/* Job Stat Strip */
.job-stat-strip {
    display: flex;
    flex-wrap: wrap;
    gap: 2rem;
    margin-bottom: 2rem;
    padding: 1.5rem;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 12px;
}
.breakdown-section {
    margin-bottom: 1.5rem;
    padding: 1.25rem 1.5rem;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 12px;
}
.stat-pill {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}
.stat-label {
    font-size: 0.8rem;
    color: var(--color-text-muted);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
}
.stat-value {
    font-size: 1.25rem;
    font-weight: 600;
    color: var(--color-text);
}
.monospace-value {
    font-family: monospace;
    font-size: 1.1rem;
    overflow: hidden;
    text-overflow: ellipsis;
}

.pass-file-icon {
    font-size: 1rem;
    margin-top: 0.15rem;
    flex-shrink: 0;
    color: var(--color-text-muted) !important; /* Fixed: default to visible muted gray */
    z-index: 2;
    opacity: 0.9;
}

.file-icon {
    color: var(--color-text-muted) !important;
    opacity: 0.9;
}

.pass-file-icon.icon-success { color: var(--color-success) !important; opacity: 1; }
.pass-file-icon.icon-failed { color: var(--color-danger) !important; opacity: 1; }
.pass-file-icon.icon-processing { color: var(--color-accent) !important; animation: pulse 2s infinite; }
.pass-file-icon.icon-pending { color: var(--color-text-muted) !important; opacity: 0.5; }

@keyframes pulse {
    0% { opacity: 0.6; }
    50% { opacity: 1; }
    100% { opacity: 0.6; }
}

.tree-folder-btn i.fi {
    color: var(--color-accent);
}

/* Comment Sidebar Specifics */
.comment-node-btn .pass-nav-info {
    display: flex;
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
    width: 100%;
}

.comment-count-pill {
    font-size: 0.7rem;
    background: rgba(255, 255, 255, 0.15); /* More opaque for contrast */
    color: #fff; /* Pure white for readability */
    padding: 0.13rem 0.45rem;
    border-radius: 10px;
    font-weight: 700;
}

.active .comment-count-pill {
    background: rgba(255, 255, 255, 0.25);
    color: var(--color-accent) !important;
}

.active-folder {
    background: rgba(255, 255, 255, 0.03) !important;
}
.pass-nav-meta {
    display: flex;
    justify-content: space-between;
    align-items: center;
    width: 100%;
}

/* Event cards */
.kind-badge {
    display: inline-flex;
    align-items: center;
    padding: 0.18rem 0.58rem;
    border-radius: 999px;
    font-size: 0.68rem;
    font-weight: 700;
    text-transform: uppercase;
    letter-spacing: 0.04em;
}
.badge-purple { background: rgba(168, 85, 247, 0.15); color: #c084fc; }
.badge-cyan { background: rgba(34, 211, 238, 0.15); color: #22d3ee; }
.badge-green { background: rgba(52, 211, 153, 0.15); color: #34d399; }
.badge-gray { background: rgba(255, 255, 255, 0.1); color: var(--color-text-muted); }

.tool-name { font-weight: 600; font-family: monospace; }
.ai-name { font-style: italic; color: #d7d0f5; }
.memory-name { color: #34d399; }
.operational-name { color: var(--color-text-muted); }

.kind-parent-pill {
    display: inline-flex;
    align-items: center;
    padding: 0.12rem 0.48rem;
    border-radius: 999px;
    background: rgba(168, 85, 247, 0.14);
    color: #e9d5ff;
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
    color: #d8b4fe;
    border-radius: 999px;
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
    border-radius: 999px;
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

.event-parent-context {
    display: flex;
    align-items: center;
    gap: 0.35rem;
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
    border-radius: 999px;
    transform: none;
}

.event-child-rail::after {
    content: '';
    position: absolute;
    right: 0;
    top: 1.05rem;
    width: 0.34rem;
    height: 0.34rem;
    border-radius: 999px;
    background: rgba(34, 211, 238, 0.78);
    box-shadow: 0 0 0 0.18rem rgba(34, 211, 238, 0.12);
    transform: none;
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
    word-break: break-word;
}

.event-parent-pill {
    display: inline-flex;
    align-items: center;
    padding: 0.12rem 0.5rem;
    border-radius: 999px;
    background: rgba(168, 85, 247, 0.12);
    color: #d8b4fe;
    font-size: 0.7rem;
    font-weight: 700;
    white-space: nowrap;
}

/* Memory operation — discarded/downgraded comment list */
.memory-comment-list {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    margin-top: 0.25rem;
}
.memory-comment-row {
    background: rgba(255, 255, 255, 0.025);
    border: 1px solid var(--color-border);
    border-radius: 8px;
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
/* Severity chips for memory modal */
.memory-sev-chip {
    display: inline-flex;
    align-items: center;
    padding: 0.1rem 0.5rem;
    border-radius: 9999px;
    font-size: 0.68rem;
    font-weight: 700;
    letter-spacing: 0.04em;
    flex-shrink: 0;
}
.memory-sev-chip--error    { background: rgba(239, 68, 68, 0.15);  color: #f87171; }
.memory-sev-chip--warning  { background: rgba(234, 179, 8, 0.15);  color: #fbbf24; }
.memory-sev-chip--info     { background: rgba(34, 211, 238, 0.12); color: #22d3ee; }
.memory-sev-chip--suggestion { background: rgba(168, 85, 247, 0.12); color: #c084fc; }
.memory-sev-chip--note     { background: rgba(255, 255, 255, 0.08); color: var(--color-text-muted); }
/* Memory — no output state */
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

/* Modal JSON Renderer */
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
    border-radius: 6px;
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
.json-comment-item.severity-warning { border-left: 2px solid #eab308; padding-left: 0.5rem; list-style-type: none; margin-left: -1.25rem; }
.json-comment-item.severity-suggestion { border-left: 2px solid var(--color-accent); padding-left: 0.5rem; list-style-type: none; margin-left: -1.25rem; }
.json-comment-item.severity-info, .json-comment-item.severity-note { border-left: 2px solid #3b82f6; padding-left: 0.5rem; list-style-type: none; margin-left: -1.25rem; }

.json-confidence-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(120px, 1fr));
    gap: 0.5rem;
}
.json-confidence-item {
    display: flex;
    justify-content: space-between;
    padding: 0.5rem;
    border-radius: 6px;
    border: 1px solid var(--color-border);
}

.drawer-section-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    margin-bottom: 0.75rem;
}

.drawer-section-header h4 { margin-bottom: 0; }
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
    border-radius: 8px;
    border: 1px solid rgba(59, 130, 246, 0.22);
    background: rgba(59, 130, 246, 0.08);
    color: var(--color-text-muted);
    font-size: 0.78rem;
    line-height: 1.45;
}
.provider-managed-note i {
    color: #60a5fa;
    margin-top: 0.1rem;
    flex-shrink: 0;
}
.error-block {
    color: var(--color-danger);
    background: rgba(239, 68, 68, 0.05) !important;
    border-color: rgba(239, 68, 68, 0.2) !important;
}

.synthesis-tab-container {
    padding: 2rem;
    display: flex;
    justify-content: center;
}

.synthesis-tab-container {
    padding: 2rem;
    display: flex;
    justify-content: center;
}

.synthesis-main {
    width: 100%;
}

.comments-main-title {
    margin-top: 0 !important;
    text-align: left;
}

.comments-toolbar {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    flex-wrap: wrap;
    gap: 0.75rem;
    margin-top: 1rem;
    margin-bottom: 1rem;
}

.comments-filter-controls {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    flex-wrap: wrap;
}

.comment-search-input {
    background: rgba(255, 255, 255, 0.05);
    border: 1px solid var(--color-border);
    border-radius: 6px;
    padding: 0.35rem 0.75rem;
    color: var(--color-text);
    font-size: 0.85rem;
    width: 200px;
}

.comment-search-input:focus {
    outline: none;
    border-color: var(--color-accent);
    background: rgba(255, 255, 255, 0.08);
}

.severity-pills {
    display: flex;
    gap: 0.35rem;
    flex-wrap: wrap;
}

.severity-pill {
    padding: 0.35rem 0.85rem;
    border-radius: 8px;
    font-size: 0.85rem;
    font-weight: 600;
    cursor: pointer;
    text-transform: capitalize;
    border: 1px solid var(--color-border);
    background: rgba(255, 255, 255, 0.05);
    color: var(--color-text-muted);
    transition: all 0.15s ease;
    display: flex;
    align-items: center;
    justify-content: center;
}

.severity-pill:hover {
    background: rgba(255, 255, 255, 0.1);
    color: var(--color-text);
    border-color: rgba(255, 255, 255, 0.2);
}

.severity-pill--error.severity-pill--active { background: rgba(239, 68, 68, 0.15); border-color: rgba(239, 68, 68, 0.4); color: #ef4444; }
.severity-pill--warning.severity-pill--active { background: rgba(234, 179, 8, 0.15); border-color: rgba(234, 179, 8, 0.4); color: #eab308; }
.severity-pill--info.severity-pill--active { background: rgba(59, 130, 246, 0.15); border-color: rgba(59, 130, 246, 0.4); color: #3b82f6; }
.severity-pill--suggestion.severity-pill--active { background: rgba(168, 85, 247, 0.15); border-color: rgba(168, 85, 247, 0.4); color: #a855f7; }

.comments-empty-state {
    text-align: center;
    color: var(--color-text-muted);
    font-style: italic;
    padding: 2rem 0;
}

.comment-group-header {
    font-size: 0.8rem;
    font-weight: 700;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.1em;
    margin: 2.5rem 0 1rem 0;
    padding-bottom: 0.5rem;
    border-bottom: 1px solid rgba(255, 255, 255, 0.08);
}

.ai-disclaimer {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    background: rgba(139, 92, 246, 0.1);
    border: 1px solid rgba(139, 92, 246, 0.3);
    padding: 1rem 1.25rem;
    border-radius: 8px;
    margin: 1.5rem 2rem; /* Added horizontal margin and increased vertical */
}

.ai-icon {
    font-size: 1.25rem;
}

.ai-text {
    font-size: 0.9rem;
    color: var(--color-text);
    font-weight: 500;
}

.synthesis-summary {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 8px;
    padding: 2rem;
    font-size: 1rem;
    line-height: 1.7;
    color: var(--color-text);
    margin-bottom: 2rem;
}

.synthesis-comments {
    list-style: none;
    padding: 0;
    margin: 0;
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.synthesis-comment {
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    border-radius: 8px;
    padding: 1.5rem;
    display: flex;
    flex-direction: column;
    overflow: hidden;
}

.comment-header {
    display: flex;
    align-items: center;
    gap: 1rem;
    margin-bottom: 0.75rem;
    font-size: 0.85rem;
}

.comment-sev {
    font-weight: 700;
}

.comment-msg-container {
    width: 100%;
    overflow: hidden;
}

/* Markdown Global Styles within this view */
.markdown-content :first-child { margin-top: 0; }
.markdown-content :last-child { margin-bottom: 0; }
.markdown-content p { margin-bottom: 0.75rem; line-height: 1.6; }
.markdown-content ul, .markdown-content ol { margin-bottom: 0.75rem; padding-left: 1.5rem; }
.markdown-content code { background: rgba(255, 255, 255, 0.1); padding: 0.1rem 0.3rem; border-radius: 4px; font-family: monospace; }
.markdown-content pre { background: var(--color-bg); border: 1px solid var(--color-border); padding: 1rem; border-radius: 8px; overflow-x: auto; margin-bottom: 1rem; }
.markdown-content h1, .markdown-content h2, .markdown-content h3 { margin: 1rem 0 0.5rem 0; }

.ui-tabs {
    display: flex;
    gap: 2rem;
    margin-top: 2rem;
    margin-bottom: 2rem;
    padding: 0 1.5rem;
    border-bottom: 1px solid var(--color-border);
}

.ui-tab {
    background: transparent !important; /* Force reset global btn style */
    border: none;
    border-bottom: 2px solid transparent;
    border-radius: 0 !important; /* Force reset global btn rounding */
    padding: 0.75rem 0;
    font-size: 0.95rem;
    font-weight: 500;
    color: var(--color-text);
    opacity: 0.7;
    cursor: pointer;
    transition: all 0.2s ease;
}

.ui-tab:hover {
    opacity: 1;
    background: transparent !important;
}

/* ────────────────────────────────────────────────────────────────────────────── */
/* UTILS                                                                          */
/* ────────────────────────────────────────────────────────────────────────────── */

.monospace-value {
    font-family: var(--font-mono, monospace);
}

.tokens-tab-view {
    padding: 2rem;
    max-width: 60rem;
    margin: 0 auto;
}

.section-inner {
    padding: 1.5rem;
}

.section-description {
    color: var(--color-text-muted);
    font-size: 0.9rem;
    margin-bottom: 1.5rem;
}

.compact-stats {
    margin-bottom: 1.5rem !important;
}

.monospace-badge {
    font-family: var(--font-mono, monospace);
    background: rgba(255, 255, 255, 0.05);
    padding: 0.1rem 0.4rem;
    border-radius: 4px;
    font-size: 0.8rem;
}

.synthesis-waiting-state {
    padding: 4rem 2rem;
    display: flex;
    justify-content: center;
    border: 1px dashed var(--color-border);
    border-radius: 12px;
    background: rgba(255, 255, 255, 0.02);
}

.waiting-card {
    text-align: center;
    max-width: 400px;
}

.waiting-orb {
    width: 48px;
    height: 48px;
    margin: 0 auto 1.5rem;
}

.waiting-card h3 {
    margin: 0 0 0.75rem;
    font-size: 1.25rem;
    font-weight: 600;
}

.waiting-card p {
    color: var(--color-text-muted);
    font-size: 0.95rem;
    line-height: 1.5;
    margin: 0;
}

.failed-state {
    border-color: rgba(239, 68, 68, 0.3);
    background: rgba(239, 68, 68, 0.03);
}

.error-icon {
    font-size: 2.5rem;
    display: block;
    margin-bottom: 1rem;
}

/* Back to Summary button (US5/T022) */
.back-to-summary-row {
    margin-bottom: 0.5rem;
}
.back-to-summary-btn {
    font-size: 0.85rem;
    padding: 0.25rem 0.5rem;
}

/* Dismiss button (US1) */
.dismiss-btn {
    margin-left: auto;
    padding: 0.15rem 0.6rem;
    font-size: 0.75rem;
    border: 1px solid var(--color-border);
    border-radius: 4px;
    background: rgba(255, 255, 255, 0.05);
    color: var(--color-text-muted);
    cursor: pointer;
    white-space: nowrap;
    flex-shrink: 0;
    transition: background 0.15s ease, color 0.15s ease;
}
.dismiss-btn:hover:not(:disabled) {
    background: rgba(255, 255, 255, 0.1);
    color: var(--color-text);
}
.dismiss-btn:disabled {
    opacity: 0.5;
    cursor: not-allowed;
}

/* Dismiss toast (US1) */
.dismiss-toast {
    position: fixed;
    bottom: 1.5rem;
    right: 1.5rem;
    padding: 0.75rem 1.25rem;
    border-radius: 8px;
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    color: var(--color-text);
    font-size: 0.9rem;
    font-weight: 500;
    box-shadow: 0 4px 16px rgba(0, 0, 0, 0.3);
    z-index: 9999;
}
.dismiss-toast--error {
    border-color: var(--color-danger);
    color: var(--color-danger);
}
.toast-fade-enter-active, .toast-fade-leave-active { transition: opacity 0.25s ease, transform 0.25s ease; }
.toast-fade-enter-from, .toast-fade-leave-to { opacity: 0; transform: translateY(8px); }
</style>
