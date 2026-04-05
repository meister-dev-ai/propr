<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="section-card client-procursor-tab">
    <div class="section-card-header">
      <div class="procursor-header-left">
        <h3>ProCursor Knowledge Bases</h3>
        <span v-if="!loading" class="chip chip-muted">
          {{ sources.length }} source{{ sources.length === 1 ? '' : 's' }}
        </span>
        <p class="procursor-subtitle">
          Register git-backed knowledge sources, manage tracked branches, and queue indexing refreshes for this client.
        </p>
      </div>
      <div class="section-card-header-actions">
        <span v-if="!canManage" class="chip chip-muted">Read-only</span>
        <button v-else class="btn-primary" @click="openCreateSourceModal">
          <i class="fi fi-rr-plus"></i> Add Source
        </button>
      </div>
    </div>

    <div v-if="loading" class="loading-state">
      <ProgressOrb class="state-orb" />
      <span>Loading ProCursor sources...</span>
    </div>

    <div v-else-if="error" class="error-state">
      <i class="fi fi-rr-warning error-icon"></i>
      <p>{{ error }}</p>
      <button class="btn-slide" @click="loadSources">
        <div class="sign"><i class="fi fi-rr-refresh"></i></div>
        <span class="text">Try Again</span>
      </button>
    </div>

    <div v-else-if="!sources.length" class="empty-state">
      <i class="fi fi-rr-books empty-icon"></i>
      <h3>No knowledge sources yet</h3>
      <p>Create the first ProCursor source to index repositories or Azure DevOps wikis for this client.</p>
      <button v-if="canManage" class="btn-primary" @click="openCreateSourceModal">
        <i class="fi fi-rr-plus"></i> Create Source
      </button>
    </div>

    <div v-else class="sources-stack">
      <article
        v-for="source in sources"
        :key="source.sourceId ?? source.displayName ?? source.repositoryId ?? source.projectId ?? 'source'"
        class="source-card"
      >
        <header class="source-card-header">
          <div class="source-card-title-group">
            <div class="source-title-row">
              <h4>{{ source.displayName || 'Unnamed source' }}</h4>
              <span class="chip chip-muted">{{ formatSourceKind(source.sourceKind) }}</span>
              <span :class="['chip', statusChipClass(source.status)]">{{ formatStatus(source.status) }}</span>
              <span
                v-if="source.latestSnapshot?.freshnessStatus"
                :class="['chip', statusChipClass(source.latestSnapshot.freshnessStatus)]"
              >
                {{ formatStatus(source.latestSnapshot.freshnessStatus) }}
              </span>
            </div>
            <p class="source-card-subtitle">
              {{ source.organizationUrl || 'No organization URL' }} / {{ source.projectId || 'No project' }} / {{ source.sourceDisplayName || source.repositoryId || 'No source selected' }}
            </p>
          </div>

          <div class="source-card-actions">
            <button
              v-if="canManage"
              class="btn-secondary btn-sm"
              :disabled="isRefreshing(refreshKeyForSource(source.sourceId)) || !source.sourceId"
              @click="queueSourceRefresh(source)"
            >
              <i class="fi fi-rr-refresh"></i>
              {{ isRefreshing(refreshKeyForSource(source.sourceId)) ? 'Queueing...' : 'Refresh Default' }}
            </button>
          </div>
        </header>

        <div class="source-meta-grid">
          <div class="meta-item">
            <span class="meta-key">Default branch</span>
            <span class="meta-value">{{ source.defaultBranch || 'Not set' }}</span>
          </div>
          <div class="meta-item">
            <span class="meta-key">Root path</span>
            <span class="meta-value">{{ source.rootPath || 'Repository root' }}</span>
          </div>
          <div class="meta-item">
            <span class="meta-key">Selected source</span>
            <span class="meta-value">{{ source.sourceDisplayName || source.repositoryId || 'Not set' }}</span>
          </div>
          <div class="meta-item">
            <span class="meta-key">Symbol mode</span>
            <span class="meta-value">{{ formatSymbolMode(source.symbolMode) }}</span>
          </div>
          <div class="meta-item">
            <span class="meta-key">Latest snapshot branch</span>
            <span class="meta-value">{{ source.latestSnapshot?.branch || 'Not indexed yet' }}</span>
          </div>
          <div class="meta-item">
            <span class="meta-key">Latest commit</span>
            <code class="sha-value">{{ formatSha(source.latestSnapshot?.commitSha) }}</code>
          </div>
          <div class="meta-item">
            <span class="meta-key">Last completed</span>
            <span class="meta-value">{{ formatDate(source.latestSnapshot?.completedAt) }}</span>
          </div>
        </div>

        <div class="source-note">
          <span v-if="source.latestSnapshot?.supportsSymbolQueries">Symbol queries are enabled for the latest snapshot.</span>
          <span v-else-if="source.latestSnapshot">Latest snapshot is text-only.</span>
          <span v-else>Queue the first refresh after creating a source to populate the knowledge base.</span>
        </div>

        <section v-if="canManage && source.sourceId && !isTokenUsageReportingEnabled" class="usage-rollout-panel">
          <div class="usage-drilldown-header">
            <div>
              <h5>Reporting Rollout</h5>
              <p>Usage reporting rollout is disabled in this environment.</p>
            </div>
          </div>

          <div class="inline-state">
            <i class="fi fi-rr-lock"></i>
            <span>Set <code>VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING=true</code> to enable source-level usage reporting.</span>
          </div>
        </section>

        <section v-if="canManage && source.sourceId && isTokenUsageReportingEnabled" class="usage-drilldown-panel">
          <div class="usage-drilldown-header">
            <div>
              <h5>Source Usage</h5>
              <p>Last {{ sourceUsagePeriod }} of source-linked ProCursor activity with safe recent-event inspection.</p>
            </div>

            <div class="usage-drilldown-actions">
              <span v-if="usageFor(source.sourceId)?.lastRollupCompletedAtUtc" class="chip chip-muted">
                Rollup {{ formatDate(usageFor(source.sourceId)?.lastRollupCompletedAtUtc) }}
              </span>
              <button
                class="btn-secondary btn-sm"
                :disabled="usageLoadingFor(source.sourceId) || recentEventsLoadingFor(source.sourceId)"
                @click="reloadSourceDrilldown(source.sourceId)"
              >
                Refresh Usage
              </button>
            </div>
          </div>

          <div v-if="usageLoadingFor(source.sourceId)" class="inline-state">
            <ProgressOrb class="state-orb state-orb-inline" />
            <span>Loading source usage...</span>
          </div>

          <div v-else-if="usageErrorFor(source.sourceId)" class="inline-state inline-state-error">
            <p>{{ usageErrorFor(source.sourceId) }}</p>
            <button class="btn-secondary btn-sm" @click="reloadSourceUsage(source.sourceId)">Retry</button>
          </div>

          <div v-else-if="usageFor(source.sourceId)" class="usage-drilldown-content">
            <div class="usage-stats-grid">
              <div class="usage-stat-card">
                <span class="usage-stat-label">Total Tokens</span>
                <strong>{{ formatNumber(usageFor(source.sourceId)?.totals?.totalTokens) }}</strong>
              </div>
              <div class="usage-stat-card">
                <span class="usage-stat-label">Estimated Cost</span>
                <strong>{{ formatUsd(usageFor(source.sourceId)?.totals?.estimatedCostUsd) }}</strong>
              </div>
              <div class="usage-stat-card">
                <span class="usage-stat-label">Events</span>
                <strong>{{ formatNumber(usageFor(source.sourceId)?.totals?.eventCount) }}</strong>
              </div>
            </div>

            <div v-if="usageFor(source.sourceId)?.includesEstimatedUsage" class="usage-callout">
              <i class="fi fi-rr-info"></i>
              <p>{{ formatNumber(usageFor(source.sourceId)?.totals?.estimatedEventCount) }} events in this source window used estimated token counts.</p>
            </div>

            <div v-if="usageFor(source.sourceId)?.includesGapFilledEvents" class="usage-callout usage-callout-subtle">
              <i class="fi fi-rr-history"></i>
              <p>Recent activity newer than the last completed rollup is merged directly from captured event rows.</p>
            </div>

            <div class="usage-detail-grid">
              <div class="usage-panel">
                <div class="usage-panel-header">
                  <div>
                    <h6>Model Breakdown</h6>
                    <p>Which models consumed the most tokens for this source.</p>
                  </div>
                </div>

                <div v-if="!(usageFor(source.sourceId)?.byModel?.length ?? 0)" class="inline-empty-state">
                  No model usage recorded yet.
                </div>

                <ul v-else class="model-usage-list">
                  <li v-for="model in usageFor(source.sourceId)?.byModel ?? []" :key="model.modelName ?? 'model'" class="model-usage-row">
                    <div>
                      <strong>{{ model.modelName || 'Unknown model' }}</strong>
                      <p>{{ formatNumber(model.eventCount) }} events</p>
                    </div>
                    <div class="model-usage-side">
                      <strong>{{ formatNumber(model.totalTokens) }}</strong>
                      <span>{{ formatUsd(model.estimatedCostUsd) }}</span>
                    </div>
                  </li>
                </ul>
              </div>

              <div class="usage-panel">
                <div class="usage-panel-header">
                  <div>
                    <h6>Recent Snapshots</h6>
                    <p>Latest bucket totals from the source history window.</p>
                  </div>
                </div>

                <div v-if="!recentSnapshotsFor(source.sourceId).length" class="inline-empty-state">
                  No historical snapshots recorded yet.
                </div>

                <ul v-else class="snapshot-list">
                  <li v-for="snapshot in recentSnapshotsFor(source.sourceId)" :key="snapshot.bucketStart ?? 'snapshot'" class="snapshot-row">
                    <div>
                      <strong>{{ formatBucketDate(snapshot.bucketStart) }}</strong>
                      <p>{{ formatUsd(snapshot.estimatedCostUsd) }}</p>
                    </div>
                    <div class="model-usage-side">
                      <strong>{{ formatNumber(snapshot.totalTokens) }}</strong>
                      <span>{{ formatNumber(snapshot.promptTokens) }} in / {{ formatNumber(snapshot.completionTokens) }} out</span>
                    </div>
                  </li>
                </ul>
              </div>
            </div>

            <div class="usage-panel">
              <div class="usage-panel-header">
                <div>
                  <h6>Recent Safe Events</h6>
                  <p>Latest captured calls for this source without prompt or response content.</p>
                </div>
                <router-link
                  class="btn-ghost pr-view-btn"
                  :to="{ name: 'client-procursor-source-events', params: { id: clientId, sourceId: source.sourceId } }"
                >
                  Show Events ↗
                </router-link>
              </div>

            </div>
          </div>
        </section>

        <section class="branches-panel">
          <div class="branches-panel-header">
            <div>
              <h5>Tracked branches</h5>
              <p>Control refresh triggers and mini-index coverage per tracked branch.</p>
            </div>
            <button
              v-if="canManage"
              class="btn-secondary btn-sm"
              :disabled="!source.sourceId"
              @click="openCreateBranchModal(source)"
            >
              <i class="fi fi-rr-branching"></i> Add Branch
            </button>
          </div>

          <div v-if="branchLoadingFor(source.sourceId)" class="inline-state">
            <ProgressOrb class="state-orb state-orb-inline" />
            <span>Loading tracked branches...</span>
          </div>

          <div v-else-if="branchErrorFor(source.sourceId)" class="inline-state inline-state-error">
            <p>{{ branchErrorFor(source.sourceId) }}</p>
            <button
              class="btn-secondary btn-sm"
              :disabled="!source.sourceId"
              @click="reloadBranches(source.sourceId)"
            >
              Retry
            </button>
          </div>

          <div v-else-if="!branchesFor(source.sourceId).length" class="inline-empty-state">
            No tracked branches configured.
          </div>

          <table v-else class="branches-table">
            <thead>
              <tr>
                <th>Branch</th>
                <th>Trigger</th>
                <th>Mini Index</th>
                <th>Status</th>
                <th>Last Seen</th>
                <th>Last Indexed</th>
                <th></th>
              </tr>
            </thead>
            <tbody>
              <tr
                v-for="branch in branchesFor(source.sourceId)"
                :key="branch.branchId ?? branch.branchName ?? 'branch'"
              >
                <td>
                  <div class="branch-name-cell">
                    <strong>{{ branch.branchName || 'Unnamed branch' }}</strong>
                    <span :class="['chip', branch.isEnabled ? 'chip-success' : 'chip-muted']">
                      {{ branch.isEnabled ? 'Enabled' : 'Disabled' }}
                    </span>
                  </div>
                </td>
                <td>{{ formatTriggerMode(branch.refreshTriggerMode) }}</td>
                <td>{{ branch.miniIndexEnabled ? 'On' : 'Off' }}</td>
                <td>
                  <span :class="['chip', statusChipClass(branch.freshnessStatus)]">
                    {{ formatStatus(branch.freshnessStatus) }}
                  </span>
                </td>
                <td><code class="sha-value">{{ formatSha(branch.lastSeenCommitSha) }}</code></td>
                <td><code class="sha-value">{{ formatSha(branch.lastIndexedCommitSha) }}</code></td>
                <td class="branch-actions-cell">
                  <div class="branch-actions">
                    <button
                      v-if="canManage"
                      class="btn-secondary btn-sm"
                      :disabled="isRefreshing(refreshKeyForBranch(branch.branchId)) || !source.sourceId || !branch.branchId"
                      @click="queueBranchRefresh(source, branch)"
                    >
                      {{ isRefreshing(refreshKeyForBranch(branch.branchId)) ? 'Queueing...' : 'Refresh' }}
                    </button>
                    <button
                      v-if="canManage"
                      class="btn-secondary btn-sm"
                      :disabled="!source.sourceId || !branch.branchId"
                      @click="openEditBranchModal(source, branch)"
                    >
                      Edit
                    </button>
                    <button
                      v-if="canManage"
                      class="btn-danger btn-sm"
                      :disabled="!source.sourceId || !branch.branchId"
                      @click="openDeleteBranchDialog(source, branch)"
                    >
                      Remove
                    </button>
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </section>
      </article>
    </div>

    <ModalDialog :isOpen="createSourceModal.open" title="Add ProCursor Source" @update:isOpen="createSourceModal.open = $event">
      <div class="modal-form-grid">
        <div class="form-field">
          <label for="procursorDisplayName">Display Name</label>
          <input id="procursorDisplayName" v-model="createSourceModal.displayName" type="text" placeholder="Platform docs" />
        </div>

        <div class="form-field">
          <label for="procursorSourceKind">Source Kind</label>
          <select id="procursorSourceKind" v-model="createSourceModal.sourceKind" @change="handleCreateSourceKindChange">
            <option value="repository">Repository</option>
            <option value="adoWiki">ADO Wiki</option>
          </select>
        </div>

        <div class="form-field modal-form-grid-full">
          <label for="procursorOrganizationScope">Organization</label>
          <select
            id="procursorOrganizationScope"
            v-model="createSourceModal.organizationScopeId"
            :disabled="createSourceModal.loadingScopes"
            @change="handleCreateSourceOrganizationScopeChange"
          >
            <option value="">Select an organization</option>
            <option
              v-for="scope in createSourceModal.organizationScopes"
              :key="scope.id ?? scope.organizationUrl ?? 'scope'"
              :value="scope.id ?? ''"
            >
              {{ formatOrganizationScopeLabel(scope) }}
            </option>
          </select>
          <p v-if="createSourceModal.loadingScopes" class="field-help">Loading allowed organizations...</p>
          <p v-else-if="createSourceModal.scopeError" class="error">{{ createSourceModal.scopeError }}</p>
          <p v-else-if="!createSourceModal.organizationScopes.length" class="field-help">
            Add and enable an Azure DevOps organization in the credentials section before creating guided sources.
          </p>
        </div>

        <div class="form-field">
          <label for="procursorProjectId">Project</label>
          <select
            id="procursorProjectId"
            v-model="createSourceModal.projectId"
            :disabled="!createSourceModal.organizationScopeId || createSourceModal.loadingProjects"
            @change="handleCreateSourceProjectChange"
          >
            <option value="">Select a project</option>
            <option
              v-for="project in createSourceModal.projects"
              :key="project.projectId ?? 'project'"
              :value="project.projectId ?? ''"
            >
              {{ project.projectName || project.projectId }}
            </option>
          </select>
          <p v-if="createSourceModal.loadingProjects" class="field-help">Loading Azure DevOps projects...</p>
          <p v-else-if="createSourceModal.projectError" class="error">{{ createSourceModal.projectError }}</p>
          <p v-else-if="createSourceModal.organizationScopeId && !createSourceModal.projects.length" class="field-help">
            No projects are currently available for this organization.
          </p>
        </div>

        <div class="form-field">
          <label for="procursorSourceSelection">{{ createSourceModal.sourceKind === 'adoWiki' ? 'Wiki' : 'Repository' }}</label>
          <select
            id="procursorSourceSelection"
            v-model="createSourceModal.selectedSourceKey"
            :disabled="!createSourceModal.projectId || createSourceModal.loadingSourceOptions"
            @change="handleCreateSourceSelectionChange"
          >
            <option value="">Select a {{ createSourceModal.sourceKind === 'adoWiki' ? 'wiki' : 'repository' }}</option>
            <option
              v-for="sourceOption in createSourceModal.sourceOptions"
              :key="sourceOptionKey(sourceOption)"
              :value="sourceOptionKey(sourceOption)"
            >
              {{ sourceOption.displayName || sourceOption.canonicalSourceRef?.value }}
            </option>
          </select>
          <p v-if="createSourceModal.loadingSourceOptions" class="field-help">
            Loading {{ createSourceModal.sourceKind === 'adoWiki' ? 'wikis' : 'repositories' }}...
          </p>
          <p v-else-if="createSourceModal.sourceError" class="error">{{ createSourceModal.sourceError }}</p>
          <p v-else-if="createSourceModal.projectId && !createSourceModal.sourceOptions.length" class="field-help">
            No {{ createSourceModal.sourceKind === 'adoWiki' ? 'wikis' : 'repositories' }} are currently available for this project.
          </p>
        </div>

        <div class="form-field">
          <label for="procursorDefaultBranch">Default Branch</label>
          <select
            id="procursorDefaultBranch"
            v-model="createSourceModal.defaultBranch"
            :disabled="!createSourceModal.selectedSourceKey || createSourceModal.loadingBranches"
            @change="handleDefaultBranchChange"
          >
            <option value="">Select a branch</option>
            <option
              v-for="branch in createSourceModal.branchOptions"
              :key="branch.branchName ?? 'branch'"
              :value="branch.branchName ?? ''"
            >
              {{ formatBranchOptionLabel(branch) }}
            </option>
          </select>
          <p v-if="createSourceModal.loadingBranches" class="field-help">Loading Azure DevOps branches...</p>
          <p v-else-if="createSourceModal.branchError" class="error">{{ createSourceModal.branchError }}</p>
          <p v-else-if="createSourceModal.selectedSourceKey && !createSourceModal.branchOptions.length" class="field-help">
            No branches are currently available for this source.
          </p>
        </div>

        <div class="form-field">
          <label for="procursorRootPath">Root Path <span class="field-hint-inline">(optional)</span></label>
          <input id="procursorRootPath" v-model="createSourceModal.rootPath" type="text" placeholder="/docs" />
        </div>

        <div class="form-field">
          <label for="procursorSymbolMode">Symbol Mode</label>
          <select id="procursorSymbolMode" v-model="createSourceModal.symbolMode">
            <option value="auto">Auto</option>
            <option value="text_only">Text only</option>
          </select>
        </div>

        <div class="form-field">
          <label for="procursorInitialBranch">Initial Tracked Branch</label>
          <select
            id="procursorInitialBranch"
            v-model="createSourceModal.initialBranchName"
            :disabled="!createSourceModal.selectedSourceKey || createSourceModal.loadingBranches"
          >
            <option value="">Select the initial tracked branch</option>
            <option
              v-for="branch in createSourceModal.branchOptions"
              :key="`initial-${branch.branchName ?? 'branch'}`"
              :value="branch.branchName ?? ''"
            >
              {{ formatBranchOptionLabel(branch) }}
            </option>
          </select>
        </div>

        <div class="form-field">
          <label for="procursorInitialTrigger">Initial Refresh Trigger</label>
          <select id="procursorInitialTrigger" v-model="createSourceModal.initialRefreshTriggerMode">
            <option value="branchUpdate">On branch update</option>
            <option value="manual">Manual only</option>
          </select>
        </div>

        <label class="checkbox-field modal-form-grid-full">
          <input v-model="createSourceModal.initialMiniIndexEnabled" type="checkbox" />
          <span>Create review mini-index overlays for the initial tracked branch.</span>
        </label>
      </div>

      <p class="modal-hint">
        Use <strong>Auto</strong> for code-aware indexing. Choose <strong>Text only</strong> for documentation-heavy sources where symbol extraction is not needed.
      </p>

      <p v-if="createSourceModal.error" class="error">{{ createSourceModal.error }}</p>

      <template #footer>
        <button class="btn-secondary" @click="createSourceModal.open = false">Cancel</button>
        <button class="btn-primary" :disabled="createSourceModal.saving" @click="handleCreateSource">
          {{ createSourceModal.saving ? 'Creating...' : 'Create Source' }}
        </button>
      </template>
    </ModalDialog>

    <ModalDialog :isOpen="createBranchModal.open" :title="`Add Branch to ${createBranchModal.sourceName}`" @update:isOpen="createBranchModal.open = $event">
      <div class="modal-form-grid">
        <div class="form-field modal-form-grid-full">
          <label for="procursorBranchName">Branch Name</label>
          <input id="procursorBranchName" v-model="createBranchModal.branchName" type="text" placeholder="main" />
        </div>

        <div class="form-field">
          <label for="procursorBranchTrigger">Refresh Trigger</label>
          <select id="procursorBranchTrigger" v-model="createBranchModal.refreshTriggerMode">
            <option value="branchUpdate">On branch update</option>
            <option value="manual">Manual only</option>
          </select>
        </div>

        <label class="checkbox-field">
          <input v-model="createBranchModal.miniIndexEnabled" type="checkbox" />
          <span>Enable mini-index for PR overlays</span>
        </label>
      </div>

      <p v-if="createBranchModal.error" class="error">{{ createBranchModal.error }}</p>

      <template #footer>
        <button class="btn-secondary" @click="createBranchModal.open = false">Cancel</button>
        <button class="btn-primary" :disabled="createBranchModal.saving" @click="handleCreateBranch">
          {{ createBranchModal.saving ? 'Adding...' : 'Add Branch' }}
        </button>
      </template>
    </ModalDialog>

    <ModalDialog :isOpen="editBranchModal.open" :title="`Edit ${editBranchModal.branchName}`" @update:isOpen="editBranchModal.open = $event">
      <div class="modal-form-grid">
        <div class="form-field modal-form-grid-full">
          <label>Branch Name</label>
          <div class="readonly-value">{{ editBranchModal.branchName }}</div>
        </div>

        <div class="form-field">
          <label for="procursorEditBranchTrigger">Refresh Trigger</label>
          <select id="procursorEditBranchTrigger" v-model="editBranchModal.refreshTriggerMode">
            <option value="branchUpdate">On branch update</option>
            <option value="manual">Manual only</option>
          </select>
        </div>

        <label class="checkbox-field">
          <input v-model="editBranchModal.miniIndexEnabled" type="checkbox" />
          <span>Enable mini-index for PR overlays</span>
        </label>

        <label class="checkbox-field modal-form-grid-full">
          <input v-model="editBranchModal.isEnabled" type="checkbox" />
          <span>Branch is eligible for refresh scheduling and indexing.</span>
        </label>
      </div>

      <p v-if="editBranchModal.error" class="error">{{ editBranchModal.error }}</p>

      <template #footer>
        <button class="btn-secondary" @click="editBranchModal.open = false">Cancel</button>
        <button class="btn-primary" :disabled="editBranchModal.saving" @click="handleSaveBranch">
          {{ editBranchModal.saving ? 'Saving...' : 'Save Changes' }}
        </button>
      </template>
    </ModalDialog>

    <ConfirmDialog
      :open="!!deleteBranchTarget"
      :message="deleteBranchTarget ? `Remove branch ${deleteBranchTarget.branchName} from ${deleteBranchTarget.sourceName}?` : ''"
      @cancel="deleteBranchTarget = null"
      @confirm="confirmDeleteBranch"
    />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ModalDialog from '@/components/ModalDialog.vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import { useNotification } from '@/composables/useNotification'
