namespace BunkFy.Modules.Workspaces.AdminCli;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.Json;
using BunkFy.Modules.Workspaces.Admin.Contracts;
using BunkFy.Modules.Workspaces.Application;
using BunkFy.Modules.Workspaces.Application.Commands;
using BunkFy.Modules.Workspaces.Application.Queries;
using BunkFy.Modules.Workspaces.Contracts;
using BunkFy.Modules.Workspaces.Domain;
using BunkFy.Modules.Workspaces.Persistence;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Gma.Modules.Auth.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public sealed class WorkspacesAdminCliModule : IAdminCliModule
{
    private const string ApplyOutcomeUnknownOperatorAction =
        "Apply outcome is indeterminate. Archive this output and run identity-anchors status with the reviewed owner manifest; do not retry this reconcile request.";

    public string Name => WorkspacesModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(WorkspacesProfiles.Default, "BunkFy.Modules.Workspaces.AdminCli");
        string globalAuthScopeId = builder.Configuration["Auth:GlobalScopeId"] ??
            AuthProfile.DefaultGlobalScopeId;
        builder.Services.AddWorkspacesApplication(
            builder.Configuration,
            globalAuthScopeId);
        builder.AddWorkspacesPersistence();
    }

    public void MapCommands(IAdminCliCommandRegistry commands)
    {
        AdminCliGlobalOptions global = commands.Services.GetRequiredService<AdminCliGlobalOptions>();
        Command access = new("access", "Inspect and migrate workspace access profiles.")
        {
            CreateStatusCommand(commands.Services, global),
            CreateBootstrapCommand(commands.Services, global)
        };
        Command staffAccess = new("staff-access", "Inspect and retry Staff access lifecycle processes.")
        {
            CreateStaffAccessListCommand(commands.Services, global),
            CreateStaffAccessRetryCommand(commands.Services, global)
        };
        Command identityAnchors = new(
            "identity-anchors",
            "Inspect and reconcile durable Staff identity provisioning anchors.")
        {
            CreateIdentityAnchorStatusCommand(commands.Services, global),
            CreateIdentityAnchorReconcileCommand(commands.Services, global),
            CreateIdentityAnchorHistoricalNoProvisionCommand(
                commands.Services,
                global)
        };
        Command module = new(WorkspacesModuleMetadata.Name, "Workspace composition administration operations.")
        {
            access,
            staffAccess,
            identityAnchors
        };
        commands.AddCommand(this.Name, module);
    }

    private static Command CreateStatusCommand(IServiceProvider services, AdminCliGlobalOptions global)
    {
        Command command = new("status", "Inspect access seed and legacy-member migration status.");
        command.SetAction((parse, cancellationToken) => services.GetRequiredService<AdminCliExecutor>().ExecuteAsync(
            parse,
            AdminOperation.Create(
                WorkspacesAdminOperationNames.AccessBootstrapStatus,
                WorkspacesAdminPermissions.AccessBootstrap),
            parse.GetValue(global.TenantOption),
            requireTenant: true,
            async (provider, token) =>
            {
                Result<WorkspaceAccessBootstrapStatus> result = await provider
                    .GetRequiredService<IRequestDispatcher>()
                    .QueryAsync(new GetWorkspaceAccessBootstrapStatusQuery(), token)
                    .ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    WriteStatus(result.Value, parse.GetValue(global.OutputOption) ?? AdminCliOutput.Table);
                }

                return result;
            },
            cancellationToken));
        return command;
    }

    private static Command CreateBootstrapCommand(IServiceProvider services, AdminCliGlobalOptions global)
    {
        Option<bool> yes = new("--yes");
        Command command = new("bootstrap", "Seed profiles and migrate every legacy member in the workspace.")
        {
            yes
        };
        command.SetAction((parse, cancellationToken) => services.GetRequiredService<AdminCliExecutor>().ExecuteAsync(
            parse,
            AdminOperation.Create(
                WorkspacesAdminOperationNames.AccessBootstrapRun,
                WorkspacesAdminPermissions.AccessBootstrap),
            parse.GetValue(global.TenantOption),
            requireTenant: true,
            async (provider, token) =>
            {
                Result<WorkspaceAccessBootstrapResult> result = parse.GetValue(yes)
                    ? await provider.GetRequiredService<IRequestDispatcher>()
                        .SendAsync(new BootstrapWorkspaceAccessCommand(), token)
                        .ConfigureAwait(false)
                    : Result.Failure<WorkspaceAccessBootstrapResult>(AdminErrors.ConfirmationRequired);
                if (result.IsSuccess)
                {
                    WriteBootstrapResult(
                        result.Value,
                        parse.GetValue(global.OutputOption) ?? AdminCliOutput.Table);
                }

                return result;
            },
            cancellationToken));
        return command;
    }

    internal static void WriteStatus(WorkspaceAccessBootstrapStatus status, string output)
    {
        if (AdminCliOutput.NormalizeFormat(output) == AdminCliOutput.Json)
        {
            AdminCliOutput.WriteObject(status, output);
            return;
        }

        AdminCliOutput.WriteRows(
            [status],
            output,
            [
                ("SeedVersion", item => item.SeedVersion.ToString(CultureInfo.InvariantCulture)),
                ("ExpectedSeeds", item => item.ExpectedSeedProfileCount.ToString(CultureInfo.InvariantCulture)),
                ("ActiveSeeds", item => item.ActiveSeedProfileCount.ToString(CultureInfo.InvariantCulture)),
                ("DriftedSeeds", item => item.DriftedSeedProfileCount.ToString(CultureInfo.InvariantCulture)),
                ("ArchivedSeeds", item => item.ArchivedSeedProfileCount.ToString(CultureInfo.InvariantCulture)),
                ("LegacyMembers", item => item.LegacyMemberCount.ToString(CultureInfo.InvariantCulture)),
                ("MarkerMembers", item => item.MarkerMemberCount.ToString(CultureInfo.InvariantCulture)),
                ("RequiresBackfill", item => item.RequiresBackfill.ToString())
            ]);
    }

    internal static void WriteBootstrapResult(
        WorkspaceAccessBootstrapResult result,
        string output)
    {
        if (AdminCliOutput.NormalizeFormat(output) == AdminCliOutput.Json)
        {
            AdminCliOutput.WriteObject(result, output);
            return;
        }

        AdminCliOutput.WriteMessage(
            $"Workspace access seed v{result.SeedVersion} is ready; " +
            $"migrated {result.MigratedMemberCount} legacy member(s).");
    }

    private static Command CreateStaffAccessListCommand(
        IServiceProvider services,
        AdminCliGlobalOptions global)
    {
        Option<int> page = new("--page")
        {
            DefaultValueFactory = _ => PageRequest.DefaultPage
        };
        Option<int> pageSize = new("--page-size")
        {
            DefaultValueFactory = _ => PageRequest.DefaultPageSize
        };
        Command command = new("list", "List non-completed Staff access lifecycle processes.")
        {
            page,
            pageSize
        };
        command.SetAction((parse, cancellationToken) => services.GetRequiredService<AdminCliExecutor>().ExecuteAsync(
            parse,
            AdminOperation.Create(
                WorkspacesAdminOperationNames.StaffAccessList,
                WorkspacesAdminPermissions.StaffAccessManage),
            parse.GetValue(global.TenantOption),
            requireTenant: true,
            async (provider, token) =>
            {
                Result<WorkspaceStaffAccessProcessListResponse> result = await provider
                    .GetRequiredService<IRequestDispatcher>()
                    .QueryAsync(new ListOpenWorkspaceStaffAccessProcessesQuery(
                        parse.GetValue(page),
                        parse.GetValue(pageSize)), token)
                    .ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    AdminCliOutput.WriteRows(
                        result.Value.Items,
                        parse.GetValue(global.OutputOption) ?? AdminCliOutput.Table,
                        [
                            ("ProcessId", item => item.ProcessId.ToString("D")),
                            ("StaffMemberId", item => item.StaffMemberId.ToString("D")),
                            ("Target", item => item.TargetStatus.ToString()),
                            ("StaffVersion", item => item.TargetStaffVersion.ToString(CultureInfo.InvariantCulture)),
                            ("Status", item => item.Status.ToString()),
                            ("Profiles", item => item.ProfileCount.ToString(CultureInfo.InvariantCulture)),
                            ("Failure", item => item.FailureCode ?? string.Empty)
                        ]);
                }

                return result;
            },
            cancellationToken));
        return command;
    }

    private static Command CreateStaffAccessRetryCommand(
        IServiceProvider services,
        AdminCliGlobalOptions global)
    {
        Option<Guid> processId = new("--process-id") { Required = true };
        Option<bool> yes = new("--yes");
        Command command = new("retry", "Retry one Staff access lifecycle process.")
        {
            processId,
            yes
        };
        command.SetAction((parse, cancellationToken) => services.GetRequiredService<AdminCliExecutor>().ExecuteAsync(
            parse,
            AdminOperation.Create(
                WorkspacesAdminOperationNames.StaffAccessRetry,
                WorkspacesAdminPermissions.StaffAccessManage),
            parse.GetValue(global.TenantOption),
            requireTenant: true,
            async (provider, token) =>
            {
                Result<WorkspaceStaffAccessProcessDto> result = parse.GetValue(yes)
                    ? await provider.GetRequiredService<IRequestDispatcher>()
                        .SendAsync(new RetryWorkspaceStaffAccessProcessCommand(
                            parse.GetValue(processId)), token)
                        .ConfigureAwait(false)
                    : Result.Failure<WorkspaceStaffAccessProcessDto>(AdminErrors.ConfirmationRequired);
                if (result.IsSuccess)
                {
                    AdminCliOutput.WriteMessage(
                        $"Staff access process {result.Value.ProcessId:D} is {result.Value.Status}.");
                }

                return result;
            },
            cancellationToken));
        return command;
    }

    private static Command CreateIdentityAnchorStatusCommand(
        IServiceProvider services,
        AdminCliGlobalOptions global)
    {
        Option<string?> ownerMap = new("--owner-map");
        Command command = new(
            "status",
            "Inspect the complete tenant identity-anchor source and state plan.")
        {
            ownerMap
        };
        command.SetAction((parse, cancellationToken) =>
            services.GetRequiredService<AdminCliExecutor>().ExecuteAsync(
                parse,
                AdminOperation.Create(
                    WorkspacesAdminOperationNames.IdentityAnchorsStatus,
                    WorkspacesAdminPermissions.IdentityAnchorsReconcile),
                parse.GetValue(global.TenantOption),
                requireTenant: true,
                async (provider, token) =>
                {
                    Result<WorkspaceStaffIdentityAnchorOwnerManifest?> manifest =
                        await ReadOwnerManifestAsync(
                            parse.GetValue(ownerMap),
                            required: false,
                            token).ConfigureAwait(false);
                    if (manifest.IsFailure)
                    {
                        return Result.Failure<
                            WorkspaceStaffIdentityAnchorCutoverStatus>(
                                manifest.Error);
                    }

                    Result<WorkspaceStaffIdentityAnchorCutoverStatus> result =
                        await provider.GetRequiredService<IRequestDispatcher>()
                            .QueryAsync(
                                new GetWorkspaceStaffIdentityAnchorCutoverStatusQuery(
                                    manifest.Value),
                                token).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        WriteIdentityAnchorStatus(
                            result.Value,
                            parse.GetValue(global.OutputOption) ??
                                AdminCliOutput.Table);
                    }

                    return result;
                },
                cancellationToken));
        return command;
    }

    private static Command CreateIdentityAnchorReconcileCommand(
        IServiceProvider services,
        AdminCliGlobalOptions global)
    {
        Option<string> expectedSourceEvidenceSha256 = new(
            "--expected-source-evidence-sha256")
        {
            Required = true
        };
        Option<string> expectedAnchorStateSha256 = new(
            "--expected-anchor-state-sha256")
        {
            Required = true
        };
        Option<string> ownerMap = new("--owner-map") { Required = true };
        Option<string> expectedOwnerManifestSha256 = new(
            "--expected-owner-manifest-sha256")
        {
            Required = true
        };
        Option<int> batchSize = new("--batch-size")
        {
            DefaultValueFactory = _ => 100
        };
        Option<bool> yes = new("--yes");
        Command command = new(
            "reconcile",
            "Import one reviewed batch after a whole-tenant fail-closed plan.")
        {
            expectedSourceEvidenceSha256,
            expectedAnchorStateSha256,
            ownerMap,
            expectedOwnerManifestSha256,
            batchSize,
            yes
        };
        command.SetAction((parse, cancellationToken) =>
            services.GetRequiredService<AdminCliExecutor>().ExecuteAsync(
                parse,
                AdminOperation.Create(
                    WorkspacesAdminOperationNames.IdentityAnchorsReconcile,
                    WorkspacesAdminPermissions.IdentityAnchorsReconcile),
                parse.GetValue(global.TenantOption),
                requireTenant: true,
                async (provider, token) =>
                {
                    if (!parse.GetValue(yes))
                    {
                        return Result.Failure<
                            WorkspaceStaffIdentityAnchorReconcileResult>(
                                AdminErrors.ConfirmationRequired);
                    }

                    Result<WorkspaceStaffIdentityAnchorOwnerManifest?> manifest =
                        await ReadOwnerManifestAsync(
                            parse.GetValue(ownerMap),
                            required: true,
                            token).ConfigureAwait(false);
                    if (manifest.IsFailure || manifest.Value is null)
                    {
                        return Result.Failure<
                            WorkspaceStaffIdentityAnchorReconcileResult>(
                                manifest.IsFailure
                                    ? manifest.Error
                                    : WorkspaceStaffIdentityAnchorCutoverErrors
                                        .OwnerManifestRequired);
                    }

                    Result<WorkspaceStaffIdentityAnchorReconcileResult> result =
                        await provider.GetRequiredService<IRequestDispatcher>()
                            .SendAsync(
                                new ReconcileWorkspaceStaffIdentityAnchorsCommand(
                                    parse.GetValue(
                                        expectedSourceEvidenceSha256)!,
                                    parse.GetValue(
                                        expectedAnchorStateSha256)!,
                                    manifest.Value,
                                    parse.GetValue(
                                        expectedOwnerManifestSha256)!,
                                    parse.GetValue(batchSize)),
                                token).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        string output = parse.GetValue(global.OutputOption) ??
                            AdminCliOutput.Table;
                        bool verified =
                            IsVerifiedIdentityAnchorReconcileResult(
                                result.Value);
                        WorkspaceStaffIdentityAnchorReconcileResult printable =
                            verified
                                ? result.Value
                                : result.Value with
                                {
                                    Outcome =
                                        WorkspaceStaffIdentityAnchorReconcileOutcome
                                            .ApplyOutcomeUnknown,
                                    MustRerunStatus = true
                                };
                        if (printable.Status is not null)
                        {
                            WriteIdentityAnchorStatus(
                                printable.Status,
                                output,
                                printable.AppliedCount,
                                printable);
                        }
                        else
                        {
                            WriteIdentityAnchorApplyOutcomeUnknown(
                                printable,
                                output);
                        }

                        if (!verified)
                        {
                            return Result.Failure<
                                WorkspaceStaffIdentityAnchorReconcileResult>(
                                    WorkspaceStaffIdentityAnchorCutoverErrors
                                        .ApplyOutcomeUnknown);
                        }
                    }

                    return result;
                },
                cancellationToken));
        return command;
    }

    private static Command CreateIdentityAnchorHistoricalNoProvisionCommand(
        IServiceProvider services,
        AdminCliGlobalOptions global)
    {
        Option<Guid> operationId = new("--operation-id") { Required = true };
        Option<Guid> applicationId = new("--application-id")
        {
            Required = true
        };
        Option<long> applicationVersion = new("--application-version")
        {
            Required = true
        };
        Option<WorkspaceStaffOnboardingState> applicationStatus = new(
            "--application-status")
        {
            Required = true
        };
        Option<long> organizationsRevision = new("--organizations-revision")
        {
            Required = true
        };
        Option<long> organizationsSourceVersion = new(
            "--organizations-source-version")
        {
            Required = true
        };
        Option<WorkspaceStaffHistoricalNoProvisionAuthorityStatus>
            organizationsSourceStatus = new(
                "--organizations-source-status")
            {
                Required = true
            };
        Option<Guid> evidenceManifestId = new("--evidence-manifest-id")
        {
            Required = true
        };
        Option<string> evidenceSha256 = new("--evidence-sha256")
        {
            Required = true
        };
        Option<bool> yes = new("--yes");
        Command command = new(
            "review-historical-no-provision",
            "Durably classify one source-terminal onboarding as having no Staff authority.")
        {
            operationId,
            applicationId,
            applicationVersion,
            applicationStatus,
            organizationsRevision,
            organizationsSourceVersion,
            organizationsSourceStatus,
            evidenceManifestId,
            evidenceSha256,
            yes
        };
        command.SetAction((parse, cancellationToken) =>
            services.GetRequiredService<AdminCliExecutor>().ExecuteAsync(
                parse,
                AdminOperation.Create(
                    WorkspacesAdminOperationNames
                        .IdentityAnchorsHistoricalNoProvisionReview,
                    WorkspacesAdminPermissions
                        .IdentityAnchorsHistoricalNoProvisionReview),
                parse.GetValue(global.TenantOption),
                requireTenant: true,
                async (provider, token) =>
                {
                    Guid expectedOperationId = parse.GetValue(operationId);
                    Guid expectedApplicationId = parse.GetValue(applicationId);
                    long expectedApplicationVersion = parse.GetValue(
                        applicationVersion);
                    WorkspaceStaffOnboardingState expectedApplicationStatus =
                        parse.GetValue(applicationStatus);
                    string reviewerId = ResolveHistoricalReviewActor(provider);
                    Result<WorkspaceStaffHistoricalNoProvisionDispositionResult>
                        result = parse.GetValue(yes)
                            ? await provider
                                .GetRequiredService<IRequestDispatcher>()
                                .SendAsync(
                                    new
                                        ReviewWorkspaceStaffHistoricalNoProvisionCommand(
                                            expectedOperationId,
                                            expectedApplicationId,
                                            expectedApplicationVersion,
                                            expectedApplicationStatus,
                                            parse.GetValue(
                                                organizationsRevision),
                                            parse.GetValue(
                                                organizationsSourceVersion),
                                            parse.GetValue(
                                                organizationsSourceStatus),
                                            parse.GetValue(evidenceManifestId),
                                            parse.GetValue(evidenceSha256) ??
                                                string.Empty,
                                            reviewerId),
                                    token)
                                .ConfigureAwait(false)
                            : Result.Failure<
                                WorkspaceStaffHistoricalNoProvisionDispositionResult>(
                                    AdminErrors.ConfirmationRequired);
                    if (result.IsSuccess &&
                        (!IsWellFormedHistoricalNoProvisionResult(
                            result.Value,
                            expectedApplicationVersion,
                            expectedApplicationStatus) ||
                         result.Value.OperationId != expectedOperationId ||
                         result.Value.ApplicationId !=
                            expectedApplicationId))
                    {
                        result = Result.Failure<
                            WorkspaceStaffHistoricalNoProvisionDispositionResult>(
                                WorkspaceStaffHistoricalNoProvisionApplicationErrors
                                    .Conflict);
                    }

                    if (result.IsSuccess)
                    {
                        AdminCliOutput.WriteRows(
                            [result.Value],
                            parse.GetValue(global.OutputOption) ??
                                AdminCliOutput.Table,
                            [
                                ("ReceiptId", item => item.ReceiptId.ToString("D")),
                                ("OperationId", item => item.OperationId.ToString("D")),
                                ("ApplicationId", item => item.ApplicationId.ToString("D")),
                                ("ApplicationVersion", item =>
                                    item.ResultApplicationVersion.ToString(
                                        CultureInfo.InvariantCulture)),
                                ("ApplicationStatus", item =>
                                    item.ResultApplicationStatus.ToString()),
                                ("StaffEvidenceSha256", item =>
                                    item.StaffEvidenceSha256),
                                ("CanonicalSha256", item =>
                                    item.CanonicalSha256),
                                ("ReviewedAtUtc", item =>
                                    item.ReviewedAtUtc.ToUniversalTime()
                                        .ToString(
                                            "O",
                                            CultureInfo.InvariantCulture)),
                                ("AlreadyReviewed", item =>
                                    item.AlreadyReviewed.ToString())
                            ]);
                    }

                    return result;
                },
                cancellationToken));
        return command;
    }

    internal static bool IsWellFormedHistoricalNoProvisionResult(
        WorkspaceStaffHistoricalNoProvisionDispositionResult result,
        long expectedApplicationVersion,
        WorkspaceStaffOnboardingState expectedApplicationStatus) =>
        result is not null &&
        result.ReceiptId != Guid.Empty &&
        result.OperationId != Guid.Empty &&
        result.ApplicationId != Guid.Empty &&
        expectedApplicationVersion is >= 1 and < long.MaxValue &&
        Enum.IsDefined(expectedApplicationStatus) &&
        expectedApplicationStatus != WorkspaceStaffOnboardingState.Unknown &&
        IsExpectedHistoricalNoProvisionTransition(
            expectedApplicationVersion,
            expectedApplicationStatus,
            result.ResultApplicationVersion,
            result.ResultApplicationStatus) &&
        IsSha256(result.StaffEvidenceSha256) &&
        IsSha256(result.CanonicalSha256) &&
        result.ReviewedAtUtc != default;

    internal static string ResolveHistoricalReviewActor(
        IServiceProvider provider) => provider
        .GetRequiredService<IAdminActorContext>()
        .Actor?.Id ?? string.Empty;

    private static bool IsExpectedHistoricalNoProvisionTransition(
        long expectedVersion,
        WorkspaceStaffOnboardingState expectedStatus,
        long resultVersion,
        WorkspaceStaffOnboardingState resultStatus) =>
        expectedStatus is WorkspaceStaffOnboardingState.Completed or
            WorkspaceStaffOnboardingState.Rejected or
            WorkspaceStaffOnboardingState.Superseded or
            WorkspaceStaffOnboardingState.Expired or
            WorkspaceStaffOnboardingState.Withdrawn
                ? resultStatus == expectedStatus &&
                  resultVersion >= expectedVersion &&
                  resultVersion <= expectedVersion + 1
                : resultStatus == WorkspaceStaffOnboardingState.Superseded &&
                  resultVersion == expectedVersion + 1;

    internal static bool IsVerifiedIdentityAnchorReconcileResult(
        WorkspaceStaffIdentityAnchorReconcileResult result) =>
        result.Outcome ==
            WorkspaceStaffIdentityAnchorReconcileOutcome.AppliedAndVerified &&
        result.AppliedCount is >= 0 &&
        result.Status is not null &&
        !result.MustRerunStatus &&
        IsSha256(result.AcceptedSourceEvidenceSha256) &&
        IsSha256(result.AcceptedAnchorStateSha256) &&
        IsSha256(result.AcceptedOwnerManifestSha256) &&
        string.Equals(
            result.AcceptedSourceEvidenceSha256,
            result.Status.SourceEvidenceSha256,
            StringComparison.OrdinalIgnoreCase) &&
        result.Status.OwnerManifestProvided &&
        string.Equals(
            result.AcceptedOwnerManifestSha256,
            result.Status.OwnerManifestSha256,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static async Task<Result<
        WorkspaceStaffIdentityAnchorOwnerManifest?>> ReadOwnerManifestAsync(
            string? path,
            bool required,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return required
                ? Result.Failure<WorkspaceStaffIdentityAnchorOwnerManifest?>(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .OwnerManifestRequired)
                : Result.Success<
                    WorkspaceStaffIdentityAnchorOwnerManifest?>(null);
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            WorkspaceStaffIdentityAnchorOwnerManifest? manifest =
                await JsonSerializer.DeserializeAsync<
                    WorkspaceStaffIdentityAnchorOwnerManifest>(
                        stream,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            return manifest is null
                ? Result.Failure<
                    WorkspaceStaffIdentityAnchorOwnerManifest?>(
                        WorkspaceStaffIdentityAnchorCutoverErrors
                            .OwnerManifestInvalid)
                : Result.Success<
                    WorkspaceStaffIdentityAnchorOwnerManifest?>(manifest);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or
                JsonException)
        {
            return Result.Failure<
                WorkspaceStaffIdentityAnchorOwnerManifest?>(
                    WorkspaceStaffIdentityAnchorCutoverErrors
                        .OwnerManifestInvalid);
        }
    }

    private static void WriteIdentityAnchorStatus(
        WorkspaceStaffIdentityAnchorCutoverStatus status,
        string output,
        int? appliedCount = null,
        WorkspaceStaffIdentityAnchorReconcileResult? reconcile = null) =>
        AdminCliOutput.WriteRows(
            [new IdentityAnchorStatusOutput(
                appliedCount,
                status,
                reconcile?.Outcome.ToString(),
                reconcile?.MustRerunStatus,
                reconcile?.AcceptedSourceEvidenceSha256,
                reconcile?.AcceptedAnchorStateSha256,
                reconcile?.AcceptedOwnerManifestSha256,
                reconcile?.Outcome ==
                    WorkspaceStaffIdentityAnchorReconcileOutcome
                        .ApplyOutcomeUnknown
                    ? ApplyOutcomeUnknownOperatorAction
                    : null)],
            output,
            [
                ("Applied", item => item.AppliedCount?.ToString(
                    CultureInfo.InvariantCulture) ?? string.Empty),
                ("Outcome", item => item.Outcome ?? string.Empty),
                ("MustRerunStatus", item =>
                    item.MustRerunStatus?.ToString() ?? string.Empty),
                ("WorkspaceSources", item => item.Status.WorkspaceSourceCount
                    .ToString(CultureInfo.InvariantCulture)),
                ("OwnerBindings", item => item.Status.OwnerBindingCount
                    .ToString(CultureInfo.InvariantCulture)),
                ("AlreadyAnchored", item => item.Status.AlreadyAnchoredCount
                    .ToString(CultureInfo.InvariantCulture)),
                ("SeedableWorkspace", item => item.Status.SeedableWorkspaceCount
                    .ToString(CultureInfo.InvariantCulture)),
                ("SeedableOwners", item => item.Status.SeedableOwnerCount
                    .ToString(CultureInfo.InvariantCulture)),
                ("Ambiguous", item => item.Status.AmbiguousCount
                    .ToString(CultureInfo.InvariantCulture)),
                ("Conflicts", item => item.Status.ConflictCount
                    .ToString(CultureInfo.InvariantCulture)),
                ("Issues", item => item.Status.TotalIssueCount
                    .ToString(CultureInfo.InvariantCulture)),
                ("HasMoreIssues", item =>
                    item.Status.HasMoreIssues.ToString()),
                ("SourceEvidenceSha256", item =>
                    item.Status.SourceEvidenceSha256),
                ("AnchorStateSha256", item => item.Status.AnchorStateSha256),
                ("OwnerManifestSha256", item =>
                    item.Status.OwnerManifestSha256 ?? string.Empty),
                ("HistoricalEvidenceKind", item =>
                    item.Status.HistoricalEvidenceKind.ToString()),
                ("HistoricalEvidenceSha256", item =>
                    item.Status.HistoricalEvidenceSha256 ?? string.Empty),
                ("CanReconcile", item => item.Status.CanReconcile.ToString()),
                ("Ready", item => item.Status.IsReady.ToString())
                ,("AcceptedSourceEvidenceSha256", item =>
                    item.AcceptedSourceEvidenceSha256 ?? string.Empty)
                ,("AcceptedAnchorStateSha256", item =>
                    item.AcceptedAnchorStateSha256 ?? string.Empty)
                ,("AcceptedOwnerManifestSha256", item =>
                    item.AcceptedOwnerManifestSha256 ?? string.Empty)
                ,("OperatorAction", item => item.OperatorAction ?? string.Empty)
            ]);

    private sealed record IdentityAnchorStatusOutput(
        int? AppliedCount,
        WorkspaceStaffIdentityAnchorCutoverStatus Status,
        string? Outcome,
        bool? MustRerunStatus,
        string? AcceptedSourceEvidenceSha256,
        string? AcceptedAnchorStateSha256,
        string? AcceptedOwnerManifestSha256,
        string? OperatorAction);

    private static void WriteIdentityAnchorApplyOutcomeUnknown(
        WorkspaceStaffIdentityAnchorReconcileResult result,
        string output) =>
        AdminCliOutput.WriteRows(
            [new IdentityAnchorApplyOutcomeUnknownOutput(
                result.AppliedCount,
                result.Outcome.ToString(),
                result.MustRerunStatus,
                result.AcceptedSourceEvidenceSha256,
                result.AcceptedAnchorStateSha256,
                result.AcceptedOwnerManifestSha256,
                ApplyOutcomeUnknownOperatorAction)],
            output,
            [
                ("Applied", item => item.AppliedCount?.ToString(
                    CultureInfo.InvariantCulture) ?? string.Empty),
                ("Outcome", item => item.Outcome),
                ("MustRerunStatus", item =>
                    item.MustRerunStatus.ToString()),
                ("AcceptedSourceEvidenceSha256", item =>
                    item.AcceptedSourceEvidenceSha256),
                ("AcceptedAnchorStateSha256", item =>
                    item.AcceptedAnchorStateSha256),
                ("AcceptedOwnerManifestSha256", item =>
                    item.AcceptedOwnerManifestSha256),
                ("OperatorAction", item => item.OperatorAction)
            ]);

    private sealed record IdentityAnchorApplyOutcomeUnknownOutput(
        int? AppliedCount,
        string Outcome,
        bool MustRerunStatus,
        string AcceptedSourceEvidenceSha256,
        string AcceptedAnchorStateSha256,
        string AcceptedOwnerManifestSha256,
        string OperatorAction);
}
