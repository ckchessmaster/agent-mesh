# Driver Capability Discovery — Design Discussion

**Status:** Proposal, not implemented. Requesting review from @Astra / @Sorrn before any code is written.
**Related:** Job `mesh/job-a5b3ad3f` checklist item 6 ("we aren't pulling the correct info from the drivers with regards to available models, context sizes"). Item 3 (quota, fixed in PR #5) is the same class of bug one layer up, so this doc reuses the contract that fix established.

---

## 1. Problem

Model lists, effort levels and context sizes shown in the dashboard are **mostly authored constants in the API**, not data read from the driver. Where a real source does exist, its output is decorated with invented per-model metadata before it reaches the UI.

This is the same failure mode as the quota panel fixed in `57a16ed`: when a provider cannot be read, the code substitutes a plausible-looking default instead of reporting "unknown". For quota the result was a fake green bar. Here it is a picker offering context windows the selected model may not support, and effort levels the driver may reject.

The difference in severity: a fake quota meter is a cosmetic lie. A fake context size is **actionable** — the user selects it, it is sent to the driver, and it changes execution.

## 2. What is real vs. invented today

Verified by reading `src/AgentMesh.Api/Controllers/SystemController.cs` and by running the installed CLIs.

| Surface | Source | Real or invented |
| :--- | :--- | :--- |
| agy **model ids** | `agy models` (parsed, grouped) | **Real** — the only genuinely provider-sourced field in the whole response |
| agy **efforts** | Suffix stripped from model id (`-low`/`-medium`/`-high`/`-xhigh`/`-max`) | **Derived** — plausible, but inferred from naming, not declared by the provider |
| agy **context sizes** | `GetAgyModelContextSizes()` — substring match on the model id | **Invented** |
| agy **fallback catalog** | `GetDefaultAgyCatalog()` — 5 hardcoded models | **Invented**, and indistinguishable from real in the UI |
| copilot **models** | 31 hardcoded entries in `SystemController` | **Invented** — no probe exists |
| copilot **context sizes** | Hardcoded per model | **Invented** |
| claude **models** | 4 hardcoded entries | **Invented** |
| claude **context sizes** | Hardcoded per model | **Invented** |
| openai/ollama **models** | `GET {endpoint}/models` | **Real** (ids only) |
| openai/ollama **context sizes** | Hardcoded list | **Invented** |
| generic | empty | Honest |

### The specific mechanism for agy

`GetAgyModelsAsync()` does call the real CLI, then decorates each result:

```csharp
// src/AgentMesh.Api/Controllers/SystemController.cs:404
private static IReadOnlyCollection<string> GetAgyModelContextSizes(string baseId)
{
    var lower = baseId.ToLowerInvariant();
    if (lower.Contains("argon") || lower.Contains("pro") || lower.StartsWith("gemini-4"))
        return new[] { "200k", "500k", "1m", "2m" };
    if (lower.Contains("gemini") || lower.Contains("claude"))
        return new[] { "200k", "500k", "1m" };
    ...
}
```

Substring matching on a model id tells us nothing about a window. Note it also returns `500k` for agy models, while the agy adapter never sends a context size at all (below) — so the picker advertises a control that is a no-op.

And the fallback is silent:

```csharp
// SystemController.cs:375
if (models.Count == 0)
{
    models = GetDefaultAgyCatalog();   // 5 invented models
}
```

Verified against the installed binary — this fallback is what a signed-out host gets:

```
$ agy models
Fetching available models...
Error: Please sign in to view available models.
```

So on any unauthenticated API host the dashboard renders five fabricated model names as if they were the account's real catalogue. `--output-format json` is not accepted by `agy models` (it is a print-mode flag), so there is no structured variant to reach for either.

## 3. Verified provider constraints

These bound what any option can promise. All checked against the binaries on this host.

| Claim | Evidence |
| :--- | :--- |
| `agy` has **no context-size flag** | `agy --help` lists `--model`, `--effort`, `--mode`, `--agent` — no context option. `AgyAdapter` passes `--model`/`--effort`/`--mode` only (`AgentAdapters.cs:700-755`). |
| `agy` effort set is `low\|medium\|high\|xhigh\|max` | `agy --help`: `--effort ... (low|medium|high|xhigh|max)` |
| `agy` silently clamps `xhigh`/`max` → `high` | `AgentAdapters.cs:735-741` rewrites them before invocation |
| `claude` context is a **token count, 100k–1M**, not a tier | `claude --help`: `--autocompact <auto|tokens>  (auto, or 100k–1M tokens)` |
| `claude` model ids are aliases (`fable`, `opus`, `sonnet`) or full names | `claude --help` `--model` description. The hardcoded `claude-sonnet-5-5` style ids are not validated against it. |
| `copilot` is **not installed** on this host | `command -v copilot` → not found. Its ~30-model list cannot have been probed here. |
| OpenAI-compatible `/models` returns **ids only** | `SystemController.cs:264-277` parses `id` and nothing else; `OpenAiModelItemDto(string Id, string Name)` has no capacity field |
| OpenAI context limit is a **second hardcoded table** | `OpenAiAdapter.ResolveModelContextLimit()` (`OpenAiAdapter.cs:108-129`) — `qwen3 → 524_288`, `llama3 → 131_072`, else `131_072` |

The last two rows matter: for OpenAI-compatible drivers there are now **two independent, unlinked sources of truth** for context size — the API's picker list and the adapter's runtime limit table. They can drift silently, and the runtime one wins.

## 4. A latent bug found while mapping this

`GetCapabilities` declares a per-driver parameter it never reads:

```csharp
// SystemController.cs:21
public async Task<IActionResult> GetCapabilities([FromQuery] string? agentId = null)
```

`agentId` is unused throughout the method body, and the dashboard never sends it (`api.ts:106` calls `/api/system/capabilities` with no query). The endpoint is **adapter-granular, not driver-granular**: two OpenAI-compatible drivers pointing at different servers get the identical hardcoded model list.

I am not fixing this here — it is the seam the whole design hangs off, so it should be settled deliberately rather than patched incidentally.

## 5. Options

### Option A — Honest nulls (the item-3 pattern, applied here)

Keep the current architecture. Make absence legible.

- Delete `GetDefaultAgyCatalog()`; return an empty list plus `source: "unavailable"` when the probe fails or the CLI is signed out.
- Drop `GetAgyModelContextSizes()`; return `null` context sizes for agy, since agy has no context control to describe.
- Add `source` + `observedAt` to `RuntimeCapabilitiesDto` (same provenance fields added to quota in `57a16ed`).
- Copilot/Claude model lists become explicitly labelled **static presets** in the UI, not driver readings.
- UI renders "model list not reported by this driver" instead of a fabricated picker.

**Cost:** small, contained, no schema change. **Downside:** users lose the convenience picker for providers that genuinely expose nothing. That is the point, but it is a UX regression Astra should sign off on.

### Option B — Per-driver discovery, cached, with a declared capability flag

Make `agentId` real. Resolve the driver row, read its adapter from the capabilities blob (exactly the discriminator `IsAntigravity` now uses), probe *that* driver, and return per-driver capabilities.

- Probe runs per driver, cached with positive/negative TTLs and single-flight, reusing the `AgyDriverQuotaProbe` shape verbatim.
- Each capability carries a `CapabilitySource` enum: `Probed | StaticPreset | UserOverride`.
- Where a provider exposes nothing, the driver declares `supportsContextSelection: false` and the UI **hides the control** rather than showing a no-op.
- The OpenAI adapter's `ResolveModelContextLimit` table becomes the fallback of last resort and reports which branch it took, so the two sources stop being silent peers.

**Cost:** medium. Touches the API, the adapter contract, and the picker UI. **Benefit:** fixes the actual complaint — correct info per driver — and makes the two-sources-of-truth problem impossible to hide.

### Option C — Driver-declared capabilities (runner pushes, API stores)

Invert the direction. The runner already knows its own binary and version; let it declare what it supports at registration, and have the API treat that as authoritative.

- Extend the registration payload with a `capabilities` block: model ids, effort values, context options, and whether context selection exists at all.
- Adapters implement a `DescribeCapabilities()` that shells out once at registration (and on an explicit refresh), not on every dashboard load.
- `SystemController` becomes a merge of declared data plus operator overrides; the hardcoded tables move to seed/defaults for adapters that declare nothing.

**Cost:** largest — changes the CLI↔API contract, requires a CLI release and a `dist/cli` rebuild, and old runners keep sending nothing. **Benefit:** the API stops shelling out to CLIs on a request path entirely (a security and latency win — today `GET /api/system/capabilities` is `[AllowAnonymous]` and spawns a subprocess). Removes the fan-out problem at the source.

### Recommendation

**B, with C as the target end-state.** A alone is honest but leaves the per-driver complaint (the literal wording of item 6) unresolved. C is right in principle but is a protocol change with a rollout tail, and I would not smuggle that in under a bugfix job.

B is the smallest change that makes the data both real *and* per-driver, and it reuses machinery already built and tested in `57a16ed` rather than inventing new infrastructure.

## 6. Decisions I need before writing code

1. **Direction of truth** — B (API probes per driver) or C (runner declares)? This is architectural, not mine to pick. C changes the CLI↔API contract, which is Sorrn's call.
2. **Is `[AllowAnonymous]` on `/api/system/capabilities` intentional?** It currently accepts an unauthenticated request that spawns a CLI subprocess. Combined with per-driver probing (B) that becomes an unauthenticated subprocess fan-out primitive. I would not ship B without closing this, but auth changes are outside my trust tier.
3. **How to treat static presets** — label them in the UI as bundled defaults, or remove them so an unprobed driver shows nothing?
4. **Copilot/Claude model lists** — keep as editable operator config, or drop entirely? They are 35 invented entries today (31 Copilot + 4 Claude), none of them probed.
5. **Context selection on agy** — the control is a no-op there. Hide it, or keep it as a prompt-hint only? Needs a product decision, not a technical one.
6. **Effort derivation** — is stripping `-medium` from a model id acceptable as "reported by provider", or must it be marked `Derived` and displayed as such?

## 7. Explicitly out of scope

- Item 7 (thinking text across driver types) — different surface, different data path.
- Any EF Core migration or schema change. If C requires persisting declared capabilities, that is a schema discussion first.
- Quota, which shipped in PR #5.