import { useSession } from '@/composables/useSession'
import {
  listAdoBranches,
  listAdoOrganizationScopes,
  listAdoProjects,
  listAdoSources,
  type AdoBranchOptionDto,
  type AdoSourceKind,
  type AdoSourceOptionDto,
  type ClientAdoOrganizationScopeDto,
} from '@/services/adoDiscoveryService'
import {
  createProCursorSource,
  createProCursorTrackedBranch,
  deleteProCursorTrackedBranch,
  getProCursorRecentEvents,
  getProCursorSourceTokenUsage,
  listProCursorSources,
  listProCursorTrackedBranches,
  queueProCursorRefresh,
  updateProCursorTrackedBranch,
  type ProCursorKnowledgeSourceDto,
  type ProCursorKnowledgeSourceRequest,
  type ProCursorRefreshTriggerMode,
  type ProCursorSourceKind,
  type ProCursorTrackedBranchDto,
} from '@/services/proCursorService'
import type {
  ProCursorSourceTokenUsageResponse,
  ProCursorTokenUsageEventDto,
  ProCursorTokenUsageSeriesPointDto,
} from '@/types/proCursorTokenUsage'

const props = defineProps<{
  clientId: string
}>()

interface BranchState {
  items: ProCursorTrackedBranchDto[]
  loading: boolean
  error: string
}

