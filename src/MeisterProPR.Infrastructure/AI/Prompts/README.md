# Prompt Templates

This directory contains file-based prompt templates for stable review stages.

- `shared/` holds shared stage templates and reusable partials.
- `file-by-file/` holds file-by-file review stage templates.
- `agentic-file-by-file/` holds agentic file-scoped stage templates.
- `pr-wide-agentic/` holds PR-wide agentic stage templates.

Template file names map to stable prompt stage identifiers through `PromptTemplateCatalog`.
Missing templates or partials are configuration errors and should fail fast.

## Authoring Rules

- Keep stage keys stable. Add or update stage-to-file mappings in `PromptTemplateCatalog` rather than inferring them from filenames.
- Preserve output contracts exactly. If a stage currently requires raw JSON, exact property names, or specific reminder text, keep that wording aligned with the corresponding tests and runtime expectations.
- Prefer explicit boolean flags in models such as `hasThreads` and `hasEvidence` instead of `{{#if list.length}}` style checks.
- Prompt text should render without HTML escaping. Template content should be written as final prompt prose, not HTML.
- Keep dynamic sections simple and data-shaped. Complex branching belongs in the C# model builder, not in deeply nested template logic.
- Shared wording that must stay consistent across stages should move into shared partials under `shared/partials/`.
- Stage templates define the default prompt text only. Prompt overrides and prompt experiments are still applied in `ReviewPrompts` after template rendering.

## Validation Notes

- Add regression coverage before migrating a stage.
- When a stage is template-backed, verify both the rendered default prompt text and its stable stage-key mapping.
- Treat missing template files, missing partials, and invalid mappings as startup or render-time failures that must be fixed rather than silently bypassed.
