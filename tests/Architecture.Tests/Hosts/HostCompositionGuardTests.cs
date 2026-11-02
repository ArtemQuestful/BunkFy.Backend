namespace Architecture.Tests.Hosts;

using System.Security.Cryptography;
using System.Text.Json;
using Architecture.Tests.Support;
using Xunit;

[Trait("Category", "Architecture")]
public sealed class HostCompositionGuardTests
{
    [Fact]
    public void Stations_http_is_explicit_default_off_postgresql_only_and_checks_lanes_before_authentication()
    {
        foreach (string file in new[] { "appsettings.json", "appsettings.Development.json" })
        {
            using JsonDocument settings = JsonDocument.Parse(RepositoryPaths.Read("src", "BunkFy.Host.Api", file));
            Assert.False(settings.RootElement.GetProperty("Stations").GetProperty("Http").GetProperty("Enabled").GetBoolean());
        }
        string api = RepositoryPaths.Read("src", "BunkFy.Host.Api", "Program.cs");
        AssertSourceOrder(api, "app.UseGmaProductionHttp();", "app.UseStationHttpSecurity();", "app.UseAuthentication();");
        Assert.Contains("StationHttpHostAdmission.Validate(builder.Configuration)", api, StringComparison.Ordinal);
        Assert.Contains("AddStationsManagement(destructiveOperationAssurance, authScopeId)", api, StringComparison.Ordinal);
        string persistence = RepositoryPaths.Read("src", "Modules", "Stations", "BunkFy.Modules.Stations.Persistence", "DependencyInjection.cs");
        Assert.Contains("throw new NotSupportedException", persistence, StringComparison.Ordinal);
        string composition = RepositoryPaths.Read("src", "BunkFy.Host.Migrations", "BunkFyMigrationHostComposition.cs");
        Assert.Contains("GetValue<bool>(\"Stations:Http:Enabled\")", composition, StringComparison.Ordinal);
        Assert.Contains("builder.AddStationsPersistence();", composition, StringComparison.Ordinal);
    }

    [Fact]
    public void Station_inventory_validation_runs_after_all_routes_and_before_each_host_starts()
    {
        string publicProgram = RepositoryPaths.Read("src", "BunkFy.Host.Api", "Program.cs");
        AssertSourceOrder(
            publicProgram,
            "app.MapUserNotificationSignalR();",
            "app.ValidateBunkFyStationK0(\"public\");",
            "app.Run();");

        string adminProgram = RepositoryPaths.Read("src", "BunkFy.Host.AdminApi", "Program.cs");
        AssertSourceOrder(
            adminProgram,
            "app.MapAdminApiModules();",
            "app.ValidateBunkFyStationK0(\"admin\");",
            "app.Run();");
    }

    [Fact]
    public void Development_country_policies_are_synthetic_and_identically_digest_pinned_across_api_and_worker()
    {
        (string FileName, int SchemaVersion, int PolicyVersion)[] expectedArtifacts =
        [
            ("example-hostel-policy.v1.json", 1, 1),
            ("example-hostel-policy.v2.json", 2, 2)
        ];
        Dictionary<int, string> digests = [];

        foreach ((string fileName, int schemaVersion, int policyVersion) in expectedArtifacts)
        {
            string packPath = RepositoryPaths.Resolve(
                "eng",
                "country-policies",
                "development",
                fileName);
            digests.Add(
                policyVersion,
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(packPath))));
            using JsonDocument pack = JsonDocument.Parse(File.ReadAllText(packPath));
            Assert.Equal(schemaVersion, pack.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(policyVersion, pack.RootElement.GetProperty("policyVersion").GetInt32());
            Assert.Equal("example", pack.RootElement.GetProperty("approvalState").GetString());

            JsonElement rightsRule = pack.RootElement.GetProperty("rightsRule");
            if (schemaVersion == 1)
            {
                Assert.False(rightsRule.TryGetProperty("responseRules", out _));
            }
            else
            {
                Assert.Equal(4, rightsRule.GetProperty("responseRules").GetArrayLength());
            }
        }

        (string Host, string PackDirectory)[] hosts =
        [
            ("BunkFy.Host.Api", "../../eng/country-policies/development"),
            ("BunkFy.Host.Worker", "../../../../../eng/country-policies/development")
        ];

