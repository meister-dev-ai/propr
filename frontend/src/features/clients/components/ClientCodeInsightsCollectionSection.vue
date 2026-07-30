<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->
<!-- This file implements commercial-only functionality. A commercial license is required to activate or use that functionality. -->

<template>
    <div v-if="client" class="section-card" data-testid="code-insights-collection">
        <div class="section-card-header">
            <h3>Quality-metrics collection</h3>
        </div>
        <div class="section-card-body">
            <!-- Not licensed: say so rather than offering a control that would do nothing. The server enforces
                 the same gate, so a forced flag here could not start collection either. -->
            <p v-if="!isCodeInsightsAvailable" class="muted-inline" data-testid="code-insights-upgrade-note">
                {{ codeInsightsUpgradeMessage || "Code Insights requires a commercial license. Nothing is collected on this installation." }}
            </p>

            <div class="inline-field-row">
                <div class="form-field flex-1">
                    <label class="checkbox-field" for="codeInsightsCollectionEnabled">
                        <input id="codeInsightsCollectionEnabled" v-model="editedCodeInsightsCollectionEnabled"
                            :disabled="!isCodeInsightsAvailable" name="codeInsightsCollectionEnabled"
                            type="checkbox" />
                        <strong>Collect quality metrics for this client</strong>
                    </label>
                    <p class="muted-inline collection-copy">
                        Records each finding this client's reviews produce, classifies it by type, and tracks what
                        happened to it, so quality can be measured over time. This spends model tokens on
                        classification and reads the pull-request discussion, which is why it is off by default.
                        Collection is forward-only: turning it on collects from now on, and turning it off stops
                        further collection without deleting what was already collected.
                    </p>
                </div>
                <button :disabled="!isCodeInsightsAvailable || saving || !isDirty"
                    class="btn-primary inline-save-btn" @click="saveCodeInsightsCollection">
                    {{ saving ? "Saving…" : "Save" }}
                </button>
            </div>
            <span v-if="saveError" class="error">{{ saveError }}</span>
        </div>
    </div>
</template>

<script lang="ts" setup>
import { computed, inject } from "vue";
import { ClientDetailVmKey } from "@/features/clients/view-models/useClientDetailViewModel";

const vm = inject(ClientDetailVmKey)!;
const {
    client,
    saving,
    saveError,
    editedCodeInsightsCollectionEnabled,
    isCodeInsightsAvailable,
    codeInsightsUpgradeMessage,
    saveCodeInsightsCollection,
} = vm;

const isDirty = computed(
    () =>
        editedCodeInsightsCollectionEnabled.value !==
        Boolean(client.value?.codeInsightsCollectionEnabled),
);
</script>

<style scoped>
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

.collection-copy {
    margin-top: 0.5rem;
}

.muted-inline {
    color: var(--color-text-muted);
    font-style: italic;
}
</style>
