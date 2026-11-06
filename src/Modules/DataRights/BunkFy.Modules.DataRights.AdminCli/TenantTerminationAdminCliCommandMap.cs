namespace BunkFy.Modules.DataRights.AdminCli;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using BunkFy.Modules.DataRights.Admin.Contracts;
using BunkFy.Modules.DataRights.Application.Commands;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Queries;
using BunkFy.Modules.DataRights.Contracts;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Microsoft.Extensions.DependencyInjection;

internal static class TenantTerminationAdminCliCommandMap
{
    public static Command Create(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions) =>
        new(
            "tenant-termination",
            "Request, approve, execute, inspect, and recover tenant termination.")
        {
            CreateStatusCommand(services, globalOptions),
            CreateRequestCommand(services, globalOptions),
            CreateApproveCommand(services, globalOptions),
            CreateDenyCommand(services, globalOptions),
            CreateStartCommand(services, globalOptions),
            CreateDownloadExportCommand(services, globalOptions),
            CreateConfirmExportCommand(services, globalOptions),
            CreateRetryCommand(services, globalOptions),
            CreateCancelCommand(services, globalOptions),
            CreateRecoverCommand(services, globalOptions)
        };

    private static Command CreateStatusCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> caseId = RequiredGuid("--case-id");
        Command command = new("status", "Show bounded case, process, and owner progress.")
        {
            caseId
        };
        command.SetAction((parse, token) => ExecuteAsync(
            services,
            globalOptions,
            parse,
            DataRightsAdminOperationNames.TenantTerminationStatus,
            DataRightsAdminPermissions.TenantTerminationRead,
            (dispatcher, cancellationToken) => dispatcher.QueryAsync(
                new GetTenantTerminationOperatorStatusQuery(
                    parse.GetRequiredValue(caseId)),
                cancellationToken),
            WriteStatus,
            token));
        return command;
    }

    private static Command CreateRequestCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> requestId = RequiredGuid("--request-id");
        Option<bool> export = new("--export");
        Option<DataRightsRequesterRelationship> relationship =
            new("--relationship") { Required = true };
        Option<bool> yes = new("--yes");
        Command command = new(
            "request",
            "Open a tenant-termination case and move it into review.")
        {
            requestId,
            export,
            relationship,
            yes
        };
        command.SetAction((parse, token) => ExecuteConfirmedAsync(
            services,
            globalOptions,
            parse,
            yes,
            DataRightsAdminOperationNames.TenantTerminationRequest,
            DataRightsAdminPermissions.TenantTerminationRequest,
            (dispatcher, actor, cancellationToken) => dispatcher.SendAsync(
                new RequestTenantTerminationCommand(
                    parse.GetRequiredValue(requestId),
                    parse.GetValue(export),
                    parse.GetRequiredValue(relationship),
                    actor),
                cancellationToken),
            WriteCase,
            token));
        return command;
    }

    private static Command CreateApproveCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> caseId = RequiredGuid("--case-id");
        Option<long> expectedVersion = RequiredLong("--expected-version");
        Option<bool> yes = new("--yes");
        ApprovalEvidenceOptions evidence = new();
        Command command = new(
            "approve",
            "Approve tenant termination with production evidence.")
        {
            caseId,
            expectedVersion,
            yes
        };
        evidence.AddTo(command);
        command.SetAction((parse, token) => ExecuteConfirmedAsync(
            services,
            globalOptions,
            parse,
            yes,
            DataRightsAdminOperationNames.TenantTerminationDecide,
            DataRightsAdminPermissions.TenantTerminationApprove,
            (dispatcher, actor, cancellationToken) => dispatcher.SendAsync(
                new DecideTenantTerminationCommand(
                    parse.GetRequiredValue(caseId),
                    DataRightsDecisionOutcome.Approved,
                    DataRightsDecisionReason.RequestValidated,
                    evidence.Read(parse),
                    parse.GetRequiredValue(expectedVersion),
                    actor),
                cancellationToken),
            WriteCase,
            token));
        return command;
    }

    private static Command CreateDenyCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> caseId = RequiredGuid("--case-id");
        Option<long> expectedVersion = RequiredLong("--expected-version");
        Option<DataRightsDecisionReason> reason =
            new("--reason") { Required = true };
        Option<bool> yes = new("--yes");
        Command command = new(
            "deny",
            "Deny a tenant-termination request with a bounded reason.")
        {
            caseId,
            expectedVersion,
            reason,
            yes
        };
        command.SetAction((parse, token) => ExecuteConfirmedAsync(
            services,
            globalOptions,
            parse,
            yes,
            DataRightsAdminOperationNames.TenantTerminationDecide,
            DataRightsAdminPermissions.TenantTerminationApprove,
            (dispatcher, actor, cancellationToken) => dispatcher.SendAsync(
                new DecideTenantTerminationCommand(
                    parse.GetRequiredValue(caseId),
                    DataRightsDecisionOutcome.Denied,
                    parse.GetRequiredValue(reason),
                    ApprovalEvidence: null,
                    parse.GetRequiredValue(expectedVersion),
                    actor),
                cancellationToken),
            WriteCase,
            token));
        return command;
    }

    private static Command CreateStartCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> caseId = RequiredGuid("--case-id");
        Option<Guid> processId = RequiredGuid("--process-id");
        Option<long> expectedCaseVersion = RequiredLong("--expected-case-version");
        Option<bool> yes = new("--yes");
        ApprovalEvidenceOptions evidence = new();
        Command command = new(
            "start",
            "Start an approved tenant termination with a distinct executor.")
        {
            caseId,
            processId,
            expectedCaseVersion,
            yes
        };
        evidence.AddTo(command);
        command.SetAction((parse, token) => ExecuteConfirmedAsync(
            services,
            globalOptions,
            parse,
            yes,
            DataRightsAdminOperationNames.TenantTerminationStart,
            DataRightsAdminPermissions.TenantTerminationExecute,
            (dispatcher, actor, cancellationToken) => dispatcher.SendAsync(
                new StartTenantTerminationCommand(
                    parse.GetRequiredValue(caseId),
                    parse.GetRequiredValue(processId),
                    evidence.Read(parse),
                    parse.GetRequiredValue(expectedCaseVersion),
                    actor),
                cancellationToken),
            WriteStart,
            token));
        return command;
    }

    private static Command CreateRetryCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> processId = RequiredGuid("--process-id");
        Option<long> expectedProcessVersion =
            RequiredLong("--expected-process-version");
        Option<bool> yes = new("--yes");
        Command command = new(
            "retry",
            "Retry only the exact blocked or failed owner work set.")
        {
            processId,
            expectedProcessVersion,
            yes
        };
        command.SetAction((parse, token) => ExecuteConfirmedAsync(
            services,
            globalOptions,
            parse,
            yes,
            DataRightsAdminOperationNames.TenantTerminationRetry,
            DataRightsAdminPermissions.TenantTerminationRetry,
            (dispatcher, actor, cancellationToken) => dispatcher.SendAsync(
                new RetryTenantTerminationCommand(
                    parse.GetRequiredValue(processId),
                    parse.GetRequiredValue(expectedProcessVersion),
                    actor),
                cancellationToken),
            WriteProcess,
            token));
        return command;
    }

    private static Command CreateDownloadExportCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> caseId = RequiredGuid("--case-id");
        Option<Guid> processId = RequiredGuid("--process-id");
        Option<Guid> artifactId = RequiredGuid("--artifact-id");
        Option<string> outputFile = RequiredString("--output-file");
        Option<bool> overwrite = new("--overwrite");
        Option<bool> yes = new("--yes");
        Command command = new(
            "download-export",
            "Download the exact protected tenant export for review.")
        {
            caseId,
            processId,
            artifactId,
            outputFile,
            overwrite,
            yes
        };
        command.SetAction((parse, token) => services
            .GetRequiredService<AdminCliExecutor>()
            .ExecuteAsync(
                parse,
                AdminOperation.Create(
                    DataRightsAdminOperationNames
                        .TenantTerminationExportDownload,
                    DataRightsAdminPermissions
                        .TenantTerminationExportDownload),
                parse.GetValue(globalOptions.TenantOption),
                requireTenant: true,
                async (provider, cancellationToken) =>
                {
                    if (!parse.GetValue(yes))
                    {
                        return Result.Failure<
                            TenantTerminationExportDownloadReport>(
                                AdminErrors.ConfirmationRequired);
                    }

                    Result<DataRightsExportDownload>
                        prepared = await provider
                            .GetRequiredService<IRequestDispatcher>()
                            .SendAsync(
                                new PrepareTenantTerminationExportDownloadCommand(
                                    parse.GetRequiredValue(caseId),
                                    parse.GetRequiredValue(processId),
                                    parse.GetRequiredValue(artifactId),
                                    Actor(parse, globalOptions)),
                                cancellationToken).ConfigureAwait(false);
                    if (prepared.IsFailure)
                    {
                        return Result.Failure<
                            TenantTerminationExportDownloadReport>(
                                prepared.Error);
                    }

                    Result<TenantTerminationExportDownloadReport> written =
                        await TenantTerminationExportFileWriter.WriteAsync(
                            parse.GetRequiredValue(outputFile),
                            parse.GetValue(overwrite),
                            prepared.Value,
                            cancellationToken).ConfigureAwait(false);
                    if (written.IsSuccess)
                    {
                        AdminCliOutput.WriteObject(
                            written.Value,
                            Output(parse, globalOptions));
                    }

                    return written;
                },
                token));
        return command;
    }

    private static Command CreateConfirmExportCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> caseId = RequiredGuid("--case-id");
        Option<Guid> processId = RequiredGuid("--process-id");
        Option<Guid> artifactId = RequiredGuid("--artifact-id");
        Option<long> operationRevision =
            RequiredLong("--export-operation-revision");
        Option<long> expectedProcessVersion =
            RequiredLong("--expected-process-version");
        Option<long> expectedArtifactVersion =
            RequiredLong("--expected-artifact-version");
        Option<string> frozenRevision =
            RequiredString("--frozen-revision-sha256");
        Option<string> fragmentSet =
            RequiredString("--fragment-set-sha256");
        Option<bool> yes = new("--yes");
        Command command = new(
            "confirm-export",
            "Confirm the reviewed export and authorize irreversible destruction.")
        {
            caseId,
            processId,
            artifactId,
            operationRevision,
            expectedProcessVersion,
            expectedArtifactVersion,
            frozenRevision,
            fragmentSet,
            yes
        };
        command.SetAction((parse, token) => ExecuteConfirmedAsync(
            services,
            globalOptions,
            parse,
            yes,
            DataRightsAdminOperationNames.TenantTerminationExportConfirm,
            DataRightsAdminPermissions.TenantTerminationExportConfirm,
            (dispatcher, actor, cancellationToken) => dispatcher.SendAsync(
                new ConfirmTenantTerminationExportCommand(
                    parse.GetRequiredValue(caseId),
                    parse.GetRequiredValue(processId),
                    parse.GetRequiredValue(artifactId),
                    parse.GetRequiredValue(operationRevision),
                    parse.GetRequiredValue(expectedProcessVersion),
                    parse.GetRequiredValue(expectedArtifactVersion),
                    parse.GetRequiredValue(frozenRevision),
                    parse.GetRequiredValue(fragmentSet),
                    actor),
                cancellationToken),
            WriteProcess,
            token));
        return command;
    }

    private static Command CreateCancelCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> processId = RequiredGuid("--process-id");
        Option<long> expectedProcessVersion =
            RequiredLong("--expected-process-version");
        Option<bool> yes = new("--yes");
        Command command = new(
            "cancel",
            "Request verified restoration while termination is reversible.")
        {
            processId,
            expectedProcessVersion,
            yes
        };
        command.SetAction((parse, token) => ExecuteConfirmedAsync(
            services,
            globalOptions,
            parse,
            yes,
            DataRightsAdminOperationNames.TenantTerminationCancel,
            DataRightsAdminPermissions.TenantTerminationCancel,
            (dispatcher, actor, cancellationToken) => dispatcher.SendAsync(
                new RequestTenantTerminationCancellationCommand(
                    parse.GetRequiredValue(processId),
                    parse.GetRequiredValue(expectedProcessVersion),
                    actor),
                cancellationToken),
            WriteProcess,
            token));
        return command;
    }

    private static Command CreateRecoverCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> caseId = RequiredGuid("--case-id");
        Option<Guid> processId = RequiredGuid("--process-id");
        Option<long> expectedCaseVersion = RequiredLong("--expected-case-version");
        Option<long?> expectedProcessVersion =
            new("--expected-process-version");
        Option<bool> yes = new("--yes");
        ApprovalEvidenceOptions evidence = new();
        Command command = new(
            "recover",
            "Reconstruct a protected start or re-signal durable coordination.")
        {
            caseId,
            processId,
            expectedCaseVersion,
            expectedProcessVersion,
            yes
        };
        evidence.AddTo(command);
        command.SetAction((parse, token) => ExecuteConfirmedAsync(
            services,
            globalOptions,
            parse,
            yes,
            DataRightsAdminOperationNames.TenantTerminationRecover,
            DataRightsAdminPermissions.TenantTerminationRecover,
            (dispatcher, actor, cancellationToken) => dispatcher.SendAsync(
                new RecoverTenantTerminationCommand(
                    parse.GetRequiredValue(caseId),
                    parse.GetRequiredValue(processId),
                    evidence.Read(parse),
                    parse.GetRequiredValue(expectedCaseVersion),
                    parse.GetValue(expectedProcessVersion),
                    actor),
                cancellationToken),
            WriteStart,
            token));
        return command;
    }

    private static Task<int> ExecuteConfirmedAsync<T>(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions,
        ParseResult parse,
        Option<bool> confirmation,
        string operationName,
        AdminPermission permission,
        Func<IRequestDispatcher, string, CancellationToken, Task<Result<T>>> action,
        Action<T, string> write,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            services,
            globalOptions,
            parse,
            operationName,
            permission,
            (dispatcher, token) => parse.GetValue(confirmation)
                ? action(dispatcher, Actor(parse, globalOptions), token)
                : Task.FromResult(Result.Failure<T>(
                    AdminErrors.ConfirmationRequired)),
            write,
            cancellationToken);

    private static Task<int> ExecuteAsync<T>(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions,
        ParseResult parse,
        string operationName,
        AdminPermission permission,
        Func<IRequestDispatcher, CancellationToken, Task<Result<T>>> action,
        Action<T, string> write,
        CancellationToken cancellationToken) =>
        services.GetRequiredService<AdminCliExecutor>().ExecuteAsync(
            parse,
            AdminOperation.Create(operationName, permission),
            parse.GetValue(globalOptions.TenantOption),
            requireTenant: true,
            async (provider, token) =>
            {
                Result<T> result = await action(
                    provider.GetRequiredService<IRequestDispatcher>(),
                    token).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    write(result.Value, Output(parse, globalOptions));
                }

                return result;
            },
            cancellationToken);

    private static void WriteStatus(
        TenantTerminationOperatorStatusDto status,
        string output)
    {
        if (AdminCliOutput.NormalizeFormat(output) == AdminCliOutput.Json)
        {
            AdminCliOutput.WriteObject(status, output);
            return;
        }

        WriteCase(status.Case, output);
        if (status.Process is not null)
        {
            WriteProcess(status.Process, output);
        }

        AdminCliOutput.WriteRows(
            status.OwnerWorkItems,
            output,
            [
                ("Owner", item => item.OwnerKey),
                ("Phase", item => item.Phase.ToString()),
                ("Status", item => item.Status.ToString()),
                ("Attempts", item => item.AttemptCount.ToString(CultureInfo.InvariantCulture)),
                ("Result", item => item.ResultCode ?? string.Empty),
                ("Affected", item => item.AffectedCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                ("Remaining", item => item.RemainingActiveCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                ("Version", item => item.Version.ToString(CultureInfo.InvariantCulture))
            ]);
        if (status.ExportHandoff is not null)
        {
            AdminCliOutput.WriteRows(
                [status.ExportHandoff],
                output,
                [
                    ("ArtifactId", item => item.ArtifactId.ToString("D")),
                    ("State", item => item.Status.ToString()),
                    ("ExportRevision", item => item.ExportOperationRevision.ToString(CultureInfo.InvariantCulture)),
                    ("ArtifactVersion", item => item.ArtifactVersion.ToString(CultureInfo.InvariantCulture)),
                    ("Records", item => item.RecordCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                    ("Expires", item => item.ExpiresAtUtc.ToString("O", CultureInfo.InvariantCulture)),
                    ("Confirmed", item => item.Confirmed ? "yes" : "no"),
                    ("ConfirmedArtifactVersion", item => item.ConfirmedArtifactVersion?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
                ]);
        }
    }

    private static void WriteStart(TenantTerminationStartDto value, string output)
    {
        if (AdminCliOutput.NormalizeFormat(output) == AdminCliOutput.Json)
        {
            AdminCliOutput.WriteObject(value, output);
            return;
        }

        WriteCase(value.Case, output);
        WriteProcess(value.Process, output);
    }

    private static void WriteCase(TenantTerminationCaseDto value, string output) =>
        AdminCliOutput.WriteRows(
            [value],
            output,
            [
                ("CaseId", item => item.Id.ToString("D")),
                ("Status", item => item.Status.ToString()),
                ("Decision", item => item.Decision.ToString()),
                ("Export", item => item.ExportRequested ? "yes" : "no"),
                ("DecisionRevision", item => item.DecisionRevision?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                ("ExecutionRevision", item => item.ExecutionRevision?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                ("Version", item => item.Version.ToString(CultureInfo.InvariantCulture))
            ]);

    private static void WriteProcess(
        TenantTerminationProcessDto value,
        string output) =>
        AdminCliOutput.WriteRows(
            [value],
            output,
            [
                ("ProcessId", item => item.Id.ToString("D")),
                ("CaseId", item => item.CaseId.ToString("D")),
                ("Phase", item => item.Phase.ToString()),
                ("Status", item => item.Status.ToString()),
                ("Revision", item => item.OperationRevision.ToString(CultureInfo.InvariantCulture)),
                ("Outcome", item => item.OutcomeCode ?? string.Empty),
                ("Version", item => item.Version.ToString(CultureInfo.InvariantCulture))
            ]);

    private static string Output(
        ParseResult parse,
        AdminCliGlobalOptions globalOptions) =>
        parse.GetValue(globalOptions.OutputOption) ?? AdminCliOutput.Table;

    private static string Actor(
        ParseResult parse,
        AdminCliGlobalOptions globalOptions)
    {
        string actor = string.IsNullOrWhiteSpace(
                parse.GetValue(globalOptions.ActorOption))
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : parse.GetValue(globalOptions.ActorOption)!.Trim();
        return $"admin-cli:{actor}";
    }

    private static Option<Guid> RequiredGuid(string name) =>
        new(name) { Required = true };

    private static Option<long> RequiredLong(string name) =>
        new(name) { Required = true };

    private static Option<string> RequiredString(string name) =>
        new(name) { Required = true };

    private sealed class ApprovalEvidenceOptions
    {
        private readonly Option<string> approval =
            new("--approval-reference") { Required = true };
        private readonly Option<string> ownerCatalog =
            new("--owner-catalog-sha256") { Required = true };
        private readonly Option<string> backup =
            new("--backup-evidence-reference") { Required = true };
        private readonly Option<string> restoreDrill =
            new("--restore-drill-evidence-reference") { Required = true };
        private readonly Option<string> operatorAssurance =
            new("--operator-assurance-reference") { Required = true };

        public void AddTo(Command command)
        {
            command.Options.Add(this.approval);
            command.Options.Add(this.ownerCatalog);
            command.Options.Add(this.backup);
            command.Options.Add(this.restoreDrill);
            command.Options.Add(this.operatorAssurance);
        }

        public TenantTerminationApprovalEvidence Read(ParseResult parse) => new(
            parse.GetRequiredValue(this.approval),
            parse.GetRequiredValue(this.ownerCatalog),
            parse.GetRequiredValue(this.backup),
            parse.GetRequiredValue(this.restoreDrill),
            parse.GetRequiredValue(this.operatorAssurance));
    }
}
