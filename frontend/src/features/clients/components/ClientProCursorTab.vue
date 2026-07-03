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
          :key="source.sourceId ?? source.displayName ?? source.repositoryId ?? source.providerProjectKey ?? 'source'"
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
              {{ source.providerScopePath || 'No organization URL' }} / {{ source.providerProjectKey || 'No project' }} / {{ source.sourceDisplayName || source.repositoryId || 'No source selected' }}
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
          <span class="field-caption">Branch Name</span>
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
import ConfirmDialog from '@/components/dialogs/ConfirmDialog.vue'
import ModalDialog from '@/components/dialogs/ModalDialog.vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import {
  formatBranchOptionLabel,
  formatBucketDate,
  formatDate,
  formatNumber,
  formatOrganizationScopeLabel,
  formatSha,
  formatSourceKind,
  formatStatus,
  formatSymbolMode,
  formatTriggerMode,
  formatUsd,
  refreshKeyForBranch,
  refreshKeyForSource,
  sourceOptionKey,
  statusChipClass,
} from './clientProCursorFormatters'
import { useClientProCursorTab } from './useClientProCursorTab'

const props = defineProps<{
  clientId: string
}>()

const {
  sources,
  loading,
  error,
  deleteBranchTarget,
  isTokenUsageReportingEnabled,
  sourceUsagePeriod,
  canManage,
  createSourceModal,
  selectedOrganizationScope,
  selectedSourceOption,
  createBranchModal,
  editBranchModal,
  branchesFor,
  branchLoadingFor,
  branchErrorFor,
  usageFor,
  usageLoadingFor,
  usageErrorFor,
  recentEventsFor,
  recentEventsLoadingFor,
  recentEventsErrorFor,
  recentSnapshotsFor,
  isRefreshing,
  loadSources,
  reloadBranches,
  reloadSourceUsage,
  reloadSourceDrilldown,
  openCreateSourceModal,
  handleCreateSourceOrganizationScopeChange,
  handleCreateSourceProjectChange,
  handleCreateSourceKindChange,
  handleCreateSourceSelectionChange,
  handleDefaultBranchChange,
  handleCreateSource,
  openCreateBranchModal,
  handleCreateBranch,
  openEditBranchModal,
  handleSaveBranch,
  openDeleteBranchDialog,
  confirmDeleteBranch,
  queueSourceRefresh,
  queueBranchRefresh,
} = useClientProCursorTab(props)
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

.premium-unavailable-state {
  padding-block: 3rem;
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
  border-radius: var(--radius-pill);
  background: var(--color-muted-soft);
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

.field-caption {
  display: block;
  font-size: 0.875rem;
  font-weight: 500;
  margin-bottom: 0.5rem;
  color: var(--color-text-muted);
}

.readonly-value {
  display: flex;
  align-items: center;
  min-height: 3rem;
  padding: 0.75rem 1rem;
  border: 1px solid var(--color-border);
  border-radius: var(--radius-md);
  background: rgba(255, 255, 255, 0.04);
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
