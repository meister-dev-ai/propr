<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="page-view">
        <div class="header-stack">
            <div class="header-nav-links">
                <RouterLink class="back-link" :to="backToReviewsLink">← Back to reviews</RouterLink>
                <RouterLink v-if="prReviewLink" :to="prReviewLink" class="back-link pr-view-link">PR Review ↗</RouterLink>
            </div>
            <h2>Job Protocol</h2>
        </div>

        <p v-if="loading" class="loading">Loading…</p>
        <p v-else-if="error" class="error">{{ error }}</p>
        <p v-else-if="protocols.length === 0" class="empty-state">No protocol available for this job.</p>

        <template v-else>
            <!-- Aggregated totals across all passes -->


            <!-- Page Header Stats (Compact) -->
            <div class="job-stat-strip compact-stats">
                <div class="stat-pill"><span class="stat-label">Job</span><span class="stat-value monospace-value" :title="protocols[0].jobId">{{ jobShortId }}</span></div>
                <div class="stat-pill"><span class="stat-label">Duration</span><span class="stat-value">{{ overallDuration }}</span></div>
                <div class="stat-pill"><span class="stat-label">Total Tokens</span><span class="stat-value fat-tokens">{{ formatTokens(totalInputTokens + totalOutputTokens) }}</span></div>
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
                                    <span v-if="item.protocol.attemptNumber" class="attempt-pill">try {{ item.protocol.attemptNumber }}</span>
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
                        <dl class="summary-grid pass-summary">
                            <div><dt>Attempt</dt><dd>{{ activePass.attemptNumber ?? '—' }}</dd></div>
                            <div><dt>Started</dt><dd>{{ formatDate(activePass.startedAt) }}</dd></div>
                            <div><dt>Completed</dt><dd>{{ formatDate(activePass.completedAt) }}</dd></div>
                            <div><dt>Duration</dt><dd>{{ computePassDuration(activePass) }}</dd></div>
                            <div><dt>Iterations</dt><dd>{{ activePass.iterationCount ?? '—' }}</dd></div>
                            <div><dt>Tool Calls</dt><dd>{{ activePass.toolCallCount ?? '—' }}</dd></div>
                            <div><dt>In Tokens</dt><dd class="fat-tokens">{{ formatTokens(activePass.totalInputTokens) }}</dd></div>
                            <div><dt>Out Tokens</dt><dd class="fat-tokens">{{ formatTokens(activePass.totalOutputTokens) }}</dd></div>
                        </dl>

                        <section class="events-section">
                            <h4>Events ({{ activePass.events?.length ? Math.ceil(activePass.events.length / 2) : 0 }})</h4>
                            <p v-if="!activePass.events?.length" class="empty-state">{{ emptyPassMessage(activePass) }}</p>
                            <table v-else class="events-table">
                                <thead>
                                    <tr>
                                        <th>Time</th>
                                        <th>Kind</th>
                                        <th>Name</th>
                                        <th>Input Tokens</th>
                                        <th>Output Tokens</th>
                                        <th>Error</th>
                                    </tr>
                                </thead>
                                <TransitionGroup name="list" tag="tbody">
                                    <tr
                                        v-for="merged in processEvents(activePass.events)"
                                        :key="merged.id"
                                        class="row-clickable"
                                        :class="{
                                            'row-error': !!merged.callDetails.error || !!merged.resultDetails?.error,
                                            'row-processing': !merged.resultDetails
                                        }"
                                        @click="openMergedModal(merged)"
                                    >
                                        <td class="date-cell">{{ formatDate(merged.time) }}</td>
                                        <td class="kind-cell">
                                            <span class="kind-badge" :class="kindBadgeClass(merged.callDetails.kind)">{{ merged.callDetails.kind ?? 'unknown' }}</span>
                                        </td>
                                        <td class="name-cell" :class="{ 'tool-name': merged.callDetails.kind === 'toolCall', 'ai-name': merged.callDetails.kind === 'aiCall', 'memory-name': merged.callDetails.kind === 'memoryOperation' }">
                                            {{ merged.name }}
                                            <span v-if="merged.resultDetails?.outputSummary === null && merged.resultDetails?.error === null && merged.callDetails.kind !== 'memoryOperation'" class="status-badge status-processing" style="margin-left: 0.5rem">Executing...</span>
                                        </td>
                                        <td class="tokens-cell fat-tokens">{{ formatTokens(merged.callDetails.inputTokens) }}</td>
                                        <td class="tokens-cell fat-tokens">{{ formatTokens(merged.callDetails.outputTokens) }}</td>
                                        <td class="error-cell">{{ merged.callDetails.error ?? '' }}</td>
                                    </tr>
                                </TransitionGroup>
                            </table>
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
                            v-if="jobDetail"
                            :breakdown="jobDetail.tokenBreakdown"
                            :breakdown-consistent="jobDetail.breakdownConsistent"
                        />
                        <p v-else class="empty-state">No detailed token breakdown available for this job.</p>
                    </div>
                </section>
            </div>
            
            <ModalDialog v-model:isOpen="isEventModalOpen" :title="selectedMergedEvent?.name ?? 'Event Protocol'">
                <div v-if="selectedMergedEvent" class="merged-modal-layout">
                    <section class="drawer-section">
                        <h4>Input</h4>
                        <div v-if="parsedInputResult" class="parsed-json-block">
                            <template v-if="selectedMergedEvent.callDetails.kind === 'memoryOperation' && selectedMergedEvent.callDetails.name === 'memory_reconsideration_completed' && typeof parsedInputResult === 'object'">
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
                        <pre v-else-if="selectedMergedEvent.callDetails.inputTextSample" class="content-block">{{ selectedMergedEvent.callDetails.inputTextSample }}</pre>
                        <p v-else class="no-content">No input captured.</p>
                    </section>
                    
                    <div class="modal-arrow">
                        <i class="fi fi-rr-arrow-right"></i>
                    </div>

                    <section class="drawer-section">
                        <div class="drawer-section-header">
                            <h4>Output</h4>
                            <span v-if="selectedMergedEvent.callDetails.kind === 'aiCall'" class="ai-disclaimer"><i class="fi fi-rr-magic-wand"></i> AI-generated content</span>
                        </div>
                        <template v-if="selectedMergedEvent.resultDetails">
                            <div v-if="parsedOutputResult" class="parsed-json-block">
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
                            </div>
                            <pre v-else-if="selectedMergedEvent.resultDetails.outputSummary !== null" class="content-block">{{ selectedMergedEvent.resultDetails.outputSummary }}</pre>
                            
                            <template v-else-if="selectedMergedEvent.resultDetails.error !== null">
                                <pre class="content-block error-block">{{ selectedMergedEvent.resultDetails.error }}</pre>
                            </template>
                            <div v-else-if="selectedMergedEvent.callDetails.kind === 'memoryOperation'" class="memory-no-output">
                                <i class="fi fi-rr-check-circle memory-no-output-icon"></i>
                                <span>No output recorded for this memory operation.</span>
                            </div>
                            <p v-else-if="selectedMergedEvent.resultDetails.outputSummary === null && selectedMergedEvent.resultDetails.error === null" class="no-content" style="color: var(--color-accent); font-weight: bold;">Currently Executing...</p>
                        </template>
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
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
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
type ReviewJobResultDto = components['schemas']['ReviewJobResultDto']

