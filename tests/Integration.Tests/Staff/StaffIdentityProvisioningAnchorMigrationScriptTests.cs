namespace Integration.Tests.Staff;

using BunkFy.Modules.Staff.Persistence;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.RegularExpressions;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffIdentityProvisioningAnchorMigrationScriptTests
{
    private const string PreviousMigration =
        "20260822003301_AddStaffRetentionControlProofAuditIntegrity";
    private const string AnchorMigration =
        "20261102220100_AddStaffIdentityProvisioningAnchors";

    [Fact]
    public void Upgrade_requires_fail_fast_writer_drain_and_fails_all_Closing_stages()
    {
        string script = NormalizeWhitespace(GenerateUpgradeScript());

        Assert.Contains(
            "LOCK TABLE \"staff\".\"tenant_revisions\" IN SHARE MODE NOWAIT",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "LOCK TABLE \"staff\".\"member_mutation_operations\" IN SHARE MODE NOWAIT",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "LOCK TABLE \"staff\".\"staff_members\" IN SHARE MODE NOWAIT",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE state.\"LifecycleStatus\" = 2",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Cannot install Staff identity provisioning anchors while a tenant destruction is in progress",
            script,
            StringComparison.Ordinal);
        int createTable = script.IndexOf(
            "CREATE TABLE",
            StringComparison.Ordinal);
        Assert.True(createTable > 0);
        Assert.DoesNotContain(
            "operation.\"Stage\"",
            script[..createTable],
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(18)]
    public void Upgrade_does_not_bypass_an_in_flight_legacy_destroy_stage(
        int legacyStage)
    {
        string script = NormalizeWhitespace(GenerateUpgradeScript());

        Assert.Contains(
            "WHERE state.\"LifecycleStatus\" = 2",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"\"Stage\" = {legacyStage}",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Backfill_is_same_schema_exact_Kind8_and_excludes_closed_scopes()
    {
        string script = NormalizeWhitespace(GenerateUpgradeScript());
        int backfillStart = script.IndexOf(
            "INSERT INTO \"staff\".\"identity_provisioning_anchors\"",
            StringComparison.Ordinal);
        int triggerStart = script.IndexOf(
            "CREATE FUNCTION \"staff\".prevent_identity_provisioning_anchor_mutation",
            backfillStart,
            StringComparison.Ordinal);
        Assert.True(backfillStart >= 0);
        Assert.True(triggerStart > backfillStart);
        string backfill = script[backfillStart..triggerStart];

        Assert.Contains(
            "FROM \"staff\".\"member_mutation_operations\" operation",
            backfill,
            StringComparison.Ordinal);
        Assert.Contains(
            "operation.\"Kind\" = 8",
            backfill,
            StringComparison.Ordinal);
        Assert.Contains(
            "COALESCE(state.\"LifecycleStatus\", 1) = 1",
            backfill,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "workspaces",
            backfill,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "AuthSubject",
            backfill,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ON CONFLICT",
            backfill,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Downgrade_serializes_close_destroy_and_anchor_insert_before_guard()
    {
        string script = NormalizeWhitespace(GenerateDowngradeScript());

        int revisionLock = script.IndexOf(
            "LOCK TABLE \"staff\".\"tenant_revisions\" IN SHARE MODE",
            StringComparison.Ordinal);
        int destroyLock = script.IndexOf(
            "LOCK TABLE \"staff\".\"tenant_destroy_operations\" IN SHARE MODE",
            StringComparison.Ordinal);
        int anchorLock = script.IndexOf(
            "LOCK TABLE \"staff\".\"identity_provisioning_anchors\"",
            StringComparison.Ordinal);
        int guard = script.IndexOf(
            "Cannot remove Staff identity provisioning anchors while durable anchors exist",
            StringComparison.Ordinal);
        int stageRemap = script.IndexOf(
            "UPDATE \"staff\".\"tenant_destroy_operations\"",
            StringComparison.Ordinal);
        int drop = script.LastIndexOf("DROP TABLE", StringComparison.Ordinal);

        Assert.True(revisionLock >= 0);
        Assert.True(destroyLock > revisionLock);
        Assert.True(anchorLock > destroyLock);
        Assert.Contains("IN ACCESS EXCLUSIVE MODE", script, StringComparison.Ordinal);
        Assert.True(guard > anchorLock);
        Assert.True(stageRemap > guard);
        Assert.True(drop > stageRemap);
        Assert.Contains(
            "identity_provisioning_anchors",
            script[drop..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void Anchor_fk_and_immutable_trigger_require_the_exact_destroy_stage()
    {
        string script = NormalizeWhitespace(GenerateUpgradeScript());

        Assert.Contains("ON DELETE RESTRICT", script, StringComparison.Ordinal);
        Assert.Contains(
            "destroy_stage = '24'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "operation.\"Stage\" IN (17, 24)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.\"LifecycleStatus\" = 2",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.\"DestroyOperationId\" = operation.\"OperationId\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "state.\"DestroyRequestSha256\" = operation.\"RequestSha256\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Staff identity provisioning anchors are immutable",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Resolution_and_legacy_writer_backstops_are_deferred_and_exact()
    {
        string script = NormalizeWhitespace(GenerateUpgradeScript());

        Assert.DoesNotContain(
            "workspace_onboarding_resolution_event_id",
            script,
            StringComparison.Ordinal);
        Assert.Contains("gen_random_uuid()", script, StringComparison.Ordinal);
        Assert.Contains(
            "CREATE UNIQUE INDEX \"IX_identity_provisioning_anchors_ResolutionEventId\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "FOREIGN KEY (\"ScopeId\", \"SourceKind\", \"SourceId\", \"StaffMemberId\")",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "destroy_stage = '25'",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION \"staff\".enforce_identity_provisioning_anchor_resolution_integrity()",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "anchor.\"ResolutionEventId\" = NEW.\"ResolutionEventId\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION \"staff\".enforce_workspace_onboarding_receipt_anchor()",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Workspace onboarding receipt requires an exact Staff identity provisioning anchor",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Unresolved Workspace onboarding receipt cannot be removed",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Unresolved Workspace onboarding anchor blocks Staff identity lifecycle mutation",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Workspace onboarding anchor requires access closure before Staff Auth subject change",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "NOT (OLD.\"Status\" <> 4 AND NEW.\"Status\" = 4 AND NEW.\"AuthSubjectId\" IS NULL)",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "NEW.\"Status\" = 4 AND OLD.\"Status\" <> 4 AND NEW.\"AuthSubjectId\" IS NOT NULL",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "NEW.\"Status\" = 1 AND OLD.\"Status\" <> 1",
            script,
            StringComparison.Ordinal);
    }

    private static string GenerateUpgradeScript()
    {
        DbContextOptions<StaffDbContext> options =
            new DbContextOptionsBuilder<StaffDbContext>()
                .UseNpgsql(
                    "Host=127.0.0.1;Database=script_only;Username=script_only;Password=script_only",
                    provider => provider
                        .MigrationsAssembly(
                            StaffMigrations.PostgreSqlAssembly)
                        .MigrationsHistoryTable(
                            StaffMigrations.HistoryTable,
                            StaffMigrations.Schema))
                .Options;
        using StaffDbContext context = new(
            options,
            new TestScopeContext());
        return context.Database.GetService<IMigrator>().GenerateScript(
            PreviousMigration,
            AnchorMigration);
    }

    private static string GenerateDowngradeScript()
    {
        DbContextOptions<StaffDbContext> options =
            new DbContextOptionsBuilder<StaffDbContext>()
                .UseNpgsql(
                    "Host=127.0.0.1;Database=script_only;Username=script_only;Password=script_only",
                    provider => provider
                        .MigrationsAssembly(
                            StaffMigrations.PostgreSqlAssembly)
                        .MigrationsHistoryTable(
                            StaffMigrations.HistoryTable,
                            StaffMigrations.Schema))
                .Options;
        using StaffDbContext context = new(
            options,
            new TestScopeContext());
        return context.Database.GetService<IMigrator>().GenerateScript(
            AnchorMigration,
            PreviousMigration);
    }

    private static string NormalizeWhitespace(string value) =>
        Regex.Replace(value, "\\s+", " ");

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId =>
            "10000000-0000-0000-0000-000000000001";
    }
}
