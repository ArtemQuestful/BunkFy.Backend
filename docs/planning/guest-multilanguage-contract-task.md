# Guest language collection — compatibility implementation

Status: backend implementation under review; no deployed migration, web collection editor, or production admission is claimed.

## Contract

`LanguageTags` is an optional nullable request collection and a non-null response/storage collection. There is no primary, ordering, or proficiency model. `PreferredLanguageTag` remains compatibility metadata; a new collection-only client must not invent it.

| Request | Create | Update |
|---|---|---|
| Missing or null collection | Derive zero/one entry from the scalar | Preserve existing entries; a changed non-null scalar adds its identity |
| Empty collection | Store empty; reject a non-null scalar | Clear atomically; reject a non-null scalar |
| Nonempty collection | Store normalized identities | Replace membership, preserving existing representatives for retained identities |

Clearing only the scalar does not clear the collection. Contradictory explicit scalar/collection combinations fail, without dropping secondary languages. Limits are 12 submitted entries and 12 unique entries, with each entry 1–35 characters after .NET trimming. Empty/null members fail. The existing envelope of ASCII letters, UTF-16 `char.IsDigit` characters, and hyphen remains; there is no `CultureInfo`, registry, or new BCP47 validation gate. Unknown values remain valid. Identity lowercases ASCII letters only and compares ordinally. Backfill preserves the exact existing scalar bytes.

Public API, AdminAPI, Guest commands, correction commands and the ReservationGuestRecords bridge carry the same request. AdminCLI retains `--language` for the scalar; `--languages en fr` replaces the collection, omitting it preserves, and `--languages` with no values explicitly clears. Listing/getting includes the collection.

## Replay and privacy

Omitted/null collection retains the previous `guest-profile-update-v1` fingerprint algorithm exactly. Explicit collections, including empty, use v2 with presence, count and length-prefixed sorted identity keys. Fingerprints describe the normalized request before reading the aggregate, not the merged stored collection. Replay still precedes aggregate version checks. Equivalent collection case/order does not change membership or collection changed-field meaning. Ordinary update version increments are unchanged.

Creation replay compares the resolved creation set, including the legacy scalar-derived set. Correction equality treats an omitted collection as preservation; an explicit collection compares identity sets. Language changes use the appended field `LanguageTags=9`, key `guest.profile.language-tags`, never the old scalar bit. New correction receipts are v2; persisted v1 receipt bytes/bits and serialized completion events retain their original meaning.

The collection is included in the Guest personal-data catalogue, subject export and tenant export, and removed by the shared Guest anonymisation state transition used by approved erasure/retention/restore. Hold, restriction, actor/property authority and retention policy admission are unchanged. Events/receipts carry only field identifiers and operational proof, not language values. These engineering metadata changes do not grant legal/country approval.

Guest subject export schema is v2 and personal-data catalogue is v17; tenant export schema is v5 and tenant catalogue is v6. A valid frozen plan carrying the previous Guest catalogue is rejected by the existing owner-catalogue equality gate before owner output. A newly approved/frozen plan is required; no automatic replanning is introduced.

## Migration and deployment gate

The additive PostgreSQL `text[]` column has a non-null empty default and exact scalar backfill. The anonymised-row constraint requires an empty collection. Historical v1 correction receipts remain admitted with masks 1–255; v2 admits masks 1–511. Unknown versions, empty/invalid masks and v1 language bits remain rejected.

Real isolated PostgreSQL tests, not provider documentation alone, must prove mapping, backfill, equal-content change tracking, copied-array protection, add/remove/clear, old receipts, constraints and failure safety. Historical migration fixtures must seed their historical schema explicitly rather than applying today's model to an old table. Check logs and failures are retained in the implementation evidence packet.

`Down` is only lossless for the original scalar-representable state. It holds exclusive profile and correction-receipt table locks in the migration transaction, then checks exact raw scalar/array equality and v1-only receipts with masks 1–255 before any destructive statement. Collection-only, additional or differently cased values, and any v2 receipt, abort the downgrade without schema/data changes. This bounded test/development downgrade is not authorization to downgrade a deployed multilingual backend.

Before enabling any collection-writing UI, migrate and verify a homogeneous collection-aware API/worker release. Old API peers silently ignore new JSON properties, so mixed-version writes are unsafe. Roll back the web first; retain the additive schema and collection-aware backend and forward-fix it. An old backend cannot safely continue writes: scalar edits can diverge from the collection and old anonymisation cannot clear it. Any backend rollback needs separately approved, tested write suspension and restore/repair evidence. No preview migration or deployment is authorized by this document.

Production controls remain governed by [Migrations Host Production Safety](../operations/migrations-host-production-safety.md) and the current SP master at `/home/artem/projects/BunkFy/docs/company-readiness/security-privacy-and-international-engineering-plan.md`. Relevant impacts are SP001 inventory, SP002 rights, SP003 retention, SP007 migration, SP009 payload-free monitoring, SP011 supply chain, and SP012 independent assurance. Source/tests are not hosted, legal, operational, or human/AT proof.

Remaining feature gate: a reviewed searchable multi-selection UI in all three Guest consumers, current generated API contracts, and fresh real persistence/permissions/recovery/localization/keyboard/reflow evidence. Backend-only completion is not the complete multilingual feature.
