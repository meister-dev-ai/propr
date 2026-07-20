<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div v-if="client" class="client-system-tab">
        <!-- Section 1: Client Identity -->
        <div class="section-card">
            <div class="section-card-header">
                <h3>Client Identity</h3>
                <div class="section-card-header-actions">
                    <span :class="client.isActive ? 'chip chip-success' : 'chip chip-muted'">
                        <i :class="client.isActive ? 'fi fi-rr-check-circle' : 'fi fi-rr-ban'"></i>
                        {{ client.isActive ? "Active" : "Inactive" }}
                    </span>
                    <button :disabled="saving" :class="client.isActive
                            ? 'btn-danger toggle-status-btn'
                            : 'btn-primary toggle-status-btn'
                        " @click="toggleStatus">
                        {{ client.isActive ? "Disable" : "Enable" }}
                    </button>
                </div>
            </div>
            <div class="section-card-body section-card-body--compact">
                <div class="inline-field-row">
                    <div class="form-field flex-1">
                        <label for="displayName">Display Name</label>
                        <input id="displayName" v-model="editedDisplayName" name="displayName" type="text" />
                    </div>
                    <button :disabled="saving" class="btn-primary inline-save-btn save-btn" @click="saveDisplayName">
                        Save
                    </button>
                </div>
                <span v-if="saveError" class="error">{{ saveError }}</span>
            </div>
        </div>

        <div class="section-card">
            <div class="section-card-header">
                <h3>Overview</h3>
            </div>
            <div class="section-card-body section-card-body--compact">
                <ClientOverview :client-id="clientId" @navigate="handleOverviewNavigate" />
            </div>
        </div>

        <div class="section-card">
            <div class="section-card-header">
                <h3>Advanced Settings</h3>
            </div>
            <div class="section-card-body section-card-body--compact">
                <div class="inline-field-row review-publication-row">
                    <div class="form-field flex-1 review-publication-field">
                        <label for="defaultReviewPipelineProfileId">Review Aggressiveness</label>
                        <select
                            id="defaultReviewPipelineProfileId"
                            v-model="editedDefaultReviewPipelineProfileId"
                            name="defaultReviewPipelineProfileId"
                        >
                            <option
                                v-for="profile in reviewProfiles"
                                :key="profile.profileId"
                                :value="profile.profileId"
                            >
                                {{ profile.displayName }}
                            </option>
                        </select>
                        <p class="muted review-publication-copy">
                            Active source:
                            <strong>{{ clientReviewProfile?.source === 'clientDefault' ? 'Client default' : 'System default' }}</strong>
                            ({{ clientReviewProfile?.defaultReviewPipelineProfileId ?? editedDefaultReviewPipelineProfileId }})
                        </p>
                    </div>
                    <button :disabled="!isReviewProfileButtonEnabled()"
                        class="btn-primary inline-save-btn review-profile-save-btn"
                        @click="saveReviewProfile">
                        Save
                    </button>
                </div>
                <div class="inline-field-row review-publication-row">
                    <div class="form-field flex-1 review-publication-field">
                        <label class="checkbox-field" for="scmCommentPostingEnabled">
                            <input id="scmCommentPostingEnabled" v-model="editedScmCommentPostingEnabled"
                                name="scmCommentPostingEnabled" type="checkbox" />
                            <strong>Post review comments to SCM</strong>
                            <p class="muted review-publication-copy">
                                Disable this to keep reviews running in the background
                                without posting new pull request comments.
                            </p>
                        </label>
                    </div>
                </div>
                <div class="inline-field-row review-publication-row">
                    <div class="form-field flex-1 review-publication-field">
                        <label class="checkbox-field" for="enableEvidenceBackedVerification">
                            <input id="enableEvidenceBackedVerification"
                                v-model="editedEnableEvidenceBackedVerification"
                                name="enableEvidenceBackedVerification" type="checkbox" />
                            <strong>Enable evidence-backed verification</strong>
                            <p class="muted review-publication-copy">
                                Enable this to let the reviewer read anchor code and confirm candidate
                                findings the deterministic verifier would otherwise withhold.
                            </p>
                        </label>
                    </div>
                </div>
                <div class="inline-field-row review-publication-row">
                    <div class="form-field flex-1 review-publication-field">
                        <label class="checkbox-field" for="enableLanguageRobustScreening">
                            <input id="enableLanguageRobustScreening"
                                v-model="editedEnableLanguageRobustScreening"
                                name="enableLanguageRobustScreening" type="checkbox" />
                            <strong>Enable language-robust comment screening</strong>
                            <p class="muted review-publication-copy">
                                Screen hedged or vague review comments by meaning (multilingual embeddings)
                                rather than English phrase lists, folding low-confidence comments into the
                                summary instead of posting them as threads. Off by default.
                            </p>
                        </label>
                    </div>
                </div>
                <div class="inline-field-row review-publication-row">
                    <div class="form-field flex-1 review-publication-field">
                        <label class="checkbox-field" for="includeLinkedItemsInContext">
                            <input id="includeLinkedItemsInContext"
                                v-model="editedIncludeLinkedItemsInContext"
                                name="includeLinkedItemsInContext" type="checkbox" />
                            <strong>Include linked work items and issues</strong>
                            <p class="muted review-publication-copy">
                                Fetch the work items (Azure DevOps) or issues (GitHub, GitLab, Forgejo)
                                linked to a pull request and include their content in the review context,
                                so the review can judge the change against its intended direction. On by default.
                            </p>
                        </label>
                    </div>
                </div>
                <div class="inline-field-row review-publication-row">
                    <div class="form-field flex-1 review-publication-field">
                        <label class="checkbox-field" for="enableMultiPassUnion">
                            <input id="enableMultiPassUnion"
                                v-model="editedEnableMultiPassUnion"
                                name="enableMultiPassUnion" type="checkbox" />
                            <strong>Enable multi-pass union</strong>
                            <p class="muted review-publication-copy">
                                Enable this to review higher-complexity files across multiple independent
                                passes and union their findings before deduplication. Configure the
                                per-pass models in the AI Providers tab under "Review passes".
                            </p>
                        </label>
                    </div>
                </div>
                <div class="inline-field-row review-publication-row">
                    <div class="form-field flex-1 review-publication-field">
                        <label for="baselineReasoningEffort">Baseline reasoning effort</label>
                        <select id="baselineReasoningEffort"
                            v-model="editedBaselineReasoningEffort"
                            name="baselineReasoningEffort">
                            <option v-for="option in REASONING_EFFORT_OPTIONS" :key="option.value" :value="option.value">
                                {{ option.label }}
                            </option>
                        </select>
                        <p class="muted review-publication-copy">
                            How much reasoning the model spends on the baseline (tier) review pass.
                            <strong>None</strong> (default) sends no reasoning effort, so behavior and cost
                            are unchanged. Per-additional-pass effort is set in the AI Providers tab under
                            "Review passes".
                        </p>
                    </div>
                    <button :disabled="!isAdvancedSettingsButtonEnabled()"
                        class="btn-primary inline-save-btn scm-advanced-settings-save-btn"
                        @click="saveAdvancedSettings">
                        Save
                    </button>
                </div>
                <span v-if="saveError" class="error">{{ saveError }}</span>
            </div>
        </div>

        <div class="section-card">
            <div class="section-card-header">
                <h3>Budget</h3>
            </div>
            <div class="section-card-body section-card-body--compact">
                <p class="muted budget-intro">
                    Optional USD spend caps. A <strong>soft cap</strong> holds new reviews once the scope's spend
                    reaches it (running reviews still finish); a <strong>hard cap</strong> cuts further model calls
                    mid-review and publishes the findings produced so far. Leave a field blank for no limit. Caps
                    compose most-restrictively across scopes, and a held or stopped review is resumed by restarting it.
                </p>
                <p v-if="!isBudgetingAvailable && budgetingUpgradeMessage" class="muted budget-upgrade">
                    {{ budgetingUpgradeMessage }}
                </p>
                <fieldset class="budget-grid" :disabled="!isBudgetingAvailable">
                    <div class="form-field">
                        <label for="monthlyBudgetSoftCapUsd">Monthly soft cap (USD)</label>
                        <input id="monthlyBudgetSoftCapUsd" v-model="editedMonthlyBudgetSoftCapUsd"
                            name="monthlyBudgetSoftCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                    </div>
                    <div class="form-field">
                        <label for="monthlyBudgetHardCapUsd">Monthly hard cap (USD)</label>
                        <input id="monthlyBudgetHardCapUsd" v-model="editedMonthlyBudgetHardCapUsd"
                            name="monthlyBudgetHardCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                    </div>
                    <div class="form-field">
                        <label for="pullRequestBudgetSoftCapUsd">Per-PR soft cap (USD)</label>
                        <input id="pullRequestBudgetSoftCapUsd" v-model="editedPullRequestBudgetSoftCapUsd"
                            name="pullRequestBudgetSoftCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                    </div>
                    <div class="form-field">
                        <label for="pullRequestBudgetHardCapUsd">Per-PR hard cap (USD)</label>
                        <input id="pullRequestBudgetHardCapUsd" v-model="editedPullRequestBudgetHardCapUsd"
                            name="pullRequestBudgetHardCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                    </div>
                    <div class="form-field">
                        <label for="incrementBudgetHardCapUsd">Per-increment hard cap (USD)</label>
                        <input id="incrementBudgetHardCapUsd" v-model="editedIncrementBudgetHardCapUsd"
                            name="incrementBudgetHardCapUsd" type="number" min="0" step="0.01" placeholder="No limit" />
                        <p class="muted budget-field-note">A single increment is one review job, so it is capped by a hard limit only.</p>
                    </div>
                </fieldset>
                <div class="budget-actions">
                    <button :disabled="!isBudgetingAvailable || !isBudgetButtonEnabled()"
                        class="btn-primary inline-save-btn budget-save-btn"
                        @click="saveBudgetConfig">
                        Save
                    </button>
                </div>
                <span v-if="saveError" class="error">{{ saveError }}</span>
            </div>
        </div>

        <!-- Danger Zone -->
        <div class="danger-zone-card">
            <div class="danger-zone-info">
                <i class="fi fi-rr-triangle-warning"></i>
                <div>
                    <h3>Danger Zone</h3>
                    <p>Deleting this client is permanent and cannot be undone.</p>
                </div>
            </div>
            <button class="btn-danger" @click="showDeleteDialog = true">
                <i class="fi fi-rr-trash"></i> Delete Client
            </button>
            <ConfirmDialog :open="showDeleteDialog" message="Delete this client permanently?"
                @cancel="showDeleteDialog = false" @confirm="handleDelete" />
        </div>
    </div>