interface DeleteBranchTarget {
  sourceId: string
  sourceName: string
  branchId: string
  branchName: string
}

interface SourceUsageState {
  usage: ProCursorSourceTokenUsageResponse | null
  usageLoading: boolean
  usageError: string
  recentEvents: ProCursorTokenUsageEventDto[]
  recentEventsLoading: boolean
  recentEventsError: string
}

const { notify } = useNotification()
const { hasClientRole } = useSession()

const sources = ref<ProCursorKnowledgeSourceDto[]>([])
const loading = ref(false)
const error = ref('')
const branchStateBySource = reactive<Record<string, BranchState>>({})
const usageStateBySource = reactive<Record<string, SourceUsageState>>({})
const refreshingByKey = reactive<Record<string, boolean>>({})
const deleteBranchTarget = ref<DeleteBranchTarget | null>(null)
const sourceUsagePeriod = '30d'
const sourceRecentEventsLimit = 10
const sourceDrilldownConcurrency = 3
const isTokenUsageReportingEnabled = import.meta.env.VITE_FEATURE_PROCURSOR_TOKEN_USAGE_REPORTING !== 'false'

const canManage = computed(() => hasClientRole(props.clientId, 1))

const createSourceModal = reactive({
  open: false,
  saving: false,
  error: '',
  displayName: '',
  sourceKind: 'repository' as ProCursorSourceKind,
  organizationScopeId: '',
  organizationScopes: [] as ClientAdoOrganizationScopeDto[],
  loadingScopes: false,
  scopeError: '',
  projectId: '',
  projects: [] as Array<{ projectId?: string | null; projectName?: string | null }>,
  loadingProjects: false,
  projectError: '',
  selectedSourceKey: '',
  sourceOptions: [] as AdoSourceOptionDto[],
  loadingSourceOptions: false,
  sourceError: '',
  branchOptions: [] as AdoBranchOptionDto[],
  loadingBranches: false,
  branchError: '',
  defaultBranch: '',
  rootPath: '',
  symbolMode: 'auto',
  initialBranchName: '',
  initialRefreshTriggerMode: 'branchUpdate' as ProCursorRefreshTriggerMode,
  initialMiniIndexEnabled: true,
})