interface TokenBreakdownEntry {
    connectionCategory: number | null
    modelId: string | null
    totalInputTokens: number
    totalOutputTokens: number
}

interface JobDetail {
    tokenBreakdown: TokenBreakdownEntry[]
    breakdownConsistent: boolean | null
}

interface MergedEvent {
    id: string
    time: string
    name: string
    callDetails: ProtocolEventDto
    resultDetails: ProtocolEventDto | null
}

type ReviewProtocolPass = ReviewJobProtocolDto & {
    id?: string
    attemptNumber?: number
    label?: string | null
    outcome?: string | null
    events?: ProtocolEventDto[]
    startedAt?: string
    completedAt?: string | null
    totalInputTokens?: number | null
    totalOutputTokens?: number | null
    iterationCount?: number | null
    toolCallCount?: number | null
}

const loading = ref(false)
const error = ref('')
const activeTab = ref<'summary' | 'traces' | 'tokens'>('summary')
const protocols = ref<ReviewProtocolPass[]>([])
const activePassId = ref<string | null>(null)
const selectedMergedEvent = ref<MergedEvent | null>(null)
const reviewStatus = ref<ReviewJobResultDto | null>(null)
const jobDetail = ref<JobDetail | null>(null)
const collapsedFolders = ref<Set<string>>(new Set())
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

const activePass = computed<ReviewProtocolPass | null>(() => {
    if (!protocols.value.length || activeTab.value === 'summary') return null
    return protocols.value.find(p => p.id === activePassId.value) ?? protocols.value[0]
})

const totalInputTokens = computed(() =>
    protocols.value.reduce((sum, p) => sum + (p.totalInputTokens ?? 0), 0),
)
const totalOutputTokens = computed(() =>
    protocols.value.reduce((sum, p) => sum + (p.totalOutputTokens ?? 0), 0),
)