</template>

<script lang="ts" setup>
import { inject } from "vue";
import ClientOverview from "@/features/clients/components/ClientOverview.vue";
import ConfirmDialog from "@/components/dialogs/ConfirmDialog.vue";
import { ClientDetailVmKey } from "@/features/clients/view-models/useClientDetailViewModel";

const vm = inject(ClientDetailVmKey)!;
const {
    clientId,
    client,
    saving,
    saveError,
    showDeleteDialog,
    editedDisplayName,
    editedDefaultReviewPipelineProfileId,
    editedScmCommentPostingEnabled,
    editedEnableEvidenceBackedVerification,
    editedEnableMultiPassUnion,
    editedIncludeLinkedItemsInContext,
    editedEnableLanguageRobustScreening,
    editedBaselineReasoningEffort,
    editedMonthlyBudgetSoftCapUsd,
    editedMonthlyBudgetHardCapUsd,
    editedPullRequestBudgetSoftCapUsd,
    editedPullRequestBudgetHardCapUsd,
    editedIncrementBudgetHardCapUsd,
    reviewProfiles,
    clientReviewProfile,
    saveDisplayName,
    toggleStatus,
    saveAdvancedSettings,
    saveReviewProfile,
    saveBudgetConfig,
    isAdvancedSettingsButtonEnabled,
    isBudgetButtonEnabled,
    isBudgetingAvailable,
    budgetingUpgradeMessage,
    isReviewProfileButtonEnabled,
    handleDelete,
    handleOverviewNavigate,
} = vm;

