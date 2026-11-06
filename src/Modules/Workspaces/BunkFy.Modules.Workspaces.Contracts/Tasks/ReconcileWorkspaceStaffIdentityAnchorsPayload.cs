namespace BunkFy.Modules.Workspaces.Contracts;

using Gma.Framework.Scoping;
using Gma.Framework.Tasks;

[TaskName(TaskName)]
[TaskPayloadVersion(PayloadVersion)]
[TaskDescription(
    "Reconcile migration-backfilled Staff identity anchors with Workspaces onboarding records.")]
[TaskKind(ModuleTaskKind.Recurring)]
[TaskWorkerGroup(WorkerGroup)]
[SupportsTaskControl]
[ScopeAware]
public sealed record ReconcileWorkspaceStaffIdentityAnchorsPayload(
    int BatchSize = ReconcileWorkspaceStaffIdentityAnchorsPayload
        .DefaultBatchSize,
    int MaxBatches = ReconcileWorkspaceStaffIdentityAnchorsPayload
        .DefaultMaxBatches) : ITaskPayload
{
    public const string TaskName = "reconcile-staff-identity-anchors";
    public const string WorkerGroup = "workspaces-maintenance-workers";
    public const int PayloadVersion = 1;
    public const int DefaultBatchSize = 100;
    public const int MaximumBatchSize = 500;
    public const int DefaultMaxBatches = 10;
    public const int MaximumBatches = 100;
}
