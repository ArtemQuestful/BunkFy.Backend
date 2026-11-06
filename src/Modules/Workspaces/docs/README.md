# Workspaces Module

Workspaces coordinates the BunkFy product boundary between GMA Organizations,
Auth, Access Control, and the Staff domain. It owns staff-onboarding applications,
access plans, and recoverable access-lifecycle processes; it does not take
ownership of organization claims, Auth identities, access profiles, properties,
or durable Staff profiles.

Operator onboarding surfaces require the Workspaces-owned
`workspaces.staff-onboarding.manage` capability together with
`access-control.profiles.read`, because source plans select a server-owned
access profile. Product mutations also require the host's configured
privileged-operation assurance. Applicant submission and own-status reads stay
subject-bound self-service flows, while Administration-only access-process
repair remains a separate recovery authority.

The executable [`personal-data-catalog.v1.json`](personal-data-catalog.v1.json)
classifies Workspaces persistence, public/application boundaries, one-time join
tokens, and the person-linked Organizations and Staff events consumed by this
module. The generated
[`personal-data-inventory.v1.md`](personal-data-inventory.v1.md) is checked by
reflection tests against every selected mapped member and public contract.

## Operational surfaces

Workspaces-owned onboarding and access-recovery queues use stable offset
ordering and `pageSize + 1` lookahead. Their contracts expose `HasMore` without
issuing exact-count queries or requiring clients to probe an empty terminal
page. The access-process queue projects only the scalar fields it serves,
including its profile count, instead of loading aggregate-owned profile
snapshots. Admin responses are non-cacheable and publish explicit success
contracts, and the Admin CLI accepts deliberate page and page-size selection.

Workflow detail and mutation responses remain bounded, decision-oriented
models because their immediate state is needed to continue onboarding and
access recovery. Membership, invitation, enrollment-link, and join-claim
directories remain owned by GMA Organizations; adding truthful continuation to
those generic contracts requires a separate Organizations change and
coordinated consumer alignment.

Staff-access lifecycle mutations are serialized by tenant and staff member
before Workspaces reads replay state or invokes Organizations and Access
Control. Preparation, denial, restoration, retries, Staff lifecycle events,
retention correlation, and reversible correlation work share that coordinate;
waiters reload the committed process before deciding. The lock is a
transaction-scoped GMA key rather than persisted module data, so unrelated
staff remain concurrent and no migration, export, retention, or destruction
surface is introduced.

Staff-onboarding source graphs use the same GMA transaction-key primitive with
Workspaces-owned coordinates. Plan and source-lifecycle work holds one source
identifier exclusively; ordinary applicant processing shares that source and
then locks its application row. First submission also takes a SHA-256-derived
applicant coordinate so concurrent link or QR submissions cannot create two
applications without exposing an Auth subject in the lock resource. Source
expiry and supersession therefore wait for in-flight applicants, while other
sources and different applicants on a reusable enrollment link remain
concurrent. Application-id flows discover only immutable source coordinates
before locking and then reload authoritative state.

Rare subject-wide retention scrub, Data Rights anonymisation, and database
restore replay span both the onboarding-source and staff-access graphs. They
therefore take the existing Workspaces tenant-mutation coordinate exclusively
before narrower staff or row locks. Ordinary writers use that coordinate in
shared mode, so the destructive transaction drains and pauses only its tenant;
other tenants remain concurrent and no subject data enters a lock resource.

Copied applicant identity and contact fields are transient onboarding data.
Completion, rejection, supersession, invitation expiry, and claim expiry
redact those fields from the Workspaces record; the Staff module becomes
authoritative after successful provisioning. Unclaimed enrollment-link
staging is also reconciled by the tenant-scoped Retention schedule after the
source expires. Workspaces checks the exact Organizations claim before
redaction and reports an operational failure instead of deleting data when
the authoritative history window has lapsed. Sensitive API and Admin API
responses are explicitly non-cacheable.

Each automatic staging-cleanup run is admitted by a Workspaces-owned execution
record keyed by the generic Retention execution id. Begin, bounded discovery,
per-candidate reconciliation, and completion serialize on that tenant-qualified
coordinate before source locks are taken. Higher retries preserve cumulative
scanned and affected counts; stale attempts fail before discovery, and exact
terminal replay returns the stored result. The evidence is classified,
tenant-exported, and removed by the bounded termination lifecycle.
Withdrawal observations that arrive before the corresponding claim-request
event are retained in a bounded Workspaces-owned correlation table. Claim
request binding and withdrawal consumption share one exclusive source
coordinate, so neither subscription can miss the other's commit. Exact
duplicates replay, divergent coordinates fail closed, and the observation is
removed only after authoritative terminalization, source terminal cleanup, or
tenant destruction. This composed v14 persistence/export shape advances both the
personal-data catalogue and tenant-termination manifest; rollout evidence and
frozen owner approvals must use the new catalog version and digest.
Organizations admission permits a claim on a BunkFy-owned link only while a
matching admissible Workspaces application exists. The table is lifecycle-
bounded by link terminalization and tenant destruction, but it is not count-
bounded by `MaximumClaims`: withdrawal releases that concurrent reservation.
Operational capacity is therefore the link lifetime multiplied by admitted
claim rate and worst-case claim-request consumer lag. Source cleanup uses one
set-based delete inside the serialized transaction; rollout must load-test that
duration/rate envelope within the 30-second consumer timeout and alert on
deferred-row age and count while a source remains active.

