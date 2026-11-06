namespace BunkFy.Modules.Stations.Tests;

using BunkFy.Modules.Stations.Application;
using BunkFy.Modules.Stations.Contracts;
using BunkFy.Modules.Stations.Domain;
using Xunit;

public sealed class StationDomainTests
{
    internal const string Tenant = "aa000000-0000-0000-0000-000000000001";
    internal static readonly DateTimeOffset Now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);
    internal static StationPinMaterial Material() => new(Convert.ToBase64String(new byte[16]), Convert.ToBase64String(new byte[32]), "test-v1");
    internal static StationOptions Options() => new() { ExternalEpoch = 1, PepperVersion = "test-v1" };
    internal static StationBrowserSession Browser() => new(Guid.NewGuid(), Tenant, Guid.NewGuid(), Guid.NewGuid(), new string('a', 64), Now, Now.AddDays(30), 1);

    [Fact]
    public void Registration_reference_survives_deactivation_without_changing_credentials_or_grants()
    {
        var registration = new StationStaffRegistration(Tenant, Guid.NewGuid(), Guid.NewGuid(), 7);
        Assert.True(registration.Active);
        registration.SetActive(false);
        Assert.False(registration.Active);
        registration.SetActive(true);
        Assert.Equal(7, registration.RosterReference);
        Assert.Equal(3, registration.Version);
        Assert.Throws<ArgumentOutOfRangeException>(() => new StationStaffRegistration(Tenant, Guid.NewGuid(), Guid.NewGuid(), 0));
    }

    [Fact]
    public void Own_PIN_receipt_requires_original_self_session_and_exact_resource_coordinates()
    {
        Guid property = Guid.NewGuid(), staff = Guid.NewGuid(), session = Guid.NewGuid();
        string subject = Guid.NewGuid().ToString("D");
        var coordinates = new StationManagementCoordinates(null, null, property, staff, null, 1);
        Assert.Throws<ArgumentException>(() => Receipt(coordinates, null));
        Assert.Throws<ArgumentException>(() => Receipt(coordinates with { StaffMemberId = null }, session));
        Assert.Throws<ArgumentException>(() => Receipt(coordinates with { PropertyId = Guid.Empty }, session));
        Assert.Throws<ArgumentException>(() => Receipt(coordinates with { Version = 0 }, session));
        Assert.Throws<ArgumentException>(() => Receipt(coordinates, session, issuer: StationIssuerKind.Manager));
        Assert.Equal(session, Receipt(coordinates, session).IssuerSessionId);
        Assert.Equal(0, Receipt(coordinates with { Version = 0 }, session, StationMutationOutcome.Conflict).ResourceVersion);
        StationOperationReceipt Receipt(StationManagementCoordinates values, Guid? originalSession,
            StationMutationOutcome outcome = StationMutationOutcome.Applied, StationIssuerKind issuer = StationIssuerKind.Self) =>
            new(Tenant, Guid.NewGuid(), StationMutationKind.OwnPin, new string('a', 64), new(outcome, Management: values), Now,
                subject, issuer, originalSession);
    }

    [Fact]
    public void Enrollment_binding_is_exact_closed_and_reset_does_not_retain_identity()
    {
        Assert.Throws<ArgumentException>(() => new StationEnrollmentBinding(StationActorKind.LinkedStation, null));
        Assert.Throws<ArgumentException>(() => new StationEnrollmentBinding(StationActorKind.StationOnly, "account"));
        Assert.Throws<ArgumentException>(() => new StationEnrollmentBinding(StationActorKind.LinkedStation, " account "));
        Assert.Throws<ArgumentException>(() => new StationEnrollmentBinding((StationActorKind)99, null));
        var binding = new StationEnrollmentBinding(StationActorKind.LinkedStation, "Account-A");
        Assert.NotEqual(binding, new StationEnrollmentBinding(StationActorKind.LinkedStation, "account-a"));
        var credential = new StationStaffCredential(Tenant, Guid.NewGuid(), Material(), Now, binding);
        Assert.Equal(binding, credential.Enrollment());
        credential.Revoke(Now);
        Assert.False(credential.Enrollment().IsBound);
        Assert.Null(credential.EnrollmentAuthSubjectId);
        credential.Replace(Material(), Now, new(StationActorKind.StationOnly, null));
        Assert.Equal(StationActorKind.StationOnly, credential.EnrollmentAuthorityKind);
        Assert.Null(credential.EnrollmentAuthSubjectId);
    }

    [Fact]
    public void Setup_intent_cannot_substitute_for_enrollment_and_conflicting_kind_is_invalid()
    {
        var browser = Browser();
        var legacy = new StationSetupGrant(Guid.NewGuid(), Tenant, browser.StationId, browser.Id, browser.PropertyId,
            Guid.NewGuid(), StationActorKind.LinkedStation, 0, Now, Now.AddMinutes(10));
        Assert.False(legacy.ExpectedEnrollment().IsBound);
        Assert.Throws<ArgumentException>(() => new StationSetupGrant(Guid.NewGuid(), Tenant, browser.StationId,
            browser.Id, browser.PropertyId, Guid.NewGuid(), StationActorKind.LinkedStation, 0, Now, Now.AddMinutes(10),
            new(StationActorKind.StationOnly, null)));
    }

    [Fact]
    public void Lock_retains_device_binding_and_invalidates_the_previous_generation()
    {
        StationBrowserSession browser = Browser();
        browser.Activate(Guid.NewGuid(), Guid.NewGuid(), StationActorKind.LinkedStation, 1, 1, Now, TimeSpan.FromMinutes(5), TimeSpan.FromHours(12));
        long previous = browser.Generation;
        Assert.True(browser.ActorCurrent(Now.AddMinutes(1), 1));
        Assert.False(browser.ActorCurrent(Now.AddMinutes(5), 1));
        browser.Lock(Now.AddMinutes(5));
        Assert.True(browser.PairingCurrent(Now.AddHours(1), 1));
        Assert.Null(browser.StaffMemberId);
        Assert.Null(browser.ActorSessionId);
        Assert.True(browser.Generation > previous);
        Assert.False(browser.PairingCurrent(Now.AddHours(1), 2));
        browser.Revoke(Now.AddHours(1));
        Assert.False(browser.PairingCurrent(Now.AddHours(1), 1));
    }
    [Fact]
    public void Foreground_activity_renews_idle_only_with_exact_live_actor_and_capped_lifetime()
    {
        var browser = Browser();
        Guid actor = Guid.NewGuid();
        browser.Activate(Guid.NewGuid(), actor, StationActorKind.LinkedStation, 1, 1, Now, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        Assert.True(browser.RecordForegroundActivity(actor, browser.Generation, Now.AddMinutes(4), TimeSpan.FromMinutes(5)));
        Assert.Equal(Now.AddMinutes(9), browser.ActorIdleExpiresAtUtc);
        Assert.True(browser.RecordForegroundActivity(actor, browser.Generation, Now.AddMinutes(8), TimeSpan.FromMinutes(5)));
        Assert.Equal(Now.AddMinutes(10), browser.ActorIdleExpiresAtUtc);
        Assert.False(browser.RecordForegroundActivity(actor, browser.Generation - 1, Now.AddMinutes(9), TimeSpan.FromMinutes(5)));
        Assert.False(browser.RecordForegroundActivity(actor, browser.Generation, Now.AddMinutes(10), TimeSpan.FromMinutes(5)));
    }
    [Fact]
    public void Successful_device_attempts_do_not_consume_budget_or_erase_earlier_failures()
    {
        var browser = Browser();
        for (int i = 0; i < 4; i++)
        {
            Assert.True(browser.ReserveAttempt(Now, 20, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)));
        }

        for (int i = 0; i < 30; i++)
        {
            Assert.True(browser.CanAttempt(Now));
        }
        Assert.Equal(4, browser.AttemptCount);
        Assert.Null(browser.CooldownUntilUtc);
    }
    [Fact]
    public void Pin_reset_destroys_old_material_and_advances_revision()
    {
        var credential = new StationStaffCredential(Tenant, Guid.NewGuid(), Material(), Now);
        credential.Revoke(Now.AddMinutes(1));
        Assert.True(credential.Revoked);
        Assert.Empty(credential.Verifier);
        Assert.Equal(2, credential.Revision);
        credential.Replace(Material(), Now.AddMinutes(2));
        Assert.Equal(3, credential.Revision);
        Assert.False(credential.Revoked);
    }
    [Fact]
    public void Five_failures_cool_down_and_clock_rollback_never_reopens_budget()
    {
        var credential = new StationStaffCredential(Tenant, Guid.NewGuid(), Material(), Now);
        for (int i = 0; i < 5; i++)
        {
            credential.RecordFailure(Now, 5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        }

        Assert.Equal(5, credential.FailureCount);
        Assert.False(credential.CanAttempt(Now.AddMinutes(14)));
        Assert.False(credential.CanAttempt(Now.AddSeconds(-1)));
        Assert.True(credential.CanAttempt(Now.AddMinutes(15)));
        credential.RecordFailure(Now.AddMinutes(15), 5, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15));
        Assert.Equal(1, credential.FailureCount);
    }
    [Fact]
    public void Device_budget_is_independent_of_selected_identity()
    {
        var browser = Browser();
        for (int i = 0; i < 20; i++)
        {
            Assert.True(browser.ReserveAttempt(Now, 20, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)));
        }

        Assert.False(browser.ReserveAttempt(Now, 20, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15)));
        Assert.Equal(20, browser.AttemptCount);
    }
    [Fact]
    public void Setup_is_one_use_exact_lifetime_and_revocable()
    {
        var grant = new StationSetupGrant(Guid.NewGuid(), Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), StationActorKind.StationOnly, 0, Now, Now.AddMinutes(10));
        Assert.False(grant.Consume(Now.AddSeconds(-1)));
        Assert.True(grant.Consume(Now));
        Assert.False(grant.Consume(Now));
        var expired = new StationSetupGrant(Guid.NewGuid(), Tenant, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), StationActorKind.LinkedStation, 0, Now, Now.AddMinutes(10));
        Assert.False(expired.Consume(Now.AddMinutes(10)));
        expired.Revoke();
        Assert.False(expired.Consume(Now));
    }
    [Fact]
    public void Only_closed_authority_branches_can_activate()
    {
        var browser = Browser();
        Assert.Throws<InvalidOperationException>(() => browser.Activate(Guid.NewGuid(), Guid.NewGuid(), StationActorKind.StationOnly, 1, null, Now, TimeSpan.FromMinutes(5), TimeSpan.FromHours(12)));
        Assert.Throws<InvalidOperationException>(() => browser.Activate(Guid.NewGuid(), Guid.NewGuid(), StationActorKind.Unknown, 1, null, Now, TimeSpan.FromMinutes(5), TimeSpan.FromHours(12)));
        Assert.Throws<ArgumentException>(() => new Station(Guid.NewGuid(), "global", Guid.NewGuid(), "Front desk"));
    }
    [Fact]
    public void Receipt_replay_is_exact_without_secret_payload()
    {
        var receipt = new StationOperationReceipt(Tenant, Guid.NewGuid(), StationMutationKind.Lock, new string('a', 64), new(StationMutationOutcome.Applied), Now);
        Assert.True(receipt.Matches(StationMutationKind.Lock, new string('a', 64)));
        Assert.False(receipt.Matches(StationMutationKind.Reset, new string('a', 64)));
        Assert.False(receipt.Matches(StationMutationKind.Lock, new string('b', 64)));
    }
    [Fact]
    public void Defaults_are_bounded_and_require_external_epoch_and_pepper()
    {
        Assert.False(new StationOptions().IsValid());
        Assert.True(Options().IsValid());
        var options = Options();
        options.CredentialFailures = 6;
        Assert.False(options.IsValid());
        options = Options();
        options.MaximumConcurrentKdf = 0;
        Assert.False(options.IsValid());
        options = Options();
        options.PairingDays = 31;
        Assert.False(options.IsValid());
    }
}