const selectedOrganizationScope = computed(() => {
  return createSourceModal.organizationScopes.find((scope) => scope.id === createSourceModal.organizationScopeId) ?? null
})

const selectedSourceOption = computed(() => {
  return createSourceModal.sourceOptions.find((sourceOption) => sourceOptionKey(sourceOption) === createSourceModal.selectedSourceKey) ?? null
})

let createSourceScopesRequestId = 0
let createSourceProjectsRequestId = 0
let createSourceSourcesRequestId = 0
let createSourceBranchesRequestId = 0

const createBranchModal = reactive({
  open: false,
  saving: false,
  error: '',
  sourceId: '',
  sourceName: '',
  branchName: '',
  refreshTriggerMode: 'branchUpdate' as ProCursorRefreshTriggerMode,
  miniIndexEnabled: true,
})

const editBranchModal = reactive({
  open: false,
  saving: false,
  error: '',
  sourceId: '',
  sourceName: '',
  branchId: '',
  branchName: '',
  refreshTriggerMode: 'branchUpdate' as ProCursorRefreshTriggerMode,
  miniIndexEnabled: true,
  isEnabled: true,
})

onMounted(() => {
  void loadSources()
})

function sortSources(items: ProCursorKnowledgeSourceDto[]): ProCursorKnowledgeSourceDto[] {
  return [...items].sort((left, right) => {
    return (left.displayName ?? '').localeCompare(right.displayName ?? '', undefined, {
      sensitivity: 'base',
    })
  })
}

function sortBranches(items: ProCursorTrackedBranchDto[]): ProCursorTrackedBranchDto[] {
  return [...items].sort((left, right) => {
    return (left.branchName ?? '').localeCompare(right.branchName ?? '', undefined, {
      sensitivity: 'base',
    })
  })
}

function sortOrganizationScopes(items: ClientAdoOrganizationScopeDto[]): ClientAdoOrganizationScopeDto[] {
  return [...items].sort((left, right) => {
    const leftLabel = (left.displayName || left.organizationUrl || '').trim()
    const rightLabel = (right.displayName || right.organizationUrl || '').trim()
    return leftLabel.localeCompare(rightLabel, undefined, { sensitivity: 'base' })
  })
}

function sortProjects(items: Array<{ projectId?: string | null; projectName?: string | null }>) {
  return [...items].sort((left, right) => {
    return (left.projectName || left.projectId || '').localeCompare(right.projectName || right.projectId || '', undefined, {
      sensitivity: 'base',
    })
  })
}

function sortSourceOptions(items: AdoSourceOptionDto[]): AdoSourceOptionDto[] {
  return [...items].sort((left, right) => {
    return (left.displayName || left.canonicalSourceRef?.value || '').localeCompare(
      right.displayName || right.canonicalSourceRef?.value || '',
      undefined,
      { sensitivity: 'base' },
    )
  })
}

function sortDiscoveredBranches(items: AdoBranchOptionDto[]): AdoBranchOptionDto[] {
  return [...items].sort((left, right) => {
    if (Boolean(left.isDefault) !== Boolean(right.isDefault)) {
      return left.isDefault ? -1 : 1
    }

    return (left.branchName || '').localeCompare(right.branchName || '', undefined, {
      sensitivity: 'base',
    })
  })
}

function toErrorMessage(cause: unknown, fallback: string): string {
  return cause instanceof Error && cause.message ? cause.message : fallback
}

function trimOptional(value: string): string | null {
  const trimmed = value.trim()
  return trimmed ? trimmed : null
}

function ensureBranchState(sourceId: string): BranchState {
  if (!branchStateBySource[sourceId]) {
    branchStateBySource[sourceId] = {
      items: [],
      loading: false,
      error: '',
    }
  }

  return branchStateBySource[sourceId]
}

function syncBranchStateKeys(loadedSources: ProCursorKnowledgeSourceDto[]) {
  const validIds = new Set(
    loadedSources
      .map((source) => source.sourceId)
      .filter((sourceId): sourceId is string => Boolean(sourceId)),
  )

  for (const sourceId of Object.keys(branchStateBySource)) {
    if (!validIds.has(sourceId)) {
      delete branchStateBySource[sourceId]
    }
  }

  for (const sourceId of validIds) {
    ensureBranchState(sourceId)
  }
}

function ensureUsageState(sourceId: string): SourceUsageState {
  if (!usageStateBySource[sourceId]) {
    usageStateBySource[sourceId] = {
      usage: null,
      usageLoading: false,
      usageError: '',
      recentEvents: [],
      recentEventsLoading: false,
      recentEventsError: '',
    }
  }

  return usageStateBySource[sourceId]
}

function syncUsageStateKeys(loadedSources: ProCursorKnowledgeSourceDto[]) {
  const validIds = new Set(
    loadedSources
      .map((source) => source.sourceId)
      .filter((sourceId): sourceId is string => Boolean(sourceId)),
  )

  for (const sourceId of Object.keys(usageStateBySource)) {
    if (!validIds.has(sourceId)) {
      delete usageStateBySource[sourceId]
    }
  }

  for (const sourceId of validIds) {
    ensureUsageState(sourceId)
  }
}

function branchesFor(sourceId?: string): ProCursorTrackedBranchDto[] {
  return sourceId ? ensureBranchState(sourceId).items : []
}

function branchLoadingFor(sourceId?: string): boolean {
  return sourceId ? ensureBranchState(sourceId).loading : false
}

function branchErrorFor(sourceId?: string): string {
  return sourceId ? ensureBranchState(sourceId).error : 'Source identifier is missing.'
}

function usageFor(sourceId?: string): ProCursorSourceTokenUsageResponse | null {
  return sourceId ? ensureUsageState(sourceId).usage : null
}

function usageLoadingFor(sourceId?: string): boolean {
  return sourceId ? ensureUsageState(sourceId).usageLoading : false
}

function usageErrorFor(sourceId?: string): string {
  return sourceId ? ensureUsageState(sourceId).usageError : 'Source identifier is missing.'
}

