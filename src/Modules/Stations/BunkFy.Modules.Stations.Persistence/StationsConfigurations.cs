namespace BunkFy.Modules.Stations.Persistence;

using BunkFy.Modules.Stations.Domain;
using Microsoft.EntityFrameworkCore;

internal static class StationsConfigurations
{
    public static void Configure(ModelBuilder model)
    {
        var station = model.Entity<Station>();
        station.ToTable("stations", t => t.HasCheckConstraint("CK_station_version", "\"Version\" > 0"));
        station.HasKey(x => new { x.ScopeId, x.Id });
        station.HasAlternateKey(x => new { x.ScopeId, x.Id, x.PropertyId });
        station.Property(x => x.ScopeId).HasMaxLength(36);
        station.Property(x => x.Label).HasMaxLength(100);
        station.Property(x => x.Version).IsConcurrencyToken();

        var browser = model.Entity<StationBrowserSession>();
        browser.ToTable("browser_sessions", t =>
        {
            t.HasCheckConstraint("CK_browser_lifetime", "\"PairingExpiresAtUtc\" > \"IssuedAtUtc\" AND \"ExternalEpoch\" > 0 AND \"Generation\" > 0");
            t.HasCheckConstraint("CK_browser_actor", "(\"StaffMemberId\" IS NULL AND \"ActorSessionId\" IS NULL AND \"AuthorityKind\" = 0 AND \"CredentialRevision\" IS NULL AND \"GrantRevision\" IS NULL AND \"RegistrationRevision\" IS NULL AND \"ActorIdleExpiresAtUtc\" IS NULL AND \"ActorAbsoluteExpiresAtUtc\" IS NULL) OR (\"StaffMemberId\" IS NOT NULL AND \"ActorSessionId\" IS NOT NULL AND \"AuthorityKind\" IN (1,2) AND \"CredentialRevision\" IS NOT NULL AND \"CredentialRevision\" > 0 AND \"RegistrationRevision\" IS NOT NULL AND \"RegistrationRevision\" > 0 AND \"GrantRevision\" IS NULL AND \"ActorIdleExpiresAtUtc\" IS NOT NULL AND \"ActorAbsoluteExpiresAtUtc\" IS NOT NULL)");
            t.HasCheckConstraint("CK_browser_attempts", "\"AttemptCount\" >= 0");
        });
        browser.HasKey(x => new { x.ScopeId, x.Id });
        browser.HasAlternateKey(x => new { x.ScopeId, x.Id, x.StationId, x.PropertyId });
        browser.Property(x => x.ScopeId).HasMaxLength(36);
        browser.Property(x => x.CredentialDigest).HasMaxLength(64);
        browser.HasIndex(x => x.CredentialDigest).IsUnique();
        browser.HasIndex(x => new { x.ScopeId, x.StaffMemberId });
        browser.HasOne<Station>().WithMany().HasForeignKey(x => new { x.ScopeId, x.StationId, x.PropertyId })
            .HasPrincipalKey(x => new { x.ScopeId, x.Id, x.PropertyId }).OnDelete(DeleteBehavior.Restrict);
        browser.Property(x => x.Generation).IsConcurrencyToken();

        var credential = model.Entity<StationStaffCredential>();
        credential.ToTable("staff_credentials", t =>
        {
            t.HasCheckConstraint("CK_credential_version", "\"Revision\" > 0 AND \"AlgorithmVersion\" = 1 AND \"Iterations\" = 600000");
            t.HasCheckConstraint("CK_credential_failure", "\"FailureCount\" >= 0");
            t.HasCheckConstraint("CK_credential_enrollment", "(\"EnrollmentAuthorityKind\" IN (0,2) AND \"EnrollmentAuthSubjectId\" IS NULL) OR (\"EnrollmentAuthorityKind\" = 1 AND \"EnrollmentAuthSubjectId\" IS NOT NULL AND length(btrim(\"EnrollmentAuthSubjectId\")) > 0 AND \"EnrollmentAuthSubjectId\" = btrim(\"EnrollmentAuthSubjectId\"))");
        });
        credential.HasKey(x => new { x.ScopeId, x.StaffMemberId });
        credential.Property(x => x.ScopeId).HasMaxLength(36);
        credential.Property(x => x.Revision).IsConcurrencyToken();
        credential.Property(x => x.Salt).HasMaxLength(88);
        credential.Property(x => x.Verifier).HasMaxLength(44);
        credential.Property(x => x.PepperVersion).HasMaxLength(64);
        credential.Property(x => x.EnrollmentAuthSubjectId).HasMaxLength(256);

        var setup = model.Entity<StationSetupGrant>();
        setup.ToTable("setup_grants", t =>
        {
            t.HasCheckConstraint("CK_setup_valid", "\"ExpiresAtUtc\" > \"CreatedAtUtc\" AND \"ExpectedCredentialRevision\" >= 0 AND \"AuthorityKind\" IN (1,2)");
            t.HasCheckConstraint("CK_setup_enrollment", "(\"ExpectedEnrollmentAuthorityKind\" = 0 AND \"ExpectedEnrollmentAuthSubjectId\" IS NULL) OR (\"ExpectedEnrollmentAuthorityKind\" = \"AuthorityKind\" AND ((\"ExpectedEnrollmentAuthorityKind\" = 2 AND \"ExpectedEnrollmentAuthSubjectId\" IS NULL) OR (\"ExpectedEnrollmentAuthorityKind\" = 1 AND \"ExpectedEnrollmentAuthSubjectId\" IS NOT NULL AND length(btrim(\"ExpectedEnrollmentAuthSubjectId\")) > 0 AND \"ExpectedEnrollmentAuthSubjectId\" = btrim(\"ExpectedEnrollmentAuthSubjectId\"))))");
            t.HasCheckConstraint("CK_setup_issuer", "(\"IssuerKind\" = 0 AND \"IssuerSubjectId\" IS NULL AND \"AssuranceExpiresAtUtc\" IS NULL) OR (\"IssuerKind\" IN (1,2) AND \"IssuerSubjectId\" IS NOT NULL AND length(\"IssuerSubjectId\") = 36 AND \"AssuranceExpiresAtUtc\" IS NOT NULL AND \"AssuranceExpiresAtUtc\" >= \"ExpiresAtUtc\" AND (\"IssuerKind\" = 1 OR (\"ExpectedEnrollmentAuthorityKind\" = 1 AND \"IssuerSubjectId\" = \"ExpectedEnrollmentAuthSubjectId\")))");
        });
        setup.HasKey(x => new { x.ScopeId, x.Id });
        setup.Property(x => x.ScopeId).HasMaxLength(36);
        setup.Property(x => x.ExpectedEnrollmentAuthSubjectId).HasMaxLength(256);
        setup.Property(x => x.IssuerSubjectId).HasMaxLength(36);
        setup.HasOne<StationBrowserSession>().WithMany().HasForeignKey(x => new { x.ScopeId, x.BrowserSessionId, x.StationId, x.PropertyId })
            .HasPrincipalKey(x => new { x.ScopeId, x.Id, x.StationId, x.PropertyId }).OnDelete(DeleteBehavior.Restrict);

        var grant = model.Entity<StationStaffCheckInGrant>();
        grant.ToTable("staff_check_in_grants", t => t.HasCheckConstraint("CK_grant_revision", "\"Revision\" > 0"));
        grant.HasKey(x => new { x.ScopeId, x.PropertyId, x.StaffMemberId });
        grant.Property(x => x.ScopeId).HasMaxLength(36);
        grant.Property(x => x.Revision).IsConcurrencyToken();

        var checkoutGrant = model.Entity<StationStaffCheckOutGrant>();
        checkoutGrant.ToTable("staff_check_out_grants", t => t.HasCheckConstraint("CK_check_out_grant_revision", "\"Revision\" > 0"));
        checkoutGrant.HasKey(x => new { x.ScopeId, x.PropertyId, x.StaffMemberId });
        checkoutGrant.Property(x => x.ScopeId).HasMaxLength(36);
        checkoutGrant.Property(x => x.Revision).IsConcurrencyToken();

        var receipt = model.Entity<StationOperationReceipt>();
        receipt.ToTable("operation_receipts", t =>
        {
            t.HasCheckConstraint("CK_receipt_kind", "\"Kind\" BETWEEN 1 AND 17 AND \"Outcome\" BETWEEN 1 AND 5");
            t.HasCheckConstraint("CK_receipt_issuer", "(\"IssuerKind\" = 0 AND \"IssuerSubjectId\" IS NULL) OR (\"IssuerKind\" IN (1,2) AND \"IssuerSubjectId\" IS NOT NULL AND length(\"IssuerSubjectId\") = 36)");
            t.HasCheckConstraint("CK_receipt_session", "\"IssuerSessionId\" IS NULL OR (\"IssuerKind\" IN (1,2) AND \"Kind\" IN (1,9,15) AND \"IssuerSessionId\" <> '00000000-0000-0000-0000-000000000000'::uuid)");
            t.HasCheckConstraint("CK_receipt_own_pin", "\"Kind\" <> 15 OR (\"IssuerKind\" = 2 AND \"IssuerSubjectId\" IS NOT NULL AND \"IssuerSessionId\" IS NOT NULL AND \"IssuerSessionId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"PropertyId\" IS NOT NULL AND \"PropertyId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"StaffMemberId\" IS NOT NULL AND \"StaffMemberId\" <> '00000000-0000-0000-0000-000000000000'::uuid AND \"ResourceVersion\" IS NOT NULL AND \"ResourceVersion\" >= 0 AND (\"Outcome\" <> 1 OR \"ResourceVersion\" > 0))");
        });
        receipt.HasKey(x => new { x.ScopeId, x.Id });
        receipt.Property(x => x.ScopeId).HasMaxLength(36);
        receipt.Property(x => x.Fingerprint).HasMaxLength(64);
        receipt.Property(x => x.IssuerSubjectId).HasMaxLength(36);

        var registration = model.Entity<StationStaffRegistration>();
        registration.ToTable("staff_registrations", t => t.HasCheckConstraint("CK_registration_version", "\"Version\" > 0 AND \"RosterReference\" > 0"));
        registration.HasKey(x => new { x.ScopeId, x.PropertyId, x.StaffMemberId });
        registration.HasIndex(x => new { x.ScopeId, x.PropertyId, x.RosterReference }).IsUnique();
        registration.Property(x => x.ScopeId).HasMaxLength(36);
        registration.Property(x => x.Version).IsConcurrencyToken();

        var lifecycle = model.Entity<StationsTenantLifecycleState>();
        lifecycle.ToTable("tenant_lifecycle");
        lifecycle.HasKey(x => x.ScopeId);
        lifecycle.Property(x => x.ScopeId).HasMaxLength(36);
        lifecycle.Property(x => x.SharedStationPinEnabled)
            .HasDefaultValue(true)
            .IsRequired();
        lifecycle.Property(x => x.Revision).IsConcurrencyToken();
    }
}
