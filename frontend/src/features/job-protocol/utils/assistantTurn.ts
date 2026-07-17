// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

export interface AssistantTurnToolCallView {
    name: string
    arguments: unknown
}

export interface AssistantReviewCommentView {
    severity?: string
    file_path?: string
    line_number?: number
    message?: string
}

export interface AssistantReviewView {
    summary?: string
    comments?: AssistantReviewCommentView[]
}

export interface AssistantTurnView {
    text: string
    reasoning: string | null
    toolCalls: AssistantTurnToolCallView[]
    parsedReview: AssistantReviewView | null
}

/**
 * Interprets the parsed `output_summary` of an AI-call event as a structured assistant-turn record and returns a
 * view of it, or `null` when the payload is not such a record. The record is recognised by the always-present
 * `assistantText` key, which distinguishes it from a bare final-review JSON body (`summary`/`comments`) or from any
 * other event payload. When present, `assistantText` may itself be the final review JSON, so it is parsed into
 * `parsedReview` for structured rendering; otherwise the raw text is surfaced for display.
 */
export function parseAssistantTurnRecord(parsedOutput: unknown): AssistantTurnView | null {
    if (!parsedOutput || typeof parsedOutput !== 'object' || !('assistantText' in parsedOutput)) {
        return null
    }

    const record = parsedOutput as { assistantText?: unknown; reasoning?: unknown; toolCalls?: unknown }
    const text = typeof record.assistantText === 'string' ? record.assistantText : ''
    const reasoning = typeof record.reasoning === 'string' && record.reasoning.length > 0 ? record.reasoning : null
    const toolCalls = Array.isArray(record.toolCalls)
        ? (record.toolCalls as Array<{ name?: unknown; arguments?: unknown }>).map((call) => ({
            name: typeof call?.name === 'string' ? call.name : '(unknown)',
            arguments: call?.arguments ?? null,
        }))
        : []

    let parsedReview: AssistantReviewView | null = null
    if (text) {
        try {
            const reviewJson = JSON.parse(text)
            // Only treat the parsed JSON as a review when it carries a review-shaped field. Other structured
            // outputs (e.g. a synthesis body) are valid JSON objects too, but must fall through to the raw text.
            if (looksLikeReview(reviewJson)) {
                parsedReview = reviewJson as AssistantReviewView
            }
        } catch {
            parsedReview = null
        }
    }

    return { text, reasoning, toolCalls, parsedReview }
}

function looksLikeReview(value: unknown): value is AssistantReviewView {
    if (!value || typeof value !== 'object') {
        return false
    }

    const candidate = value as { summary?: unknown; comments?: unknown }
    return typeof candidate.summary === 'string' || Array.isArray(candidate.comments)
}
