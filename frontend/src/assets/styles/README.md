# Design system

Global, layered stylesheet for the admin UI. `main.ts` imports **`index.css`** only;
it pulls in the layers in cascade order:

| Layer | File | Contains |
|-------|------|----------|
| tokens | `tokens.css` | All CSS custom properties: colour + semantic tints, radius scale, surface fills, layout sizes. **The only file allowed to hold raw hex/rgba.** |
| base | `base.css` | Element defaults: reset, `body`, links, native `input`/`select`/`textarea`/`table`/`label`, `.fi` icon. |
| primitives | `primitives.css` | Reusable class components: buttons, the **chip** system, cards, state placeholders, form helpers. |
| layout | `layout.css` | Page shells, app header/nav/footer, toolbars, detail-page tabs/sections, dialog, login, timeline. |
| utilities | `utilities.css` | Opt-in effects: `.glass`, `.hover-lift`, `.gradient-text`. |
| vendor | `vendor/diff2html.css` | diff2html import + dark-theme overrides. |

## Conventions

- **Never hard-code colours or radii in component `<style scoped>`.** Use the tokens:
  `var(--color-*)`, `var(--color-*-soft)` (tinted backgrounds), `var(--radius-*)`.
- **Chips/badges/status pills → use the unified `.chip` system**, not bespoke
  `.pill-*` / `.badge-*` classes:
  - base `.chip` + size `.chip-sm`
  - tone `.chip-success | -warning | -danger | -info | -accent | -muted`
- **Cards → `.section-card` / `.section-card-header` / `.section-card-body` / `.state-card`**.
- **Empty/loading/error → `.empty-state` / `.loading-state` / `.error` / `.field-error`**.
- Add a *new* shared variant (a new chip tone, a new card) **here**, in the primitive,
  not in a component. Component `<style scoped>` is for genuinely component-specific
  layout only.

## Guardrail

`npm run lint:css` (stylelint) warns on raw hex, raw `px` border-radius, and
re-introduced `.pill-*` / `.badge-*` selectors. It starts at **warning** severity
during the consolidation and will be promoted to **error** (and wired into the test
gate) once the component back-catalogue is migrated.

## Migration status

Phase 1 (this foundation + unified primitives + stylelint baseline) is done.
Per-surface component migration (replacing bespoke scoped CSS with these primitives)
is tracked in the project memory `frontend-ui-refactor-hotspots`.
