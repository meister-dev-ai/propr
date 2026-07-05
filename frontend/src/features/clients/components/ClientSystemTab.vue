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
                        <label for="defaultReviewStrategy">Default Review Strategy</label>
                        <select
                            id="defaultReviewStrategy"
                            v-model="editedDefaultReviewStrategy"
                            name="defaultReviewStrategy"
                        >
                            <option value="fileByFile">File-by-File</option>
                            <option value="agenticFileByFile">Agentic File-by-File (Experimental)</option>
                            <option value="prWideAgentic">PR-wide Agentic (Experimental)</option>
                        </select>
                        <p class="muted review-publication-copy">
                            Choose whether new reviews for this client use the classic file-by-file flow,
                            the plan-driven agentic file-by-file flow, or the PR-wide agentic review flow.
                        </p>
                    </div>
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
                        <label class="checkbox-field" for="enableProRV">
                            <input id="enableProRV" v-model="editedEnableProRV" name="enableProRV"
                                type="checkbox" />
                            <strong>Run ProRV verification</strong>
                            <p class="muted review-publication-copy">
                                Disable this to skip ProRV during review generation for this client.
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
                    <button :disabled="!isAdvancedSettingsButtonEnabled()"
                        class="btn-primary inline-save-btn scm-advanced-settings-save-btn"
                        @click="saveAdvancedSettings">
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
    editedDefaultReviewStrategy,
    editedDefaultReviewPipelineProfileId,
    editedScmCommentPostingEnabled,
    editedEnableProRV,
    editedEnableEvidenceBackedVerification,
    editedEnableMultiPassUnion,
    reviewProfiles,
    clientReviewProfile,
    saveDisplayName,
    toggleStatus,
    saveAdvancedSettings,
    saveReviewProfile,
    isAdvancedSettingsButtonEnabled,
    isReviewProfileButtonEnabled,
    handleDelete,
    handleOverviewNavigate,
} = vm;
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
</style>
