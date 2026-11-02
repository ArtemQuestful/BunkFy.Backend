namespace BunkFy.Modules.Inventory.AdminCli;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using BunkFy.Modules.Inventory.Admin.Contracts;
using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Microsoft.Extensions.DependencyInjection;

internal static class InventoryBlockGroupAdminCliCommandGroup
{
    public static Command Create(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions) =>
        new("block-groups", "Manage bounded manual inventory block groups.")
        {
            CreatePreviewCommand(services, globalOptions),
            CreateListCommand(services, globalOptions),
            CreateGetCommand(services, globalOptions),
            CreateMembersCommand(services, globalOptions),
            CreateCreateCommand(services, globalOptions),
            CreateReplaceCommand(services, globalOptions),
            CreateReleaseCommand(services, globalOptions),
            CreateGetCreateOperationCommand(services, globalOptions),
            CreateGetOperationCommand(services, globalOptions)
        };

    private static Command CreatePreviewCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> property = RequiredId("--property-id");
        TargetOptions target = TargetOptions.Create();
        StayOptions stay = StayOptions.Create();
        Option<string> reason = RequiredString("--reason");
        Option<Guid?> blockGroup = new("--block-group-id");
        Option<long?> expectedVersion = new("--expected-version");
        Command command = new(
            "preview",
            $"Preview an atomic block group of at most {InventoryContractLimits.MaximumManualInventoryBlockGroupMembers} members.");
        AddOptions(
            command,
            [property, .. target.Options, .. stay.Options, reason, blockGroup, expectedVersion]);
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            globalOptions,
            parse,
            InventoryAdminOperationNames.BlockGroupsPreview,
            InventoryAdminPermissions.BlockGroupsManage,
            (dispatcher, token) => ParseStayAndExecute(
                parse,
                stay,
                (arrival, departure) => dispatcher.QueryAsync(
                    new PreviewManualInventoryBlockGroupQuery(
                        parse.GetValue(property),
                        target.Read(parse),
                        arrival,
                        departure,
                        parse.GetRequiredValue(reason),
                        parse.GetValue(blockGroup),
                        parse.GetValue(expectedVersion)),
                    token)),
            cancellationToken,
            WritePreview));
        return command;
    }

    private static Command CreateListCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> property = RequiredId("--property-id");
        Option<ManualInventoryBlockGroupStatus?> status = new("--status");
        Option<string?> cursor = new("--cursor");
        Option<int> pageSize = new("--page-size")
        {
            DefaultValueFactory = _ => PageRequest.DefaultPageSize
        };
        Command command = new("list", "List block-group history.")
        {
            property,
            status,
            cursor,
            pageSize
        };
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            globalOptions,
            parse,
            InventoryAdminOperationNames.BlockGroupsList,
            InventoryAdminPermissions.Read,
            (dispatcher, token) => dispatcher.QueryAsync(
                new ListManualInventoryBlockGroupsQuery(
                    parse.GetValue(property),
                    parse.GetValue(status),
                    parse.GetValue(cursor),
                    parse.GetValue(pageSize)),
                token),
            cancellationToken,
            WriteGroupList));
        return command;
    }

    private static Command CreateGetCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> property = RequiredId("--property-id");
        Option<Guid> group = RequiredId("--block-group-id");
        Command command = new("get", "Get one authoritative block group.")
        {
            property,
            group
        };
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            globalOptions,
            parse,
            InventoryAdminOperationNames.BlockGroupsGet,
            InventoryAdminPermissions.Read,
            (dispatcher, token) => dispatcher.QueryAsync(
                new GetManualInventoryBlockGroupQuery(
                    parse.GetValue(property),
                    parse.GetValue(group)),
                token),
            cancellationToken,
            WriteGroup));
        return command;
    }

    private static Command CreateMembersCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> property = RequiredId("--property-id");
        Option<Guid> group = RequiredId("--block-group-id");
        Option<ManualInventoryBlockStatus?> status = new("--status");
        Option<string?> cursor = new("--cursor");
        Option<int> pageSize = new("--page-size")
        {
            DefaultValueFactory = _ => PageRequest.DefaultPageSize
        };
        Command command = new("members", "List block-group members by keyset cursor.")
        {
            property,
            group,
            status,
            cursor,
            pageSize
        };
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            globalOptions,
            parse,
            InventoryAdminOperationNames.BlockGroupMembersList,
            InventoryAdminPermissions.Read,
            (dispatcher, token) => dispatcher.QueryAsync(
                new ListManualInventoryBlockGroupMembersQuery(
                    parse.GetValue(property),
                    parse.GetValue(group),
                    parse.GetValue(status),
                    parse.GetValue(cursor),
                    parse.GetValue(pageSize)),
                token),
            cancellationToken,
            WriteMemberList));
        return command;
    }

    private static Command CreateCreateCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> operation = RequiredId("--operation-id");
        Option<Guid> property = RequiredId("--property-id");
        TargetOptions target = TargetOptions.Create();
        StayOptions stay = StayOptions.Create();
        Option<string> reason = RequiredString("--reason");
        Option<string> digest = RequiredString("--selection-digest");
        Option<int> count = new("--expected-affected-count") { Required = true };
        Option<bool> yes = new("--yes");
        Command command = new("create", "Create the confirmed previewed block group atomically.");
        AddOptions(
            command,
            [operation, property, .. target.Options, .. stay.Options, reason, digest, count, yes]);
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            globalOptions,
            parse,
            InventoryAdminOperationNames.BlockGroupsCreate,
            InventoryAdminPermissions.BlockGroupsManage,
            (dispatcher, token) => ParseStayAndExecute(
                parse,
                stay,
                (arrival, departure) => dispatcher.SendAsync(
                    new CreateManualInventoryBlockGroupCommand(
                        parse.GetValue(operation),
                        parse.GetValue(property),
                        target.Read(parse),
                        arrival,
                        departure,
                        parse.GetRequiredValue(reason),
                        parse.GetRequiredValue(digest),
                        parse.GetValue(count),
                        parse.GetValue(yes),
                        ResolveActor(parse, globalOptions)),
                    token)),
            cancellationToken,
            WriteMutationReceipt));
        return command;
    }

    private static Command CreateReplaceCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> operation = RequiredId("--operation-id");
        Option<Guid> property = RequiredId("--property-id");
        Option<Guid> group = RequiredId("--block-group-id");
        Option<long> expectedVersion = new("--expected-version")
        {
            Required = true
        };
        TargetOptions target = TargetOptions.Create();
        StayOptions stay = StayOptions.Create();
        Option<string> reason = RequiredString("--reason");
        Option<string> digest = RequiredString("--selection-digest");
        Option<int> count = new("--expected-affected-count") { Required = true };
        Option<bool> yes = new("--yes");
        Command command = new(
            "replace",
            "Atomically release a group and create its confirmed immutable successor.");
        AddOptions(
            command,
            [operation, property, group, expectedVersion, .. target.Options, .. stay.Options, reason, digest, count, yes]);
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            globalOptions,
            parse,
            InventoryAdminOperationNames.BlockGroupsReplace,
            InventoryAdminPermissions.BlockGroupsManage,
            (dispatcher, token) => ParseStayAndExecute(
                parse,
                stay,
                (arrival, departure) => dispatcher.SendAsync(
                    new ReplaceManualInventoryBlockGroupCommand(
                        parse.GetValue(operation),
                        parse.GetValue(property),
                        parse.GetValue(group),
                        parse.GetValue(expectedVersion),
                        target.Read(parse),
                        arrival,
                        departure,
                        parse.GetRequiredValue(reason),
                        parse.GetRequiredValue(digest),
                        parse.GetValue(count),
                        parse.GetValue(yes),
                        ResolveActor(parse, globalOptions)),
                    token)),
            cancellationToken,
            WriteMutationReceipt));
        return command;
    }

    private static Command CreateReleaseCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> operation = RequiredId("--operation-id");
        Option<Guid> property = RequiredId("--property-id");
        Option<Guid> group = RequiredId("--block-group-id");
        Option<long> expectedVersion = new("--expected-version")
        {
            Required = true
        };
        Option<bool> yes = new("--yes");
        Command command = new("release", "Release the confirmed block group atomically.")
        {
            operation,
            property,
            group,
            expectedVersion,
            yes
        };
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            globalOptions,
            parse,
            InventoryAdminOperationNames.BlockGroupsRelease,
            InventoryAdminPermissions.BlockGroupsManage,
            (dispatcher, token) => dispatcher.SendAsync(
                new ReleaseManualInventoryBlockGroupCommand(
                    parse.GetValue(operation),
                    parse.GetValue(property),
                    parse.GetValue(group),
                    parse.GetValue(expectedVersion),
                    parse.GetValue(yes),
                    ResolveActor(parse, globalOptions)),
                token),
            cancellationToken,
            WriteMutationReceipt));
        return command;
    }

    private static Command CreateGetCreateOperationCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> property = RequiredId("--property-id");
        Option<Guid> operation = RequiredId("--operation-id");
        Command command = new(
            "get-create-operation",
            "Recover a committed create receipt after a lost response.")
        {
            property,
            operation
        };
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            globalOptions,
            parse,
            InventoryAdminOperationNames.BlockGroupCreateOperationsGet,
            InventoryAdminPermissions.BlockGroupsManage,
            (dispatcher, token) => dispatcher.QueryAsync(
                new GetManualInventoryBlockGroupCreateOperationQuery(
                    parse.GetValue(property),
                    parse.GetValue(operation)),
                token),
            cancellationToken,
            WriteOperation));
        return command;
    }

    private static Command CreateGetOperationCommand(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> property = RequiredId("--property-id");
        Option<Guid> group = RequiredId("--block-group-id");
        Option<Guid> operation = RequiredId("--operation-id");
        Command command = new("get-operation", "Get a committed group-operation receipt.")
        {
            property,
            group,
            operation
        };
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            globalOptions,
            parse,
            InventoryAdminOperationNames.BlockGroupOperationsGet,
            InventoryAdminPermissions.BlockGroupsManage,
            (dispatcher, token) => dispatcher.QueryAsync(
                new GetManualInventoryBlockGroupOperationQuery(
                    parse.GetValue(property),
                    parse.GetValue(group),
                    parse.GetValue(operation)),
                token),
            cancellationToken,
            WriteOperation));
        return command;
    }

    private static Task<int> ExecuteAsync<T>(
        IServiceProvider services,
        AdminCliGlobalOptions globalOptions,
        ParseResult parse,
        string operationName,
        AdminPermission permission,
        Func<IRequestDispatcher, CancellationToken, Task<Result<T>>> execute,
        CancellationToken cancellationToken,
        Action<T, string>? writeOutput = null) =>
        services.GetRequiredService<AdminCliExecutor>().ExecuteAsync(
            parse,
            AdminOperation.Create(operationName, permission),
            parse.GetValue(globalOptions.TenantOption),
            requireTenant: true,
            async (provider, token) =>
            {
                Result<T> result = await execute(
                    provider.GetRequiredService<IRequestDispatcher>(),
                    token).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    string output =
                        parse.GetValue(globalOptions.OutputOption) ??
                        AdminCliOutput.Table;
                    if (writeOutput is null)
                    {
                        AdminCliOutput.WriteObject(result.Value, output);
                    }
                    else
                    {
                        writeOutput(result.Value, output);
                    }
                }
                else
                {
                    AdminCliOutput.WriteErrorInline($"{result.Error.Code}: ");
                }

                return result;
            },
            cancellationToken);

    private static void WritePreview(
        ManualInventoryBlockGroupSelectionPreviewDto preview,
        string output)
    {
        if (IsJson(output))
        {
            AdminCliOutput.WriteObject(preview, output);
            return;
        }

        AdminCliOutput.WriteRows(
            [preview],
            output,
            [
                ("Status", item => item.Status.ToString()),
                ("Affected", item => Value(item.AffectedBlockCount)),
                ("AtLeast", item => Value(item.AtLeastAffectedBlockCount)),
                ("Maximum", item => Value(item.MaximumAffectedBlockCount)),
                ("TooLarge", item => item.ExceedsMaximumAffectedBlockCount.ToString()),
                ("ManualConflict", item => item.HasManualBlockConflict.ToString()),
                ("AllocationConflict", item => item.HasActiveAllocationConflict.ToString()),
                ("SelectionDigest", item => item.SelectionDigest ?? string.Empty),
                ("GroupId", item => Value(item.BlockGroupId)),
                ("GroupVersion", item => Value(item.BlockGroupVersion)),
                ("NoOp", item => item.IsNoOpReplacement.ToString())
            ]);
        if (preview.Members.Count > 0)
        {
            AdminCliOutput.WriteRows(
                preview.Members,
                output,
                [
                    ("UnitId", item => item.InventoryUnitId.ToString("D")),
                    ("RoomId", item => item.RoomId.ToString("D")),
                    ("Room", item => item.RoomName),
                    ("Unit", item => item.UnitLabel ?? string.Empty)
                ]);
        }

        if (preview.HasMoreMembers)
        {
            AdminCliOutput.WriteMessage("Preview member evidence is truncated.");
        }
    }

    private static void WriteGroupList(
        ManualInventoryBlockGroupListResponse response,
        string output)
    {
        if (IsJson(output))
        {
            AdminCliOutput.WriteObject(response, output);
            return;
        }

        WriteGroups(response.BlockGroups, output);
        WriteNextCursor(response.NextCursor);
    }

    private static void WriteGroup(
        ManualInventoryBlockGroupDto group,
        string output)
    {
        if (IsJson(output))
        {
            AdminCliOutput.WriteObject(group, output);
            return;
        }

        WriteGroups([group], output);
        AdminCliOutput.WriteRows(
            [group],
            output,
            [
                ("Reason", item => item.Reason),
                ("SelectionDigest", item => item.SelectionDigest ?? string.Empty),
                ("MembershipDigest", item => item.MembershipDigest),
                ("CreatedBy", item => item.CreatedByActorId ?? string.Empty),
                ("LastModifiedBy", item => item.LastModifiedByActorId ?? string.Empty),
                ("ReleasedAtUtc", item => Value(item.ReleasedAtUtc))
            ]);
    }

    private static void WriteGroups(
        IReadOnlyCollection<ManualInventoryBlockGroupDto> groups,
        string output) =>
        AdminCliOutput.WriteRows(
            groups,
            output,
            [
                ("GroupId", item => item.BlockGroupId.ToString("D")),
                ("Status", item => item.Status.ToString()),
                ("Target", item => FormatTarget(item.Target)),
                ("Arrival", item => item.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("Departure", item => item.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("Total", item => item.InitialBlockCount.ToString(CultureInfo.InvariantCulture)),
                ("Active", item => item.ActiveBlockCount.ToString(CultureInfo.InvariantCulture)),
                ("Version", item => item.Version.ToString(CultureInfo.InvariantCulture)),
                ("Replaces", item => Value(item.ReplacesGroupId)),
                ("ReplacedBy", item => Value(item.ReplacedByGroupId)),
                ("UpdatedAtUtc", item => Value(item.UpdatedAtUtc ?? item.CreatedAtUtc))
            ]);

    private static void WriteMemberList(
        ManualInventoryBlockGroupMemberListResponse response,
        string output)
    {
        if (IsJson(output))
        {
            AdminCliOutput.WriteObject(response, output);
            return;
        }

        AdminCliOutput.WriteRows(
            response.Blocks,
            output,
            [
                ("BlockId", item => item.BlockId.ToString("D")),
                ("UnitId", item => item.InventoryUnitId.ToString("D")),
                ("Status", item => item.Status.ToString()),
                ("Version", item => item.Version.ToString(CultureInfo.InvariantCulture)),
                ("Arrival", item => item.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("Departure", item => item.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("ReleasedAtUtc", item => Value(item.ReleasedAtUtc))
            ]);
        WriteNextCursor(response.NextCursor);
    }

    private static void WriteMutationReceipt(
        ManualInventoryBlockGroupMutationReceiptDto receipt,
        string output)
    {
        if (IsJson(output))
        {
            AdminCliOutput.WriteObject(receipt, output);
            return;
        }

        AdminCliOutput.WriteRows(
            [receipt],
            output,
            [
                ("ResultGroupId", item => item.ResultBlockGroupId.ToString("D")),
                ("PreviousGroupId", item => Value(item.PreviousBlockGroupId)),
                ("Status", item => Value(item.Status)),
                ("Version", item => Value(item.Version)),
                ("Affected", item => Value(item.AffectedBlockCount)),
                ("Total", item => Value(item.TotalBlockCount)),
                ("Active", item => Value(item.ActiveBlockCount)),
                ("ReleasedNow", item => Value(item.ReleasedNowBlockCount)),
                ("AlreadyReleased", item => Value(item.AlreadyReleasedBlockCount)),
                ("CreatedNow", item => Value(item.CreatedNowBlockCount)),
                ("MembershipDigest", item => item.MembershipDigest ?? string.Empty)
            ]);
    }

    private static void WriteOperation(
        ManualInventoryBlockGroupOperationDto operation,
        string output)
    {
        if (IsJson(output))
        {
            AdminCliOutput.WriteObject(operation, output);
            return;
        }

        AdminCliOutput.WriteRows(
            [operation],
            output,
            [
                ("OperationId", item => item.OperationId.ToString("D")),
                ("Kind", item => item.Kind.ToString()),
                ("Status", item => item.Status.ToString()),
                ("RequestedGroupId", item => Value(item.RequestedBlockGroupId)),
                ("ResultGroupId", item => item.Receipt.ResultBlockGroupId.ToString("D")),
                ("PreviousGroupId", item => Value(item.Receipt.PreviousBlockGroupId)),
                ("Version", item => Value(item.Receipt.Version)),
                ("Total", item => Value(item.Receipt.TotalBlockCount)),
                ("Active", item => Value(item.Receipt.ActiveBlockCount)),
                ("ReleasedNow", item => Value(item.Receipt.ReleasedNowBlockCount)),
                ("AlreadyReleased", item => Value(item.Receipt.AlreadyReleasedBlockCount)),
                ("CreatedNow", item => Value(item.Receipt.CreatedNowBlockCount)),
                ("CompletedAtUtc", item => Value(item.CompletedAtUtc))
            ]);
    }

    private static void WriteNextCursor(string? cursor) =>
        AdminCliOutput.WriteMessage(
            cursor is null
                ? "End of results."
                : $"Next cursor: {cursor}");

    private static bool IsJson(string output) =>
        string.Equals(
            AdminCliOutput.NormalizeFormat(output),
            AdminCliOutput.Json,
            StringComparison.Ordinal);

    private static string FormatTarget(InventoryBlockTarget target) =>
        target.Kind switch
        {
            InventoryBlockTargetKind.Property => "property",
            InventoryBlockTargetKind.Building => $"building:{target.BuildingLabel}",
            InventoryBlockTargetKind.Floor => $"floor:{target.BuildingLabel}/{target.FloorLabel}",
            InventoryBlockTargetKind.Room => $"room:{Value(target.RoomId)}",
            InventoryBlockTargetKind.Unit => $"unit:{Value(target.InventoryUnitId)}",
            _ => "unknown"
        };

    private static string Value(object? value) =>
        value switch
        {
            null => string.Empty,
            Guid id => id.ToString("D"),
            DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };

    private static Task<Result<T>> ParseStayAndExecute<T>(
        ParseResult parse,
        StayOptions stay,
        Func<DateOnly, DateOnly, Task<Result<T>>> execute) =>
        TryParseStayRange(
            parse.GetRequiredValue(stay.Arrival),
            parse.GetRequiredValue(stay.Departure),
            out DateOnly arrival,
            out DateOnly departure)
            ? execute(arrival, departure)
            : Task.FromResult(Result.Failure<T>(
                InventoryApplicationErrors.StayRangeInvalid));

    private static bool TryParseStayRange(
        string arrivalValue,
        string departureValue,
        out DateOnly arrival,
        out DateOnly departure)
    {
        bool arrivalParsed = DateOnly.TryParseExact(
            arrivalValue,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out arrival);
        bool departureParsed = DateOnly.TryParseExact(
            departureValue,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out departure);
        return arrivalParsed && departureParsed && arrival < departure;
    }

    private static Option<Guid> RequiredId(string name) => new(name)
    {
        Required = true
    };

    private static Option<string> RequiredString(string name) => new(name)
    {
        Required = true
    };

    private static void AddOptions(
        Command command,
        IEnumerable<Option> options)
    {
        foreach (Option option in options)
        {
            command.Options.Add(option);
        }
    }

    private static string ResolveActor(
        ParseResult parse,
        AdminCliGlobalOptions globalOptions)
    {
        string actor = string.IsNullOrWhiteSpace(
            parse.GetValue(globalOptions.ActorOption))
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : parse.GetValue(globalOptions.ActorOption)!.Trim();
        return $"admin-cli:{actor}";
    }

    private sealed record StayOptions(
        Option<string> Arrival,
        Option<string> Departure)
    {
        public IReadOnlyCollection<Option> Options =>
            [this.Arrival, this.Departure];

        public static StayOptions Create() => new(
            RequiredString("--arrival"),
            RequiredString("--departure"));
    }

    private sealed record TargetOptions(
        Option<InventoryBlockTargetKind> Kind,
        Option<string?> Building,
        Option<string?> Floor,
        Option<Guid?> Room,
        Option<Guid?> Unit)
    {
        public IReadOnlyCollection<Option> Options =>
            [this.Kind, this.Building, this.Floor, this.Room, this.Unit];

        public static TargetOptions Create() => new(
            new("--target-kind") { Required = true },
            new("--building"),
            new("--floor"),
            new("--room-id"),
            new("--unit-id"));

        public InventoryBlockTarget Read(ParseResult parse) => new(
            parse.GetValue(this.Kind),
            parse.GetValue(this.Building),
            parse.GetValue(this.Floor),
            parse.GetValue(this.Room),
            parse.GetValue(this.Unit));
    }
}
