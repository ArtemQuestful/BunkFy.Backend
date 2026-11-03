namespace BunkFy.Host.Migrations;

// The published convergence migration sorts before two migrations already
// shipped on dev. Admit only this exact, reviewed transition; arbitrary gaps
// and future catalogues still require an exact-prefix history.
internal static class InventoryConvergenceHistoryTransition
{
    internal const string Name = "inventory-convergence-v1";
    internal const string MigrationId =
        "20260812082324_AddInventoryManualBlockGroupConvergence";

    private static readonly string[] PublishedHistory =
    [
        "20260710192324_InitialCreate",
        "20260712005508_AddAllocationAmendments",
        "20260712211655_AddInboxRetentionIndex",
        "20260714073952_AddManualInventoryBlockGroups",
        "20260714205750_AddRoomAvailabilityFenceAndBedRetirements",
        "20260715064134_AddRoomRetirements",
        "20260726231750_AddInventoryAllocationAnonymisationOwnerProof",
        "20260731183224_AddInventoryTenantExportRevision",
        "20260804114733_AddInventoryTenantDestructionLifecycle",
        "20260807222200_AddInventoryManagementOperations",
        "20260809004643_AddInventoryManualBlockManagementOperations",
        "20260809015632_AddInventoryRetirementManagementOperations",
        "20260811135229_AddInventoryRetirementCancellation",
        "20260821144203_AddInventoryAuthoritativeStateIntegrity",
        "20260821195120_AddInventoryAllocationAmendmentDecisionTenantIntegrity"
    ];

    private static readonly string[] TargetHistory =
        [.. PublishedHistory.Append(MigrationId).Order(StringComparer.Ordinal)];

    internal static bool Matches(string moduleName, string[] target, string[] applied) =>
        string.Equals(moduleName, "inventory", StringComparison.Ordinal) &&
        applied.Length <= PublishedHistory.Length &&
        PublishedHistory.AsSpan(0, applied.Length).SequenceEqual(applied) &&
        TargetHistory.SequenceEqual(target, StringComparer.Ordinal);
}
