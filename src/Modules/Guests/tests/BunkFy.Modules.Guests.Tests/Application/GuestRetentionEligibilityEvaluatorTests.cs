namespace BunkFy.Modules.Guests.Tests.Application;

using BunkFy.Modules.Guests.Application.Policies;
using BunkFy.Modules.Guests.Application.Ports;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Properties.Contracts;
using BunkFy.TimeZones;
using Xunit;

public sealed class GuestRetentionEligibilityEvaluatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Terminal_stay_past_the_exact_policy_period_is_eligible()
    {
        GuestRetentionPolicyFixture fixture =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot snapshot =
            GuestRetentionTestData.Snapshot(
            fixture.Binding,
            GuestStayStatus.CheckedOut,
            new DateOnly(2025, 7, 26));

        GuestRetentionEligibilityResult result =
            new GuestRetentionEligibilityEvaluator(fixture.Registry)
                .Evaluate(snapshot, Now);

        Assert.Equal(GuestRetentionEligibilityStatus.Eligible, result.Status);
        Assert.Equal(GuestRetentionEligibilityCode.None, result.Code);
        Assert.Equal(1, result.AffectedPropertyCount);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero),
            result.RetentionDeadlineUtc);
        Assert.NotNull(result.PolicySetSha256);
        Assert.Equal(64, result.PolicySetSha256.Length);
        Assert.Equal(
            TimeZoneCatalog.Default.CatalogVersion,
            result.TimeZoneCatalogVersion);
    }

    [Fact]
    public void Current_operational_stay_is_not_due()
    {
        GuestRetentionPolicyFixture fixture =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot snapshot =
            GuestRetentionTestData.Snapshot(
            fixture.Binding,
            GuestStayStatus.Confirmed,
            new DateOnly(2024, 1, 1));

        GuestRetentionEligibilityResult result =
            new GuestRetentionEligibilityEvaluator(fixture.Registry)
                .Evaluate(snapshot, Now);

        Assert.Equal(GuestRetentionEligibilityStatus.NotDue, result.Status);
        Assert.Equal(GuestRetentionEligibilityCode.OperationalStay, result.Code);
    }

    [Fact]
    public void Due_profile_with_an_active_hold_is_blocked()
    {
        GuestRetentionPolicyFixture fixture =
            GuestRetentionTestData.CreatePolicy();
        DateTimeOffset placedAtUtc = Now.AddDays(-7);
        GuestRetentionCandidateSnapshot source =
            GuestRetentionTestData.Snapshot(
            fixture.Binding,
            GuestStayStatus.CheckedOut,
            new DateOnly(2025, 1, 1));
        GuestRetentionCandidateSnapshot snapshot = source with
        {
            ActiveHolds =
            [
                new(source.OriginPropertyId, placedAtUtc)
            ]
        };

        GuestRetentionEligibilityResult result =
            new GuestRetentionEligibilityEvaluator(fixture.Registry)
                .Evaluate(snapshot, Now);

        Assert.Equal(GuestRetentionEligibilityStatus.Blocked, result.Status);
        Assert.Equal(GuestRetentionEligibilityCode.ActiveDataHold, result.Code);
        Assert.Equal(placedAtUtc, result.HoldReviewDueAtUtc);
    }

    [Fact]
    public void Suspended_retired_property_keeps_its_last_valid_policy_authority()
    {
        GuestRetentionPolicyFixture fixture =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot source =
            GuestRetentionTestData.Snapshot(
            fixture.Binding,
            GuestStayStatus.Cancelled,
            new DateOnly(2025, 1, 1));
        GuestRetentionPropertySnapshot property = Assert.Single(source.Properties);
        GuestRetentionCandidateSnapshot snapshot = source with
        {
            Properties =
            [
                property with
                {
                    Status = PropertyStatus.Retired,
                    ProcessingStatus = PropertyProcessingStatus.Suspended
                }
            ]
        };

        GuestRetentionEligibilityResult result =
            new GuestRetentionEligibilityEvaluator(fixture.Registry)
                .Evaluate(snapshot, Now);

        Assert.Equal(GuestRetentionEligibilityStatus.Eligible, result.Status);
    }

    [Fact]
    public void Missing_policy_projection_fails_closed()
    {
        GuestRetentionPolicyFixture fixture =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot source =
            GuestRetentionTestData.Snapshot(
            fixture.Binding,
            GuestStayStatus.CheckedOut,
            new DateOnly(2025, 1, 1));
        GuestRetentionPropertySnapshot property = Assert.Single(source.Properties);
        GuestRetentionCandidateSnapshot snapshot = source with
        {
            Properties =
            [
                property with { GovernancePolicy = null }
            ]
        };

        GuestRetentionEligibilityResult result =
            new GuestRetentionEligibilityEvaluator(fixture.Registry)
                .Evaluate(snapshot, Now);

        Assert.Equal(GuestRetentionEligibilityStatus.Failed, result.Status);
        Assert.Equal(
            GuestRetentionEligibilityCode.PolicyUnavailable,
            result.Code);
    }

    [Fact]
    public void Association_overflow_is_projection_failure_not_partial_evidence()
    {
        GuestRetentionPolicyFixture fixture =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot snapshot =
            GuestRetentionTestData.Snapshot(
                fixture.Binding,
                GuestStayStatus.CheckedOut,
                new DateOnly(2025, 1, 1)) with
            {
                AssociationOverflowed = true
            };

        GuestRetentionEligibilityResult result =
            new GuestRetentionEligibilityEvaluator(fixture.Registry)
                .Evaluate(snapshot, Now);

        Assert.Equal(GuestRetentionEligibilityStatus.Failed, result.Status);
        Assert.Equal(
            GuestRetentionEligibilityCode.ProjectionUnavailable,
            result.Code);
    }

    [Fact]
    public void Non_canonical_or_stale_time_zone_evidence_fails_distinctly()
    {
        GuestRetentionPolicyFixture fixture =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot source =
            GuestRetentionTestData.Snapshot(
                fixture.Binding,
                GuestStayStatus.CheckedOut,
                new DateOnly(2025, 1, 1));
        GuestRetentionPropertySnapshot property =
            Assert.Single(source.Properties);
        GuestRetentionPropertySnapshot[] invalidEvidence =
        [
            property with
            {
                TimeZoneId = "UTC",
                CanonicalTimeZoneId = "Etc/UTC",
                TimeZoneStatus = PropertyTimeZoneStatus.Alias,
                TimeZoneCatalogVersion =
                    TimeZoneCatalog.Default.CatalogVersion
            },
            property with
            {
                TimeZoneId = "GMT Standard Time",
                CanonicalTimeZoneId = null,
                TimeZoneStatus = PropertyTimeZoneStatus.Legacy,
                TimeZoneCatalogVersion = null
            },
            property with
            {
                TimeZoneId = "Missing/Zone",
                CanonicalTimeZoneId = null,
                TimeZoneStatus = PropertyTimeZoneStatus.Unrecognized,
                TimeZoneCatalogVersion = null
            },
            property with
            {
                TimeZoneCatalogVersion = "TZDB: stale"
            },
            property with
            {
                CanonicalTimeZoneId = "Europe/London"
            },
            property with
            {
                TimeZoneStatus = PropertyTimeZoneStatus.RuntimeUnavailable
            },
            property with
            {
                TimeZoneEvidenceSource =
                    GuestPropertyTimeZoneEvidenceSource.Legacy
            },
            property with
            {
                TimeZoneEvidenceSourceVersion =
                    property.TopologySourceVersion + 1
            }
        ];

        foreach (GuestRetentionPropertySnapshot invalid in invalidEvidence)
        {
            GuestRetentionEligibilityResult result =
                new GuestRetentionEligibilityEvaluator(fixture.Registry)
                    .Evaluate(
                        source with { Properties = [invalid] },
                        Now);

            Assert.Equal(
                GuestRetentionEligibilityStatus.Failed,
                result.Status);
            Assert.Equal(
                GuestRetentionEligibilityCode.TimeZoneUnavailable,
                result.Code);
        }
    }

    [Fact]
    public void Policy_digest_commits_the_time_zone_evidence_source()
    {
        GuestRetentionPolicyFixture fixture =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot generic =
            GuestRetentionTestData.Snapshot(
                fixture.Binding,
                GuestStayStatus.CheckedOut,
                new DateOnly(2025, 1, 1));
        GuestRetentionPropertySnapshot property =
            Assert.Single(generic.Properties);
        GuestRetentionCandidateSnapshot dedicated = generic with
        {
            Properties =
            [
                property with
                {
                    TimeZoneEvidenceSource =
                        GuestPropertyTimeZoneEvidenceSource.Dedicated
                }
            ]
        };
        var evaluator =
            new GuestRetentionEligibilityEvaluator(fixture.Registry);

        GuestRetentionEligibilityResult genericResult =
            evaluator.Evaluate(generic, Now);
        GuestRetentionEligibilityResult dedicatedResult =
            evaluator.Evaluate(dedicated, Now);

        Assert.Equal(
            GuestRetentionEligibilityStatus.Eligible,
            genericResult.Status);
        Assert.Equal(
            GuestRetentionEligibilityStatus.Eligible,
            dedicatedResult.Status);
        Assert.NotEqual(
            genericResult.PolicySetSha256,
            dedicatedResult.PolicySetSha256);
    }

    [Fact]
    public void Retired_property_can_converge_an_exact_current_alias()
    {
        GuestRetentionPolicyFixture fixture =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot source =
            GuestRetentionTestData.Snapshot(
                fixture.Binding,
                GuestStayStatus.CheckedOut,
                new DateOnly(2025, 1, 1));
        GuestRetentionPropertySnapshot property =
            Assert.Single(source.Properties) with
            {
                Status = PropertyStatus.Retired,
                ProcessingStatus = PropertyProcessingStatus.Suspended,
                TimeZoneId = "UTC",
                CanonicalTimeZoneId = "Etc/UTC",
                TimeZoneStatus = PropertyTimeZoneStatus.Alias,
                TimeZoneCatalogVersion =
                    TimeZoneCatalog.Default.CatalogVersion
            };
        var evaluator =
            new GuestRetentionEligibilityEvaluator(fixture.Registry);

        foreach (GuestPropertyTimeZoneEvidenceSource evidenceSource in
                 new[]
                 {
                     GuestPropertyTimeZoneEvidenceSource.Generic,
                     GuestPropertyTimeZoneEvidenceSource.Rebuild
                 })
        {
            GuestRetentionEligibilityResult result = evaluator.Evaluate(
                source with
                {
                    Properties =
                    [
                        property with
                        {
                            TimeZoneEvidenceSource = evidenceSource
                        }
                    ]
                },
                Now);

            Assert.Equal(
                GuestRetentionEligibilityStatus.Eligible,
                result.Status);
            Assert.Equal(
                TimeZoneCatalog.Default.CatalogVersion,
                result.TimeZoneCatalogVersion);
        }

        GuestRetentionEligibilityResult dedicatedAlias =
            evaluator.Evaluate(
                source with
                {
                    Properties =
                    [
                        property with
                        {
                            TimeZoneEvidenceSource =
                                GuestPropertyTimeZoneEvidenceSource.Dedicated
                        }
                    ]
                },
                Now);
        Assert.Equal(
            GuestRetentionEligibilityCode.TimeZoneUnavailable,
            dedicatedAlias.Code);
    }

    [Fact]
    public void Current_projected_zone_deterministically_recomputes_threshold()
    {
        GuestRetentionPolicyFixture fixture =
            GuestRetentionTestData.CreatePolicy();
        GuestRetentionCandidateSnapshot utc =
            GuestRetentionTestData.Snapshot(
                fixture.Binding,
                GuestStayStatus.CheckedOut,
                new DateOnly(2025, 7, 26));
        GuestRetentionPropertySnapshot property =
            Assert.Single(utc.Properties);
        GuestRetentionCandidateSnapshot losAngeles = utc with
        {
            Properties =
            [
                property with
                {
                    TimeZoneId = "America/Los_Angeles",
                    CanonicalTimeZoneId = "America/Los_Angeles",
                    TimeZoneEvidenceSource =
                        GuestPropertyTimeZoneEvidenceSource.Dedicated,
                    TimeZoneEvidenceSourceVersion = 4,
                    TopologySourceVersion = 4
                }
            ]
        };
        var evaluator =
            new GuestRetentionEligibilityEvaluator(fixture.Registry);

        GuestRetentionEligibilityResult utcResult =
            evaluator.Evaluate(utc, Now);
        GuestRetentionEligibilityResult losAngelesResult =
            evaluator.Evaluate(losAngeles, Now);

        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero),
            utcResult.RetentionDeadlineUtc);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 27, 7, 0, 0, TimeSpan.Zero),
            losAngelesResult.RetentionDeadlineUtc);
        Assert.NotEqual(
            utcResult.PolicySetSha256,
            losAngelesResult.PolicySetSha256);
    }

}
