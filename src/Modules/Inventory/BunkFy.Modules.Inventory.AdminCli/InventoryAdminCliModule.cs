namespace BunkFy.Modules.Inventory.AdminCli;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using BunkFy.Modules.Inventory.Admin.Contracts;
using BunkFy.Modules.Inventory.Application;
using BunkFy.Modules.Inventory.Application.Commands;
using BunkFy.Modules.Inventory.Application.Queries;
using BunkFy.Modules.Inventory.Contracts;
using BunkFy.Modules.Inventory.Persistence;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public sealed class InventoryAdminCliModule : IAdminCliModule
{
    public string Name => InventoryModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(InventoryProfiles.Default, "BunkFy.Modules.Inventory.AdminCli");
        builder.Services.AddInventoryApplication();
        builder.AddInventoryPersistence();
    }

    public void MapCommands(IAdminCliCommandRegistry commands)
    {
        AdminCliGlobalOptions globalOptions = commands.Services.GetRequiredService<AdminCliGlobalOptions>();
        Command inventory = new(InventoryModuleMetadata.Name, "Inventory configuration operations.")
        {
            new Command("rooms", "Manage room inventory configuration.")
            {
                CreateListRoomsCommand(commands.Services, globalOptions),
                CreateConfigureRoomCommand(commands.Services, globalOptions)
            },
            CreateAvailabilityCommand(commands.Services, globalOptions),
            new Command("blocks", "Manage manual inventory blocks.")
            {
                CreateListBlocksCommand(commands.Services, globalOptions),
                CreateBlockCommand(commands.Services, globalOptions),
                CreateReleaseBlockCommand(commands.Services, globalOptions)
            },
            InventoryBlockGroupAdminCliCommandGroup.Create(
                commands.Services,
                globalOptions),
            InventoryBedRetirementAdminCliCommandGroup.Create(commands.Services, globalOptions),
            InventoryRoomRetirementAdminCliCommandGroup.Create(commands.Services, globalOptions)
        };

        commands.AddCommand(this.Name, inventory);
    }

    private static Command CreateListRoomsCommand(IServiceProvider services, AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> propertyOption = new("--property-id") { Required = true };
        Option<int> pageOption = new("--page") { DefaultValueFactory = _ => PageRequest.DefaultPage };
        Option<int> pageSizeOption = new("--page-size") { DefaultValueFactory = _ => PageRequest.DefaultPageSize };
        Command command = new("list", "List room inventory configuration.")
        {
            propertyOption,
            pageOption,
            pageSizeOption
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            AdminCliExecutor executor = services.GetRequiredService<AdminCliExecutor>();
            return executor.ExecuteAsync(
                parseResult,
                AdminOperation.Create(InventoryAdminOperationNames.RoomsList, InventoryAdminPermissions.Read),
                parseResult.GetValue(globalOptions.TenantOption),
                requireTenant: true,
                async (provider, token) =>
                {
                    IRequestDispatcher dispatcher = provider.GetRequiredService<IRequestDispatcher>();
                    Result<RoomInventoryListResponse> result = await dispatcher.QueryAsync(
                        new ListRoomInventoryQuery(
                            parseResult.GetValue(propertyOption),
                            parseResult.GetValue(pageOption),
                            parseResult.GetValue(pageSizeOption)),
                        token).ConfigureAwait(false);

                    if (result.IsSuccess)
                    {
                        WriteRooms(
                            result.Value.Rooms,
                            parseResult.GetValue(globalOptions.OutputOption) ?? AdminCliOutput.Table);
                    }

                    return result;
                },
                cancellationToken);
        });

        return command;
    }

    private static Command CreateAvailabilityCommand(IServiceProvider services, AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> propertyOption = new("--property-id") { Required = true };
        Option<string> arrivalOption = new("--arrival") { Required = true };
        Option<string> departureOption = new("--departure") { Required = true };
        Command command = new("availability", "Read sellable inventory availability for a stay range.")
        {
            propertyOption,
            arrivalOption,
            departureOption
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            AdminCliExecutor executor = services.GetRequiredService<AdminCliExecutor>();
            return executor.ExecuteAsync(
                parseResult,
                AdminOperation.Create(InventoryAdminOperationNames.AvailabilityRead, InventoryAdminPermissions.Read),
                parseResult.GetValue(globalOptions.TenantOption),
                requireTenant: true,
                async (provider, token) =>
                {
                    if (!TryParseStayRange(
                            parseResult.GetValue(arrivalOption),
                            parseResult.GetValue(departureOption),
                            out DateOnly arrival,
                            out DateOnly departure))
                    {
                        return Result.Failure<InventoryAvailabilityResponse>(InventoryApplicationErrors.StayRangeInvalid);
                    }

                    IRequestDispatcher dispatcher = provider.GetRequiredService<IRequestDispatcher>();
                    Result<InventoryAvailabilityResponse> result = await dispatcher.QueryAsync(
                        new GetInventoryAvailabilityQuery(
                            parseResult.GetValue(propertyOption),
                            arrival,
                            departure),
                        token).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        AdminCliOutput.WriteRows(
                            result.Value.Units,
                            parseResult.GetValue(globalOptions.OutputOption) ?? AdminCliOutput.Table,
                            [
                                ("UnitId", item => item.Unit.InventoryUnitId.ToString()),
                                ("Kind", item => item.Unit.Kind.ToString()),
                                ("Label", item => item.Unit.Label),
                                ("Available", item => item.IsAvailable.ToString()),
                                ("ActiveBlocks", item => item.ActiveBlockIds.Count.ToString(CultureInfo.InvariantCulture))
                            ]);
                    }

                    return result;
                },
                cancellationToken);
        });

        return command;
    }

    private static Command CreateListBlocksCommand(IServiceProvider services, AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> propertyOption = new("--property-id") { Required = true };
        Option<Guid?> unitOption = new("--unit-id");
        Option<bool> includeReleasedOption = new("--include-released");
        Option<int> pageOption = new("--page") { DefaultValueFactory = _ => PageRequest.DefaultPage };
        Option<int> pageSizeOption = new("--page-size") { DefaultValueFactory = _ => PageRequest.DefaultPageSize };
        Command command = new("list", "List manual inventory blocks.")
        {
            propertyOption,
            unitOption,
            includeReleasedOption,
            pageOption,
            pageSizeOption
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            AdminCliExecutor executor = services.GetRequiredService<AdminCliExecutor>();
            return executor.ExecuteAsync(
                parseResult,
                AdminOperation.Create(InventoryAdminOperationNames.BlocksList, InventoryAdminPermissions.Read),
                parseResult.GetValue(globalOptions.TenantOption),
                requireTenant: true,
                async (provider, token) =>
                {
                    IRequestDispatcher dispatcher = provider.GetRequiredService<IRequestDispatcher>();
                    Result<ManualInventoryBlockListResponse> result = await dispatcher.QueryAsync(
                        new ListManualInventoryBlocksQuery(
                            parseResult.GetValue(propertyOption),
                            parseResult.GetValue(unitOption),
                            parseResult.GetValue(includeReleasedOption),
                            parseResult.GetValue(pageOption),
                            parseResult.GetValue(pageSizeOption)),
                        token).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        WriteBlocks(result.Value.Blocks, parseResult.GetValue(globalOptions.OutputOption) ?? AdminCliOutput.Table);
                    }

                    return result;
                },
                cancellationToken);
        });

        return command;
    }

    private static Command CreateBlockCommand(IServiceProvider services, AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> propertyOption = new("--property-id") { Required = true };
        Option<Guid> unitOption = new("--unit-id") { Required = true };
        Option<Guid> operationOption = new("--operation-id")
        {
            Required = true
        };
        Option<string> arrivalOption = new("--arrival") { Required = true };
        Option<string> departureOption = new("--departure") { Required = true };
        Option<string> reasonOption = new("--reason") { Required = true };
        Command command = new("create", "Create a manual inventory block.")
        {
            propertyOption,
            unitOption,
            operationOption,
            arrivalOption,
            departureOption,
            reasonOption
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            AdminCliExecutor executor = services.GetRequiredService<AdminCliExecutor>();
            return executor.ExecuteAsync(
                parseResult,
                AdminOperation.Create(InventoryAdminOperationNames.BlocksCreate, InventoryAdminPermissions.BlocksManage),
                parseResult.GetValue(globalOptions.TenantOption),
                requireTenant: true,
                async (provider, token) =>
                {
                    if (!TryParseStayRange(
                            parseResult.GetValue(arrivalOption),
                            parseResult.GetValue(departureOption),
                            out DateOnly arrival,
                            out DateOnly departure))
                    {
                        return Result.Failure<ManualInventoryBlockMutationReceiptDto>(InventoryApplicationErrors.StayRangeInvalid);
                    }

                    IRequestDispatcher dispatcher = provider.GetRequiredService<IRequestDispatcher>();
                    Result<ManualInventoryBlockMutationReceiptDto> result = await dispatcher.SendAsync(
                        new CreateManualInventoryBlockCommand(
                            parseResult.GetValue(operationOption),
                            parseResult.GetValue(propertyOption),
                            parseResult.GetValue(unitOption),
                            arrival,
                            departure,
                            parseResult.GetValue(reasonOption) ?? string.Empty,
                            ResolveActor(parseResult, globalOptions)),
                        token).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        AdminCliOutput.WriteObject(
                            result.Value,
                            parseResult.GetValue(globalOptions.OutputOption) ?? AdminCliOutput.Table);
                    }

                    return result;
                },
                cancellationToken);
        });

        return command;
    }

    private static Command CreateReleaseBlockCommand(IServiceProvider services, AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> propertyOption = new("--property-id") { Required = true };
        Option<Guid> blockOption = new("--block-id") { Required = true };
        Option<Guid> operationOption = new("--operation-id")
        {
            Required = true
        };
        Option<long> expectedVersionOption = new("--expected-version") { Required = true };
        Command command = new("release", "Release a manual inventory block.")
        {
            propertyOption,
            blockOption,
            operationOption,
            expectedVersionOption
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            AdminCliExecutor executor = services.GetRequiredService<AdminCliExecutor>();
            return executor.ExecuteAsync(
                parseResult,
                AdminOperation.Create(InventoryAdminOperationNames.BlocksRelease, InventoryAdminPermissions.BlocksManage),
                parseResult.GetValue(globalOptions.TenantOption),
                requireTenant: true,
                async (provider, token) =>
                {
                    IRequestDispatcher dispatcher = provider.GetRequiredService<IRequestDispatcher>();
                    Result<ManualInventoryBlockMutationReceiptDto> result = await dispatcher.SendAsync(
                        new ReleaseManualInventoryBlockCommand(
                            parseResult.GetValue(operationOption),
                            parseResult.GetValue(propertyOption),
                            parseResult.GetValue(blockOption),
                            parseResult.GetValue(expectedVersionOption),
                            ResolveActor(parseResult, globalOptions)),
                        token).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        AdminCliOutput.WriteObject(
                            result.Value,
                            parseResult.GetValue(globalOptions.OutputOption) ?? AdminCliOutput.Table);
                    }

                    return result;
                },
                cancellationToken);
        });

        return command;
    }

    private static Command CreateConfigureRoomCommand(IServiceProvider services, AdminCliGlobalOptions globalOptions)
    {
        Option<Guid> propertyOption = new("--property-id") { Required = true };
        Option<Guid> roomOption = new("--room-id") { Required = true };
        Option<Guid> operationOption = new("--operation-id") { Required = true };
        Option<string> salesModeOption = new("--sales-mode") { Required = true };
        Option<long> expectedVersionOption = new("--expected-version") { Required = true };
        Command command = new("configure", "Configure a room for room-level or bed-level sales.")
        {
            propertyOption,
            roomOption,
            operationOption,
            salesModeOption,
            expectedVersionOption
        };

        command.SetAction((parseResult, cancellationToken) =>
        {
            AdminCliExecutor executor = services.GetRequiredService<AdminCliExecutor>();
            return executor.ExecuteAsync(
                parseResult,
                AdminOperation.Create(InventoryAdminOperationNames.RoomsConfigure, InventoryAdminPermissions.Configure),
                parseResult.GetValue(globalOptions.TenantOption),
                requireTenant: true,
                async (provider, token) =>
                {
                    if (!TryParseSalesMode(parseResult.GetValue(salesModeOption), out InventorySalesMode salesMode))
                    {
                        return Result.Failure<RoomInventoryMutationReceiptDto>(InventoryApplicationErrors.SalesModeInvalid);
                    }

                    IRequestDispatcher dispatcher = provider.GetRequiredService<IRequestDispatcher>();
                    Result<RoomInventoryMutationReceiptDto> result = await dispatcher.SendAsync(
                        new ConfigureRoomSalesModeCommand(
                            parseResult.GetValue(operationOption),
                            parseResult.GetValue(propertyOption),
                            parseResult.GetValue(roomOption),
                            salesMode,
                            parseResult.GetValue(expectedVersionOption)),
                        token).ConfigureAwait(false);

                    if (result.IsSuccess)
                    {
                        AdminCliOutput.WriteObject(
                            result.Value,
                            parseResult.GetValue(globalOptions.OutputOption) ?? AdminCliOutput.Table);
                    }

                    return result;
                },
                cancellationToken);
        });

        return command;
    }

    private static bool TryParseSalesMode(string? value, out InventorySalesMode salesMode)
    {
        salesMode = value?.Trim().ToLowerInvariant() switch
        {
            "room" or "room-level" => InventorySalesMode.RoomLevel,
            "bed" or "bed-level" => InventorySalesMode.BedLevel,
            _ => InventorySalesMode.Unknown
        };
        return salesMode != InventorySalesMode.Unknown;
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

    private static bool TryParseStayRange(
        string? arrivalValue,
        string? departureValue,
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

    private static void WriteRooms(IReadOnlyCollection<RoomInventoryDto> rooms, string output) =>
        AdminCliOutput.WriteRows(
            rooms,
            output,
            [
                ("PropertyId", room => room.PropertyId.ToString()),
                ("RoomId", room => room.RoomId.ToString()),
                ("Room", room => room.RoomName),
                ("SalesMode", room => room.SalesMode.ToString()),
                ("Version", room => room.Version.ToString(CultureInfo.InvariantCulture)),
                ("SellableUnits", room => room.Units.Count(unit => unit.IsSellable).ToString(CultureInfo.InvariantCulture))
            ]);

    private static void WriteBlocks(IReadOnlyCollection<ManualInventoryBlockDto> blocks, string output) =>
        AdminCliOutput.WriteRows(
            blocks,
            output,
            [
                ("BlockId", block => block.BlockId.ToString()),
                ("UnitId", block => block.InventoryUnitId.ToString()),
                ("Arrival", block => block.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("Departure", block => block.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("Status", block => block.Status.ToString()),
                ("Version", block => block.Version.ToString(CultureInfo.InvariantCulture)),
                ("Reason", block => block.Reason)
            ]);
}
