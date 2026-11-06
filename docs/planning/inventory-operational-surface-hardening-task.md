# Inventory Operational Surface Hardening Task

Status: complete
Date: 2026-08-04

## Goal

Make Inventory's ordinary operator surfaces truthful, bounded, cache-safe, and inexpensive without changing availability semantics or weakening topology-change safety.

## Ownership

- Inventory continues to own sellability, availability, blocks, allocations, sales modes, and topology-retirement processes.
- Properties continues to own physical property, room, and bed topology.
- Reservations continues to own reservation state and staff reassignment.
- Public, Admin API, Admin CLI, and web consumers use Inventory contracts only.
- This slice is BunkFy-specific. GMA's existing pagination primitive is sufficient and no framework change is required.

## Decisions

- Room and block directories expose `HasMore`, implemented with deterministic `pageSize + 1` lookahead and no exact count query.
- Full room rows remain in the room directory because the current operator workflow needs unit topology, sales mode, and drain state together.
- Availability remains a complete property/date-range decision snapshot. It is not treated as a directory and will not be partially paged in this slice.
- Ordinary sales-mode and block mutations return minimal receipts; callers invalidate and refetch authoritative reads.
- Retirement requests and retries keep their bounded process DTOs because the immediate process state, impact counts, and capped reservation references drive the operator workflow.
- Inventory HTTP responses are marked `no-store`; block reasons, staff actor references, claim identifiers, and availability state must not be retained by shared caches.
- Admin API endpoints declare explicit success response metadata, matching the public API contract.

## Delivery

- [x] Add truthful continuation metadata and bounded lookahead to room and block repositories.
- [x] Add minimal room-mode, block, and block-group mutation receipts and align handlers, APIs, Admin CLI, and tests.
- [x] Apply cache controls and explicit response metadata to public and Admin API endpoints.
- [x] Align generated OpenAPI contracts and web consumers, using `HasMore` instead of probing for an empty page.
- [x] Add focused contract, persistence, API-security, and frontend regression coverage.
- [x] Update Inventory development notes with the resulting operational contract.

## Deferred

- A first-class persisted manual-block-group aggregate and group-paged history
  were deferred by this historical slice and are addressed by
  `inventory-manual-block-group-convergence-task.md`, which preserves original
  target intent and marks legacy intent unknown rather than inferring it.
- Server-side searchable block-target discovery for unusually large properties.
- Partial availability paging or search. That requires a reservation-selection UX and consistency contract of its own.
- Provider-specific query tuning or partitioning before measurements justify it.

## Completion Criteria

- room and block pages never require an exact count and report continuation accurately;
- ordinary Inventory writes do not return nested room/unit or unbounded block collections;
- public and Admin API operational responses carry `Cache-Control: no-store` and explicit success metadata;
- web callers refetch authoritative reads and do not issue a redundant terminal page request;
- existing retirement, allocation, and availability behavior remains unchanged;
- focused Inventory, architecture, composition, generated-contract, and web checks pass.

## Completion Note

Implemented bounded room and manual-block directories with deterministic `pageSize + 1` lookahead and truthful `HasMore`. Ordinary room-mode, block, and block-group writes now return contract-owned receipts, while availability and retirement keep their deliberate decision-oriented response shapes. Public and Admin HTTP surfaces prevent shared caching, and Admin endpoints declare explicit success schemas.

Generated OpenAPI and TypeScript contracts are aligned. The web inventory traversal follows `HasMore` without a terminal empty-page request, reuses the same helper in reservation inventory selection, and refetches authoritative state after writes. The obsolete nested block-group response contract was removed; Inventory personal-data catalogue version 4 and its generated inventory reflect the current boundary.

Verification completed on 2026-08-04:

- backend solution build, including the integration project: passed with zero warnings and errors;
- migration drift checks for every configured GMA and BunkFy provider: passed;
- Inventory tests: 90 passed;
- architecture guards: 83 passed;
- non-Docker integration tests: 54 passed;
- web tests: 140 passed across 22 files;
- web lint, TypeScript check, and production build: passed;
- generated OpenAPI and TypeScript contract drift check: passed;
- Docker: intentionally not run because the slice changed no schema, database-provider behavior, or broker contract.