function recentEventsFor(sourceId?: string): ProCursorTokenUsageEventDto[] {
  return sourceId ? ensureUsageState(sourceId).recentEvents : []
}

function recentEventsLoadingFor(sourceId?: string): boolean {
  return sourceId ? ensureUsageState(sourceId).recentEventsLoading : false
}

function recentEventsErrorFor(sourceId?: string): string {
  return sourceId ? ensureUsageState(sourceId).recentEventsError : 'Source identifier is missing.'
}

function recentSnapshotsFor(sourceId?: string): ProCursorTokenUsageSeriesPointDto[] {
  const series = usageFor(sourceId)?.series ?? []
  return [...series].slice(-6).reverse()
}

function refreshKeyForSource(sourceId?: string): string {
  return `source:${sourceId ?? 'missing'}`
}

function refreshKeyForBranch(branchId?: string): string {
  return `branch:${branchId ?? 'missing'}`
}

function isRefreshing(key: string): boolean {
  return Boolean(refreshingByKey[key])
}

function setRefreshing(key: string, active: boolean) {
  if (active) {
    refreshingByKey[key] = true
    return
  }

  delete refreshingByKey[key]
}

async function loadSources() {
  loading.value = true
  error.value = ''

  try {
    const loadedSources = sortSources(await listProCursorSources(props.clientId))
    const sourceIdsToWarm: string[] = []

    sources.value = loadedSources
    syncBranchStateKeys(loadedSources)
    syncUsageStateKeys(loadedSources)

    for (const source of loadedSources) {
      if (source.sourceId) {
        void loadBranches(source.sourceId)
        if (canManage.value && isTokenUsageReportingEnabled) {
          sourceIdsToWarm.push(source.sourceId)
        }
      }
    }

    if (sourceIdsToWarm.length > 0) {
      void warmSourceDrilldowns(sourceIdsToWarm)
    }
  } catch (cause) {
    error.value = toErrorMessage(cause, 'Failed to load ProCursor sources.')
  } finally {
    loading.value = false
  }
}

async function warmSourceDrilldowns(sourceIds: string[]) {
  const queue = [...sourceIds]
  const workerCount = Math.min(sourceDrilldownConcurrency, queue.length)

  await Promise.all(
    Array.from({ length: workerCount }, async () => {
      while (queue.length > 0) {
        const sourceId = queue.shift()
        if (!sourceId) {
          return
        }

        await Promise.all([loadSourceUsage(sourceId), loadRecentEvents(sourceId)])
      }
    }),
  )
}

async function loadBranches(sourceId: string) {
  const state = ensureBranchState(sourceId)
  state.loading = true
  state.error = ''

  try {
    state.items = sortBranches(await listProCursorTrackedBranches(props.clientId, sourceId))
  } catch (cause) {
    state.error = toErrorMessage(cause, 'Failed to load tracked branches.')
  } finally {
    state.loading = false
  }
}

async function loadSourceUsage(sourceId: string) {
  const state = ensureUsageState(sourceId)
  state.usageLoading = true
  state.usageError = ''

  try {
    state.usage = await getProCursorSourceTokenUsage(props.clientId, sourceId, {
      period: sourceUsagePeriod,
      granularity: 'daily',
    })
  } catch (cause) {
    state.usage = null
    state.usageError = toErrorMessage(cause, 'Failed to load source usage.')
  } finally {
    state.usageLoading = false
  }
}

async function loadRecentEvents(sourceId: string) {
  const state = ensureUsageState(sourceId)
  state.recentEventsLoading = true
  state.recentEventsError = ''

  try {
    const response = await getProCursorRecentEvents(props.clientId, sourceId, sourceRecentEventsLimit)
    state.recentEvents = response.items ?? []
  } catch (cause) {
    state.recentEvents = []
    state.recentEventsError = toErrorMessage(cause, 'Failed to load recent usage events.')
  } finally {
    state.recentEventsLoading = false
  }
}

function reloadBranches(sourceId?: string) {
  if (!sourceId) {
    notify('Source identifier is missing.', 'error')
    return
  }

  void loadBranches(sourceId)
}

function reloadSourceUsage(sourceId?: string) {
  if (!sourceId) {
    notify('Source identifier is missing.', 'error')
    return
  }

  void loadSourceUsage(sourceId)
}

function reloadSourceDrilldown(sourceId?: string) {
  if (!sourceId) {
    notify('Source identifier is missing.', 'error')
    return
  }

  void Promise.all([loadSourceUsage(sourceId), loadRecentEvents(sourceId)])
}

function sourceOptionKey(sourceOption: AdoSourceOptionDto): string {
  const provider = sourceOption.canonicalSourceRef?.provider?.trim()
  const value = sourceOption.canonicalSourceRef?.value?.trim()
  return provider && value ? `${provider}::${value}` : ''
}

function formatOrganizationScopeLabel(scope: ClientAdoOrganizationScopeDto): string {
  const displayName = scope.displayName?.trim()
  const organizationUrl = scope.organizationUrl?.trim() || 'Unnamed organization'
  return displayName && !displayName.localeCompare(organizationUrl, undefined, { sensitivity: 'base' })
    ? displayName
    : displayName
      ? `${displayName} (${organizationUrl})`
      : organizationUrl
}

function formatBranchOptionLabel(branch: AdoBranchOptionDto): string {
  return branch.isDefault ? `${branch.branchName || 'Unnamed branch'} (default)` : branch.branchName || 'Unnamed branch'
}

function clearCreateSourceBranches() {
  createSourceBranchesRequestId += 1
  createSourceModal.branchOptions = []
  createSourceModal.loadingBranches = false
  createSourceModal.branchError = ''
  createSourceModal.defaultBranch = ''
  createSourceModal.initialBranchName = ''
}

function clearCreateSourceSources() {
  createSourceSourcesRequestId += 1
  createSourceModal.selectedSourceKey = ''
  createSourceModal.sourceOptions = []
  createSourceModal.loadingSourceOptions = false
  createSourceModal.sourceError = ''
  clearCreateSourceBranches()
}

function clearCreateSourceProjects() {
  createSourceProjectsRequestId += 1
  createSourceModal.projectId = ''
  createSourceModal.projects = []
  createSourceModal.loadingProjects = false
  createSourceModal.projectError = ''
  clearCreateSourceSources()
}

async function loadCreateSourceOrganizationScopes() {
  const requestId = ++createSourceScopesRequestId
  createSourceModal.loadingScopes = true
  createSourceModal.scopeError = ''

  try {
    const scopes = sortOrganizationScopes(
      (await listAdoOrganizationScopes(props.clientId)).filter((scope) => Boolean(scope.isEnabled)),
    )

    if (requestId !== createSourceScopesRequestId) {
      return
    }

    createSourceModal.organizationScopes = scopes
    if (!scopes.some((scope) => scope.id === createSourceModal.organizationScopeId)) {
      createSourceModal.organizationScopeId = ''
      clearCreateSourceProjects()
    }
  } catch (cause) {
    if (requestId !== createSourceScopesRequestId) {
      return
    }

    createSourceModal.organizationScopes = []
    createSourceModal.scopeError = toErrorMessage(cause, 'Failed to load organization scopes.')
    createSourceModal.organizationScopeId = ''
    clearCreateSourceProjects()
  } finally {
    if (requestId === createSourceScopesRequestId) {
      createSourceModal.loadingScopes = false
    }
  }
}

async function loadCreateSourceProjects(scopeId: string) {
  const requestId = ++createSourceProjectsRequestId
  createSourceModal.loadingProjects = true
  createSourceModal.projectError = ''

  try {
    const projects = sortProjects(await listAdoProjects(props.clientId, scopeId))
    if (requestId !== createSourceProjectsRequestId || createSourceModal.organizationScopeId !== scopeId) {
      return
    }

    createSourceModal.projects = projects
  } catch (cause) {
    if (requestId !== createSourceProjectsRequestId || createSourceModal.organizationScopeId !== scopeId) {
      return
    }

    createSourceModal.projects = []
    createSourceModal.projectError = toErrorMessage(cause, 'Failed to load Azure DevOps projects.')
  } finally {
    if (requestId === createSourceProjectsRequestId && createSourceModal.organizationScopeId === scopeId) {
      createSourceModal.loadingProjects = false
    }
  }
}

