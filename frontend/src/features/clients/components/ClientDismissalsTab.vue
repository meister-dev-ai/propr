<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
    <div class="section-card">
        <div class="section-card-header">
            <h3>Dismiss Finding</h3>
            <button class="btn-primary btn-sm" @click="showDismissalForm = !showDismissalForm">
                <i class="fi fi-rr-plus"></i> Dismiss Finding
            </button>
        </div>

        <div class="section-card-body">
            <p class="muted" style="margin-bottom: 1rem">
                Dismissed findings are stored as admin memory records. The AI
                memory reconsideration pipeline will suppress similar findings
                in future reviews. Dismissed patterns can be viewed and managed
                in the <strong>Memory</strong> tab.
            </p>

            <!-- Dismiss form -->
            <div v-if="showDismissalForm">
                <div class="form-field">
                    <label>Finding Message
                        <span class="field-hint-inline">(exact AI finding text)</span>
                        <textarea v-model="newDismissal.originalMessage" rows="3"
                            placeholder="Paste the exact finding message to suppress" class="form-input" />
                    </label>
                </div>
                <div class="form-field">
                    <label>Label
                        <span class="field-hint-inline">(optional — why it's dismissed)</span>
                        <input v-model="newDismissal.label" type="text"
                            placeholder="e.g. False positive: naming style" class="form-input" />
                    </label>
                </div>
                <span v-if="dismissalCreateError" class="error">{{ dismissalCreateError }}</span>
                <div class="form-actions">
                    <button :disabled="dismissalSaving || !newDismissal.originalMessage.trim()"
                        class="btn-primary" @click="handleCreateDismissal">
                        {{ dismissalSaving ? "Saving…" : "Dismiss Finding" }}
                    </button>
                    <button class="btn-secondary" @click="showDismissalForm = false">
                        Cancel
                    </button>
                </div>
            </div>

            <p v-if="dismissalSuccess" class="success-hint">
                <i class="fi fi-rr-check-circle"></i> Finding dismissed and
                stored as a memory record.
            </p>
        </div>
    </div>
</template>

<script lang="ts" setup>
import { reactive, ref } from "vue";
import { dismissFinding } from "@/services/findingDismissalsService";

const props = defineProps<{ clientId: string }>();

const newDismissal = reactive({ originalMessage: "", label: "" });
const dismissalCreateError = ref("");
const dismissalSaving = ref(false);
const showDismissalForm = ref(false);
const dismissalSuccess = ref(false);

async function handleCreateDismissal() {
    dismissalCreateError.value = "";
    dismissalSaving.value = true;
    dismissalSuccess.value = false;
    try {
        await dismissFinding(props.clientId, {
            findingMessage: newDismissal.originalMessage,
            label: newDismissal.label || null,
        });
        newDismissal.originalMessage = "";
        newDismissal.label = "";
        showDismissalForm.value = false;
        dismissalSuccess.value = true;
        setTimeout(() => {
            dismissalSuccess.value = false;
        }, 3000);
    } catch {
        dismissalCreateError.value = "Failed to dismiss finding.";
    } finally {
        dismissalSaving.value = false;
    }
}
</script>

<style scoped>
.btn-sm {
    font-size: 0.8rem;
    padding: 0.3rem 0.7rem;
}

.field-hint-inline {
    font-weight: normal;
    font-size: 0.78rem;
    color: var(--color-text-muted);
    text-transform: none;
    letter-spacing: 0;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}
</style>