const overallDuration = computed(() => {
    if (!protocols.value.length) return '—'
    
    let earliest = Infinity;
    let latest = -Infinity;
    let hasProcessing = false;
    
    protocols.value.forEach(p => {
        if (p.startedAt) {
            earliest = Math.min(earliest, new Date(p.startedAt).getTime());
        }
        if (p.completedAt) {
            latest = Math.max(latest, new Date(p.completedAt).getTime());
        } else {
            hasProcessing = true;
        }
    });
    
    if (earliest === Infinity) return '—';
    if (hasProcessing || latest === -Infinity) {
        latest = Date.now();
    }
    
    return formatDurationMs(latest - earliest);
});

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

let pollInterval: ReturnType<typeof setInterval> | null = null

async function loadProtocol(showLoading = false) {
    if (showLoading) loading.value = true
    try {
        const jobId = route.params.id as string
        const [protocolRes, resultRes, detailRes] = await Promise.all([
            createAdminClient().GET('/jobs/{id}/protocol', { params: { path: { id: jobId } } }),
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
            if (resultRes.data) {
                reviewStatus.value = resultRes.data
            }
            if (detailRes.data) {
                const d = detailRes.data as any
                jobDetail.value = {
                    tokenBreakdown: d.tokenBreakdown ?? [],
                    breakdownConsistent: d.breakdownConsistent ?? null,
                }
            }
            if (!activePassId.value && normalizedProtocols.length > 0 && normalizedProtocols[0].id) {
                activePassId.value = normalizedProtocols[0].id
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

onMounted(() => {
    loadProtocol(true)
})

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

function openMergedModal(event: MergedEvent): void {
    selectedMergedEvent.value = event
    isEventModalOpen.value = true
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

function formatTokens(n: number | null | undefined): string {
    if (n == null) return '—'
    return n.toLocaleString()
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

.attempt-pill {
    display: inline-flex;
    align-items: center;
    margin-left: 0.5rem;
    padding: 0.1rem 0.45rem;
    border: 1px solid rgba(255, 255, 255, 0.12);
    border-radius: 9999px;
    font-size: 0.7rem;
    color: var(--color-text-muted);
    white-space: nowrap;
}

.status-processing {
    background: rgba(34, 211, 238, 0.15);
    color: var(--color-accent);
    animation: flash 2s cubic-bezier(0.4, 0, 0.6, 1) infinite;
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

@media (min-width: 768px) {
    .merged-modal-layout {
        flex-direction: row;
        align-items: stretch;
    }
    .merged-modal-layout .drawer-section {
        flex: 1;
        min-width: 0; 
        margin: 0;
    }
}

.modal-arrow {
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--color-text-muted);
}

@media (max-width: 767px) {
    .modal-arrow {
        transform: rotate(90deg);
    }
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

.events-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
}

.events-table th,
.events-table td {
    padding: 0.75rem 1rem;
    text-align: left;
    border-bottom: 1px solid var(--color-border);
}

.events-table th {
    background: var(--color-surface);
    font-weight: 600;
    color: var(--color-text);
}

.row-error td {
    background: rgba(239, 68, 68, 0.1);
}

.row-clickable {
    cursor: pointer;
    transition: background 0.15s;
}

.row-clickable:hover td {
    background: var(--color-border);
}

.row-selected td {
    background: var(--color-border);
    border-left: 2px solid var(--color-accent);
}

.row-selected.row-error td {
    background: rgba(239, 68, 68, 0.2);
}

.date-cell {
    white-space: nowrap;
    color: var(--color-text-muted);
    min-width: 11rem;
}

.kind-cell {
    white-space: nowrap;
    color: var(--color-text-muted);
}

.tokens-cell {
    text-align: right;
    font-family: monospace;
    color: var(--color-text);
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
    width: 320px;
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

/* Events Table badges */
.kind-badge {
    display: inline-block;
    padding: 0.15rem 0.5rem;
    border-radius: 4px;
    font-size: 0.7rem;
    font-weight: 600;
    text-transform: uppercase;
}
.badge-purple { background: rgba(168, 85, 247, 0.15); color: #c084fc; }
.badge-cyan { background: rgba(34, 211, 238, 0.15); color: #22d3ee; }
.badge-green { background: rgba(52, 211, 153, 0.15); color: #34d399; }
.badge-gray { background: rgba(255, 255, 255, 0.1); color: var(--color-text-muted); }

.tool-name { font-weight: 600; font-family: monospace; }
.ai-name { font-style: italic; color: var(--color-text-muted); }
.memory-name { color: #34d399; }

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
