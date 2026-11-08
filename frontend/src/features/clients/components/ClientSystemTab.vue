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
                        <label class="checkbox-field" for="reviewEveryIncrementEnabled">
                            <input id="reviewEveryIncrementEnabled"
                                v-model="editedReviewEveryIncrementEnabled"
                                name="reviewEveryIncrementEnabled" type="checkbox" />
                            <strong>Review every pushed update</strong>
                            <p class="muted review-publication-copy">
                                When this is off, ProPR reviews a pull request once, at the first revision
                                it sees, and later pushes wait for a requested review. Turn it on to start
                                a new review for every pushed update, which cancels any review still running
                                for an older revision. Off by default.
                            </p>
                        </label>
                    </div>
                </div>
                <div class="inline-field-row review-publication-row">
                    <div class="form-field flex-1 review-publication-field">
                        <label for="commentResolutionBehavior">Resolving comment threads</label>
                        <select id="commentResolutionBehavior"
                            v-model="editedCommentResolutionBehavior"
                            name="commentResolutionBehavior">
                            <option v-for="option in COMMENT_RESOLUTION_OPTIONS" :key="option.value" :value="option.value">
                                {{ option.label }}
                            </option>
                        </select>
                        <p class="muted review-publication-copy">
                            What ProPR does with its own comment threads once the developer has acted on them.
                            This is checked on every pushed update, whether or not the files are reviewed again.
                            <strong>Resolve quietly</strong> (default) closes a thread whose finding was fixed
                            without adding a comment. <strong>Resolve with an explanation</strong> posts why it
                            closed first. <strong>Leave threads alone</strong> turns the whole thing off, so
                            nothing is resolved and nothing is answered. Either resolving option still answers a
                            developer who asks a question in a thread. Azure DevOps, GitHub and GitLab only; on
                            Forgejo there is no thread to address and the setting does nothing.
                        </p>
                    </div>
                </div>
                <div class="inline-field-row review-publication-row">
                    <div class="form-field flex-1 review-publication-field">
                        <label for="outputLanguage">Output language</label>
                        <input id="outputLanguage"
                            v-model="editedOutputLanguage"
                            name="outputLanguage" type="text" maxlength="16"
                            placeholder="en" />
                        <p class="muted review-publication-copy">
                            The language the review writes in, as a language tag such as
                            <strong>en</strong>, <strong>de</strong> or <strong>pt-BR</strong>. It applies to
                            finding messages, the pull-request summary, thread replies and mention answers, so
                            every part of a review reads the same way. Defaults to <strong>en</strong>. Severity
                            labels and the fixed headings ProPR adds around the text stay English.
                        </p>
                    </div>
                </div>
                <div class="inline-field-row review-publication-row">
                    <div class="form-field flex-1 review-publication-field">
                        <label for="baselineReasoningLevel">Baseline reasoning level</label>
                        <select id="baselineReasoningLevel"
                            v-model="baselineReasoningLevelSelection"
                            name="baselineReasoningLevel">
                            <option value="">Not set</option>
                            <option v-for="level in baselineReasoningLevelOptions" :key="level" :value="level">
                                {{ level }}
                            </option>
                        </select>
                        <p class="muted review-publication-copy">
                            The level of the baseline (tier) review pass when neither the purpose assignment nor the
                            logical model of the file's tier sets one. A model that does not accept the level
                            receives the nearest lower level it accepts. With <strong>Not set</strong> (default), the
                            baseline pass takes the default level of the model, or the provider's default when the
                            model has none.
                            The level of each additional pass is set in the AI Providers tab under "Review passes".
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

        <ClientPostConfigSection />

        <!-- Danger Zone -->
        <div class="danger-zone-card">
            <div class="danger-zone-info">
                <i class="fi fi-rr-triangle-warning"></i>
                <div>
                    <h3>Danger Zone</h3>
                    <p>Deleting this client is permanent and cannot be undone.</p>
                </div>
            </div>
            <button class="btn-danger" :disabled="deletionWaitSeconds !== null" @click="showDeleteDialog = true">
                <i class="fi fi-rr-trash"></i> Delete Client
            </button>
            <ConfirmDialog :open="showDeleteDialog" message="Delete this client permanently?"
                @cancel="showDeleteDialog = false" @confirm="handleDelete" />
        </div>
        <p v-if="deletionWaitSeconds !== null" class="deletion-status" role="status">
            ProPR has deactivated this client and stopped its work. ProPR waits {{ deletionWaitSeconds }} seconds
            for running work to end and then continues the deletion. Keep this page open until then.
        </p>
        <span v-if="deleteError" class="error" role="alert">{{ deleteError }}</span>
    </div>
</template>

<script lang="ts" setup>
import { computed, inject } from "vue";
import ClientOverview from "@/features/clients/components/ClientOverview.vue";
import { standardReasoningLevels } from "@/features/clients/components/aiConnectionsFormatters";
import ClientPostConfigSection from "@/features/clients/components/ClientPostConfigSection.vue";
import ConfirmDialog from "@/components/dialogs/ConfirmDialog.vue";
import { ClientDetailVmKey } from "@/features/clients/view-models/useClientDetailViewModel";

const vm = inject(ClientDetailVmKey)!;
const {
    clientId,
    client,
    saving,
    saveError,
    showDeleteDialog,
    deletionWaitSeconds,
    deleteError,
    editedDisplayName,
    editedDefaultReviewPipelineProfileId,
    editedScmCommentPostingEnabled,
    editedEnableMultiPassUnion,
    editedReviewEveryIncrementEnabled,
    editedCommentResolutionBehavior,
    editedIncludeLinkedItemsInContext,
    editedOutputLanguage,
    editedBaselineReasoningLevel,
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

// What ProPR does with its own threads once a finding has been acted on. Mirrors the backend
// CommentResolutionBehavior enum. Named for what a reader sees happen rather than after the enum:
// "silent" describes the absence of a comment, which is not what someone scanning a settings page is
// looking for.
const COMMENT_RESOLUTION_OPTIONS: { value: string; label: string }[] = [
    { value: "silent", label: "Resolve quietly (default)" },
    { value: "withReply", label: "Resolve with an explanation" },
    { value: "disabled", label: "Leave threads alone" },
];

// The baseline level applies to the models of every tier, so the select offers the shared level names. A stored
// name outside them, set through the API, stays selectable so the select shows it.
const baselineReasoningLevelOptions = computed<string[]>(() => {
    const stored = editedBaselineReasoningLevel.value;
    const names: string[] = [...standardReasoningLevels];
    return stored && !names.includes(stored) ? [...names, stored] : names;
});

// An option cannot hold null, so the select holds an empty value for "Not set".
const baselineReasoningLevelSelection = computed<string>({
    get: () => editedBaselineReasoningLevel.value ?? "",
    set: (value) => {
        editedBaselineReasoningLevel.value = value === "" ? null : value;
    },
});
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

.deletion-status {
    color: var(--color-text-muted);
    font-size: 0.875rem;
    margin: 0.5rem 0;
}
</style>
