# React + TypeScript + Vite

This template provides a minimal setup to get React working in Vite with HMR and some Oxlint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

## Pending attention & desktop notifications

| Concern | Where it lives |
| :--- | :--- |
| Wire contract (`AttentionItem`, `AttentionRollup`, event names) | `src/types/attention.ts` — mirrors the C# records in `AttentionQueryService`/`AttentionNotifier`. |
| Desktop alert engine: permission, OS banner (tag = item id), WebAudio chime, mute, seen-set, click-through | `src/services/notifications.ts` (exported singleton `notifications`) |
| Bell UI: unread badge, inbox list, dismiss, permission affordance | `src/components/AttentionBell.tsx` (also exports `MuteToggle`) |
| Read / dismiss bookkeeping | `src/utils/attentionInbox.ts` |
| Mount point + live SignalR wiring | `src/App.tsx` — header controls, `attentionBellRef` |
| Rollup source of truth | `GET /api/attention` via `api.getAttention()` in `src/services/api.ts` |

Rules worth knowing before editing this area:

* **The field names and `type` strings in `types/attention.ts` are frozen**, because the seen-set
  persists `"{jobId}:{type}"` keys in `localStorage`. A rename orphans every stored key and silently
  re-notifies waits the operator already saw. Additions are fine; renames are breaking.
* **The bell owns polling** (30s interval + tab-foreground refresh + one coalesced read after each
  push). Polling `/api/attention` from somewhere else doubles the requests for no benefit.
* **`notifications.sync(items)` must receive the whole rollup, and only after a successful read.**
  Syncing an empty list because the API returned 500 clears the seen-set, so the next good poll
  re-notifies every wait the operator already saw. Single push events go to `announce(item)` instead.
* **Opening the bell is the acknowledgement** (it clears the badge). Dismissing hides a row without
  answering the agent, and the row returns if the job clears and parks again.
* **The master mute switch lives in the header only** — the popover footer reports permission state.
  A second copy of the same switch inside the popover it opens would be a control that lies about how
  many switches exist.

### Testing desktop alerts locally

`npm run dev` serves `http://localhost:5173`, which **is** a secure context, so OS banners and the chime
work in development. Opening the same dev server through a LAN IP or machine name is **not**: browsers
hide the `Notification` API from insecure origins without throwing, so nothing appears in the console to
chase — the bell footer shows *"Desktop alerts need HTTPS or localhost"* instead.

Two more browser behaviours that look like bugs but are not:

* Sound requires a user gesture. The app registers a one-shot `pointerdown`/`keydown` listener to unlock
  `AudioContext`; a tab that is never touched banners silently.
* Permission is requested only from the bell's *Enable desktop alerts* button, never on load. Once the
  operator has clicked **Block**, `permission()` is `'denied'` for good and the footer tells them to
  change it in site settings.

In tests, jsdom provides neither `Notification` nor `AudioContext`: `vi.stubGlobal` them per test and
restore in `afterEach`, and spy on `window.open` **and** `window.focus` (jsdom logs *Not implemented* for
`focus()`). See `src/services/__tests__/notifications.test.ts`,
`src/components/__tests__/AttentionBell.test.tsx` and `src/__tests__/AttentionWiring.test.tsx`.

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the Oxlint configuration

If you are developing a production application, we recommend enabling type-aware lint rules by installing `oxlint-tsgolint` and editing `.oxlintrc.json`:

```json
{
  "$schema": "./node_modules/oxlint/configuration_schema.json",
  "plugins": ["react", "typescript", "oxc"],
  "options": {
    "typeAware": true
  },
  "rules": {
    "react/rules-of-hooks": "error",
    "react/only-export-components": ["warn", { "allowConstantExport": true }]
  }
}
```

See the [Oxlint rules documentation](https://oxc.rs/docs/guide/usage/linter/rules) for the full list of rules and categories.
