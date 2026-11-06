namespace BunkFy.Modules.Staff.Tests.Persistence;

using System.Text.Json;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.Staff.Application.Ports;
using BunkFy.Modules.Staff.Contracts;
using BunkFy.Modules.Staff.Domain.Aggregates;
using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Entities;
using BunkFy.Modules.Staff.Domain.Governance;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Persistence;
using BunkFy.Modules.Staff.Persistence.Repositories;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffDataRightsExportContributorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Export_is_catalogue_versioned_and_contains_profile_governance_assignments_holds_and_update_operations()
    {
        await using StaffDbContext dbContext = CreateDbContext("tenant-a");
        StaffMember member = CreateMember("tenant-a");
        Guid propertyId = Guid.NewGuid();
        StaffPropertyAssignment assignment = member.AssignProperty(
            Guid.NewGuid(),
            propertyId,
            "Front desk",
            isPrimary: true,
            new DateOnly(2026, 1, 1),
            member.Version,
            "user:manager",
            Guid.NewGuid(),
            Now.AddMinutes(1)).Value;
        Assert.True(member.UnassignProperty(
            propertyId,
            new DateOnly(2026, 7, 26),
            member.Version,
            "user:manager",
            "Moved to another property.",
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        StaffEmploymentGovernance governance = CreateGovernance(
            member,
            Now.AddMinutes(3));
        StaffDataHold activeHold = StaffDataHold.Place(
            Guid.NewGuid(),
            "tenant-a",
            member.Id,
            StaffDataHoldReasonCodes.RegulatoryRequest,
            "user:privacy",
            Now.AddMinutes(4)).Value;
        StaffDataHold releasedHold = StaffDataHold.Place(
            Guid.NewGuid(),
            "tenant-a",
            member.Id,
            StaffDataHoldReasonCodes.Dispute,
            "user:privacy",
            Now.AddMinutes(5)).Value;
        Assert.True(releasedHold.Release(
            expectedVersion: 1,
            "user:decision-maker",
            Now.AddMinutes(6)).IsSuccess);
        dbContext.StaffMembers.Add(member);
        Guid operationId = Guid.NewGuid();
        dbContext.MemberMutationOperations.Add(
            new StaffMemberMutationOperation(
                new StaffMemberMutationOperationRecord(
                    operationId,
                    member.ScopeId,
                    member.Id,
                    StaffMemberMutationKind.ProfileUpdate,
                    member.Version,
                    new string('b', 64),
                    StaffStatus.Active,
                    member.Version,
                    Now.AddMinutes(7))));
        Guid anchorSourceId = Guid.NewGuid();
        Guid anchorResolutionEventId = Guid.NewGuid();
        dbContext.IdentityProvisioningAnchors.Add(
            new StaffIdentityProvisioningAnchor(
                new StaffIdentityProvisioningAnchorRecord(
                    member.ScopeId,
                    StaffIdentityProvisioningSourceKind
                        .WorkspaceOnboarding,
                    anchorSourceId,
                    member.Id,
                    Now.AddMinutes(8),
                    anchorResolutionEventId)));
        dbContext.EmploymentGovernance.Add(governance);
        dbContext.DataHolds.AddRange(activeHold, releasedHold);
        await dbContext.SaveChangesAsync();
        StaffDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            Request("tenant-a", member.Id, member.Version),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        Assert.Equal(7, result.RecordCount);
        Assert.Equal("staff.personal-data", contributor.Descriptor.CatalogId);
        Assert.Equal(
            StaffTenantTerminationMetadata.PersonalDataCatalogVersion,
            contributor.Descriptor.CatalogVersion);
        Assert.Equal(
            StaffDataRightsExportSchema.ExportSchemaId,
            contributor.Descriptor.ExportSchemaId);
        Assert.Equal(5, contributor.Descriptor.ExportSchemaVersion);
        Assert.DoesNotContain("staff.scope-id", contributor.Descriptor.FieldIds);
        Assert.DoesNotContain("staff.audit-actor-id", contributor.Descriptor.FieldIds);
        Assert.DoesNotContain("staff.change-reason", contributor.Descriptor.FieldIds);
        Assert.DoesNotContain(
            "staff.governance.actor-id",
            contributor.Descriptor.FieldIds);
        Assert.DoesNotContain(
            "staff.data-hold.actor-id",
            contributor.Descriptor.FieldIds);
        Assert.Contains(
            "staff.identity-provisioning-anchor.source-kind",
            contributor.Descriptor.FieldIds);
        Assert.Contains(
            "staff.identity-provisioning-anchor.resolution-event-id",
            contributor.Descriptor.FieldIds);

        DataRightsExportRecord profileRecord = Assert.Single(
            sink.Records,
            record => record.RecordType == StaffDataRightsDiscoveryContributor.ProfileRecordType);
        Assert.Equal(member.Id, profileRecord.RecordId);
        Assert.Equal("Maya Chen", Field(profileRecord, "staff.display-name").GetString());
        Assert.Equal(
            "maya.chen@example.test",
            Field(profileRecord, "staff.work-email").GetString());
        Assert.Equal("active", Field(profileRecord, "staff.status").GetString());

        DataRightsExportRecord assignmentRecord = Assert.Single(
            sink.Records,
            record => record.RecordType == StaffDataRightsExportContributor.AssignmentRecordType);
        Assert.Equal(assignment.Id, assignmentRecord.RecordId);
        Assert.Equal(member.Id, Field(assignmentRecord, "staff.staff-member-id").GetGuid());
        Assert.Equal(propertyId, Field(assignmentRecord, "staff.property-id").GetGuid());
        Assert.False(Field(assignmentRecord, "staff.assignment-is-current").GetBoolean());
        Assert.Equal(assignment.UnassignedAtVersion, assignmentRecord.RecordVersion);

        DataRightsExportRecord governanceRecord = Assert.Single(
            sink.Records,
            record =>
                record.RecordType ==
                    StaffDataRightsExportContributor
                        .EmploymentGovernanceRecordType);
        Assert.Equal(member.Id, governanceRecord.RecordId);
        Assert.Equal(
            "GB",
            Field(
                governanceRecord,
                "staff.governance.operating-country-code")
                .GetString());
        Assert.Equal(
            "operator-notice:1",
            Assert.Single(
                Field(
                        governanceRecord,
                        "staff.governance.accepted-acknowledgements")
                    .EnumerateArray()).GetString());

        DataRightsExportRecord[] holdRecords = sink.Records
            .Where(record =>
                record.RecordType ==
                    StaffDataRightsExportContributor.DataHoldRecordType)
            .ToArray();
        Assert.Equal(2, holdRecords.Length);
        Assert.Contains(holdRecords, record =>
            Field(record, "staff.data-hold.reason-code")
                .GetString() ==
            StaffDataHoldReasonCodes.RegulatoryRequest);
        Assert.All(holdRecords, record =>
            Assert.DoesNotContain(
                record.Fields,
                field =>
                    field.FieldId ==
                    "staff.data-hold.actor-id"));

        DataRightsExportRecord updateOperationRecord = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                StaffDataRightsExportContributor
                    .MemberMutationOperationRecordType);
        Assert.Equal(
            operationId,
            Field(
                updateOperationRecord,
                "staff.member-mutation-operation.id").GetGuid());
        Assert.Equal(
            new string('b', 64),
            Field(
                updateOperationRecord,
                "staff.member-mutation-operation.request-fingerprint")
                .GetString());
        Assert.Equal(
            member.Version,
            updateOperationRecord.RecordVersion);

        DataRightsExportRecord anchorRecord = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                StaffDataRightsExportContributor
                    .IdentityProvisioningAnchorRecordType);
        Assert.Equal(1, anchorRecord.RecordVersion);
        Assert.Equal(5, anchorRecord.Fields.Count);
        Assert.InRange(
            anchorRecord.Fields.Count,
            1,
            DataRightsExportLimits.MaxFieldsPerRecord);
        Assert.Equal(
            member.Id,
            Field(
                anchorRecord,
                "staff.identity-provisioning-anchor.staff-member-id")
                .GetGuid());
        Assert.Equal(
            "workspace-onboarding",
            Field(
                anchorRecord,
                "staff.identity-provisioning-anchor.source-kind")
                .GetString());
        Assert.Equal(
            anchorSourceId,
            Field(
                anchorRecord,
                "staff.identity-provisioning-anchor.source-id")
                .GetGuid());
        Assert.Equal(
            anchorResolutionEventId,
            Field(
                anchorRecord,
                "staff.identity-provisioning-anchor.resolution-event-id")
                .GetGuid());
        Assert.Equal(
            Now.AddMinutes(8),
            Field(
                anchorRecord,
                "staff.identity-provisioning-anchor.anchored-at-utc")
                .GetDateTimeOffset());

        CollectingSink replay = new();
        DataRightsSubjectExportResult replayResult =
            await contributor.ExportAsync(
                Request("tenant-a", member.Id, member.Version),
                replay,
                CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, replayResult.Status);
        Assert.Equal(result.RecordCount, replayResult.RecordCount);
        Assert.Equal(
            sink.Records.Select(RecordIdentity),
            replay.Records.Select(RecordIdentity));
    }

    [Fact]
    public async Task Export_keeps_anchor_after_profile_anonymisation_when_mutation_journal_is_absent()
    {
        await using StaffDbContext dbContext = CreateDbContext("tenant-a");
        StaffMember member = CreateMember("tenant-a");
        Assert.True(member.Depart(
            new DateOnly(2026, 7, 26),
            member.Version,
            "user:manager",
            "Employment ended.",
            Guid.NewGuid(),
            [],
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(member.Anonymise(
            member.Version,
            "user:privacy",
            Guid.NewGuid(),
            Now.AddMinutes(2)).IsSuccess);
        Guid sourceId = Guid.NewGuid();
        dbContext.StaffMembers.Add(member);
        dbContext.IdentityProvisioningAnchors.Add(
            new StaffIdentityProvisioningAnchor(
                new StaffIdentityProvisioningAnchorRecord(
                    member.ScopeId,
                    StaffIdentityProvisioningSourceKind
                        .OrganizationMembership,
                    sourceId,
                    member.Id,
                    Now.AddMinutes(-1))));
        await dbContext.SaveChangesAsync();
        StaffDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            Request("tenant-a", member.Id, member.Version),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Succeeded, result.Status);
        Assert.Equal(2, result.RecordCount);
        Assert.Empty(await dbContext.MemberMutationOperations.ToListAsync());
        DataRightsExportRecord anchorRecord = Assert.Single(
            sink.Records,
            record => record.RecordType ==
                StaffDataRightsExportContributor
                    .IdentityProvisioningAnchorRecordType);
        Assert.Equal(
            sourceId,
            Field(
                anchorRecord,
                "staff.identity-provisioning-anchor.source-id")
                .GetGuid());
    }

    [Fact]
    public async Task Export_fails_closed_for_stale_or_cross_scope_coordinate_without_writing()
    {
        await using StaffDbContext dbContext = CreateDbContext("tenant-a");
        StaffMember member = CreateMember("tenant-a");
        dbContext.StaffMembers.Add(member);
        await dbContext.SaveChangesAsync();
        StaffDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink staleSink = new();
        CollectingSink wrongTenantSink = new();
        CollectingSink propertySink = new();

        DataRightsSubjectExportResult stale = await contributor.ExportAsync(
            Request("tenant-a", member.Id, member.Version + 1),
            staleSink,
            CancellationToken.None);
        DataRightsSubjectExportResult wrongTenant = await contributor.ExportAsync(
            Request("tenant-b", member.Id, member.Version),
            wrongTenantSink,
            CancellationToken.None);
        DataRightsSubjectExportResult propertyScoped = await contributor.ExportAsync(
            new(
                "tenant-a",
                DataRightsCaseType.StaffRights,
                Guid.NewGuid(),
                new(
                    StaffDataRightsDiscoveryContributor.Owner,
                    StaffDataRightsDiscoveryContributor.ProfileRecordType,
                    member.Id,
                    member.Version)),
            propertySink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.Stale, stale.Status);
        Assert.Equal(DataRightsSubjectExportStatus.ScopeUnavailable, wrongTenant.Status);
        Assert.Equal(DataRightsSubjectExportStatus.ScopeUnavailable, propertyScoped.Status);
        Assert.Empty(staleSink.Records);
        Assert.Empty(wrongTenantSink.Records);
        Assert.Empty(propertySink.Records);
    }

    [Fact]
    public async Task Assignment_limit_is_checked_before_any_export_record_is_written()
    {
        await using StaffDbContext dbContext = CreateDbContext("tenant-a");
        StaffMember member = CreateMember("tenant-a");
        for (int index = 0;
             index <= StaffDataRightsExportContributor.MaximumAssignmentRecords;
             index++)
        {
            Assert.True(member.AssignProperty(
                Guid.NewGuid(),
                Guid.NewGuid(),
                propertyJobTitle: null,
                isPrimary: false,
                new DateOnly(2026, 1, 1),
                member.Version,
                "user:manager",
                Guid.NewGuid(),
                Now.AddSeconds(index + 1)).IsSuccess);
        }

        dbContext.StaffMembers.Add(member);
        await dbContext.SaveChangesAsync();
        StaffDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result = await contributor.ExportAsync(
            Request("tenant-a", member.Id, member.Version),
            sink,
            CancellationToken.None);

        Assert.Equal(DataRightsSubjectExportStatus.ScopeUnavailable, result.Status);
        Assert.Empty(sink.Records);
    }

    [Fact]
    public async Task Hold_limit_is_checked_before_any_export_record_is_written()
    {
        await using StaffDbContext dbContext =
            CreateDbContext("tenant-a");
        StaffMember member = CreateMember("tenant-a");
        dbContext.StaffMembers.Add(member);
        for (int index = 0;
             index <=
             StaffDataRightsExportContributor.MaximumHoldRecords;
             index++)
        {
            dbContext.DataHolds.Add(StaffDataHold.Place(
                Guid.NewGuid(),
                "tenant-a",
                member.Id,
                StaffDataHoldReasonCodes.LegalObligation,
                "user:privacy",
                Now.AddSeconds(index + 1)).Value);
        }

        await dbContext.SaveChangesAsync();
        StaffDataRightsExportContributor contributor =
            new(dbContext, new TestScopeContext("tenant-a"));
        CollectingSink sink = new();

        DataRightsSubjectExportResult result =
            await contributor.ExportAsync(
                Request(
                    "tenant-a",
                    member.Id,
                    member.Version),
                sink,
                CancellationToken.None);

        Assert.Equal(
            DataRightsSubjectExportStatus.ScopeUnavailable,
            result.Status);
        Assert.Empty(sink.Records);
    }

    private static DataRightsSubjectExportRequest Request(
        string tenantId,
        Guid staffMemberId,
        long version) =>
        new(
            tenantId,
            DataRightsCaseType.StaffRights,
            PropertyId: null,
            new(
                StaffDataRightsDiscoveryContributor.Owner,
                StaffDataRightsDiscoveryContributor.ProfileRecordType,
                staffMemberId,
                version));

    private static StaffMember CreateMember(string tenantId) =>
        StaffMember.Create(
            Guid.NewGuid(),
            tenantId,
            "Maya Chen",
            "Maya Q. Chen",
            "maya.chen@example.test",
            "+44 20 1234 5678",
            "EMP-42",
            "Manager",
            "Operations",
            "account-maya",
            "user:test",
            Guid.NewGuid(),
            Now).Value;

    private static StaffEmploymentGovernance CreateGovernance(
        StaffMember member,
        DateTimeOffset configuredAtUtc) =>
        StaffEmploymentGovernance.Configure(
            member.ScopeId,
            member.Id,
            member.Version,
            StaffEmploymentGovernanceBinding.Create(
                "GB",
                "staff-test",
                1,
                "eu-west-2",
                "uk-no-transfer",
                "staff-employment",
                1,
                new string('a', 64),
                new DateTimeOffset(
                    2026,
                    1,
                    1,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
                new DateTimeOffset(
                    2027,
                    1,
                    1,
                    0,
                    0,
                    0,
                    TimeSpan.Zero),
                configuredAtUtc.AddMinutes(-1)).Value,
            [
                StaffEmploymentGovernanceAcknowledgement.Create(
                    "operator-notice",
                    1).Value
            ],
            "user:privacy",
            configuredAtUtc).Value;

    private static JsonElement Field(DataRightsExportRecord record, string fieldId) =>
        Assert.Single(record.Fields, field => field.FieldId == fieldId).Value;

    private static string RecordIdentity(DataRightsExportRecord record) =>
        string.Join(
            '|',
            record.RecordType,
            record.RecordId,
            record.RecordVersion,
            string.Join(
                ';',
                record.Fields.Select(field =>
                    $"{field.FieldId}={field.Value.GetRawText()}")));

    private static StaffDbContext CreateDbContext(string tenantId)
    {
        DbContextOptions<StaffDbContext> options =
            new DbContextOptionsBuilder<StaffDbContext>()
                .UseInMemoryDatabase($"staff-data-rights-export-{Guid.NewGuid():N}")
                .Options;
        return new(options, new TestScopeContext(tenantId));
    }

    private sealed class CollectingSink : IDataRightsExportSink
    {
        public List<DataRightsExportRecord> Records { get; } = [];

        public ValueTask WriteAsync(
            DataRightsExportRecord record,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            this.Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestScopeContext(string scopeId) : IScopeContext
    {
        public bool IsEnabled => true;
        public string ScopeId { get; } = scopeId;
    }
}