        foreach ((string host, string packDirectory) in hosts)
        {
            using JsonDocument defaults = JsonDocument.Parse(RepositoryPaths.Read("src", host, "appsettings.json"));
            JsonElement defaultPolicies = defaults.RootElement.GetProperty("BunkFy").GetProperty("CountryPolicies");
            Assert.Equal(JsonValueKind.Null, defaultPolicies.GetProperty("PackDirectory").ValueKind);
            Assert.Empty(defaultPolicies.GetProperty("Allowlist").EnumerateArray());

            using JsonDocument development = JsonDocument.Parse(
                RepositoryPaths.Read("src", host, "appsettings.Development.json"));
            JsonElement policies = development.RootElement.GetProperty("BunkFy").GetProperty("CountryPolicies");
            Assert.Equal(packDirectory, policies.GetProperty("PackDirectory").GetString());
            JsonElement[] allowlist = policies.GetProperty("Allowlist")
                .EnumerateArray()
                .OrderBy(entry => entry.GetProperty("PolicyVersion").GetInt32())
                .ToArray();
            Assert.Equal(expectedArtifacts.Length, allowlist.Length);

            foreach ((_, _, int policyVersion) in expectedArtifacts)
            {
                JsonElement entry = Assert.Single(
                    allowlist,
                    candidate => candidate.GetProperty("PolicyVersion").GetInt32() == policyVersion);
                Assert.Equal("GB", entry.GetProperty("OperatingCountryCode").GetString());
                Assert.Equal("development-hostel-example", entry.GetProperty("PolicyId").GetString());
                Assert.Equal(digests[policyVersion], entry.GetProperty("ContentSha256").GetString());
                Assert.Equal("Engineering", entry.GetProperty("LaunchStatus").GetString());
            }
        }
    }

    [Fact]
    public void Public_api_composes_reusable_and_product_modules_explicitly()
    {
        string program = RepositoryPaths.Read("src", "BunkFy.Host.Api", "Program.cs");
        string[] expectedTokens =
        [
            "builder.AddModule<TenancyModule>();",
            "builder.Services.AddAccessProfilePermissionAllowlist(WorkspaceAccessRoles.DelegablePermissions);",
            "options => options.ProfileManagementAssurance = privilegedOperationAssurance",
            "options => options.GovernanceOperationsAssurance = privilegedOperationAssurance",
            "options.StaffOnboardingManagementAssurance =",
            "options.TimeZoneManagementAssurance = privilegedOperationAssurance",
            "options.ProcessingActivationAssurance = privilegedOperationAssurance",
            "options.PropertyRetirementAssurance = privilegedOperationAssurance",
            "options.TopologyRetirementAssurance = privilegedOperationAssurance",
            "options.CredentialManagementAssurance = privilegedOperationAssurance",
            "options.CheckpointResetAssurance = privilegedOperationAssurance",
            "options.IngressResumeAssurance = privilegedOperationAssurance",
            "builder.Services.Configure<ReservationsApiSecurityOptions>",
            "builder.Services.Configure<GuestsApiSecurityOptions>",
            "builder.AddModule<AccessControlApiModule>();",
            "builder.Services.AddGmaTenantAccessControlAspNetCore();",
            "AuthProfile authProfile = AuthProfile.Global(authScopeId);",
            "builder.AddAuthModule(authProfile);",
            "builder.AddAuthTotpAuthenticator();",
            "builder.AddAuthOpenIdConnectProviders();",
            "builder.AddBunkFyProductionDeployment(BunkFyDeploymentSurface.PublicApi);",
            "OperationsNotificationsProductionHostRole.PublicApi",
            "builder.AddGmaProductionDataProtection();",
            "builder.AddMinioFileStorage();",
            "builder.AddUserNotificationsRealtime();",
            "builder.AddModule<NotificationsModule>();",
            "builder.AddModule<OrganizationsModule>();",
            "builder.Services.AddAuthNotificationsExtension(options => options.FixedAuthScopeId = authScopeId);",
            "builder.Services.AddAuthOrganizationsExtension(options => options.GlobalAuthScopeId = authScopeId);",
            "builder.Services.AddOrganizationsTenancyExtension();",
            "builder.Services.AddBunkFyWorkspaces(options => options.GlobalAuthScopeId = authScopeId);",
            "builder.Services.AddBunkFyWorkspaceAdmission(builder.Configuration, builder.Environment.IsProduction());",
            "GetValue<bool>(\"BunkFy:WorkspaceStaffOnboardingRecovery:PostEnabled\")",
            "options.StaffOnboardingRecoveryPostEnabled =",
            "Workspace staff onboarding recovery POST cannot be enabled in Production while CP3 privacy blockers remain open.",
            "builder.Services.AddBunkFyOrganizationsDataRights();",
            "builder.Services.AddBunkFyTenantTerminationOperatorCatalog();",
            "builder.Services.AddBunkFyOperationsNotifications();",
            "builder.Services.AddBunkFyOperationsIngestionNotifications();",
            "builder.Services.AddBunkFyReservationGuestRecords();",
            "builder.Services.AddBunkFySmtpEmailSender(builder.Configuration, builder.Environment.IsProduction());",
            "builder.Services.AddNotificationEmailAdapter(builder.Configuration);",
            "builder.AddModule<PropertiesModule>();",
            "builder.AddModule<InventoryModule>();",
            "builder.AddModule<ReservationsModule>();",
            "builder.AddModule<GuestsModule>();",
            "builder.AddModule<StaffModule>();",
            "builder.AddModule<IngestionModule>();",
            "builder.AddModule<DataRightsModule>();",
            "builder.AddModule<RetentionModule>();",
            "builder.AddGmaProductionHttp();",
            "app.UseGmaProductionHttp();",
            "builder.ValidateModuleComposition();",
            "app.MapModules();",
            "app.MapBunkFyProductCapabilities();",
            "app.MapBunkFyReservationGuestRecordEndpoints();"
        ];

        string[] missing = expectedTokens
            .Where(token => !program.Contains(token, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(missing);

        using (JsonDocument defaults = JsonDocument.Parse(
                   RepositoryPaths.Read(
                       "src",
                       "BunkFy.Host.Api",
                       "appsettings.json")))
        {
            Assert.False(defaults.RootElement
                .GetProperty("BunkFy")
                .GetProperty("WorkspaceStaffOnboardingRecovery")
                .GetProperty("PostEnabled")
                .GetBoolean());
        }

        string capabilities = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Api",
            "ProductCapabilitiesEndpoints.cs");
        Assert.Contains(
            "workspaces.Value.StaffOnboardingRecoveryPostEnabled",
            capabilities,
            StringComparison.Ordinal);
        Assert.Contains(
            "bool WorkspaceStaffOnboardingRecoveryEnabled",
            capabilities,
            StringComparison.Ordinal);
        Assert.DoesNotContain("VITE_", capabilities, StringComparison.Ordinal);

        int reservationsSecurityStart = program.IndexOf(
            "builder.Services.Configure<ReservationsApiSecurityOptions>",
            StringComparison.Ordinal);
        int guestsSecurityStart = program.IndexOf(
            "builder.Services.Configure<GuestsApiSecurityOptions>",
            StringComparison.Ordinal);
        int dataRightsSecurityStart = program.IndexOf(
            "builder.Services.Configure<DataRightsApiSecurityOptions>",
            StringComparison.Ordinal);
        Assert.True(reservationsSecurityStart >= 0);
        Assert.True(guestsSecurityStart > reservationsSecurityStart);
        Assert.True(dataRightsSecurityStart > guestsSecurityStart);
        string reservationsSecurity = program[reservationsSecurityStart..guestsSecurityStart];
        Assert.Contains(
            "options.CorrectionExecutionAssurance = privilegedOperationAssurance",
            reservationsSecurity,
            StringComparison.Ordinal);
        string guestsSecurity = program[guestsSecurityStart..dataRightsSecurityStart];
        Assert.Contains(
            "options.CorrectionExecutionAssurance =",
            guestsSecurity,
            StringComparison.Ordinal);
        Assert.Contains(
            "options.RestrictionExecutionAssurance =",
            guestsSecurity,
            StringComparison.Ordinal);
        Assert.Contains(
            "options.DataHoldReleaseAssurance =",
            guestsSecurity,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Smtp_transport_is_opt_in_and_composed_before_notification_email_delivery()
    {
        (string Host, string CompositionFile)[] hosts =
        [
            ("BunkFy.Host.Api", "Program.cs"),
            ("BunkFy.Host.Worker", "WorkerHostBuilderExtensions.cs")
        ];

        foreach ((string host, string compositionFile) in hosts)
        {
            string composition = RepositoryPaths.Read("src", host, compositionFile);
            int transportRegistration = composition.IndexOf(
                "AddBunkFySmtpEmailSender(",
                StringComparison.Ordinal);
            int notificationRegistration = composition.IndexOf(
                "AddNotificationEmailAdapter(",
                StringComparison.Ordinal);

            Assert.True(transportRegistration >= 0, $"{host} does not compose the SMTP transport.");
            Assert.True(
                transportRegistration < notificationRegistration,
                $"{host} must compose SMTP before the notification email adapter.");

            using JsonDocument defaults = JsonDocument.Parse(
                RepositoryPaths.Read("src", host, "appsettings.json"));
            JsonElement smtp = defaults.RootElement.GetProperty("Email").GetProperty("Smtp");
            Assert.False(smtp.GetProperty("Enabled").GetBoolean());
            Assert.Equal("StartTls", smtp.GetProperty("SecurityMode").GetString());
            Assert.False(smtp.GetProperty("AllowUnauthenticatedInProduction").GetBoolean());
            Assert.False(smtp.GetProperty("AllowInsecureTransportInProduction").GetBoolean());
        }

        string capabilities = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Api",
            "ProductCapabilitiesEndpoints.cs");
        Assert.Contains("smtp.Value.Enabled && notificationEmail.Value.Enabled", capabilities, StringComparison.Ordinal);
        Assert.Contains(".AllowAnonymous()", capabilities, StringComparison.Ordinal);
        Assert.DoesNotContain("VITE_", capabilities, StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_profile_permission_allowlist_is_composed_in_every_provisioning_host()
    {
        const string registration =
            "AddAccessProfilePermissionAllowlist(WorkspaceAccessRoles.DelegablePermissions);";
        string[] hosts =
        [
            RepositoryPaths.Read("src", "BunkFy.Host.Api", "Program.cs"),
            RepositoryPaths.Read("src", "BunkFy.Host.AdminApi", "Program.cs"),
            RepositoryPaths.Read("src", "BunkFy.Host.AdminCli", "Program.cs"),
            RepositoryPaths.Read("src", "BunkFy.Host.Worker", "WorkerHostBuilderExtensions.cs")
        ];

        Assert.All(hosts, host => Assert.Contains(registration, host, StringComparison.Ordinal));
    }

    [Fact]
    public void Worker_composes_properties_application_with_its_persistence()
    {
        string worker = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Worker",
            "WorkerHostBuilderExtensions.cs");

        Assert.Contains("builder.Services.AddPropertiesApplication();", worker, StringComparison.Ordinal);
        Assert.Contains("builder.AddPropertiesPersistence();", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_composes_retention_application_with_its_persistence()
    {
        string worker = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Worker",
            "WorkerHostBuilderExtensions.cs");

        Assert.Contains("builder.Services.AddRetentionApplication();", worker, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddRetentionTaskHandlers();", worker, StringComparison.Ordinal);
        Assert.Contains("builder.AddRetentionPersistence();", worker, StringComparison.Ordinal);
    }

    [Fact]
    public void Reservation_guest_record_task_pipeline_is_composed_only_in_worker()
    {
        string api = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Api",
            "Program.cs");
        string worker = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Worker",
            "WorkerHostBuilderExtensions.cs");

        Assert.DoesNotContain("AddReservationsTaskHandlers", api, StringComparison.Ordinal);
        Assert.Contains(
            "builder.Services.AddReservationsTaskHandlers();",
            worker,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_notification_bridge_is_composed_with_ingestion_persistence()
    {
        string api = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Api",
            "Program.cs");
        string worker = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Worker",
            "WorkerHostBuilderExtensions.cs");

        Assert.Contains(
            "builder.Services.AddBunkFyOperationsIngestionNotifications();",
            api,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.AddIngestionPersistence();",
            worker,
            StringComparison.Ordinal);
        Assert.Contains(
            ".AddBunkFyOperationsIngestionNotifications();",
            worker,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Operations_notification_retention_is_pending_and_single_owner_admission_is_composed()
    {
        (string Host, string Role)[] hosts =
        [
            ("BunkFy.Host.Api", "OperationsNotificationsProductionHostRole.PublicApi"),
            ("BunkFy.Host.AdminApi", "OperationsNotificationsProductionHostRole.AdminApi"),
            ("BunkFy.Host.Worker", "OperationsNotificationsProductionHostRole.Worker")
        ];

        foreach ((string host, string role) in hosts)
        {
            using JsonDocument document = JsonDocument.Parse(
                RepositoryPaths.Read("src", host, "appsettings.json"));
            JsonElement admission = document.RootElement
                .GetProperty("BunkFy")
                .GetProperty("OperationsNotifications")
                .GetProperty("ProductionAdmission");
            JsonElement notifications =
                document.RootElement.GetProperty("Notifications");
            JsonElement retention =
                notifications.GetProperty("Retention");
            JsonElement delivery =
                notifications.GetProperty("Delivery");

            Assert.Equal(
                "Pending",
                admission.GetProperty("ApprovalState").GetString());
            Assert.Equal(
                JsonValueKind.Null,
                admission.GetProperty("ApprovalReference").ValueKind);
            Assert.Equal(5, admission.GetProperty("CatalogVersion").GetInt32());
            Assert.Equal(
                JsonValueKind.Null,
                admission.GetProperty("CatalogSha256").ValueKind);
            Assert.Equal(
                retention.GetProperty("ReadHistoryDays").GetInt32(),
                admission.GetProperty("ReadHistoryDays").GetInt32());
            Assert.Equal(
                retention.GetProperty("UnreadHistoryDays").GetInt32(),
                admission.GetProperty("UnreadHistoryDays").GetInt32());
            Assert.Equal(
                retention.GetProperty("BroadcastDays").GetInt32(),
                admission.GetProperty("BroadcastDays").GetInt32());
            Assert.Equal(
                delivery.GetProperty("AttemptRetentionDays").GetInt32(),
                admission.GetProperty("DeliveryAttemptDays").GetInt32());
            Assert.Equal(
                "Unspecified",
                admission.GetProperty("LegacyHistoryDisposition").GetString());
            Assert.Equal(
                "Unspecified",
                admission.GetProperty("RetentionOwner").GetString());
            Assert.Equal(
                0,
                admission.GetProperty("RetentionOwnerInstanceCount").GetInt32());
            Assert.False(retention.GetProperty("Enabled").GetBoolean());

            string composition = host == "BunkFy.Host.Worker"
                ? RepositoryPaths.Read(
                    "src",
                    host,
                    "WorkerHostBuilderExtensions.cs")
                : RepositoryPaths.Read("src", host, "Program.cs");
            Assert.Contains(
                "AddBunkFyOperationsNotificationsProductionAdmission",
                composition,
                StringComparison.Ordinal);
            Assert.Contains(role, composition, StringComparison.Ordinal);
        }

        string adminProject = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.AdminApi",
            "BunkFy.Host.AdminApi.csproj");
        Assert.Contains(
            "BunkFy.Extensions.Operations.Notifications.csproj",
            adminProject,
            StringComparison.Ordinal);

        using JsonDocument migrations = JsonDocument.Parse(
            RepositoryPaths.Read(
                "src",
                "BunkFy.Host.Migrations",
                "appsettings.json"));
        Assert.False(
            migrations.RootElement
                .GetProperty("Notifications")
                .GetProperty("Retention")
                .GetProperty("Enabled")
                .GetBoolean());
    }

    [Fact]
    public void Bunkfy_hosts_do_not_compose_the_generic_files_front_door()
    {
        string[] hostFiles = RepositoryPaths
            .EnumerateFiles("src", "*.cs")
            .Concat(RepositoryPaths.EnumerateFiles("src", "*.csproj"))
            .Where(path => Path.GetFileName(path).StartsWith("BunkFy.Host.", StringComparison.Ordinal) ||
                path.Split(Path.DirectorySeparatorChar).Any(segment =>
                    segment.StartsWith("BunkFy.Host.", StringComparison.Ordinal)))
            .ToArray();

        Assert.NotEmpty(hostFiles);
        foreach (string path in hostFiles)
        {
            string source = File.ReadAllText(path);
            Assert.DoesNotContain("Gma.Modules.Files.Api", source, StringComparison.Ordinal);
            Assert.DoesNotContain("AddModule<FilesModule>()", source, StringComparison.Ordinal);
            Assert.DoesNotContain("Gma.Modules.Files.Api.csproj", source, StringComparison.Ordinal);
        }

        Assert.Contains(
            "builder.AddMinioFileStorage();",
            RepositoryPaths.Read("src", "BunkFy.Host.Api", "Program.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Migrations_host_is_fail_closed_bounded_and_product_catalogued()
    {
        using JsonDocument settings = JsonDocument.Parse(
            RepositoryPaths.Read(
                "src",
                "BunkFy.Host.Migrations",
                "appsettings.json"));
        JsonElement migrations = settings.RootElement.GetProperty("Migrations");
        Assert.Equal("Apply", migrations.GetProperty("Mode").GetString());
        Assert.InRange(
            migrations.GetProperty("LockAcquireTimeoutSeconds").GetInt32(),
            1,
            600);
        Assert.InRange(
            migrations.GetProperty("OperationTimeoutSeconds").GetInt32(),
            30,
            7200);
        Assert.InRange(
            migrations.GetProperty("CommandTimeoutSeconds").GetInt32(),
            5,
            1800);

        JsonElement admission = migrations.GetProperty("ProductionAdmission");
        Assert.Equal("Pending", admission.GetProperty("ApprovalState").GetString());
        Assert.Equal(
            "Unspecified",
            admission.GetProperty("DeploymentProfile").GetString());
        Assert.Equal("Unspecified", admission.GetProperty("Runtime").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            admission.GetProperty("SourceCommitSha").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            admission.GetProperty("ApprovedDatabaseTargetSha256").ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            admission.GetProperty("ApprovedTargetCatalogSha256").ValueKind);
        Assert.Equal(
            "Unspecified",
            admission.GetProperty("ExistingHistoryDisposition").GetString());

        string program = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Migrations",
            "Program.cs");
        Assert.Contains(
            "MigrationsProductionAdmission.ValidateConfigurationOrThrow",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "BunkFyMigrationCatalog.Resolve",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "BunkFyMigrationCoordinator",
            program,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationToken.None", program, StringComparison.Ordinal);

        string coordinator = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Migrations",
            "BunkFyMigrationCoordinator.cs");
        Assert.Contains(
            "PostgreSqlMigrationLock.AcquireAsync",
            coordinator,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetAppliedMigrationsAsync(cancellationToken)",
            coordinator,
            StringComparison.Ordinal);
        Assert.Contains(
            "MigrateAsync(cancellationToken)",
            coordinator,
            StringComparison.Ordinal);

        string migrationLock = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Migrations",
            "PostgreSqlMigrationLock.cs");
        Assert.Contains("pg_try_advisory_lock", migrationLock, StringComparison.Ordinal);
        Assert.Contains("pg_advisory_unlock", migrationLock, StringComparison.Ordinal);

        string catalog = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Migrations",
            "BunkFyMigrationCatalog.cs");
        string[] expectedModules =
        [
            "administration",
            "access-control",
            "auth",
            "notifications",
            "organizations",
            "task-runtime",
            "data-rights",
            "properties",
            "inventory",
            "reservations",
            "guests",
            "staff",
            "workspaces",
            "ingestion",
            "retention"
        ];
        Assert.All(expectedModules, module =>
            Assert.Contains($"\"{module}\"", catalog, StringComparison.Ordinal));
    }

    [Fact]
    public void Integration_api_fixture_provisions_workspace_admission_schema()
    {
        string fixture = RepositoryPaths.Read(
            "tests",
            "Integration.Tests",
            "Support",
            "AuthTestApplication.cs");

        Assert.Contains(
            "private async Task MigrateWorkspaceAdmissionDatabaseAsync()",
            fixture,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetRequiredService<WorkspacesDbContext>()",
            fixture,
            StringComparison.Ordinal);
        Assert.Equal(
            3,
            CountOccurrences(
                fixture,
                "await this.MigrateWorkspaceAdmissionDatabaseAsync()"));
    }

    [Fact]
    public void Integration_api_fixture_disables_unrelated_notification_workers()
    {
        string fixture = RepositoryPaths.Read(
            "tests",
            "Integration.Tests",
            "Support",
            "AuthTestApplication.cs");

        Assert.Equal(
            2,
            CountOccurrences(fixture, "Notifications:Delivery:Enabled"));
        Assert.Equal(
            2,
            CountOccurrences(
                fixture,
                "Notifications:DurableStreams:MonitorEnabled"));
    }

    [Fact]
    public void Production_object_storage_consumers_have_explicit_product_owners()
    {
        string[] consumers = RepositoryPaths
            .EnumerateFiles("src/Modules", "*.cs")
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("IFileStorage", StringComparison.Ordinal))
            .Select(RepositoryPaths.ToRepositoryPath)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "src/Modules/DataRights/BunkFy.Modules.DataRights.Persistence/DataRightsExportArtifactObjectStore.cs",
            "src/Modules/DataRights/BunkFy.Modules.DataRights.Persistence/ProtectedDataRightsExportObjectReader.cs",
            "src/Modules/DataRights/BunkFy.Modules.DataRights.Persistence/ProtectedDataRightsExportObjectWriter.cs",
            "src/Modules/Ingestion/BunkFy.Modules.Ingestion.Persistence/IngestionRawPayloadStore.cs"
        ],
            consumers);
    }

    [Fact]
    public void Production_hosts_allow_only_canonical_module_object_storage()
    {
        string[] settingsPaths =
        [
            "src/BunkFy.Host.Api/appsettings.json",
            "src/BunkFy.Host.AdminApi/appsettings.json",
            "src/BunkFy.Host.AdminCli/appsettings.json",
            "src/BunkFy.Host.Worker/appsettings.json"
        ];

        foreach (string path in settingsPaths)
        {
            using JsonDocument document = JsonDocument.Parse(RepositoryPaths.Read(path.Split('/')));
            string[] allowedContentTypes = document.RootElement
                .GetProperty("FileManagement")
                .GetProperty("AllowedContentTypes")
                .EnumerateArray()
                .Select(value => value.GetString())
                .OfType<string>()
                .ToArray();
            long maximumObjectBytes = document.RootElement
                .GetProperty("FileManagement")
                .GetProperty("MaximumObjectBytes")
                .GetInt64();

            Assert.Equal(
                ["application/json", "application/octet-stream"],
                allowedContentTypes);
            Assert.Equal(64 * 1024 * 1024, maximumObjectBytes);
        }
    }

    [Fact]
    public void Public_api_rate_limits_separate_shared_network_reads_from_sensitive_writes()
    {
        using JsonDocument document = JsonDocument.Parse(
            RepositoryPaths.Read("src", "BunkFy.Host.Api", "appsettings.json"));
        JsonElement rateLimiting = document.RootElement
            .GetProperty("Http")
            .GetProperty("RateLimiting");

        Assert.Equal(60, rateLimiting.GetProperty("SensitivePermitLimit").GetInt32());
        Assert.True(
            rateLimiting.GetProperty("GlobalPermitLimit").GetInt32() >=
            rateLimiting.GetProperty("SensitivePermitLimit").GetInt32());
        Assert.Empty(rateLimiting
            .GetProperty("SensitivePathPrefixes")
            .EnumerateArray());

        Dictionary<string, JsonElement> policies = rateLimiting
            .GetProperty("Policies")
            .EnumerateArray()
            .ToDictionary(
                policy => policy.GetProperty("Name").GetString()!,
                StringComparer.Ordinal);
        Assert.Equal(
            ["authentication-write", "station-read", "station-write", "workspace-join-write"],
            policies.Keys.Order(StringComparer.Ordinal));

        AssertRateLimitPolicy(
            policies["authentication-write"],
            60,
            [
                "/api/auth/register",
                "/api/auth/login",
                "/api/auth/refresh",
                "/api/auth/browser",
                "/api/auth/password",
                "/api/auth/mfa",
                "/api/auth/external",
                "/api/auth/email-verification"
            ]);
        string[] stationPaths =
        [
            "/api/station-management",
            "/api/station-setup",
            "/api/station-runtime"
        ];
        AssertRateLimitPolicy(policies["station-write"], 30, stationPaths);
        AssertRateLimitPolicy(policies["station-read"], 120, stationPaths, ["GET", "HEAD"]);
        AssertRateLimitPolicy(
            policies["workspace-join-write"],
            60,
            [
                "/api/organization-invitations",
                "/api/organization-enrollment",
                "/api/workspace-staff-enrollment"
            ]);
    }

    private static void AssertRateLimitPolicy(
        JsonElement policy,
        int permitLimit,
        string[] expectedPaths,
        string[]? expectedMethods = null)
    {
        Assert.Equal(permitLimit, policy.GetProperty("PermitLimit").GetInt32());
        Assert.Equal(
            expectedPaths,
            policy.GetProperty("PathPrefixes")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());
        Assert.Equal(
            expectedMethods ?? ["POST", "PUT", "PATCH", "DELETE"],
            policy.GetProperty("Methods")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());
    }

    [Fact]
    public void Production_host_defaults_preserve_explicit_deployment_and_storage_safety()
    {
        string[] apiSettingsPaths =
        [
            "src/BunkFy.Host.Api/appsettings.json",
            "src/BunkFy.Host.AdminApi/appsettings.json"
        ];

        foreach (string path in apiSettingsPaths)
        {
            using JsonDocument document = JsonDocument.Parse(
                RepositoryPaths.Read(path.Split('/')));
            JsonElement deployment = document.RootElement
                .GetProperty("BunkFy")
                .GetProperty("Deployment");
            JsonElement http = document.RootElement.GetProperty("Http");

            Assert.Equal("Unspecified", deployment.GetProperty("Profile").GetString());
            Assert.Equal("Unspecified", deployment.GetProperty("ApiTopology").GetString());
            Assert.Equal("Unspecified", deployment.GetProperty("EdgeMode").GetString());
            Assert.Equal("Unspecified", deployment.GetProperty("Runtime").GetString());
            Assert.Equal(JsonValueKind.Null, deployment.GetProperty("ReleaseId").ValueKind);
            Assert.Equal(JsonValueKind.Null, deployment.GetProperty("SourceCommitSha").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                deployment.GetProperty("PromotionEvidenceReference").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                deployment.GetProperty("RollbackEvidenceReference").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                deployment.GetProperty("AdmissionEvidenceReference").ValueKind);
            Assert.Empty(
                http.GetProperty("ForwardedHeaders")
                    .GetProperty("KnownNetworks")
                    .EnumerateArray());
            Assert.Equal(
                "InProcess",
                http.GetProperty("RateLimiting").GetProperty("Mode").GetString());
        }

        using (JsonDocument workerDocument = JsonDocument.Parse(
                   RepositoryPaths.Read(
                       "src",
                       "BunkFy.Host.Worker",
                       "appsettings.json")))
        {
            JsonElement workerDeployment = workerDocument.RootElement
                .GetProperty("BunkFy")
                .GetProperty("Deployment");
            Assert.Equal(
                JsonValueKind.Null,
                workerDeployment.GetProperty("AdmissionEvidenceReference").ValueKind);
        }

        string[] storageSettingsPaths =
        [
            "src/BunkFy.Host.Api/appsettings.json",
            "src/BunkFy.Host.AdminApi/appsettings.json",
            "src/BunkFy.Host.AdminCli/appsettings.json",
            "src/BunkFy.Host.Worker/appsettings.json"
        ];

        foreach (string path in storageSettingsPaths)
        {
            using JsonDocument document = JsonDocument.Parse(
                RepositoryPaths.Read(path.Split('/')));
            JsonElement minio = document.RootElement
                .GetProperty("FileManagement")
                .GetProperty("Minio");

            Assert.False(
                minio.GetProperty("AllowInsecureTransportInProduction").GetBoolean());
            Assert.False(
                minio.GetProperty("AllowBucketCreationInProduction").GetBoolean());
            Assert.False(minio.GetProperty("CreateBucketIfMissing").GetBoolean());
        }
    }

    [Fact]
    public void Raw_email_storage_is_an_explicit_local_development_exception()
    {
        string[] settingsPaths =
        [
            "src/BunkFy.Host.Api/appsettings.Development.json",
            "src/BunkFy.Host.Worker/appsettings.Development.json"
        ];

        foreach (string path in settingsPaths)
        {
            using JsonDocument document = JsonDocument.Parse(RepositoryPaths.Read(path.Split('/')));
            string[] allowedContentTypes = document.RootElement
                .GetProperty("FileManagement")
                .GetProperty("AllowedContentTypes")
                .EnumerateArray()
                .Select(value => value.GetString())
                .OfType<string>()
                .ToArray();

            Assert.Equal(
                [
                    "application/json",
                    "application/octet-stream",
                    "message/rfc822"
                ],
                allowedContentTypes);
        }
    }

    [Fact]
    public void Public_api_protects_authentication_secrets_with_a_production_durable_key_ring()
    {
        string program = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Api",
            "Program.cs");
        string composition = RepositoryPaths.Read(
            "gma",
            "framework",
            "src",
            "Api",
            "Gma.Framework.Api.Production",
            "ProductionDataProtectionDependencyInjection.cs");
        string developmentSettings = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Api",
            "appsettings.Development.json");

        Assert.Contains("builder.AddGmaProductionDataProtection();", program, StringComparison.Ordinal);
        Assert.Contains("PersistKeysToFileSystem", composition, StringComparison.Ordinal);
        Assert.Contains("SetApplicationName", composition, StringComparison.Ordinal);
        Assert.Contains("\"KeyRingPath\": \".data/data-protection-keys\"", developmentSettings, StringComparison.Ordinal);
    }

    [Fact]
    public void Admin_front_doors_compose_the_complete_tenant_termination_control_plane()
    {
        string adminApi = RepositoryPaths.Read("src", "BunkFy.Host.AdminApi", "Program.cs");
        string adminCli = RepositoryPaths.Read("src", "BunkFy.Host.AdminCli", "Program.cs");
        string adminApiProject = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.AdminApi",
            "BunkFy.Host.AdminApi.csproj");
        string adminCliProject = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.AdminCli",
            "BunkFy.Host.AdminCli.csproj");

        Assert.Contains("builder.AddAdminApiModule<AccessControlAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAuthAdminApiModule(AuthProfile.Global(authScopeId));", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<NotificationsAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<OrganizationsAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<DataRightsAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("DataRightsDbContext", adminApi, StringComparison.Ordinal);
        Assert.Contains("Modules\\DataRights\\BunkFy.Modules.DataRights.AdminApi", adminApiProject, StringComparison.Ordinal);
        Assert.Contains("Modules\\DataRights\\BunkFy.Modules.DataRights.Persistence.PostgreSqlMigrations", adminApiProject, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<PropertiesAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<InventoryAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<ReservationsAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<GuestsAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<StaffAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<IngestionAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<RetentionAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<WorkspacesAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminApiModule<TaskRuntimeAdminApiModule>();", adminApi, StringComparison.Ordinal);
        Assert.Contains(
            "builder.AddBunkFyProductionDeployment(BunkFyDeploymentSurface.AdminApi);",
            adminApi,
            StringComparison.Ordinal);
        Assert.Contains("builder.AddGmaProductionHttp();", adminApi, StringComparison.Ordinal);
        Assert.Contains("app.UseGmaProductionHttp();", adminApi, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<AccessControlAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAuthAdminModule(AuthProfile.Global(authScopeId));", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<NotificationsAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<OrganizationsAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<DataRightsAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("Modules\\DataRights\\BunkFy.Modules.DataRights.AdminCli", adminCliProject, StringComparison.Ordinal);
        Assert.Contains("Modules\\DataRights\\BunkFy.Modules.DataRights.Persistence.PostgreSqlMigrations", adminCliProject, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<PropertiesAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<InventoryAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<ReservationsAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<GuestsAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<StaffAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<IngestionAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<RetentionAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<WorkspacesAdminCliModule>();", adminCli, StringComparison.Ordinal);
        Assert.Contains("builder.AddAdminModule<TaskRuntimeAdminCliModule>();", adminCli, StringComparison.Ordinal);

        foreach (string composition in new[] { adminApi, adminCli })
        {
            Assert.Contains("builder.Services.AddBunkFyOperationsNotifications();", composition, StringComparison.Ordinal);
            Assert.Contains("builder.Services.AddBunkFyAccessControlDataRights();", composition, StringComparison.Ordinal);
            Assert.Contains("builder.Services.AddBunkFyOrganizationsDataRights();", composition, StringComparison.Ordinal);
            Assert.Contains("builder.Services.AddBunkFyTaskRuntimeDataRights();", composition, StringComparison.Ordinal);
            Assert.DoesNotContain("AddBunkFyTenantTerminationProductionAdmission", composition, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Tenant_termination_admin_mutations_use_granular_permissions_and_confirmation()
    {
        string endpoints = RepositoryPaths.Read(
            "src",
            "Modules",
            "DataRights",
            "BunkFy.Modules.DataRights.AdminApi",
            "TenantTerminationAdminEndpoints.cs");
        string commands = RepositoryPaths.Read(
            "src",
            "Modules",
            "DataRights",
            "BunkFy.Modules.DataRights.AdminCli",
            "TenantTerminationAdminCliCommandMap.cs");
        string compactEndpoints = RemoveWhitespace(endpoints);
        string compactCommands = RemoveWhitespace(commands);
        (string Operation, string Permission)[] operationPermissionPairs =
        [
            ("TenantTerminationStatus", "TenantTerminationRead"),
            ("TenantTerminationRequest", "TenantTerminationRequest"),
            ("TenantTerminationDecide", "TenantTerminationApprove"),
            ("TenantTerminationStart", "TenantTerminationExecute"),
            ("TenantTerminationExportDownload", "TenantTerminationExportDownload"),
            ("TenantTerminationExportConfirm", "TenantTerminationExportConfirm"),
            ("TenantTerminationRetry", "TenantTerminationRetry"),
            ("TenantTerminationCancel", "TenantTerminationCancel"),
            ("TenantTerminationRecover", "TenantTerminationRecover")
        ];

        foreach ((string operation, string permission) in
                 operationPermissionPairs)
        {
            Assert.Contains($"DataRightsAdminOperationNames.{operation}", compactEndpoints, StringComparison.Ordinal);
            Assert.Contains($"DataRightsAdminPermissions.{permission}", compactEndpoints, StringComparison.Ordinal);
            Assert.Contains($"DataRightsAdminOperationNames.{operation}", compactCommands, StringComparison.Ordinal);
            Assert.Contains($"DataRightsAdminPermissions.{permission}", compactCommands, StringComparison.Ordinal);
        }

        Assert.Equal(
            7,
            CountOccurrences(
                endpoints,
                "AdminErrors.ConfirmationRequired"));
        Assert.Contains("ExecuteConfirmedAsync", commands, StringComparison.Ordinal);
        Assert.Contains("Option<bool> yes = new(\"--yes\")", commands, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_block_group_admin_front_doors_keep_permission_audit_and_confirmation_parity()
    {
        string endpoints = RepositoryPaths.Read(
            "src",
            "Modules",
            "Inventory",
            "BunkFy.Modules.Inventory.AdminApi",
            "InventoryAdminApiModule.cs");
        string commands = RepositoryPaths.Read(
            "src",
            "Modules",
            "Inventory",
            "BunkFy.Modules.Inventory.AdminCli",
            "InventoryBlockGroupAdminCliCommandGroup.cs");
        (string Operation, string Permission)[] operationPermissionPairs =
        [
            ("BlockGroupsPreview", "BlockGroupsManage"),
            ("BlockGroupsList", "Read"),
            ("BlockGroupsGet", "Read"),
            ("BlockGroupMembersList", "Read"),
            ("BlockGroupsCreate", "BlockGroupsManage"),
            ("BlockGroupsReplace", "BlockGroupsManage"),
            ("BlockGroupsRelease", "BlockGroupsManage"),
            ("BlockGroupCreateOperationsGet", "BlockGroupsManage"),
            ("BlockGroupOperationsGet", "BlockGroupsManage")
        ];

        foreach ((string operation, string permission) in
                 operationPermissionPairs)
        {
            Assert.Contains(
                $"InventoryAdminOperationNames.{operation}",
                endpoints,
                StringComparison.Ordinal);
            Assert.Contains(
                $"InventoryAdminPermissions.{permission}",
                endpoints,
                StringComparison.Ordinal);
            Assert.Contains(
                $"InventoryAdminOperationNames.{operation}",
                commands,
                StringComparison.Ordinal);
            Assert.Contains(
                $"InventoryAdminPermissions.{permission}",
                commands,
                StringComparison.Ordinal);
        }

        Assert.Equal(3, CountOccurrences(commands, "Option<bool> yes = new(\"--yes\")"));
        Assert.True(CountOccurrences(endpoints, "Actor(httpContext)") >= 3);
        Assert.Equal(3, CountOccurrences(commands, "ResolveActor(parse, globalOptions)"));
        Assert.Contains("Option<string?> cursor = new(\"--cursor\")", commands, StringComparison.Ordinal);
        Assert.Contains("result.Error.Code", commands, StringComparison.Ordinal);
        Assert.DoesNotContain("new(\"--page\")", commands, StringComparison.Ordinal);

        string legacyCommands = RepositoryPaths.Read(
            "src",
            "Modules",
            "Inventory",
            "BunkFy.Modules.Inventory.AdminCli",
            "InventoryAdminCliModule.cs");
        Assert.True(CountOccurrences(endpoints, "Actor(httpContext)") >= 5);
        Assert.Equal(2, CountOccurrences(legacyCommands, "ResolveActor(parseResult, globalOptions)"));
    }

    [Fact]
    public void Admin_hosts_keep_audit_and_bootstrap_settings_under_their_domain_owners()
    {
        string[] settingsPaths =
        [
            "src/BunkFy.Host.AdminApi/appsettings.json",
            "src/BunkFy.Host.AdminCli/appsettings.json"
        ];

        foreach (string path in settingsPaths)
        {
            using JsonDocument document = JsonDocument.Parse(RepositoryPaths.Read(path.Split('/')));
            JsonElement administration = document.RootElement.GetProperty("Administration");
            JsonElement audit = administration.GetProperty("Audit");
            JsonElement bootstrap = document.RootElement
                .GetProperty("AccessControl")
                .GetProperty("Bootstrap");

            Assert.False(administration.TryGetProperty("Bootstrap", out _));
            Assert.Equal(50, audit.GetProperty("DefaultPageSize").GetInt32());
            Assert.Equal(200, audit.GetProperty("MaxPageSize").GetInt32());
            Assert.Equal(500, audit.GetProperty("DefaultPurgeBatchSize").GetInt32());
            Assert.Equal(2000, audit.GetProperty("MaxPurgeBatchSize").GetInt32());
            Assert.False(bootstrap.GetProperty("AllowWhenAssignmentsExist").GetBoolean());
            Assert.Equal("owner", bootstrap.GetProperty("OwnerRoleName").GetString());
        }
    }

    [Fact]
    public void Worker_keeps_background_module_groups_opt_in()
    {
        string appsettings = RepositoryPaths.Read("src", "BunkFy.Host.Worker", "appsettings.json");
        string options = RepositoryPaths.Read("src", "BunkFy.Host.Worker", "WorkerHostOptions.cs");
        string project = RepositoryPaths.Read("src", "BunkFy.Host.Worker", "BunkFy.Host.Worker.csproj");
        string composition = RepositoryPaths.Read("src", "BunkFy.Host.Worker", "WorkerHostBuilderExtensions.cs");

        Assert.Contains("\"Auth\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"AccessControl\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"Notifications\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"Organizations\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"Properties\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"Inventory\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"Reservations\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"Guests\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"Staff\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"Ingestion\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"Retention\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"TaskRuntime\": false", appsettings, StringComparison.Ordinal);
        Assert.Contains("defaultValue: false", options, StringComparison.Ordinal);
        Assert.Contains(
            "AuthProfile authProfile = AuthProfile.Global(authScopeId);",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.Services.AddAuthTokenHashingInfrastructure(builder.Configuration);",
            composition,
            StringComparison.Ordinal);
        Assert.Contains("Gma.Modules.Auth.Infrastructure.TokenHashing.csproj", project, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Gma.Modules.Auth.Infrastructure\\Gma.Modules.Auth.Infrastructure.csproj",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "NotificationsProfiles.Default",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.AddWorkspacesTerminationAdmissionPersistence();",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "WorkspaceTerminationTaskExecutionContextContributor",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "builder.Services.AddBunkFyTaskRuntimeDataRights();",
            composition,
            StringComparison.Ordinal);
        Assert.Contains(
            "modules.TaskRuntime",
            composition,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Organization_retention_is_bounded_and_disabled_until_a_single_owner_is_selected()
    {
        string[] settingsPaths =
        [
            "src/BunkFy.Host.Api/appsettings.json",
            "src/BunkFy.Host.Worker/appsettings.json"
        ];

        foreach (string path in settingsPaths)
        {
            using JsonDocument document = JsonDocument.Parse(RepositoryPaths.Read(path.Split('/')));
            JsonElement retention = document.RootElement
                .GetProperty("Organizations")
                .GetProperty("Retention");

            Assert.False(retention.GetProperty("Enabled").GetBoolean());
            Assert.InRange(retention.GetProperty("InvitationHistoryDays").GetInt32(), 1, 3650);
            Assert.InRange(retention.GetProperty("EnrollmentHistoryDays").GetInt32(), 1, 3650);
            Assert.InRange(retention.GetProperty("BatchSize").GetInt32(), 1, 10000);
            Assert.InRange(retention.GetProperty("MaxBatchesPerCategoryPerCycle").GetInt32(), 1, 100);
            Assert.InRange(retention.GetProperty("IntervalMinutes").GetInt32(), 1, 1440);
        }
    }

    [Fact]
    public void Aspire_apphost_wires_infrastructure_and_optional_worker_surfaces()
    {
        string program = RepositoryPaths.Read("src", "BunkFy.Host.AppHost", "Program.cs");
        string composition = RepositoryPaths.Read(
            "src",
            "Shared",
            "BunkFy.AppHost.Composition",
            "BunkFyBackendComposition.cs");
        string appsettings = RepositoryPaths.Read("src", "BunkFy.Host.AppHost", "appsettings.json");
        string[] expectedTokens =
        [
            ".AddPostgres(\"postgres\", password: postgreSqlPassword)",
            "IsEnabled(builder, \"AppHost:SqlServer:Enabled\")",
            "builder.AddSqlServer(\"sql\")",
            "builder.AddNats(\"nats\")",
            ".AddContainer(\"minio\", \"quay.io/minio/minio\", \"latest\")",
            "FileManagement__Minio__Endpoint",
            ".AddProject(\"bunkfy-host-migrations\", projectPaths.Migrations)",
            ".AddProject(\"bunkfy-host-api\", projectPaths.Api)",
            ".WaitFor(postgreSql)",
            ".WaitForCompletion(migrations)",
            "Tasks__Worker__Enabled",
            "Worker__Modules__TaskRuntime",
            "Worker__Modules__Notifications",
            "Worker__Modules__AccessControl",
            "Worker__Modules__Organizations",
            "Organizations__Lifecycle__Enabled",
            "Worker__Modules__Guests",
            "Worker__Modules__DataRights",
            "Worker__Modules__Staff",
            "Worker__Modules__Retention",
            "Tasks__Worker__WorkerGroups__6",
            "retention-workers",
            "Tasks__Worker__WorkerGroups__7",
            "tenant-termination-workers",
            "Tasks__Worker__WorkerGroups__8",
            "guest-record-link-workers",
            "AppHost:AdminApi:Enabled",
            "AppHost:Worker:Enabled",
            "AppHost:Redis:Enabled"
        ];

        Assert.Contains("builder.AddBunkFyBackend(new(", program, StringComparison.Ordinal);
        string[] missing = expectedTokens
            .Where(token => !composition.Contains(token, StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(missing);
        Assert.Contains("\"SqlServer\"", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"Enabled\": false", appsettings, StringComparison.Ordinal);

        using JsonDocument workerSettings = JsonDocument.Parse(
            RepositoryPaths.Read("src", "BunkFy.Host.Worker", "appsettings.json"));
        string[] workerGroups = workerSettings.RootElement
            .GetProperty("Tasks")
            .GetProperty("Worker")
            .GetProperty("WorkerGroups")
            .EnumerateArray()
            .Select(item => item.GetString())
            .OfType<string>()
            .ToArray();
        Assert.Contains("retention-workers", workerGroups, StringComparer.Ordinal);
        Assert.Contains(
            "tenant-termination-workers",
            workerGroups,
            StringComparer.Ordinal);
        Assert.Equal(1, workerGroups.Count(group => group == "guest-record-link-workers"));
        Assert.Equal(workerGroups.Length, workerGroups.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Product_hosts_default_to_postgre_sql()
    {
        string[] appsettingsFiles =
        [
            "src/BunkFy.Host.Api/appsettings.json",
            "src/BunkFy.Host.AdminApi/appsettings.json",
            "src/BunkFy.Host.AdminCli/appsettings.json",
            "src/BunkFy.Host.Worker/appsettings.json"
        ];

        string[] offenders = appsettingsFiles
            .Where(path => !RepositoryPaths.Read(path.Split('/')).Contains("\"Provider\": \"PostgreSql\"", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Public_api_requires_explicit_production_admission_policy()
    {
        string appsettings = RepositoryPaths.Read("src", "BunkFy.Host.Api", "appsettings.json");
        string development = RepositoryPaths.Read("src", "BunkFy.Host.Api", "appsettings.Development.json");
        string program = RepositoryPaths.Read("src", "BunkFy.Host.Api", "Program.cs");

        Assert.Contains("\"SelfRegistration\"", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"GlobalScopeId\": \"default\"", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"PasswordEnabled\": true", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"ExternalEnabled\": true", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"SelfServiceCreationEnabled\": true", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"AccountRegistration\": \"Unspecified\"", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"WorkspaceCreation\": \"Unspecified\"", appsettings, StringComparison.Ordinal);
        Assert.Contains("\"AccountRegistration\": \"Public\"", development, StringComparison.Ordinal);
        Assert.Contains("\"WorkspaceCreation\": \"SelfService\"", development, StringComparison.Ordinal);
        Assert.Contains("\"RequireVerifiedEmailForWorkspaceCreation\": false", development, StringComparison.Ordinal);
        Assert.Contains(
            "AddBunkFyWorkspaceAdmission(builder.Configuration, builder.Environment.IsProduction())",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Workspace_membership_marker_is_permission_free_and_operation_access_is_profile_owned()
    {
        string roles = RepositoryPaths.Read(
            "src",
            "Modules",
            "Workspaces",
            "BunkFy.Modules.Workspaces.Contracts",
            "WorkspaceAccessRoles.cs");
        string seeds = RepositoryPaths.Read(
            "src",
            "Modules",
            "Workspaces",
            "BunkFy.Modules.Workspaces.Contracts",
            "WorkspaceAccessProfileSeeds.cs");

        Assert.Contains("MembershipMarkerPermissions { get; } = [];", roles, StringComparison.Ordinal);
        Assert.Contains("LegacyMemberPermissions", roles, StringComparison.Ordinal);
        Assert.Contains("WorkspaceAccessRoles.LegacyMemberPermissions", seeds, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessControlPermissionGrants.OwnerWildcard", seeds, StringComparison.Ordinal);
    }

    [Fact]
    public void Imap_adapter_metadata_and_executable_code_are_composed_in_the_correct_hosts()
    {
        string api = RepositoryPaths.Read("src", "BunkFy.Host.Api", "Program.cs");
        string adminApi = RepositoryPaths.Read("src", "BunkFy.Host.AdminApi", "Program.cs");
        string adminCli = RepositoryPaths.Read("src", "BunkFy.Host.AdminCli", "Program.cs");
        string worker = RepositoryPaths.Read("src", "BunkFy.Host.Worker", "WorkerHostBuilderExtensions.cs");
        string adapterHost = RepositoryPaths.Read("src", "BunkFy.AdapterHost", "Program.cs");

        Assert.Contains("AddImapReservationMailAdapterDescriptor();", api, StringComparison.Ordinal);
        Assert.Contains("AddImapReservationMailAdapterDescriptor();", adminApi, StringComparison.Ordinal);
        Assert.Contains("AddImapReservationMailAdapterDescriptor();", adminCli, StringComparison.Ordinal);
        Assert.DoesNotContain("AddImapReservationMailAdapter();", api, StringComparison.Ordinal);
        Assert.DoesNotContain("AddImapReservationMailAdapter();", adminApi, StringComparison.Ordinal);
        Assert.DoesNotContain("AddImapReservationMailAdapter();", adminCli, StringComparison.Ordinal);
        Assert.Contains("AddImapReservationMailAdapter();", worker, StringComparison.Ordinal);
        Assert.Contains(
            "case ImapReservationMailAdapterDescriptor.AdapterType:",
            adapterHost,
            StringComparison.Ordinal);
        Assert.Contains("AddImapReservationMailAdapter();", adapterHost, StringComparison.Ordinal);
    }

    [Fact]
    public void Observation_parser_metadata_and_executable_code_are_composed_in_the_correct_hosts()
    {
        string api = RepositoryPaths.Read("src", "BunkFy.Host.Api", "Program.cs");
        string adminApi = RepositoryPaths.Read("src", "BunkFy.Host.AdminApi", "Program.cs");
        string adminCli = RepositoryPaths.Read("src", "BunkFy.Host.AdminCli", "Program.cs");
        string worker = RepositoryPaths.Read("src", "BunkFy.Host.Worker", "WorkerHostBuilderExtensions.cs");
        string adapterHost = RepositoryPaths.Read("src", "BunkFy.AdapterHost", "Program.cs");

        Assert.Contains("AddReservationMailParserDescriptor();", api, StringComparison.Ordinal);
        Assert.Contains("AddReservationMailParserDescriptor();", adminApi, StringComparison.Ordinal);
        Assert.Contains("AddReservationMailParserDescriptor();", adminCli, StringComparison.Ordinal);
        Assert.DoesNotContain("AddReservationMailParser();", api, StringComparison.Ordinal);
        Assert.DoesNotContain("AddReservationMailParser();", adminApi, StringComparison.Ordinal);
        Assert.DoesNotContain("AddReservationMailParser();", adminCli, StringComparison.Ordinal);
        Assert.Contains("AddReservationMailParser();", worker, StringComparison.Ordinal);
        Assert.DoesNotContain("AddReservationMailParser();", adapterHost, StringComparison.Ordinal);
    }

    [Fact]
    public void File_drop_local_retention_is_composed_in_both_executable_hosts()
    {
        string worker = RepositoryPaths.Read("src", "BunkFy.Host.Worker", "WorkerHostBuilderExtensions.cs");
        string workerSettings = RepositoryPaths.Read("src", "BunkFy.Host.Worker", "appsettings.json");
        string adapterHostOptions = RepositoryPaths.Read("src", "BunkFy.AdapterHost", "AdapterHostOptions.cs");
        string adapterHostProgram = RepositoryPaths.Read("src", "BunkFy.AdapterHost", "Program.cs");

        Assert.Contains("Adapters:JsonFileDrop:ProcessedArchiveRetention", worker, StringComparison.Ordinal);
        Assert.Contains("Adapters:JsonFileDrop:FailedQuarantineRetention", worker, StringComparison.Ordinal);
        Assert.Contains("\"RetentionEnabled\": true", workerSettings, StringComparison.Ordinal);
        Assert.Contains("JsonFileDropProcessedArchiveRetention", adapterHostOptions, StringComparison.Ordinal);
        Assert.Contains("JsonFileDropFailedQuarantineRetention", adapterHostOptions, StringComparison.Ordinal);
        Assert.Contains("options.JsonFileDropRetentionEnabled", adapterHostProgram, StringComparison.Ordinal);
    }

    [Fact]
    public void Backend_container_packages_every_deployable_host()
    {
        string dockerfile = RepositoryPaths.Read("Dockerfile");
        string[] requiredTokens =
        [
            "src/BunkFy.Host.Api/BunkFy.Host.Api.csproj -c Release -o /out/api",
            "src/BunkFy.Host.Worker/BunkFy.Host.Worker.csproj -c Release -o /out/worker",
            "src/BunkFy.Host.AdminApi/BunkFy.Host.AdminApi.csproj -c Release -o /out/admin-api",
            "src/BunkFy.Host.AdminCli/BunkFy.Host.AdminCli.csproj -c Release -o /out/admin-cli",
            "src/BunkFy.AdapterHost/BunkFy.AdapterHost.csproj -c Release -o /out/adapter-host",
            "src/BunkFy.Host.Migrations/BunkFy.Host.Migrations.csproj -c Release -o /out/migrations",
            "COPY --from=publish --chown=app:app /out /opt/bunkfy"
        ];

        foreach (string token in requiredTokens)
        {
            Assert.Contains(token, dockerfile, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Backend_container_pins_an_explicit_serviced_runtime()
    {
        string dockerfile = RepositoryPaths.Read("Dockerfile");

        Assert.Matches(
            @"(?m)^FROM mcr\.microsoft\.com/dotnet/aspnet:\d+\.\d+\.\d+@sha256:[0-9a-f]{64} AS runtime-base$",
            dockerfile);
    }

    [Fact]
    public void Public_api_smoke_exposes_the_non_secret_admission_identity()
    {
        string program = RepositoryPaths.Read(
            "src",
            "BunkFy.Host.Api",
            "Program.cs");

        Assert.Contains(
            "deployment.Value.AdmissionEvidenceReference",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Adapter_host_production_admission_is_fail_closed_and_status_is_gated()
    {
        string program = RepositoryPaths.Read(
            "src",
            "BunkFy.AdapterHost",
            "Program.cs");
        string options = RepositoryPaths.Read(
            "src",
            "BunkFy.AdapterHost",
            "AdapterHostOptions.cs");
        string admission = RepositoryPaths.Read(
            "src",
            "BunkFy.AdapterHost",
            "AdapterHostProductionAdmission.cs");
        string settings = RepositoryPaths.Read(
            "src",
            "BunkFy.AdapterHost",
            "appsettings.json");

        Assert.Contains(
            "AdapterHostProductionAdmission.ValidateOrThrow(",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "AdapterHostStartupPreflight",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "AdapterHostStatusEndpointExposure.Disabled",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "AdapterHostCoordinationMode.ServerLease",
            admission,
            StringComparison.Ordinal);
        Assert.Contains(
            "runtime.AllowInsecureLoopback",
            admission,
            StringComparison.Ordinal);
        Assert.Contains(
            "listenUri.AbsolutePath != \"/\"",
            options,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"ApprovalState\": \"Pending\"",
            settings,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static void AssertSourceOrder(
        string source,
        string first,
        string second,
        string third)
    {
        int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        int secondIndex = source.IndexOf(second, StringComparison.Ordinal);
        int thirdIndex = source.IndexOf(third, StringComparison.Ordinal);

        Assert.True(firstIndex >= 0, $"Missing source token: {first}");
        Assert.True(secondIndex > firstIndex, $"'{second}' must follow '{first}'.");
        Assert.True(thirdIndex > secondIndex, $"'{third}' must follow '{second}'.");
    }

    private static string RemoveWhitespace(string value) =>
        string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}
