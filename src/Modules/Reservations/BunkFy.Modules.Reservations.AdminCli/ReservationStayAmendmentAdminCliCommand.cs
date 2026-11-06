namespace BunkFy.Modules.Reservations.AdminCli;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using BunkFy.Modules.Reservations.Admin.Contracts;
using BunkFy.Modules.Reservations.Application;
using BunkFy.Modules.Reservations.Application.Commands;
using BunkFy.Modules.Reservations.Application.Queries;
using BunkFy.Modules.Reservations.Contracts;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.Results;
using Microsoft.Extensions.DependencyInjection;

internal static class ReservationStayAmendmentAdminCliCommand
{
    public static Command Create(IServiceProvider services, AdminCliGlobalOptions globalOptions) =>
        new("stay-amendments", "Manage desired reservation-stay amendments and recovery.")
        {
            CreateAmendCommand(services, globalOptions),
            CreateStatusCommand(services, globalOptions),
            CreateListRecoveryCommand(services, globalOptions),
            CreateReconcileCommand(services, globalOptions)
        };

    private static Command CreateAmendCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> operationOption = OperationOption();
        Option<Guid> propertyOption = PropertyOption();
        Option<Guid> reservationOption = ReservationOption();
        Option<string> arrivalOption = RequiredString("--arrival");
        Option<string> departureOption = RequiredString("--departure");
        Option<string?> expectedArrivalTimeOption = new("--expected-arrival-time");
        Option<string?> expectedDepartureTimeOption = new("--expected-departure-time");
        Option<string> unitIdsOption = RequiredString("--unit-ids");
        Option<long> revisionOption = new("--expected-details-revision") { Required = true };
        Option<bool> yesOption = new("--yes");
        Command command = new("amend", "Submit a complete desired pre-arrival stay.")
        {
            operationOption,
            propertyOption,
            reservationOption,
            arrivalOption,
            departureOption,
            expectedArrivalTimeOption,
            expectedDepartureTimeOption,
            unitIdsOption,
            revisionOption,
            yesOption
        };
        command.SetAction((parseResult, cancellationToken) => ExecuteReceiptAsync(
            services,
            globalOptions,
            parseResult,
            ReservationsAdminOperationNames.AmendStay,
            ReservationsAdminPermissions.Manage,
            (provider, token) =>
            {
                if (!parseResult.GetValue(yesOption))
                {
                    return Task.FromResult(Result.Failure<ReservationStayAmendmentReceiptDto>(
                        AdminErrors.ConfirmationRequired));
                }

                if (!TryParseDate(parseResult.GetRequiredValue(arrivalOption), out DateOnly arrival) ||
                    !TryParseDate(parseResult.GetRequiredValue(departureOption), out DateOnly departure) ||
                    arrival >= departure ||
                    !TryParseOptionalTime(
                        parseResult.GetValue(expectedArrivalTimeOption),
                        out TimeOnly? expectedArrivalTime) ||
                    !TryParseOptionalTime(
                        parseResult.GetValue(expectedDepartureTimeOption),
                        out TimeOnly? expectedDepartureTime) ||
                    !TryParseUnitIds(parseResult.GetRequiredValue(unitIdsOption), out Guid[] unitIds))
                {
                    return Task.FromResult(Result.Failure<ReservationStayAmendmentReceiptDto>(
                        ReservationsApplicationErrors.StayAmendmentRequestInvalid));
                }

                return provider.GetRequiredService<IRequestDispatcher>().SendAsync(
                    new AmendReservationStayCommand(
                        parseResult.GetRequiredValue(operationOption),
                        parseResult.GetRequiredValue(propertyOption),
                        parseResult.GetRequiredValue(reservationOption),
                        arrival,
                        departure,
                        expectedArrivalTime,
                        expectedDepartureTime,
                        unitIds,
                        parseResult.GetRequiredValue(revisionOption),
                        ResolveActor(parseResult, globalOptions)),
                    token);
            },
            cancellationToken));
        return command;
    }

    private static Command CreateStatusCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> operationOption = OperationOption();
        Option<Guid> propertyOption = PropertyOption();
        Option<Guid> reservationOption = ReservationOption();
        Command command = new("status", "Get one stay-amendment outcome.")
        {
            operationOption,
            propertyOption,
            reservationOption
        };
        command.SetAction((parseResult, cancellationToken) => ExecuteReceiptAsync(
            services,
            globalOptions,
            parseResult,
            ReservationsAdminOperationNames.GetStayAmendment,
            ReservationsAdminPermissions.Read,
            (provider, token) => provider.GetRequiredService<IRequestDispatcher>().QueryAsync(
                new GetReservationStayAmendmentQuery(
                    parseResult.GetRequiredValue(propertyOption),
                    parseResult.GetRequiredValue(reservationOption),
                    parseResult.GetRequiredValue(operationOption)),
                token),
            cancellationToken));
        return command;
    }

    private static Command CreateListRecoveryCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> propertyOption = PropertyOption();
        Option<string?> cursorOutcomeOption = new("--cursor-outcome");
        Option<string?> cursorUpdatedAtOption = new("--cursor-updated-at-utc");
        Option<Guid?> cursorOperationOption = new("--cursor-operation-id");
        Option<Guid?> cursorReservationOption = new("--cursor-reservation-id");
        Option<int> pageSizeOption = new("--page-size") { DefaultValueFactory = _ => DefaultPageSize };
        Command command = new("list-recovery", "List bounded pending or outcome-unknown amendments.")
        {
            propertyOption,
            cursorOutcomeOption,
            cursorUpdatedAtOption,
            cursorOperationOption,
            cursorReservationOption,
            pageSizeOption
        };
        command.SetAction((parseResult, cancellationToken) =>
        {
            AdminCliExecutor executor = services.GetRequiredService<AdminCliExecutor>();
            return executor.ExecuteAsync(
                parseResult,
                AdminOperation.Create(
                    ReservationsAdminOperationNames.ListStayAmendmentRecovery,
                    ReservationsAdminPermissions.Read),
                parseResult.GetValue(globalOptions.TenantOption),
                requireTenant: true,
                async (provider, token) =>
                {
                    if (!TryCreateCursor(
                        parseResult.GetValue(cursorOutcomeOption),
                        parseResult.GetValue(cursorUpdatedAtOption),
                        parseResult.GetValue(cursorOperationOption),
                        parseResult.GetValue(cursorReservationOption),
                        out ReservationStayAmendmentRecoveryCursorDto? cursor))
                    {
                        return Result.Failure<ReservationStayAmendmentRecoveryPageDto>(
                            ReservationsApplicationErrors.StayAmendmentRequestInvalid);
                    }

                    Result<ReservationStayAmendmentRecoveryPageDto> result = await provider
                        .GetRequiredService<IRequestDispatcher>()
                        .QueryAsync(
                            new ListReservationStayAmendmentRecoveryQuery(
                                parseResult.GetRequiredValue(propertyOption),
                                cursor,
                                parseResult.GetValue(pageSizeOption)),
                            token)
                        .ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        WriteRecoveryPage(
                            result.Value,
                            parseResult.GetValue(globalOptions.OutputOption) ?? AdminCliOutput.Table);
                    }

                    return result;
                },
                cancellationToken);
        });
        return command;
    }

    private static Command CreateReconcileCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> operationOption = OperationOption();
        Option<Guid> propertyOption = PropertyOption();
        Option<Guid> reservationOption = ReservationOption();
        Option<long> operationVersionOption = new("--expected-operation-version") { Required = true };
        Option<bool> yesOption = new("--yes");
        Command command = new("reconcile", "Republish the exact durable pending amendment.")
        {
            operationOption,
            propertyOption,
            reservationOption,
            operationVersionOption,
            yesOption
        };
        command.SetAction((parseResult, cancellationToken) => ExecuteReceiptAsync(
            services,
            globalOptions,
            parseResult,
            ReservationsAdminOperationNames.ReconcileStayAmendment,
            ReservationsAdminPermissions.Manage,
            (provider, token) => parseResult.GetValue(yesOption)
                ? provider.GetRequiredService<IRequestDispatcher>().SendAsync(
                    new ReconcileReservationStayAmendmentCommand(
                        parseResult.GetRequiredValue(propertyOption),
                        parseResult.GetRequiredValue(reservationOption),
                        parseResult.GetRequiredValue(operationOption),
                        parseResult.GetRequiredValue(operationVersionOption),
                        ResolveActor(parseResult, globalOptions)),
                    token)
                : Task.FromResult(Result.Failure<ReservationStayAmendmentReceiptDto>(
                    AdminErrors.ConfirmationRequired)),
            cancellationToken));
        return command;
    }

    private static async Task<int> ExecuteReceiptAsync(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions,
        ParseResult parseResult,
        string operationName,
        AdminPermission permission,
        Func<IServiceProvider, CancellationToken, Task<Result<ReservationStayAmendmentReceiptDto>>> execute,
        CancellationToken cancellationToken)
    {
        AdminCliExecutor executor = services.GetRequiredService<AdminCliExecutor>();
        return await executor.ExecuteAsync(
            parseResult,
            AdminOperation.Create(operationName, permission),
            parseResult.GetValue(globalOptions.TenantOption),
            requireTenant: true,
            async (provider, token) =>
            {
                Result<ReservationStayAmendmentReceiptDto> result = await execute(provider, token)
                    .ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    WriteReceipts(
                        [result.Value],
                        parseResult.GetValue(globalOptions.OutputOption) ?? AdminCliOutput.Table);
                }

                return result;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static void WriteReceipts(
        IReadOnlyCollection<ReservationStayAmendmentReceiptDto> receipts,
        string output) =>
        AdminCliOutput.WriteRows(
            receipts,
            output,
            [
                ("OperationId", receipt => receipt.OperationId.ToString()),
                ("PropertyId", receipt => receipt.PropertyId.ToString()),
                ("ReservationId", receipt => receipt.ReservationId.ToString()),
                ("Outcome", receipt => receipt.Outcome.ToString()),
                ("Arrival", receipt => receipt.Target?.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty),
                ("Departure", receipt => receipt.Target?.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty),
                ("ExpectedArrival", receipt => receipt.Target?.ExpectedArrivalTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty),
                ("ExpectedDeparture", receipt => receipt.Target?.ExpectedDepartureTime?.ToString("HH:mm", CultureInfo.InvariantCulture) ?? string.Empty),
                ("Units", receipt => receipt.Target is null ? string.Empty : string.Join(',', receipt.Target.InventoryUnitIds)),
                ("ExpectedDetailsRevision", receipt => receipt.ExpectedDetailsRevision.ToString(CultureInfo.InvariantCulture)),
                ("OperationVersion", receipt => receipt.OperationVersion.ToString(CultureInfo.InvariantCulture)),
                ("RecoveryEligible", receipt => receipt.RecoveryEligible.ToString(CultureInfo.InvariantCulture)),
                ("NextRecoveryEligibleAtUtc", receipt => FormatTimestamp(receipt.NextRecoveryEligibleAtUtc))
            ]);

    private static void WriteRecoveryPage(ReservationStayAmendmentRecoveryPageDto page, string output)
    {
        if (AdminCliOutput.NormalizeFormat(output) == AdminCliOutput.Json)
        {
            AdminCliOutput.WriteObject(page, output);
            return;
        }

        AdminCliOutput.WriteRows(
            page.Operations,
            output,
            [
                ("OperationId", operation => operation.OperationId.ToString()),
                ("ReservationId", operation => operation.ReservationId.ToString()),
                ("Outcome", operation => operation.Outcome.ToString()),
                ("OperationVersion", operation => operation.OperationVersion.ToString(CultureInfo.InvariantCulture)),
                ("UpdatedAtUtc", operation => FormatTimestamp(operation.UpdatedAtUtc)),
                ("RecoveryEligible", operation => operation.RecoveryEligible.ToString(CultureInfo.InvariantCulture)),
                ("NextRecoveryEligibleAtUtc", operation => FormatTimestamp(operation.NextRecoveryEligibleAtUtc))
            ]);

        if (page.NextCursor is not null)
        {
            AdminCliOutput.WriteMessage(
                $"Next cursor: --cursor-outcome {page.NextCursor.Outcome} " +
                $"--cursor-updated-at-utc {FormatTimestamp(page.NextCursor.UpdatedAtUtc)} " +
                $"--cursor-operation-id {page.NextCursor.OperationId} " +
                $"--cursor-reservation-id {page.NextCursor.ReservationId}");
        }
    }

    private static bool TryCreateCursor(
        string? outcomeText,
        string? updatedAtText,
        Guid? operationId,
        Guid? reservationId,
        out ReservationStayAmendmentRecoveryCursorDto? cursor)
    {
        bool hasOutcome = !string.IsNullOrWhiteSpace(outcomeText);
        bool hasUpdatedAt = !string.IsNullOrWhiteSpace(updatedAtText);
        bool hasOperation = operationId.HasValue;
        bool hasReservation = reservationId.HasValue;
        if (!hasOutcome && !hasUpdatedAt && !hasOperation && !hasReservation)
        {
            cursor = null;
            return true;
        }

        if (!hasOutcome || !hasUpdatedAt || !hasOperation || !hasReservation ||
            !Enum.TryParse(
                outcomeText,
                ignoreCase: true,
                out ReservationStayAmendmentOutcome outcome) ||
            !DateTimeOffset.TryParse(
                updatedAtText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset updatedAtUtc))
        {
            cursor = null;
            return false;
        }

        cursor = new ReservationStayAmendmentRecoveryCursorDto(
            outcome,
            updatedAtUtc,
            operationId!.Value,
            reservationId!.Value);
        return true;
    }

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private static bool TryParseOptionalTime(string? value, out TimeOnly? time)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            time = null;
            return true;
        }

        bool parsed = TimeOnly.TryParseExact(
            value.Trim(),
            "HH:mm",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out TimeOnly result);
        time = parsed ? result : null;
        return parsed;
    }

    private static bool TryParseUnitIds(string value, out Guid[] unitIds)
    {
        string[] values = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        unitIds = values.Select(item => Guid.TryParse(item, out Guid id) ? id : Guid.Empty).ToArray();
        return unitIds.Length is > 0 and <= MaxUnitCount &&
            unitIds.All(id => id != Guid.Empty) &&
            unitIds.Distinct().Count() == unitIds.Length;
    }

    private static string ResolveActor(ParseResult parseResult, AdminCliGlobalOptions globalOptions) =>
        string.IsNullOrWhiteSpace(parseResult.GetValue(globalOptions.ActorOption))
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : parseResult.GetValue(globalOptions.ActorOption)!.Trim();

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value.HasValue ? FormatTimestamp(value.Value) : string.Empty;

    private static Option<Guid> OperationOption() => new("--operation-id") { Required = true };
    private static Option<Guid> PropertyOption() => new("--property-id") { Required = true };
    private static Option<Guid> ReservationOption() => new("--reservation-id") { Required = true };
    private static Option<string> RequiredString(string name) => new(name) { Required = true };

    private const int DefaultPageSize = 50;
    private const int MaxUnitCount = 100;
}
