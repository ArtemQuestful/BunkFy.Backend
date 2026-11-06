namespace BunkFy.Modules.Guests.Tests;

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BunkFy.Modules.Guests.Domain.Retention;
using BunkFy.TimeZones;
using Xunit;

[Trait("Category", "Unit")]
public sealed class GuestRetentionDomainTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Terminal_execution_cannot_underreport_scanned_work()
    {
        GuestRetentionExecution execution = Start();
        Assert.True(execution.RecordAffected().IsSuccess);
        Assert.True(execution.RecordAffected().IsSuccess);

        Assert.Equal(
            "Guests.RetentionExecutionResultInvalid",
            execution.Complete(
                GuestRetentionExecutionState.Completed,
                attempt: execution.Attempt,
                scannedCount: 1,
                remainingCount: 0,
                "guests.guest-operational.completed",
                Now.AddMinutes(1),
                holdReviewDueAtUtc: null).Error.Code);
        Assert.Equal(
            GuestRetentionExecutionState.Running,
            execution.State);

        Assert.True(execution.Complete(
            GuestRetentionExecutionState.Completed,
            attempt: execution.Attempt,
            scannedCount: 2,
            remainingCount: 0,
            "guests.guest-operational.completed",
            Now.AddMinutes(1),
            holdReviewDueAtUtc: null).IsSuccess);
    }

    [Fact]
    public void Retry_must_move_execution_time_forward()
    {
        GuestRetentionExecution execution = Start();

        Assert.Equal(
            "Guests.RetentionExecutionTransitionInvalid",
            execution.BeginRetry(
                attempt: 2,
                Now.AddMinutes(-1),
                Now.AddMinutes(10)).Error.Code);
        Assert.True(execution.BeginRetry(
            attempt: 2,
            Now.AddMinutes(1),
            Now.AddMinutes(11)).IsSuccess);
        Assert.Equal(2, execution.Attempt);
    }

    [Fact]
    public void New_running_execution_requires_policy_v2_or_later()
    {
        Assert.Equal(
            "Guests.RetentionExecutionCoordinateInvalid",
            GuestRetentionExecution.Start(
                Guid.NewGuid(),
                "tenant-a",
                "guest-operational",
                executionPolicyVersion:
                    GuestRetentionExecution.MinimumRunningPolicyVersion - 1,
                attempt: 1,
                startingProjectionOrdinal: 0,
                Now,
                Now.AddMinutes(15)).Error.Code);
    }

    [Fact]
    public void Terminal_execution_rejects_non_canonical_outcome_codes()
    {
        GuestRetentionExecution execution = Start();

        Assert.Equal(
            "Guests.RetentionExecutionResultInvalid",
            execution.Complete(
                GuestRetentionExecutionState.Completed,
                attempt: execution.Attempt,
                scannedCount: 0,
                remainingCount: 0,
                "guests/guest-operational/completed",
                Now.AddMinutes(1),
                holdReviewDueAtUtc: null).Error.Code);
        Assert.Equal(
            GuestRetentionExecutionState.Running,
            execution.State);
    }

    [Fact]
    public void Receipt_rejects_unbounded_property_evidence()
    {
        Assert.Equal(
            "Guests.RetentionReceiptInvalid",
            GuestRetentionAnonymisationReceipt.Create(
                Guid.NewGuid(),
                "tenant-a",
                Guid.NewGuid(),
                Guid.NewGuid(),
                selectedGuestVersion: 1,
                resultingGuestVersion: 2,
                GuestRetentionAnonymisationReceipt
                    .MaximumAffectedProperties + 1,
                Now.AddDays(-1),
                new string('a', 64),
                TimeZoneCatalog.Default.CatalogVersion,
                Guid.NewGuid(),
                "system:retention",
                Now).Error.Code);
    }

    [Fact]
    public void Receipt_rejects_non_system_attribution()
    {
        Assert.Equal(
            "Guests.RetentionReceiptInvalid",
            CreateReceipt(
                TimeZoneCatalog.Default.CatalogVersion,
                "user:operator").Error.Code);
    }

    [Fact]
    public void Receipt_v2_requires_bounded_control_free_catalog_evidence()
    {
        foreach (string invalidCatalogVersion in new[]
                 {
                     string.Empty,
                     new string('a', GuestRetentionAnonymisationReceipt
                         .TimeZoneCatalogVersionMaxLength + 1),
                     "TZDB: 2026c\nforged"
                 })
        {
            Assert.Equal(
                "Guests.RetentionReceiptInvalid",
                CreateReceipt(invalidCatalogVersion).Error.Code);
        }

        GuestRetentionAnonymisationReceipt receipt =
            CreateReceipt(TimeZoneCatalog.Default.CatalogVersion).Value;
        Assert.Equal(2, receipt.ContractVersion);
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            receipt.TimeZoneCatalogVersion);
        Assert.True(receipt.Matches(
            receipt.ExecutionId,
            receipt.GuestId,
            receipt.SelectedGuestVersion));
    }

    [Fact]
    public void Hydrated_v1_receipt_keeps_its_original_byte_proof_shape()
    {
        GuestRetentionAnonymisationReceipt receipt =
            CreateReceipt(TimeZoneCatalog.Default.CatalogVersion).Value;
        Set(receipt, nameof(receipt.ContractVersion), 1);
        Set(receipt, nameof(receipt.TimeZoneCatalogVersion), null);
        Set(
            receipt,
            nameof(receipt.CanonicalSha256),
            ComputeVersionOneCanonicalSha256(receipt));

        Assert.True(receipt.Matches(
            receipt.ExecutionId,
            receipt.GuestId,
            receipt.SelectedGuestVersion));

        Set(
            receipt,
            nameof(receipt.TimeZoneCatalogVersion),
            TimeZoneCatalog.Default.CatalogVersion);
        Assert.False(receipt.Matches(
            receipt.ExecutionId,
            receipt.GuestId,
            receipt.SelectedGuestVersion));
    }

    [Fact]
    public void Checkpoint_advance_is_optimistic_and_idempotent()
    {
        GuestRetentionSweepCheckpoint checkpoint =
            GuestRetentionSweepCheckpoint.Create(
                Guid.NewGuid(),
                "tenant-a",
                "guest-operational",
                Now).Value;
        Guid executionId = Guid.NewGuid();

        Assert.True(checkpoint.Advance(
            expectedAfterProjectionOrdinal: 0,
            nextAfterProjectionOrdinal: 42,
            executionId,
            Now.AddMinutes(1)).IsSuccess);
        Assert.True(checkpoint.Advance(
            expectedAfterProjectionOrdinal: 0,
            nextAfterProjectionOrdinal: 42,
            executionId,
            Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(
            "Guests.RetentionCheckpointConflict",
            checkpoint.Advance(
                expectedAfterProjectionOrdinal: 0,
                nextAfterProjectionOrdinal: 43,
                executionId,
                Now.AddMinutes(1)).Error.Code);
    }

    private static GuestRetentionExecution Start() =>
        GuestRetentionExecution.Start(
            Guid.NewGuid(),
            "tenant-a",
            "guest-operational",
            executionPolicyVersion:
                GuestRetentionExecution.MinimumRunningPolicyVersion,
            attempt: 1,
            startingProjectionOrdinal: 0,
            Now,
            Now.AddMinutes(15)).Value;

    private static Gma.Framework.Results.Result<
        GuestRetentionAnonymisationReceipt> CreateReceipt(
        string catalogVersion,
        string actorId = GuestRetentionAnonymisationReceipt.SystemActorId) =>
        GuestRetentionAnonymisationReceipt.Create(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "tenant-a",
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            selectedGuestVersion: 1,
            resultingGuestVersion: 2,
            affectedPropertyCount: 1,
            Now.AddDays(-1),
            new string('a', 64),
            catalogVersion,
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            actorId,
            Now);

    private static string ComputeVersionOneCanonicalSha256(
        GuestRetentionAnonymisationReceipt receipt)
    {
        StringBuilder canonical = new();
        Append(canonical, "1");
        Append(canonical, receipt.Id.ToString("N"));
        Append(canonical, receipt.ScopeId);
        Append(canonical, receipt.ExecutionId.ToString("N"));
        Append(canonical, receipt.GuestId.ToString("N"));
        Append(
            canonical,
            receipt.SelectedGuestVersion.ToString(
                CultureInfo.InvariantCulture));
        Append(
            canonical,
            receipt.ResultingGuestVersion.ToString(
                CultureInfo.InvariantCulture));
        Append(
            canonical,
            receipt.AffectedPropertyCount.ToString(
                CultureInfo.InvariantCulture));
        Append(
            canonical,
            receipt.RetentionDeadlineUtc.ToString(
                "O",
                CultureInfo.InvariantCulture));
        Append(canonical, receipt.PolicySetSha256);
        Append(canonical, receipt.EventId.ToString("N"));
        Append(canonical, receipt.ActorId);
        Append(
            canonical,
            receipt.CompletedAtUtc.ToString(
                "O",
                CultureInfo.InvariantCulture));
        return Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }

    private static void Append(StringBuilder target, string value)
    {
        target.Append(
            value.Length.ToString(CultureInfo.InvariantCulture));
        target.Append(':');
        target.Append(value);
    }

    private static void Set(
        GuestRetentionAnonymisationReceipt receipt,
        string propertyName,
        object? value) =>
        typeof(GuestRetentionAnonymisationReceipt)
            .GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(receipt, value);
}