Automatic Staff retention also asks Workspaces to close access and remove the
departed person's Auth subject from terminal onboarding and access history.
Workspaces blocks while a person-linked onboarding or access workflow is
active, replaces its remaining subject and actor references with one opaque
receipt-local pseudonym, and records an append-only canonical receipt. A retry
reasserts access denial before accepting that receipt as complete. This
irreversible scrub is not used by the reversible Data Rights anonymisation and
restore path.

For approved Staff Rights access cases, Workspaces now contributes its own
tenant-scoped subject discovery, stale-safe selection validation, and
catalogue-driven protected export. The owner exposes onboarding applications,
access processes, access plans, and immutable retention-correlation receipts
as separate versioned coordinates. Exact record or Auth account-subject
lookups are accepted; weak, mixed, property-scoped, cross-tenant, and retained
pseudonym lookups fail closed.

For an approved optional tenant-termination export, Workspaces streams the
same catalogue-approved portable staff and access-history shapes across the
whole frozen tenant, including Staff-onboarding retention execution evidence.
A tenant-scoped transaction lock drains local writes before the fence is
accepted and remains held through a repeatable-read snapshot. The export
excludes property projections, transport journals, rebuild checkpoints,
termination accountability proofs, anonymisation tombstones, and generic
Organizations, Auth, or Access Control state owned by other modules.

For irreversible tenant termination, Workspaces is the final product owner.
It waits for the terminal Properties and Task Runtime branches, transitions
the exact frozen fence into destruction, and removes its remaining operational,
projection, transport, governance, anonymisation, and historical proof rows in
foreign-key-safe batches of at most 500 physical rows. Completion retains only
the closed fence, its final close receipt, and one immutable PII-free destruction
receipt with a versioned SHA-256 proof chain. Equivalent retries resume or
replay exactly, changed requests conflict, and generic Organizations, Access
Control, Notifications, and Task Runtime records remain under their own owners.

An approved Staff Rights correction may replace only the seven applicant
profile fields on one exact `Submitted` onboarding version. The claim-bound
read and mutation require tenant `data-rights.execute`, preserve Auth identity
and source facts, and fail closed after authority moves into claim approval or
provisioning. Workspaces records an append-only replay receipt, exports its
bounded accountability proof without the request fingerprint, and completes
the central case through its own durable outbox.

Enrollment-link resubmission and correction POST recheck the authoritative
Organizations claim while holding the existing source/application lease.
Only no retained claim or one exact-coordinate `Pending` claim with a strictly
future decision deadline remains editable. A retained `Pending` claim with a
missing or elapsed deadline, or unknown, terminal, or coordinate-mismatched
authority, returns conflict without changing staged profile data. Ordinary
invitation resubmission still relies on the local
invitation-token lifecycle, but an approved invitation Data Rights
correction has only the local `Submitted`/version fence because the current
Organizations contracts do not publish an invitation-status inspector. The
correction-target GET also remains an optimistic local preview; the
enrollment-link correction POST is the authoritative external fence. A future
invitation inspector must use the same lease ordering before closing that
residual; it must not introduce a Staff-to-Workspaces reverse lock edge.

An approved Staff Rights restriction may independently suspend ordinary
processing of one exact `staff-onboarding` record while Workspaces still owns
its applicant data. Operational reads, resubmission, admission, actionable
lists, and provisioning fail closed through a versioned restriction
projection, while expiry, rejection, retention, correction, and access-safety
work remain available. Apply and release share the onboarding operation lock
hierarchy with provisioning, use append-only replay receipts, export bounded
proof, and emit a durable final-release event so an interrupted provisioning
attempt can resume. Destructive Data Rights execution remains deferred until
its owner-local authority and required companion records are specified.

The engineering defaults live under
`Workspaces:StaffOnboardingRetention`: a two-hour source-expiry grace period,
a 20-hour authoritative-inspection ceiling, a 60-minute schedule interval,
and a batch size of 50. The authority ceiling must remain below
Organizations' minimum one-day enrollment-history guarantee.

The catalogue contains engineering defaults only. It does not approve legal
bases, retention periods, country operation, or data-subject exceptions.
