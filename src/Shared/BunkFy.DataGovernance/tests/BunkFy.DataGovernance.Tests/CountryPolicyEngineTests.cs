namespace BunkFy.DataGovernance.Tests;

using System.Text;
using Xunit;

public sealed class CountryPolicyEngineTests
{
    private static readonly DateTimeOffset EvaluationTime = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parser_is_strict_and_computes_the_source_digest()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(ValidPackJson);

        CountryPolicyPackArtifact artifact = CountryPolicyPackJson.Parse(bytes);

        Assert.Equal("uk-hostel-baseline", artifact.Document.PolicyId);
        Assert.Equal(1, artifact.Document.PolicyVersion);
        Assert.Equal("GB", artifact.Document.OperatingCountryCode);
        Assert.Equal(64, artifact.ContentSha256.Length);
        Assert.All(artifact.ContentSha256, character => Assert.True(character is (>= '0' and <= '9') or (>= 'a' and <= 'f')));
        Assert.Equal(artifact.ContentSha256, CountryPolicyPackJson.Parse(bytes).ContentSha256);
    }

    [Fact]
    public void Parser_rejects_unknown_duplicate_and_oversized_documents()
    {
        string unknown = ValidPackJson.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"unexpected\": true,",
            StringComparison.Ordinal);
        string duplicate = ValidPackJson.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1, \"schemaVersion\": 1,",
            StringComparison.Ordinal);
        byte[] oversized = new byte[CountryPolicyPackJson.MaximumDocumentBytes + 1];

        Assert.Throws<InvalidDataException>(() => CountryPolicyPackJson.Parse(Encoding.UTF8.GetBytes(unknown)));
        Assert.Throws<InvalidDataException>(() => CountryPolicyPackJson.Parse(Encoding.UTF8.GetBytes(duplicate)));
        Assert.Throws<InvalidDataException>(() => CountryPolicyPackJson.Parse(oversized));
    }

    [Fact]
    public void Validator_rejects_cross_reference_and_interval_errors()
    {
        CountryPolicyPackDocument pack = Parse().Document;
        CountryPolicyPackDocument invalid = pack with
        {
            ExpiresAtUtc = pack.EffectiveAtUtc,
            FieldRules =
            [
                pack.FieldRules[0] with
                {
                    GuestCategory = "unknown-category",
                    PurposeCodes = ["unknown-purpose"]
                }
            ]
        };

        IReadOnlyList<string> errors = CountryPolicyPackValidator.Validate(invalid);

        Assert.Contains(errors, error => error.Contains("ExpiresAtUtc", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown guest category", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("unknown purpose", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_rejects_non_iso_country_insecure_sources_and_null_collection_elements()
    {
        CountryPolicyPackDocument pack = Parse().Document;
        CountryPolicyPackDocument invalid = pack with
        {
            OperatingCountryCode = "ZZ",
            PurposeRules = [null!],
            Approval = pack.Approval with
            {
                Sources =
                [
                    null!,
                    pack.Approval.Sources[0] with { Uri = "http://example.test/policy-source" }
                ]
            }
        };

        IReadOnlyList<string> errors = CountryPolicyPackValidator.Validate(invalid);

        Assert.Contains(errors, error => error.Contains("recognized uppercase ISO", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("PurposeRules[0] cannot be null", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("Sources[0] cannot be null", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("absolute HTTPS URI", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_registry_enables_no_country()
    {
        CountryPolicyRegistry registry = CountryPolicyRegistry.Create(
            [],
            [],
            CountryPolicyRuntimeMode.Production);

        Assert.Empty(registry.ListPolicies());
        Assert.Equal(
            CountryPolicyDecisionReason.UnknownPolicy,
            registry.EvaluateActivation(ValidActivation()).Reason);
    }

    [Fact]
    public void Production_registry_rejects_engineering_launch_and_digest_drift()
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyAllowlistEntry engineering = Allow(artifact) with
        {
            LaunchStatus = CountryLaunchStatus.Engineering
        };
        CountryPolicyAllowlistEntry drifted = Allow(artifact) with
        {
            ContentSha256 = new string('0', 64)
        };

        CountryPolicyRegistryValidationException engineeringError = Assert.Throws<CountryPolicyRegistryValidationException>(
            () => CountryPolicyRegistry.Create([artifact], [engineering], CountryPolicyRuntimeMode.Production));
        CountryPolicyRegistryValidationException digestError = Assert.Throws<CountryPolicyRegistryValidationException>(
            () => CountryPolicyRegistry.Create([artifact], [drifted], CountryPolicyRuntimeMode.Production));

        Assert.Contains(engineeringError.Errors, error => error.Contains("Engineering", StringComparison.Ordinal));
        Assert.Contains(digestError.Errors, error => error.Contains("content digest", StringComparison.Ordinal));
    }

    [Fact]
    public void Registry_bounds_artifacts_and_allowlist_entries()
    {
        CountryPolicyPackArtifact artifact = Parse();

        CountryPolicyRegistryValidationException artifactError = Assert.Throws<CountryPolicyRegistryValidationException>(
            () => CountryPolicyRegistry.Create(
                Enumerable.Repeat(artifact, CountryPolicyRegistry.MaximumPolicyArtifacts + 1),
                [],
                CountryPolicyRuntimeMode.Production));
        CountryPolicyRegistryValidationException allowlistError = Assert.Throws<CountryPolicyRegistryValidationException>(
            () => CountryPolicyRegistry.Create(
                [artifact],
                Enumerable.Repeat(Allow(artifact), CountryPolicyRegistry.MaximumAllowlistEntries + 1),
                CountryPolicyRuntimeMode.Production));

        Assert.Contains(artifactError.Errors, error => error.Contains("artifacts", StringComparison.Ordinal));
        Assert.Contains(allowlistError.Errors, error => error.Contains("allowlist entries", StringComparison.Ordinal));
    }

    [Fact]
    public void Registry_freezes_caller_owned_pack_collections()
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyRegistry registry = ProductionRegistry(artifact);

        artifact.Document.AccommodationTypes[0] = "hotel";
        artifact.Document.PurposeRules[0].AllowedSourceProvenance[0] = "tampered-source";

        CountryPolicyDecision decision = registry.EvaluateActivation(ValidActivation());
        CountryPolicyDescriptor descriptor = Assert.Single(registry.ListPolicies());

        Assert.True(decision.IsAllowed);
        Assert.Equal(["hostel"], descriptor.AccommodationTypes);
    }

    [Fact]
    public void Activation_returns_immutable_policy_evidence_for_an_exact_binding()
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyRegistry registry = ProductionRegistry(artifact);

        CountryPolicyDecision decision = registry.EvaluateActivation(ValidActivation());

        Assert.True(decision.IsAllowed);
        Assert.Equal(CountryPolicyDecisionReason.Allowed, decision.Reason);
        CountryPolicyEvidence evidence = Assert.IsType<CountryPolicyEvidence>(decision.Evidence);
        Assert.Equal(artifact.ContentSha256, evidence.ContentSha256);
        Assert.Equal("GB", evidence.OperatingCountryCode);
        Assert.Equal("property-activation", evidence.PurposeCode);
        Assert.Equal(CountryPolicySurface.PropertyActivation, evidence.Surface);
        Assert.Equal(EvaluationTime, evidence.EvaluatedAtUtc);
        Assert.Equal(evidence.ContentSha256, evidence.ToBinding().ContentSha256);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CountryPolicyAcknowledgement>)evidence.AcceptedAcknowledgements).Add(new("other", 1)));
    }

    [Fact]
    public void Explicit_binding_evaluates_the_requested_surface_without_changing_activation_semantics()
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyActivationRequest activation = ValidActivation();

        CountryPolicyDecision operationBinding = registry.EvaluateBinding(
            new(
                activation.OperatingCountryCode,
                activation.PolicyId,
                activation.PolicyVersion,
                activation.DataRegionId,
                activation.TransferProfileId,
                activation.RetentionPolicyId,
                activation.RetentionPolicyVersion,
                activation.AcceptedAcknowledgements,
                "hostel",
                "reservation-management",
                CountryPolicySurface.ApiWrite,
                "workspace-staff",
                EvaluationTime));
        CountryPolicyDecision activationBinding =
            registry.EvaluateActivation(activation);

        Assert.True(operationBinding.IsAllowed);
        Assert.Equal(
            CountryPolicySurface.ApiWrite,
            Assert.IsType<CountryPolicyEvidence>(
                operationBinding.Evidence).Surface);
        Assert.True(activationBinding.IsAllowed);
        Assert.Equal(
            CountryPolicySurface.PropertyActivation,
            Assert.IsType<CountryPolicyEvidence>(
                activationBinding.Evidence).Surface);
    }

    [Fact]
    public void Operation_revalidates_the_persisted_digest_and_current_effective_interval()
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding = Assert.IsType<CountryPolicyEvidence>(
            registry.EvaluateActivation(ValidActivation()).Evidence).ToBinding();

        CountryPolicyDecision allowed = registry.EvaluateOperation(
            ValidOperation(binding));
        CountryPolicyDecision drifted = registry.EvaluateOperation(
            ValidOperation(binding with { ContentSha256 = new string('0', 64) }));
        CountryPolicyDecision expired = registry.EvaluateOperation(
            ValidOperation(binding) with { ObservedAtUtc = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) });

        Assert.True(allowed.IsAllowed);
        Assert.Equal(CountryPolicyDecisionReason.ContentDigestMismatch, drifted.Reason);
        Assert.Equal(CountryPolicyDecisionReason.PolicyExpired, expired.Reason);
    }

    [Fact]
    public void Operation_rejects_unexpected_duplicate_and_unbounded_acknowledgement_evidence()
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding = Assert.IsType<CountryPolicyEvidence>(
            registry.EvaluateActivation(ValidActivation()).Evidence).ToBinding();
        CountryPolicyAcknowledgement required = Assert.Single(binding.AcceptedAcknowledgements);

        CountryPolicyDecision unexpected = registry.EvaluateOperation(ValidOperation(binding with
        {
            AcceptedAcknowledgements = [required, new("unexpected-notice", 1)]
        }));
        CountryPolicyDecision duplicate = registry.EvaluateOperation(ValidOperation(binding with
        {
            AcceptedAcknowledgements = [required, required]
        }));
        CountryPolicyDecision unbounded = registry.EvaluateOperation(ValidOperation(binding with
        {
            AcceptedAcknowledgements = Enumerable.Range(0, CountryPolicyRegistry.MaximumAcceptedAcknowledgements + 1)
                .Select(index => new CountryPolicyAcknowledgement($"notice-{index}", 1))
                .ToArray()
        }));

        Assert.Equal(CountryPolicyDecisionReason.InvalidRequest, unexpected.Reason);
        Assert.Equal(CountryPolicyDecisionReason.InvalidRequest, duplicate.Reason);
        Assert.Equal(CountryPolicyDecisionReason.InvalidRequest, unbounded.Reason);
    }

    [Fact]
    public void Retention_resolves_the_exact_authorized_rule()
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding = Assert.IsType<CountryPolicyEvidence>(
            registry.EvaluateActivation(ValidActivation()).Evidence).ToBinding();

        CountryPolicyRetentionDecision decision = registry.EvaluateRetention(
            ValidRetention(binding));

        Assert.True(decision.IsAllowed);
        Assert.Equal(CountryPolicyDecisionReason.Allowed, decision.Reason);
        Assert.Equal(
            CountryPolicySurface.Retention,
            Assert.IsType<CountryPolicyEvidence>(decision.Evidence).Surface);
        CountryPolicyRetentionRuleEvidence rule =
            Assert.IsType<CountryPolicyRetentionRuleEvidence>(
                decision.RetentionRule);
        Assert.Equal("guest-operational", rule.RetentionPolicyId);
        Assert.Equal(1, rule.RetentionPolicyVersion);
        Assert.Equal("guest-operational", rule.DataClass);
        Assert.Equal("stay-ended", rule.Trigger);
        Assert.Equal(TimeSpan.FromDays(365), rule.Period);
    }

    [Fact]
    public void Retention_policy_binding_can_authorize_multiple_data_class_rules()
    {
        CountryPolicyPackArtifact source = Parse();
        CountryPolicyRetentionRule reservationRule =
            source.Document.RetentionRules[0] with
            {
                DataClass = "reservation-operational",
                Trigger = "reservation-ended"
            };
        CountryPolicyPackDocument document = source.Document with
        {
            PurposeRules =
            [
                .. source.Document.PurposeRules,
                new()
                {
                    PurposeCode = "reservation-retention",
                    LegalRuleReferenceKeys = ["storage-limitation"],
                    AllowedSurfaces = [CountryPolicySurface.Retention],
                    AllowedSourceProvenance = ["retention-worker"]
                }
            ],
            RetentionRules =
            [
                .. source.Document.RetentionRules,
                reservationRule
            ]
        };
        CountryPolicyPackArtifact artifact =
            new(document, source.ContentSha256);
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding =
            Assert.IsType<CountryPolicyEvidence>(
                registry.EvaluateActivation(ValidActivation()).Evidence)
                .ToBinding();

        CountryPolicyRetentionDecision decision =
            registry.EvaluateRetention(
                ValidRetention(binding) with
                {
                    PurposeCode = "reservation-retention",
                    DataClass = reservationRule.DataClass,
                    Trigger = reservationRule.Trigger
                });

        Assert.True(decision.IsAllowed);
        CountryPolicyRetentionRuleEvidence evidence =
            Assert.IsType<CountryPolicyRetentionRuleEvidence>(
                decision.RetentionRule);
        Assert.Equal("reservation-operational", evidence.DataClass);
        Assert.Equal("reservation-ended", evidence.Trigger);
        Assert.Single(
            Assert.Single(registry.ListPolicies()).RetentionPolicies);
    }

    [Fact]
    public void Validator_rejects_duplicate_retention_rule_coordinates()
    {
        CountryPolicyPackDocument document = Parse().Document;
        CountryPolicyPackDocument invalid = document with
        {
            RetentionRules =
            [
                document.RetentionRules[0],
                document.RetentionRules[0]
            ]
        };

        IReadOnlyList<string> errors =
            CountryPolicyPackValidator.Validate(invalid);

        Assert.Contains(
            errors,
            error => error.Contains(
                "Duplicate retention rule",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("other-class", "stay-ended")]
    [InlineData("guest-operational", "profile-created")]
    [InlineData("Guest-Operational", "stay-ended")]
    public void Retention_fails_closed_for_mismatched_or_invalid_rule_coordinates(
        string dataClass,
        string trigger)
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding = Assert.IsType<CountryPolicyEvidence>(
            registry.EvaluateActivation(ValidActivation()).Evidence).ToBinding();

        CountryPolicyRetentionDecision decision = registry.EvaluateRetention(
            ValidRetention(binding) with
            {
                DataClass = dataClass,
                Trigger = trigger
            });

        Assert.False(decision.IsAllowed);
        Assert.Null(decision.Evidence);
        Assert.Null(decision.RetentionRule);
        Assert.Equal(
            dataClass == "Guest-Operational"
                ? CountryPolicyDecisionReason.InvalidRequest
                : CountryPolicyDecisionReason.RetentionPolicyNotPermitted,
            decision.Reason);
    }

    [Fact]
    public void Legacy_policy_fails_closed_for_rights_response_deadlines()
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding = Assert.IsType<CountryPolicyEvidence>(
            registry.EvaluateActivation(ValidActivation()).Evidence).ToBinding();

        CountryPolicyRightsResponseDecision decision =
            registry.EvaluateRightsResponse(
                new(
                    binding,
                    "hostel",
                    CountryPolicyRight.Export,
                    "Europe/London",
                    EvaluationTime));

        Assert.False(decision.IsAllowed);
        Assert.Null(decision.Evidence);
        Assert.Equal(
            CountryPolicyDecisionReason.RightsResponsePolicyNotPermitted,
            decision.Reason);
    }

    [Fact]
    public void Version_two_policy_calculates_a_calendar_month_and_freezes_rule_evidence()
    {
        CountryPolicyPackArtifact artifact = WithRightsResponseRules(Parse());
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding = Assert.IsType<CountryPolicyEvidence>(
            registry.EvaluateActivation(ValidActivation()).Evidence).ToBinding();
        DateTimeOffset receivedAtUtc =
            new(2026, 1, 31, 10, 0, 0, TimeSpan.Zero);

        CountryPolicyRightsResponseDecision decision =
            registry.EvaluateRightsResponse(
                new(
                    binding,
                    "hostel",
                    CountryPolicyRight.Export,
                    "Europe/London",
                    receivedAtUtc));

        Assert.True(decision.IsAllowed);
        CountryPolicyRightsResponseEvidence evidence =
            Assert.IsType<CountryPolicyRightsResponseEvidence>(
                decision.Evidence);
        Assert.Equal(CountryPolicyRight.Export, evidence.Right);
        Assert.Equal("standard-export", evidence.RuleReference);
        Assert.Equal(1, evidence.PeriodMonths);
        Assert.Equal("Europe/London", evidence.TimeZoneId);
        Assert.Equal(
            new DateTimeOffset(2026, 2, 28, 10, 0, 0, TimeSpan.Zero),
            evidence.DueAtUtc);
    }

    [Fact]
    public void Rights_response_resolves_a_known_tzdb_alias_before_evaluation()
    {
        CountryPolicyPackArtifact artifact = WithRightsResponseRules(Parse());
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding = Assert.IsType<CountryPolicyEvidence>(
            registry.EvaluateActivation(ValidActivation()).Evidence).ToBinding();

        CountryPolicyRightsResponseDecision decision =
            registry.EvaluateRightsResponse(
                new(
                    binding,
                    "hostel",
                    CountryPolicyRight.Export,
                    "GB",
                    EvaluationTime));

        Assert.True(decision.IsAllowed);
        CountryPolicyRightsResponseEvidence evidence =
            Assert.IsType<CountryPolicyRightsResponseEvidence>(
                decision.Evidence);
        Assert.Equal("Europe/London", evidence.TimeZoneId);
    }

    [Fact]
    public void Calendar_deadline_moves_a_nonexistent_local_time_to_the_first_valid_instant()
    {
        bool calculated =
            CountryPolicyCalendarDeadlineCalculator.TryCalculate(
                new(2026, 3, 1, 1, 30, 0, TimeSpan.Zero),
                "Europe/London",
                new CountryPolicyCalendarPeriod { Days = 28 },
                PinnedTzdbCountryPolicyTimeZoneRules.Instance,
                out DateTimeOffset dueAtUtc);

        Assert.True(calculated);
        Assert.Equal(
            new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero),
            dueAtUtc);
    }

    [Fact]
    public void Calendar_deadline_uses_the_latest_instant_for_an_ambiguous_local_time()
    {
        bool calculated =
            CountryPolicyCalendarDeadlineCalculator.TryCalculate(
                new(2026, 9, 27, 0, 30, 0, TimeSpan.Zero),
                "Europe/London",
                new CountryPolicyCalendarPeriod { Days = 28 },
                PinnedTzdbCountryPolicyTimeZoneRules.Instance,
                out DateTimeOffset dueAtUtc);

        Assert.True(calculated);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero),
            dueAtUtc);
    }

    [Theory]
    [InlineData(
        "America/Edmonton",
        2026, 10, 1, 18,
        2026, 11, 1, 18)]
    [InlineData(
        "Africa/Casablanca",
        2026, 9, 1, 10,
        2026, 10, 1, 11)]
    public void Calendar_deadline_uses_pinned_2026c_future_rules(
        string timeZoneId,
        int receivedYear,
        int receivedMonth,
        int receivedDay,
        int receivedHour,
        int dueYear,
        int dueMonth,
        int dueDay,
        int dueHour)
    {
        bool calculated =
            CountryPolicyCalendarDeadlineCalculator.TryCalculate(
                new DateTimeOffset(
                    receivedYear,
                    receivedMonth,
                    receivedDay,
                    receivedHour,
                    0,
                    0,
                    TimeSpan.Zero),
                timeZoneId,
                new CountryPolicyCalendarPeriod { Months = 1 },
                PinnedTzdbCountryPolicyTimeZoneRules.Instance,
                out DateTimeOffset dueAtUtc);

        Assert.True(calculated);
        Assert.Equal(
            new DateTimeOffset(
                dueYear,
                dueMonth,
                dueDay,
                dueHour,
                0,
                0,
                TimeSpan.Zero),
            dueAtUtc);
    }

    [Fact]
    public void Version_two_policy_requires_every_response_rule_and_valid_time_zones()
    {
        CountryPolicyPackDocument document = WithRightsResponseRules(Parse())
            .Document;
        CountryPolicyPackDocument invalid = document with
        {
            RightsRule = document.RightsRule with
            {
                ResponseRules =
                [
                    document.RightsRule.ResponseRules![0] with
                    {
                        AllowedTimeZoneIds = ["missing/time-zone"]
                    }
                ]
            }
        };

        IReadOnlyList<string> errors =
            CountryPolicyPackValidator.Validate(
                invalid,
                PinnedTzdbCountryPolicyTimeZoneRules.Instance);

        Assert.Contains(
            errors,
            error => error.Contains(
                "unavailable time zone",
                StringComparison.Ordinal));
        Assert.Contains(
            errors,
            error => error.Contains(
                "'Correction' rule",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Version_two_registry_fails_closed_without_time_zone_rules()
    {
        CountryPolicyPackArtifact artifact = WithRightsResponseRules(Parse());

        CountryPolicyRegistryValidationException exception = Assert.Throws<
            CountryPolicyRegistryValidationException>(() =>
                CountryPolicyRegistry.Create(
                    [artifact],
                    [Allow(artifact)],
                    CountryPolicyRuntimeMode.Production));

        Assert.Contains(
            exception.Errors,
            error => error.Contains(
                "unavailable time zone",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Version_two_policy_requires_primary_tzdb_identifiers()
    {
        CountryPolicyPackDocument document = WithRightsResponseRules(Parse())
            .Document;
        CountryPolicyPackDocument invalid = document with
        {
            RightsRule = document.RightsRule with
            {
                ResponseRules = document.RightsRule.ResponseRules!
                    .Select(rule => rule with
                    {
                        AllowedTimeZoneIds = ["GB"]
                    })
                    .ToArray()
            }
        };

        IReadOnlyList<string> errors =
            CountryPolicyPackValidator.Validate(
                invalid,
                PinnedTzdbCountryPolicyTimeZoneRules.Instance);

        Assert.Contains(
            errors,
            error => error.Contains(
                "primary TZDB identifiers",
                StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(" Europe/London")]
    [InlineData("Europe/London ")]
    public void Version_two_policy_rejects_time_zone_identifier_whitespace(
        string timeZoneId)
    {
        CountryPolicyPackDocument document = WithRightsResponseRules(Parse())
            .Document;
        CountryPolicyPackDocument invalid = document with
        {
            RightsRule = document.RightsRule with
            {
                ResponseRules = document.RightsRule.ResponseRules!
                    .Select(rule => rule with
                    {
                        AllowedTimeZoneIds = [timeZoneId]
                    })
                    .ToArray()
            }
        };

        IReadOnlyList<string> errors =
            CountryPolicyPackValidator.Validate(
                invalid,
                PinnedTzdbCountryPolicyTimeZoneRules.Instance);

        Assert.Contains(
            errors,
            error => error.Contains(
                "bounded time-zone identifiers",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Version_two_policy_detects_duplicate_primary_time_zones()
    {
        CountryPolicyPackDocument document = WithRightsResponseRules(Parse())
            .Document;
        CountryPolicyRightsResponseRule first =
            document.RightsRule.ResponseRules![0];
        CountryPolicyPackDocument invalid = document with
        {
            RightsRule = document.RightsRule with
            {
                ResponseRules =
                [
                    first with
                    {
                        AllowedTimeZoneIds = ["Europe/London", "GB"]
                    },
                    .. document.RightsRule.ResponseRules.Skip(1)
                ]
            }
        };

        IReadOnlyList<string> errors =
            CountryPolicyPackValidator.Validate(
                invalid,
                PinnedTzdbCountryPolicyTimeZoneRules.Instance);

        Assert.Contains(
            errors,
            error => error.Contains(
                "duplicate primary time zone 'Europe/London'",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Bound_policy_admits_only_a_canonical_zone_allowed_by_every_rule()
    {
        CountryPolicyPackArtifact artifact = WithRightsResponseRules(Parse());
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding = Assert.IsType<CountryPolicyEvidence>(
            registry.EvaluateActivation(ValidActivation()).Evidence).ToBinding();

        CountryPolicyTimeZoneDecision allowed =
            registry.EvaluateTimeZoneCompatibility(new(
                binding,
                "hostel",
                "Europe/London",
                EvaluationTime));
        CountryPolicyTimeZoneDecision denied =
            registry.EvaluateTimeZoneCompatibility(new(
                binding,
                "hostel",
                "Europe/Paris",
                EvaluationTime));
        CountryPolicyTimeZoneDecision alias =
            registry.EvaluateTimeZoneCompatibility(new(
                binding,
                "hostel",
                "GB",
                EvaluationTime));

        Assert.True(allowed.IsAllowed);
        Assert.Equal("Europe/London", allowed.TimeZoneId);
        Assert.Equal(
            CountryPolicyDecisionReason.TimeZoneNotPermitted,
            denied.Reason);
        Assert.Equal(CountryPolicyDecisionReason.InvalidRequest, alias.Reason);
    }

    [Fact]
    public void Legacy_policy_without_time_zone_rules_does_not_invent_a_restriction()
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding = Assert.IsType<CountryPolicyEvidence>(
            registry.EvaluateActivation(ValidActivation()).Evidence).ToBinding();

        CountryPolicyTimeZoneDecision decision =
            registry.EvaluateTimeZoneCompatibility(new(
                binding,
                "hostel",
                "Europe/London",
                EvaluationTime));

        Assert.True(decision.IsAllowed);
        Assert.Equal("Europe/London", decision.TimeZoneId);
    }

    [Theory]
    [InlineData(OperationMismatch.UnknownPolicy, CountryPolicyDecisionReason.UnknownPolicy)]
    [InlineData(OperationMismatch.Country, CountryPolicyDecisionReason.CountryMismatch)]
    [InlineData(OperationMismatch.DataRegion, CountryPolicyDecisionReason.DataRegionNotPermitted)]
    [InlineData(OperationMismatch.TransferProfile, CountryPolicyDecisionReason.TransferProfileNotPermitted)]
    [InlineData(OperationMismatch.RetentionPolicy, CountryPolicyDecisionReason.RetentionPolicyNotPermitted)]
    [InlineData(OperationMismatch.Acknowledgement, CountryPolicyDecisionReason.RequiredAcknowledgementMissing)]
    [InlineData(OperationMismatch.AccommodationType, CountryPolicyDecisionReason.AccommodationTypeUnsupported)]
    [InlineData(OperationMismatch.Purpose, CountryPolicyDecisionReason.PurposeUnsupported)]
    [InlineData(OperationMismatch.Surface, CountryPolicyDecisionReason.SurfaceUnsupported)]
    [InlineData(OperationMismatch.Provenance, CountryPolicyDecisionReason.SourceProvenanceUnsupported)]
    public void Operation_fails_closed_for_every_binding_and_operation_mismatch(
        OperationMismatch mismatch,
        CountryPolicyDecisionReason expectedReason)
    {
        CountryPolicyPackArtifact artifact = Parse();
        CountryPolicyRegistry registry = ProductionRegistry(artifact);
        CountryPolicyBinding binding = Assert.IsType<CountryPolicyEvidence>(
            registry.EvaluateActivation(ValidActivation()).Evidence).ToBinding();

        CountryPolicyDecision decision = registry.EvaluateOperation(Mutate(binding, mismatch));

        Assert.False(decision.IsAllowed);
        Assert.Null(decision.Evidence);
        Assert.Equal(expectedReason, decision.Reason);
    }

    private static CountryPolicyOperationRequest Mutate(
        CountryPolicyBinding binding,
        OperationMismatch mismatch) =>
        mismatch switch
        {
            OperationMismatch.UnknownPolicy => ValidOperation(binding with { PolicyId = "missing-policy" }),
            OperationMismatch.Country => ValidOperation(binding with { OperatingCountryCode = "FR" }),
            OperationMismatch.DataRegion => ValidOperation(binding with { DataRegionId = "eu-west-3" }),
            OperationMismatch.TransferProfile => ValidOperation(binding with { TransferProfileId = "unreviewed-transfer" }),
            OperationMismatch.RetentionPolicy => ValidOperation(binding with { RetentionPolicyVersion = 2 }),
            OperationMismatch.Acknowledgement => ValidOperation(binding with { AcceptedAcknowledgements = [] }),
            OperationMismatch.AccommodationType => ValidOperation(binding) with { AccommodationType = "hotel" },
            OperationMismatch.Purpose => ValidOperation(binding) with { PurposeCode = "advertising" },
            OperationMismatch.Surface => ValidOperation(binding) with { Surface = CountryPolicySurface.Export },
            OperationMismatch.Provenance => ValidOperation(binding) with { SourceProvenance = "unknown-source" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

    private static CountryPolicyPackArtifact Parse() =>
        CountryPolicyPackJson.Parse(Encoding.UTF8.GetBytes(ValidPackJson));

    private static CountryPolicyPackArtifact WithRightsResponseRules(
        CountryPolicyPackArtifact source)
    {
        CountryPolicyRightsResponseRule[] responseRules =
            Enum.GetValues<CountryPolicyRight>()
                .Where(right => right != CountryPolicyRight.Unknown)
                .Select(right => new CountryPolicyRightsResponseRule
                {
                    Right = right,
                    Period = new CountryPolicyCalendarPeriod { Months = 1 },
                    AllowedTimeZoneIds = ["Europe/London"]
                })
                .ToArray();
        return new(
            source.Document with
            {
                SchemaVersion = 2,
                RightsRule = source.Document.RightsRule with
                {
                    ResponseRules = responseRules
                }
            },
            source.ContentSha256);
    }

    private static CountryPolicyRegistry ProductionRegistry(CountryPolicyPackArtifact artifact) =>
        CountryPolicyRegistry.Create(
            [artifact],
            [Allow(artifact)],
            CountryPolicyRuntimeMode.Production,
            PinnedTzdbCountryPolicyTimeZoneRules.Instance);

    private static CountryPolicyAllowlistEntry Allow(CountryPolicyPackArtifact artifact) =>
        new(
            artifact.Document.OperatingCountryCode,
            artifact.Document.PolicyId,
            artifact.Document.PolicyVersion,
            artifact.ContentSha256,
            CountryLaunchStatus.Approved);

    private static CountryPolicyActivationRequest ValidActivation() =>
        new(
            "GB",
            "uk-hostel-baseline",
            1,
            "eu-west-2",
            "uk-no-transfer",
            "guest-operational",
            1,
            [new("operator-notice", 1)],
            "hostel",
            "property-activation",
            "workspace-owner",
            EvaluationTime);

    private static CountryPolicyOperationRequest ValidOperation(CountryPolicyBinding binding) =>
        new(
            binding,
            "hostel",
            "reservation-management",
            CountryPolicySurface.ApiWrite,
            "workspace-staff",
            EvaluationTime);

    private static CountryPolicyRetentionRequest ValidRetention(
        CountryPolicyBinding binding) =>
        new(
            binding,
            "hostel",
            "guest-profile-retention",
            "retention-worker",
            "guest-operational",
            "stay-ended",
            EvaluationTime);

    private const string ValidPackJson = """
        {
          "schemaVersion": 1,
          "policyId": "uk-hostel-baseline",
          "policyVersion": 1,
          "operatingCountryCode": "GB",
          "approvalState": "approved",
          "effectiveAtUtc": "2026-01-01T00:00:00Z",
          "expiresAtUtc": "2027-01-01T00:00:00Z",
          "accommodationTypes": [ "hostel" ],
          "guestCategories": [ "ordinary-guest" ],
          "fieldRules": [
            {
              "fieldPolicyKey": "guest.primary-name",
              "guestCategory": "ordinary-guest",
              "requirement": "required",
              "purposeCodes": [ "reservation-management" ]
            }
          ],
          "purposeRules": [
            {
              "purposeCode": "property-activation",
              "legalRuleReferenceKeys": [ "operator-approval" ],
              "allowedSurfaces": [ "property-activation" ],
              "allowedSourceProvenance": [ "workspace-owner" ]
            },
            {
              "purposeCode": "reservation-management",
              "legalRuleReferenceKeys": [ "customer-instruction" ],
              "allowedSurfaces": [ "api-write", "adapter-ingress" ],
              "allowedSourceProvenance": [ "workspace-staff", "approved-adapter" ]
            },
            {
              "purposeCode": "guest-profile-retention",
              "legalRuleReferenceKeys": [ "storage-limitation" ],
              "allowedSurfaces": [ "retention" ],
              "allowedSourceProvenance": [ "retention-worker" ]
            }
          ],
          "retentionRules": [
            {
              "retentionPolicyId": "guest-operational",
              "retentionPolicyVersion": 1,
              "dataClass": "guest-operational",
              "trigger": "stay-ended",
              "period": "365.00:00:00"
            }
          ],
          "rightsRule": {
            "registration": "standard-registration",
            "export": "standard-export",
            "correction": "standard-correction",
            "restriction": "standard-restriction",
            "erasure": "review-before-erasure"
          },
          "restrictions": {
            "minors": "not-assessed",
            "documents": "document-images-prohibited",
            "specialCategoryData": "prohibited"
          },
          "permittedDataRegions": [ "eu-west-2" ],
          "permittedTransferProfiles": [ "uk-no-transfer" ],
          "requiredAcknowledgements": [
            {
              "acknowledgementId": "operator-notice",
              "acknowledgementVersion": 1
            }
          ],
          "approval": {
            "ownerReference": "private-policy-owner",
            "reviewerReference": "private-reviewer",
            "reviewedAtUtc": "2025-12-15T00:00:00Z",
            "sources": [
              {
                "referenceId": "source-1",
                "uri": "https://example.test/policy-source"
              }
            ],
            "detachedSignatureReference": "private-signature-1"
          }
        }
        """;

    public enum OperationMismatch
    {
        UnknownPolicy = 1,
        Country = 2,
        DataRegion = 3,
        TransferProfile = 4,
        RetentionPolicy = 5,
        Acknowledgement = 6,
        AccommodationType = 7,
        Purpose = 8,
        Surface = 9,
        Provenance = 10
    }
}