async function loadCreateSourceOptions(scopeId: string, projectId: string, sourceKind: ProCursorSourceKind) {
  const requestId = ++createSourceSourcesRequestId
  createSourceModal.loadingSourceOptions = true
  createSourceModal.sourceError = ''

  try {
    const sourceOptions = sortSourceOptions(
      await listAdoSources(props.clientId, scopeId, projectId, sourceKind as AdoSourceKind),
    )

    if (
      requestId !== createSourceSourcesRequestId ||
      createSourceModal.organizationScopeId !== scopeId ||
      createSourceModal.projectId !== projectId ||
      createSourceModal.sourceKind !== sourceKind
    ) {
      return
    }

    createSourceModal.sourceOptions = sourceOptions
  } catch (cause) {
    if (
      requestId !== createSourceSourcesRequestId ||
      createSourceModal.organizationScopeId !== scopeId ||
      createSourceModal.projectId !== projectId ||
      createSourceModal.sourceKind !== sourceKind
    ) {
      return
    }

    createSourceModal.sourceOptions = []
    createSourceModal.sourceError = toErrorMessage(cause, 'Failed to load Azure DevOps sources.')
  } finally {
    if (
      requestId === createSourceSourcesRequestId &&
      createSourceModal.organizationScopeId === scopeId &&
      createSourceModal.projectId === projectId &&
      createSourceModal.sourceKind === sourceKind
    ) {
      createSourceModal.loadingSourceOptions = false
    }
  }
}

async function loadCreateSourceBranches(
  scopeId: string,
  projectId: string,
  sourceKind: ProCursorSourceKind,
  sourceOption: AdoSourceOptionDto,
) {
  const canonicalSourceRef = sourceOption.canonicalSourceRef
  const provider = canonicalSourceRef?.provider?.trim()
  const value = canonicalSourceRef?.value?.trim()
  if (!provider || !value) {
    createSourceModal.branchError = 'The selected source is missing its canonical reference.'
    return
  }

  const requestId = ++createSourceBranchesRequestId
  createSourceModal.loadingBranches = true
  createSourceModal.branchError = ''

  try {
    const branchOptions = sortDiscoveredBranches(
      await listAdoBranches(props.clientId, scopeId, projectId, sourceKind as AdoSourceKind, {
        provider,
        value,
      }),
    )

    if (
      requestId !== createSourceBranchesRequestId ||
      createSourceModal.organizationScopeId !== scopeId ||
      createSourceModal.projectId !== projectId ||
      createSourceModal.sourceKind !== sourceKind ||
      createSourceModal.selectedSourceKey !== sourceOptionKey(sourceOption)
    ) {
      return
    }

    createSourceModal.branchOptions = branchOptions
    const defaultBranch = branchOptions.find((branch) => branch.isDefault)?.branchName || branchOptions[0]?.branchName || ''
    createSourceModal.defaultBranch = defaultBranch
    createSourceModal.initialBranchName = defaultBranch
  } catch (cause) {
    if (
      requestId !== createSourceBranchesRequestId ||
      createSourceModal.organizationScopeId !== scopeId ||
      createSourceModal.projectId !== projectId ||
      createSourceModal.sourceKind !== sourceKind ||
      createSourceModal.selectedSourceKey !== sourceOptionKey(sourceOption)
    ) {
      return
    }

    createSourceModal.branchOptions = []
    createSourceModal.branchError = toErrorMessage(cause, 'Failed to load Azure DevOps branches.')
    createSourceModal.defaultBranch = ''
    createSourceModal.initialBranchName = ''
  } finally {
    if (
      requestId === createSourceBranchesRequestId &&
      createSourceModal.organizationScopeId === scopeId &&
      createSourceModal.projectId === projectId &&
      createSourceModal.sourceKind === sourceKind &&
      createSourceModal.selectedSourceKey === sourceOptionKey(sourceOption)
    ) {
      createSourceModal.loadingBranches = false
    }
  }
}

function resetCreateSourceModal() {
  createSourceScopesRequestId += 1
  createSourceProjectsRequestId += 1
  createSourceSourcesRequestId += 1
  createSourceBranchesRequestId += 1
  createSourceModal.saving = false
  createSourceModal.error = ''
  createSourceModal.displayName = ''
  createSourceModal.sourceKind = 'repository'
  createSourceModal.organizationScopeId = ''
  createSourceModal.organizationScopes = []
  createSourceModal.loadingScopes = false
  createSourceModal.scopeError = ''
  createSourceModal.projectId = ''
  createSourceModal.projects = []
  createSourceModal.loadingProjects = false
  createSourceModal.projectError = ''
  createSourceModal.selectedSourceKey = ''
  createSourceModal.sourceOptions = []
  createSourceModal.loadingSourceOptions = false
  createSourceModal.sourceError = ''
  createSourceModal.branchOptions = []
  createSourceModal.loadingBranches = false
  createSourceModal.branchError = ''
  createSourceModal.defaultBranch = ''
  createSourceModal.rootPath = ''
  createSourceModal.symbolMode = 'auto'
  createSourceModal.initialBranchName = ''
  createSourceModal.initialRefreshTriggerMode = 'branchUpdate'
  createSourceModal.initialMiniIndexEnabled = true
}

function openCreateSourceModal() {
  resetCreateSourceModal()
  createSourceModal.open = true
  void loadCreateSourceOrganizationScopes()
}

function handleCreateSourceOrganizationScopeChange() {
  createSourceModal.error = ''
  clearCreateSourceProjects()

  if (!createSourceModal.organizationScopeId) {
    return
  }

  void loadCreateSourceProjects(createSourceModal.organizationScopeId)
}

function handleCreateSourceProjectChange() {
  createSourceModal.error = ''
  clearCreateSourceSources()

  if (!createSourceModal.organizationScopeId || !createSourceModal.projectId) {
    return
  }

  void loadCreateSourceOptions(
    createSourceModal.organizationScopeId,
    createSourceModal.projectId,
    createSourceModal.sourceKind,
  )
}

function handleCreateSourceKindChange() {
  createSourceModal.error = ''
  clearCreateSourceSources()

  if (!createSourceModal.organizationScopeId || !createSourceModal.projectId) {
    return
  }

  void loadCreateSourceOptions(
    createSourceModal.organizationScopeId,
    createSourceModal.projectId,
    createSourceModal.sourceKind,
  )
}

function handleCreateSourceSelectionChange() {
  createSourceModal.error = ''
  clearCreateSourceBranches()

  const sourceOption = selectedSourceOption.value
  if (!sourceOption || !createSourceModal.organizationScopeId || !createSourceModal.projectId) {
    return
  }

  if (!createSourceModal.displayName.trim()) {
    createSourceModal.displayName = sourceOption.displayName || createSourceModal.displayName
  }

  void loadCreateSourceBranches(
    createSourceModal.organizationScopeId,
    createSourceModal.projectId,
    createSourceModal.sourceKind,
    sourceOption,
  )
}

function handleDefaultBranchChange() {
  if (!createSourceModal.initialBranchName) {
    createSourceModal.initialBranchName = createSourceModal.defaultBranch
  }
}

async function handleCreateSource() {
  createSourceModal.error = ''

  const displayName = createSourceModal.displayName.trim()
  const organizationScope = selectedOrganizationScope.value
  const sourceOption = selectedSourceOption.value
  const canonicalSourceRef = sourceOption?.canonicalSourceRef
  const projectId = createSourceModal.projectId.trim()
  const defaultBranch = createSourceModal.defaultBranch.trim()
  const trackedBranchName = (createSourceModal.initialBranchName.trim() || defaultBranch).trim()

  if (
    !displayName ||
    !organizationScope?.id ||
    !projectId ||
    !canonicalSourceRef?.provider ||
    !canonicalSourceRef.value ||
    !defaultBranch ||
    !trackedBranchName
  ) {
    createSourceModal.error = 'Display name, organization, project, source, and branch selections are required.'
    return
  }

  const request: ProCursorKnowledgeSourceRequest = {
    displayName,
    sourceKind: createSourceModal.sourceKind,
    organizationUrl: organizationScope.organizationUrl ?? null,
    projectId,
    repositoryId: canonicalSourceRef.value,
    defaultBranch,
    rootPath: trimOptional(createSourceModal.rootPath),
    symbolMode: createSourceModal.symbolMode,
    trackedBranches: [
      {
        branchName: trackedBranchName,
        refreshTriggerMode: createSourceModal.initialRefreshTriggerMode,
        miniIndexEnabled: createSourceModal.initialMiniIndexEnabled,
      },
    ],
    organizationScopeId: organizationScope.id,
    canonicalSourceRef,
    sourceDisplayName: sourceOption?.displayName || canonicalSourceRef.value,
  }

  createSourceModal.saving = true
  try {
    await createProCursorSource(props.clientId, request)
    createSourceModal.open = false
    resetCreateSourceModal()
    notify('ProCursor source created.')
    await loadSources()
  } catch (cause) {
    createSourceModal.error = toErrorMessage(cause, 'Failed to create ProCursor source.')
  } finally {
    createSourceModal.saving = false
  }
}

