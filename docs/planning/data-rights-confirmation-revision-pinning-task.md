# Data Rights Confirmation Revision Pinning Task

Status: implemented
Date: 2026-08-15

## Goal

Ensure a privacy operator can confirm only the exact case revision and action
they reviewed, even while live refreshes or another operator change the case.

## Ownership

- Data Rights owns case transitions, action eligibility, expected-version
  enforcement, and the distinction between review and execution.
- The web client owns the transient confirmation snapshot and must not silently
  reinterpret it against newer server state.
- Owner modules remain authoritative for selected-record revisions and
  execution. This slice does not alter owner contracts or data.
- GMA already supplies generic optimistic-concurrency and error behavior. No
  BunkFy privacy workflow moves into the framework.

## Findings

- The confirmation state currently stores only an action name.
- Live polling can replace the case while the panel remains open. Confirm then
  submits `dataRightsCase.version` from the latest render, not the revision the
  operator reviewed.
- The panel can also remain visible after its action disappears because action
  eligibility is checked for buttons but not for existing confirmation state.
- Server transitions remain fail-closed, but they cannot distinguish a
  deliberate review of the new revision from a client that silently adopted
  it.

## Decisions

- Snapshot case ID, case version, status, selected-subject count, operation
  kind, and confirmation action when the operator opens a panel.
- Treat a snapshot as current only while every coordinate still matches and
  the action remains available under current permissions.
- Derive the rendered panel from that validity check, so stale content
  disappears in the same render that observes new case or permission state.
- Clear stale confirmation inputs after invalidation, including destructive
  confirmation text.
- Submit the snapshotted expected version. A race after rendering therefore
  still reaches the server with the reviewed revision and fails with a version
  conflict rather than adopting newer state.
- Keep the snapshot PII-free and in component memory only.
- Make no backend, schema, broker, Docker, owner-module, or GMA code change.

## Delivery

- [x] Add a pure confirmation snapshot and validity helper.
- [x] Pin confirmed lifecycle and execution requests to the reviewed version.
- [x] Hide and clear confirmations after revision, status, selection,
  operation, action, or permission drift.
- [x] Add focused helper and source-wiring coverage.
- [x] Align Data Rights documentation and complete one web-focused slice gate.

## Deferred

- Public dependency error taxonomy remains a separate backend/API slice.
- Immediate non-confirmed actions continue to use the latest visible version;
  they do not represent a separate operator review step.
- Cross-device draft confirmations are intentionally unsupported. A privacy
  confirmation is ephemeral and must never be persisted in browser storage.

## Completion Criteria

- a confirmation is visible only for the exact reviewed case state and an
  action the current operator can still perform;
- live refresh or permission drift removes stale confirmation before it can be
  submitted;
- a post-render race submits the reviewed version, never the latest version;
- destructive confirmation text is cleared when its snapshot becomes stale;
- no case or subject data is copied into URLs, storage, logs, or telemetry;
- focused tests and one complete web gate pass.

## Verification Cadence

Use focused pure-helper and source-wiring tests while editing. Run one complete
web gate after the slice is coherent. Run backend documentation guards only;
do not repeat the full backend, Docker, or GitHub Actions gates because backend
runtime and persistence code do not change in this slice.

## Completion Evidence

- Focused confirmation, workflow, and foundation coverage passed 34/34, with
  the final exact helper check passing 3/3 after adding explicit case-switch
  coverage.
- TypeScript and targeted lint passed before the broad slice checkpoint.
- Backend and product workspace solutions were synchronized and their drift
  check passed; all 112 architecture tests passed from the existing build.
- The complete web gate passed TypeScript, lint, all 56 test files and 287
  tests, and the production Vite build.
- No backend runtime, schema, Docker, provider, broker, GMA, or GitHub Actions
  gate was required for this web-only operator-consent slice.