// The reasoning-effort levels offered for the baseline (tier) pass. Mirrors the backend
// ReviewReasoningEffort enum; 'none' (default) sends no effort so behavior/cost stay unchanged.
const REASONING_EFFORT_OPTIONS: { value: string; label: string }[] = [
    { value: "none", label: "None (off)" },
    { value: "low", label: "Low" },
    { value: "medium", label: "Medium" },
    { value: "high", label: "High" },
];
</script>

<style scoped>
.toggle-status-btn {
    font-size: 0.8rem;
    padding: 0.35rem 0.85rem;
}

.section-card-body--compact {
    padding: 1rem 1.25rem;
}

.inline-field-row {
    display: flex;
    align-items: flex-end;
    gap: 0.75rem;
}

.flex-1 {
    flex: 1;
}

.inline-save-btn {
    flex-shrink: 0;
    align-self: flex-end;
    margin-bottom: 0;
}

.checkbox-field {
    display: flex;
    align-items: center;
    gap: 0.55rem;
}

.checkbox-field span {
    font-weight: 500;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}

.budget-intro {
    padding: 0 0 0.85rem;
}

.budget-grid {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 0.85rem 1rem;
    /* Reset the fieldset defaults so the wrapper keeps behaving as a plain grid. */
    border: 0;
    margin: 0;
    padding: 0;
    min-inline-size: 0;
}

.budget-upgrade {
    padding: 0 0 0.6rem;
    color: var(--color-warning);
    font-style: normal;
}

.budget-field-note {
    padding: 0.3rem 0 0;
    font-size: 0.8rem;
}

.budget-actions {
    display: flex;
    justify-content: flex-end;
    margin-top: 0.85rem;
}
</style>