function resetCreateBranchModal() {
  createBranchModal.saving = false
  createBranchModal.error = ''
  createBranchModal.sourceId = ''
  createBranchModal.sourceName = ''
  createBranchModal.branchName = ''
  createBranchModal.refreshTriggerMode = 'branchUpdate'
  createBranchModal.miniIndexEnabled = true
}

function openCreateBranchModal(source: ProCursorKnowledgeSourceDto) {
  if (!source.sourceId) {
    notify('Source identifier is missing.', 'error')
    return
  }

  resetCreateBranchModal()
  createBranchModal.sourceId = source.sourceId
  createBranchModal.sourceName = source.displayName || 'this source'
  createBranchModal.branchName = source.defaultBranch || 'main'
  createBranchModal.open = true
}

async function handleCreateBranch() {
  createBranchModal.error = ''

  const branchName = createBranchModal.branchName.trim()
  if (!createBranchModal.sourceId || !branchName) {
    createBranchModal.error = 'Branch name is required.'
    return
  }

  createBranchModal.saving = true
  try {
    await createProCursorTrackedBranch(props.clientId, createBranchModal.sourceId, {
      branchName,
      refreshTriggerMode: createBranchModal.refreshTriggerMode,
      miniIndexEnabled: createBranchModal.miniIndexEnabled,
    })

    createBranchModal.open = false
    notify('Tracked branch added.')
    await loadBranches(createBranchModal.sourceId)
    resetCreateBranchModal()
  } catch (cause) {
    createBranchModal.error = toErrorMessage(cause, 'Failed to add tracked branch.')
  } finally {
    createBranchModal.saving = false
  }
}

function resetEditBranchModal() {
  editBranchModal.saving = false
  editBranchModal.error = ''
  editBranchModal.sourceId = ''
  editBranchModal.sourceName = ''
  editBranchModal.branchId = ''
  editBranchModal.branchName = ''
  editBranchModal.refreshTriggerMode = 'branchUpdate'
  editBranchModal.miniIndexEnabled = true
  editBranchModal.isEnabled = true
}

function openEditBranchModal(source: ProCursorKnowledgeSourceDto, branch: ProCursorTrackedBranchDto) {
  if (!source.sourceId || !branch.branchId) {
    notify('Branch identifier is missing.', 'error')
    return
  }

  resetEditBranchModal()
  editBranchModal.sourceId = source.sourceId
  editBranchModal.sourceName = source.displayName || 'this source'
  editBranchModal.branchId = branch.branchId
  editBranchModal.branchName = branch.branchName || 'Unnamed branch'
  editBranchModal.refreshTriggerMode = branch.refreshTriggerMode || 'branchUpdate'
  editBranchModal.miniIndexEnabled = Boolean(branch.miniIndexEnabled)
  editBranchModal.isEnabled = Boolean(branch.isEnabled)
  editBranchModal.open = true
}

async function handleSaveBranch() {
  editBranchModal.error = ''

  if (!editBranchModal.sourceId || !editBranchModal.branchId) {
    editBranchModal.error = 'Branch identifier is missing.'
    return
  }

  editBranchModal.saving = true
  try {
    await updateProCursorTrackedBranch(
      props.clientId,
      editBranchModal.sourceId,
      editBranchModal.branchId,
      {
        refreshTriggerMode: editBranchModal.refreshTriggerMode,
        miniIndexEnabled: editBranchModal.miniIndexEnabled,
        isEnabled: editBranchModal.isEnabled,
      },
    )

    editBranchModal.open = false
    notify('Tracked branch updated.')
    await loadBranches(editBranchModal.sourceId)
    resetEditBranchModal()
  } catch (cause) {
    editBranchModal.error = toErrorMessage(cause, 'Failed to update tracked branch.')
  } finally {
    editBranchModal.saving = false
  }
}

function openDeleteBranchDialog(source: ProCursorKnowledgeSourceDto, branch: ProCursorTrackedBranchDto) {
  if (!source.sourceId || !branch.branchId) {
    notify('Branch identifier is missing.', 'error')
    return
  }

  deleteBranchTarget.value = {
    sourceId: source.sourceId,
    sourceName: source.displayName || 'this source',
    branchId: branch.branchId,
    branchName: branch.branchName || 'this branch',
  }
}

async function confirmDeleteBranch() {
  const target = deleteBranchTarget.value
  deleteBranchTarget.value = null

  if (!target) {
    return
  }

  try {
    await deleteProCursorTrackedBranch(props.clientId, target.sourceId, target.branchId)
    notify('Tracked branch removed.')
    await loadBranches(target.sourceId)
  } catch (cause) {
    notify(toErrorMessage(cause, 'Failed to remove tracked branch.'), 'error')
  }
}

async function queueSourceRefresh(source: ProCursorKnowledgeSourceDto) {
  if (!source.sourceId) {
    notify('Source identifier is missing.', 'error')
    return
  }

  const refreshKey = refreshKeyForSource(source.sourceId)
  setRefreshing(refreshKey, true)
  try {
    const job = await queueProCursorRefresh(props.clientId, source.sourceId, { jobKind: 'refresh' })
    notify(`Refresh queued for ${job.branchName || source.defaultBranch || source.displayName || 'source'}.`)
  } catch (cause) {
    notify(toErrorMessage(cause, 'Failed to queue refresh.'), 'error')
  } finally {
    setRefreshing(refreshKey, false)
  }
}

async function queueBranchRefresh(source: ProCursorKnowledgeSourceDto, branch: ProCursorTrackedBranchDto) {
  if (!source.sourceId || !branch.branchId) {
    notify('Branch identifier is missing.', 'error')
    return
  }

  const refreshKey = refreshKeyForBranch(branch.branchId)
  setRefreshing(refreshKey, true)
  try {
    await queueProCursorRefresh(props.clientId, source.sourceId, {
      trackedBranchId: branch.branchId,
      jobKind: 'refresh',
    })

    notify(`Refresh queued for ${branch.branchName || 'branch'}.`)
  } catch (cause) {
    notify(toErrorMessage(cause, 'Failed to queue branch refresh.'), 'error')
  } finally {
    setRefreshing(refreshKey, false)
  }
}

function formatNumber(value?: number | null): string {
  return new Intl.NumberFormat('en-US').format(value ?? 0)
}

function formatUsd(value?: number | null): string {
  if (value == null) {
    return 'Cost n/a'
  }

  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value)
}

function formatBucketDate(value?: string | null): string {
  if (!value) {
    return 'Unknown bucket'
  }

  const parsed = new Date(`${value}T00:00:00`)
  if (Number.isNaN(parsed.getTime())) {
    return value
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
  }).format(parsed)
}

function formatSourceKind(kind?: ProCursorSourceKind): string {
  return kind === 'adoWiki' ? 'ADO Wiki' : 'Repository'
}

function formatTriggerMode(mode?: ProCursorRefreshTriggerMode): string {
  return mode === 'branchUpdate' ? 'On branch update' : 'Manual only'
}

function formatSymbolMode(mode?: string | null): string {
  if (!mode || mode === 'auto') {
    return 'Auto'
  }

  if (mode === 'text_only') {
    return 'Text only'
  }

  return mode
}

function formatStatus(value?: string | null): string {
  if (!value) {
    return 'Unknown'
  }

  return value
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/[_-]/g, ' ')
    .replace(/\b\w/g, (char) => char.toUpperCase())
}

function statusChipClass(value?: string | null): string {
  const normalized = (value ?? '').toLowerCase()

  if (!normalized) {
    return 'chip-muted'
  }

  if (normalized.includes('fresh') || normalized.includes('enabled') || normalized.includes('ready') || normalized.includes('complete')) {
    return 'chip-success'
  }

  if (normalized.includes('stale') || normalized.includes('pending') || normalized.includes('processing') || normalized.includes('queue') || normalized.includes('lag')) {
    return 'chip-warning'
  }

  if (normalized.includes('fail') || normalized.includes('error') || normalized.includes('disabled') || normalized.includes('cancel')) {
    return 'chip-danger'
  }

  return 'chip-muted'
}

function formatSha(value?: string | null): string {
  return value ? value.slice(0, 10) : 'n/a'
}

function formatDate(value?: string | null): string {
  if (!value) {
    return 'Never'
  }

  const parsed = new Date(value)
  if (Number.isNaN(parsed.getTime())) {
    return value
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: 'medium',
    timeStyle: 'short',
  }).format(parsed)
}
</script>

