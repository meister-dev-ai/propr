<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div v-if="client" class="section-card">
        <div class="section-card-header">
            <h3>Post configuration</h3>
        </div>
        <div class="section-card-body section-card-body--compact">
            <div class="inline-field-row review-publication-row">
                <div class="form-field flex-1 review-publication-field">
                    <label for="minimumSeverityToPost">Minimum severity to post</label>
                    <select id="minimumSeverityToPost" v-model="editedMinimumSeverityToPost"
                        name="minimumSeverityToPost">
                        <option v-for="option in MIN_SEVERITY_OPTIONS" :key="option.value" :value="option.value">
                            {{ option.label }}
                        </option>
                    </select>
                    <p class="muted review-publication-copy">
                        Findings below this severity are not posted to the pull request. They stay visible in the
                        ProPR review — only the pull request comment is suppressed. Severity order (high to low):
                        Error, Warning, Suggestion, Info.
                    </p>
                </div>
            </div>

            <div class="inline-field-row review-publication-row">
                <div class="form-field flex-1 review-publication-field">
                    <span class="post-config-group-label">Auto-resolve severities</span>
                    <label v-for="option in SEVERITY_OPTIONS" :key="option.value"
                        class="checkbox-field auto-resolve-option" :for="`autoResolve-${option.value}`">
                        <input :id="`autoResolve-${option.value}`" v-model="editedAutoResolveSeverities"
                            :value="option.value" name="autoResolveSeverities" type="checkbox" />
                        <strong>{{ option.label }}</strong>
                    </label>
                    <p class="muted review-publication-copy">
                        Comments of the selected severities are posted and then immediately resolved, with a note
                        that they were auto-resolved by ProPR. Use this to surface correct-but-low-priority
                        findings without adding manual resolution work.
                    </p>
                </div>
            </div>

            <div class="inline-field-row review-publication-row">
                <div class="form-field flex-1 review-publication-field">
                    <label class="checkbox-field" for="withholdOutOfScopeFindings">
                        <input id="withholdOutOfScopeFindings" v-model="editedWithholdOutOfScopeFindings"
                            name="withholdOutOfScopeFindings" type="checkbox" />
                        <strong>Do not post findings outside the changed lines</strong>
                    </label>
                    <p class="muted review-publication-copy">
                        A review reads whole files, so it can find something in pre-existing code far from the
                        lines a pull request changed. Those findings stay in the ProPR review and the pull request
                        summary reports how many were held back.
                    </p>
                </div>
                <button :disabled="!isPostConfigButtonEnabled()"
                    class="btn-primary inline-save-btn post-config-save-btn" @click="savePostConfiguration">
                    Save
                </button>
            </div>
            <span v-if="saveError" class="error">{{ saveError }}</span>
        </div>
    </div>
</template>

<script lang="ts" setup>
import { inject } from "vue";
import {
    ClientDetailVmKey,
    type CommentSeverity,
} from "@/features/clients/view-models/useClientDetailViewModel";

const vm = inject(ClientDetailVmKey)!;
const {
    client,
    saveError,
    editedMinimumSeverityToPost,
    editedAutoResolveSeverities,
    editedWithholdOutOfScopeFindings,
    savePostConfiguration,
    isPostConfigButtonEnabled,
} = vm;

// Minimum-severity threshold options, least to most restrictive. 'info' (the lowest rank) posts everything.
const MIN_SEVERITY_OPTIONS: { value: CommentSeverity; label: string }[] = [
    { value: "info", label: "Info — post everything (default)" },
    { value: "suggestion", label: "Suggestion and above" },
    { value: "warning", label: "Warning and above" },
    { value: "error", label: "Error only" },
];

// Severities offered for auto-resolution, highest to lowest.
const SEVERITY_OPTIONS: { value: CommentSeverity; label: string }[] = [
    { value: "error", label: "Error" },
    { value: "warning", label: "Warning" },
    { value: "suggestion", label: "Suggestion" },
    { value: "info", label: "Info" },
];
</script>

<style scoped>
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

.post-config-group-label {
    display: block;
    font-weight: 500;
    margin-bottom: 0.35rem;
}

.checkbox-field {
    display: flex;
    align-items: center;
    gap: 0.55rem;
}

.auto-resolve-option {
    margin-top: 0.25rem;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}
</style>
