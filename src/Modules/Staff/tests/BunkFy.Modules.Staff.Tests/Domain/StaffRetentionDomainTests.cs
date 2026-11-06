namespace BunkFy.Modules.Staff.Tests;

using BunkFy.Modules.Staff.Domain.DataRights;
using BunkFy.Modules.Staff.Domain.Models;
using BunkFy.Modules.Staff.Domain.Retention;
using Xunit;

[Trait("Category", "Unit")]
public sealed class StaffRetentionDomainTests
{
    private const string TenantId =
        "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void Receipt_and_tombstone_preserve_retention_authority()
    {
        DateTimeOffset departedAtUtc =
            StaffRetentionTestData.Now.AddDays(-400);
        DateTimeOffset deadlineUtc =
            departedAtUtc.AddDays(365);
        Guid eventId = Guid.NewGuid();
        Guid staffMemberId = Guid.NewGuid();
        StaffMemberAnonymisationOutcome outcome = new(
            4,
            5,
            eventId,
            StaffRetentionTestData.Now);

        StaffRetentionAnonymisationReceipt receipt =
            StaffRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                TenantId,
                Guid.NewGuid(),
                staffMemberId,
                outcome,
                8,
                9,
                departedAtUtc,
                deadlineUtc,
                new string('a', 64)).Value;
        StaffAnonymisationTombstone tombstone =
            StaffAnonymisationTombstone.CreateForRetention(
                receipt).Value;

        Assert.Equal(
            StaffAnonymisationAuthority.Retention,
            tombstone.Authority);
        Assert.True(tombstone.MatchesRetention(receipt));
        Assert.Null(tombstone.LedgerEntryId);
        Assert.Null(tombstone.LastReplayedAtUtc);
        Assert.Equal(64, receipt.CanonicalSha256.Length);
        Assert.True(receipt.HasValidCanonicalProof());
    }

    [Fact]
    public void Execution_and_checkpoint_replay_exactly()
    {
        Guid executionId = Guid.NewGuid();
        StaffRetentionExecution execution =
            StaffRetentionExecution.Start(
                executionId,
                TenantId,
                "staff-employment",
                1,
                1,
                12,
                StaffRetentionTestData.Now,
                StaffRetentionTestData.Now.AddMinutes(15)).Value;
        StaffRetentionSweepCheckpoint checkpoint =
            StaffRetentionSweepCheckpoint.Create(
                Guid.NewGuid(),
                TenantId,
                "staff-employment",
                1,
                StaffRetentionTestData.Now).Value;

        Assert.True(execution.RecordAffected().IsSuccess);
        Assert.True(execution.Complete(
            StaffRetentionExecutionState.Completed,
            attempt: 1,
            1,
            0,
            "staff.staff-employment.completed",
            StaffRetentionTestData.Now.AddMinutes(1),
            null).IsSuccess);
        Assert.True(checkpoint.Advance(
            0,
            12,
            executionId,
            StaffRetentionTestData.Now.AddMinutes(1)).IsSuccess);
        Assert.True(checkpoint.Advance(
            0,
            12,
            executionId,
            StaffRetentionTestData.Now.AddMinutes(1)).IsSuccess);

        Assert.Equal(1, execution.AffectedCount);
        Assert.Equal(12, checkpoint.AfterProjectionOrdinal);
    }
}