<style scoped>
.client-procursor-tab {
  min-height: 20rem;
}

.procursor-header-left {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  flex-wrap: wrap;
}

.procursor-subtitle {
  width: 100%;
  color: var(--color-text-muted);
  font-size: 0.8rem;
  margin: 0.15rem 0 0;
}

.sources-stack {
  width: 100%;
  display: grid;
  gap: 1rem;
}

.source-card {
  width: 100%;
  border: 1px solid var(--color-border);
  background: linear-gradient(180deg, rgba(255, 255, 255, 0.03), rgba(255, 255, 255, 0.015));
  padding: 1rem;
  margin-top: 0.5rem;
}

.source-card-header {
  width: calc(100% - 2rem);
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  align-items: flex-start;
  margin-bottom: 1rem;
}

.source-card-title-group {
  min-width: 0;
}

.source-title-row {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.source-title-row h4 {
  margin: 0;
  font-size: 1rem;
}

.source-card-subtitle {
  margin: 0.35rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.85rem;
  overflow-wrap: anywhere;
}

.source-card-actions {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.source-meta-grid {
  width: calc(100% - 1.5rem);
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 0.75rem;
}

.meta-item {
  border: 1px solid rgba(255, 255, 255, 0.06);
  border-radius: 0.75rem;
  background: rgba(255, 255, 255, 0.025);
  padding: 0.8rem 0.9rem;
}

.meta-key {
  display: block;
  font-size: 0.74rem;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--color-text-muted);
  margin-bottom: 0.35rem;
}

.meta-value {
  display: block;
  font-weight: 600;
  overflow-wrap: anywhere;
}

.source-note {
  margin-top: 0.9rem;
  color: var(--color-text-muted);
  font-size: 0.84rem;
}

.usage-drilldown-panel {
  margin-top: 1rem;
  padding: 1rem;
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 0.9rem;
  background: rgba(15, 23, 42, 0.2);
}

.usage-drilldown-header,
.usage-panel-header {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  align-items: flex-start;
}

.usage-drilldown-header {
  margin-bottom: 0.9rem;
}

.usage-drilldown-header h5,
.usage-panel-header h6 {
  margin: 0;
}

.usage-drilldown-header p,
.usage-panel-header p,
.model-usage-row p,
.snapshot-row p {
  margin: 0.25rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.8rem;
}

.usage-drilldown-actions {
  display: flex;
  gap: 0.5rem;
  flex-wrap: wrap;
  justify-content: flex-end;
}

.usage-drilldown-content {
  display: grid;
  gap: 1rem;
}

.usage-stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
  gap: 0.75rem;
}

.usage-stat-card,
.usage-panel {
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 0.85rem;
  background: rgba(255, 255, 255, 0.02);
}

.usage-stat-card {
  padding: 0.9rem 1rem;
}

.usage-stat-card strong,
.model-usage-side strong {
  display: block;
  font-size: 1.15rem;
}

.usage-stat-label {
  display: block;
  margin-bottom: 0.35rem;
  color: var(--color-text-muted);
  font-size: 0.72rem;
  text-transform: uppercase;
  letter-spacing: 0.06em;
}

.usage-callout {
  display: flex;
  align-items: flex-start;
  gap: 0.65rem;
  padding: 0.85rem 1rem;
  border-radius: 0.8rem;
  border: 1px solid rgba(245, 158, 11, 0.2);
  background: rgba(245, 158, 11, 0.08);
}

.usage-callout p {
  margin: 0;
}

.usage-detail-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(260px, 1fr));
  gap: 1rem;
}

.usage-panel {
  padding: 1rem;
}

.model-usage-list,
.snapshot-list {
  list-style: none;
  margin: 0.85rem 0 0;
  padding: 0;
  display: grid;
  gap: 0.65rem;
}

.model-usage-row,
.snapshot-row {
  display: flex;
  justify-content: space-between;
  gap: 0.75rem;
  align-items: flex-start;
  padding: 0.8rem 0.9rem;
  border-radius: 0.75rem;
  background: rgba(255, 255, 255, 0.025);
}

.model-usage-side {
  text-align: right;
}

.model-usage-side span {
  display: block;
  margin-top: 0.2rem;
  color: var(--color-text-muted);
  font-size: 0.8rem;
}

.branches-panel {
  margin-top: 1rem;
  border-top: 1px solid rgba(255, 255, 255, 0.08);
  padding-top: 1rem;
}

.branches-panel-header {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  align-items: flex-start;
  margin-bottom: 0.85rem;
}

.branches-panel-header h5 {
  margin: 0;
  font-size: 0.95rem;
}

.branches-panel-header p {
  margin: 0.25rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.8rem;
}

.branches-table {
  width: 100%;
  border-collapse: collapse;
}

.branches-table th {
  text-align: left;
  font-size: 0.72rem;
  text-transform: uppercase;
  letter-spacing: 0.06em;
  color: var(--color-text-muted);
  padding: 0.8rem 0.75rem;
  border-bottom: 1px solid var(--color-border);
}

.branches-table td {
  padding: 0.85rem 0.75rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.06);
  vertical-align: middle;
}

.branches-table tr:last-child td {
  border-bottom: none;
}

.branch-name-cell {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  flex-wrap: wrap;
}

.branch-actions-cell {
  width: 1%;
  white-space: nowrap;
}

.branch-actions {
  display: flex;
  gap: 0.5rem;
  justify-content: flex-end;
}

.loading-state,
.error-state,
.empty-state {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 4rem 2rem;
  text-align: center;
  gap: 0.75rem;
}

.inline-state,
.inline-empty-state {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  border: 1px dashed rgba(255, 255, 255, 0.12);
  border-radius: 0.75rem;
  padding: 0.9rem 1rem;
  color: var(--color-text-muted);
}

.inline-state-error {
  justify-content: space-between;
  flex-wrap: wrap;
}

.inline-state-error p {
  margin: 0;
}

.state-orb {
  width: 50px;
  height: 50px;
}

.state-orb-inline {
  width: 28px;
  height: 28px;
}

.error-icon {
  font-size: 3rem;
}

.empty-icon {
  font-size: 4rem;
  opacity: 0.4;
}

.sha-value {
  display: inline-flex;
  align-items: center;
  min-height: 1.8rem;
  padding: 0.2rem 0.5rem;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  font-size: 0.8rem;
}

.modal-form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 1rem;
}

.modal-form-grid-full {
  grid-column: 1 / -1;
}

.checkbox-field {
  display: flex;
  align-items: center;
  gap: 0.75rem;
  min-height: 100%;
  padding: 0.85rem 1rem;
  border: 1px solid var(--color-border);
  border-radius: 0.75rem;
  background: rgba(255, 255, 255, 0.02);
  color: var(--color-text);
}

.checkbox-field input {
  width: auto;
  margin: 0;
}

.checkbox-field span {
  display: block;
  font-size: 0.92rem;
}

.field-help {
  margin: 0.4rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.78rem;
}

.modal-hint {
  margin: 1rem 0 0;
  color: var(--color-text-muted);
  font-size: 0.85rem;
}

.readonly-value {
  display: flex;
  align-items: center;
  min-height: 3rem;
  padding: 0.75rem 1rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  background: rgba(255, 255, 255, 0.04);
}

.chip-warning {
  background: rgba(245, 158, 11, 0.14);
  color: #f59e0b;
}

.chip-danger {
  background: rgba(239, 68, 68, 0.14);
  color: var(--color-danger);
}

@media (max-width: 900px) {
  .source-card-header,
  .branches-panel-header {
    flex-direction: column;
  }

  .usage-drilldown-header,
  .usage-panel-header,
  .model-usage-row,
  .snapshot-row {
    flex-direction: column;
  }

  .model-usage-side {
    text-align: left;
  }

  .source-card-actions,
  .branch-actions {
    width: 100%;
    justify-content: flex-start;
  }

  .branches-table,
  .branches-table thead,
  .branches-table tbody,
  .branches-table tr,
  .branches-table th,
  .branches-table td {
    display: block;
  }

  .branches-table thead {
    display: none;
  }

  .branches-table tr {
    border: 1px solid rgba(255, 255, 255, 0.08);
    border-radius: 0.75rem;
    padding: 0.75rem;
    margin-bottom: 0.75rem;
  }

  .branches-table td {
    padding: 0.35rem 0;
    border: none;
  }

  .branch-actions-cell {
    width: auto;
  }

  .modal-form-grid {
    grid-template-columns: 1fr;
  }
}
</style>
