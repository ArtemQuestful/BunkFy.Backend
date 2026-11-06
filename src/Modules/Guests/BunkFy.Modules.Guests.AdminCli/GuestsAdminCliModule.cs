namespace BunkFy.Modules.Guests.AdminCli;

using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Gma.Framework.Administration;
using Gma.Framework.Administration.Cli;
using Gma.Framework.Cqrs;
using Gma.Framework.ModuleComposition;
using Gma.Framework.Pagination;
using Gma.Framework.Results;
using BunkFy.Modules.Guests.Admin.Contracts;
using BunkFy.Modules.Guests.Application;
using BunkFy.Modules.Guests.Application.Commands;
using BunkFy.Modules.Guests.Application.Queries;
using BunkFy.Modules.Guests.Contracts;
using BunkFy.Modules.Guests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public sealed class GuestsAdminCliModule : IAdminCliModule
{
    public string Name => GuestsModuleMetadata.Name;

    public void AddServices(IHostApplicationBuilder builder)
    {
        builder.SelectModuleProfile(GuestsProfiles.Default, "BunkFy.Modules.Guests.AdminCli");
        builder.Services.AddGuestsApplication();
        builder.AddGuestsPersistence();
    }

    public void MapCommands(IAdminCliCommandRegistry commands)
    {
        AdminCliGlobalOptions global = commands.Services.GetRequiredService<AdminCliGlobalOptions>();
        Command module = new(GuestsModuleMetadata.Name, "Guest record administration operations.")
        {
            CreateListCommand(commands.Services, global),
            CreateGetCommand(commands.Services, global),
            CreateStayHistoryCommand(commands.Services, global),
            CreateCreateCommand(commands.Services, global),
            CreateUpdateCommand(commands.Services, global),
            CreateArchiveCommand(commands.Services, global)
        };
        commands.AddCommand(this.Name, module);
    }

    private static Command CreateListCommand(IServiceProvider services, AdminCliGlobalOptions global)
    {
        Option<Guid> property = PropertyOption();
        Option<string?> search = new("--search");
        Option<string?> status = new("--status");
        Option<int> page = new("--page") { DefaultValueFactory = _ => PageRequest.DefaultPage };
        Option<int> pageSize = new("--page-size") { DefaultValueFactory = _ => PageRequest.DefaultPageSize };
        Command command = new("list", "List property-visible guest records.") { property, search, status, page, pageSize };
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            global,
            parse,
            GuestsAdminOperationNames.List,
            GuestsAdminPermissions.Read,
            async (provider, token) =>
            {
                string? statusText = parse.GetValue(status);
                GuestStatus? parsedStatus = string.IsNullOrWhiteSpace(statusText)
                    ? null
                    : Enum.TryParse(statusText, ignoreCase: true, out GuestStatus value) ? value : GuestStatus.Unknown;
                Result<GuestListResponse> result = await provider.GetRequiredService<IRequestDispatcher>().QueryAsync(
                    new ListGuestProfilesQuery(
                        parse.GetRequiredValue(property),
                        parse.GetValue(search),
                        parsedStatus,
                        parse.GetValue(page),
                        parse.GetValue(pageSize)),
                    token).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    WriteList(result.Value.Guests, Output(parse, global));
                }

                return result;
            },
            cancellationToken));
        return command;
    }

    private static Command CreateGetCommand(IServiceProvider services, AdminCliGlobalOptions global)
    {
        Option<Guid> property = PropertyOption();
        Option<Guid> guest = GuestOption();
        Command command = new("get", "Get a guest record.") { property, guest };
        command.SetAction((parse, cancellationToken) => ExecuteProfileAsync(
            services,
            global,
            parse,
            GuestsAdminOperationNames.Get,
            GuestsAdminPermissions.Read,
            (provider, token) => provider.GetRequiredService<IRequestDispatcher>().QueryAsync(
                new GetGuestProfileQuery(parse.GetRequiredValue(property), parse.GetRequiredValue(guest)),
                token),
            cancellationToken));
        return command;
    }

    private static Command CreateCreateCommand(IServiceProvider services, AdminCliGlobalOptions global)
    {
        Option<Guid> operationId = new("--operation-id") { Required = true };
        Option<Guid> property = PropertyOption();
        ProfileOptions options = new();
        Command command = new("create", "Create a guest record.") { operationId, property };
        options.AddTo(command, includeVersion: false);
        command.SetAction((parse, cancellationToken) => ExecuteMutationAsync(
            services,
            global,
            parse,
            GuestsAdminOperationNames.Create,
            GuestsAdminPermissions.Create,
            (provider, token) => TryValues(parse, options, out GuestProfileValues values)
                ? provider.GetRequiredService<IRequestDispatcher>().SendAsync(
                    new CreateGuestProfileCommand(
                        parse.GetRequiredValue(operationId),
                        parse.GetRequiredValue(property),
                        values.DisplayName,
                        values.LegalName,
                        values.Email,
                        values.Phone,
                        values.DateOfBirth,
                        values.NationalityCountryCode,
                        values.PreferredLanguageTag,
                        values.Notes,
                        ResolveActor(parse, global),
                        LanguageTags: values.LanguageTags),
                    token)
                : Task.FromResult(Result.Failure<GuestMutationReceiptDto>(InvalidDateError)),
            cancellationToken));
        return command;
    }

    private static Command CreateStayHistoryCommand(IServiceProvider services, AdminCliGlobalOptions global)
    {
        Option<Guid> property = PropertyOption();
        Option<Guid> guest = GuestOption();
        Option<int> page = new("--page") { DefaultValueFactory = _ => PageRequest.DefaultPage };
        Option<int> pageSize = new("--page-size") { DefaultValueFactory = _ => PageRequest.DefaultPageSize };
        Command command = new("stay-history", "List a guest's stay history at a property.")
        {
            property,
            guest,
            page,
            pageSize
        };
        command.SetAction((parse, cancellationToken) => ExecuteAsync(
            services,
            global,
            parse,
            GuestsAdminOperationNames.StayHistory,
            GuestsAdminPermissions.Read,
            async (provider, token) =>
            {
                Result<GuestStayHistoryListResponse> result = await provider
                    .GetRequiredService<IRequestDispatcher>()
                    .QueryAsync(new GetGuestStayHistoryQuery(
                        parse.GetRequiredValue(property),
                        parse.GetRequiredValue(guest),
                        parse.GetValue(page),
                        parse.GetValue(pageSize)), token)
                    .ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    AdminCliOutput.WriteRows(
                        result.Value.Stays,
                        Output(parse, global),
                        [
                            ("ReservationId", stay => stay.ReservationId.ToString()),
                            ("Arrival", stay => stay.Arrival.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                            ("Departure", stay => stay.Departure.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                            ("Role", stay => stay.Role.ToString()),
                            ("Status", stay => stay.Status.ToString()),
                            ("Version", stay => stay.ReservationVersion.ToString(CultureInfo.InvariantCulture))
                        ]);
                }

                return result;
            },
            cancellationToken));
        return command;
    }

    private static Command CreateUpdateCommand(IServiceProvider services, AdminCliGlobalOptions global)
    {
        Option<Guid> operationId = new("--operation-id") { Required = true };
        Option<Guid> property = PropertyOption();
        Option<Guid> guest = GuestOption();
        ProfileOptions options = new();
        Command command = new("update", "Update a guest record.")
        {
            operationId,
            property,
            guest
        };
        options.AddTo(command, includeVersion: true);
        command.SetAction((parse, cancellationToken) => ExecuteMutationAsync(
            services,
            global,
            parse,
            GuestsAdminOperationNames.Update,
            GuestsAdminPermissions.Manage,
            (provider, token) => TryValues(parse, options, out GuestProfileValues values)
                ? provider.GetRequiredService<IRequestDispatcher>().SendAsync(
                    new UpdateGuestProfileCommand(
                        parse.GetRequiredValue(operationId),
                        parse.GetRequiredValue(property),
                        parse.GetRequiredValue(guest),
                        values.DisplayName,
                        values.LegalName,
                        values.Email,
                        values.Phone,
                        values.DateOfBirth,
                        values.NationalityCountryCode,
                        values.PreferredLanguageTag,
                        values.Notes,
                        parse.GetRequiredValue(options.ExpectedVersion),
                        ResolveActor(parse, global),
                        values.LanguageTags),
                    token)
                : Task.FromResult(Result.Failure<GuestMutationReceiptDto>(InvalidDateError)),
            cancellationToken));
        return command;
    }

    private static Command CreateArchiveCommand(IServiceProvider services, AdminCliGlobalOptions global)
    {
        Option<Guid> operationId = new("--operation-id") { Required = true };
        Option<Guid> property = PropertyOption();
        Option<Guid> guest = GuestOption();
        Option<long> version = new("--expected-version") { Required = true };
        Option<bool> yes = new("--yes");
        Command command = new("archive", "Archive a guest record.")
        {
            operationId,
            property,
            guest,
            version,
            yes
        };
        command.SetAction((parse, cancellationToken) => ExecuteMutationAsync(
            services,
            global,
            parse,
            GuestsAdminOperationNames.Archive,
            GuestsAdminPermissions.Archive,
            (provider, token) => parse.GetValue(yes)
                ? provider.GetRequiredService<IRequestDispatcher>().SendAsync(
                    new ArchiveGuestProfileCommand(
                        parse.GetRequiredValue(operationId),
                        parse.GetRequiredValue(property),
                        parse.GetRequiredValue(guest),
                        parse.GetRequiredValue(version),
                        ResolveActor(parse, global)),
                    token)
                : Task.FromResult(Result.Failure<GuestMutationReceiptDto>(AdminErrors.ConfirmationRequired)),
            cancellationToken));
        return command;
    }

    private static async Task<int> ExecuteProfileAsync(
        IServiceProvider services,
        AdminCliGlobalOptions global,
        ParseResult parse,
        string operationName,
        AdminPermission permission,
        Func<IServiceProvider, CancellationToken, Task<Result<GuestProfileDto>>> action,
        CancellationToken cancellationToken) => await ExecuteAsync(
        services,
        global,
        parse,
        operationName,
        permission,
        async (provider, token) =>
        {
            Result<GuestProfileDto> result = await action(provider, token).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                WriteProfiles([result.Value], Output(parse, global));
            }

            return result;
        },
        cancellationToken).ConfigureAwait(false);

    private static async Task<int> ExecuteMutationAsync(
        IServiceProvider services,
        AdminCliGlobalOptions global,
        ParseResult parse,
        string operationName,
        AdminPermission permission,
        Func<IServiceProvider, CancellationToken, Task<Result<GuestMutationReceiptDto>>> action,
        CancellationToken cancellationToken) => await ExecuteAsync(
        services,
        global,
        parse,
        operationName,
        permission,
        async (provider, token) =>
        {
            Result<GuestMutationReceiptDto> result = await action(provider, token).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                WriteMutationReceipts([result.Value], Output(parse, global));
            }

            return result;
        },
        cancellationToken).ConfigureAwait(false);

    private static Task<int> ExecuteAsync<T>(
        IServiceProvider services,
        AdminCliGlobalOptions global,
        ParseResult parse,
        string operationName,
        AdminPermission permission,
        Func<IServiceProvider, CancellationToken, Task<Result<T>>> action,
        CancellationToken cancellationToken) => services.GetRequiredService<AdminCliExecutor>().ExecuteAsync(
        parse,
        AdminOperation.Create(operationName, permission),
        parse.GetValue(global.TenantOption),
        requireTenant: true,
        action,
        cancellationToken);

    private static bool TryValues(ParseResult parse, ProfileOptions options, out GuestProfileValues values)
    {
        string? birthDateText = parse.GetValue(options.DateOfBirth);
        DateOnly? dateOfBirth = null;
        if (!string.IsNullOrWhiteSpace(birthDateText))
        {
            if (!DateOnly.TryParseExact(
                    birthDateText,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateOnly parsed))
            {
                values = default!;
                return false;
            }

            dateOfBirth = parsed;
        }

        values = new(
            parse.GetRequiredValue(options.DisplayName),
            parse.GetValue(options.LegalName),
            parse.GetValue(options.Email),
            parse.GetValue(options.Phone),
            dateOfBirth,
            parse.GetValue(options.Nationality),
            parse.GetValue(options.Language),
            parse.GetValue(options.Notes),
            parse.GetResult(options.Languages) is null ? null : parse.GetValue(options.Languages) ?? []);
        return true;
    }

    private static void WriteList(IReadOnlyCollection<GuestListItemDto> profiles, string output) =>
        AdminCliOutput.WriteRows(
            profiles,
            output,
            [
                ("GuestId", profile => profile.GuestId.ToString()),
                ("DisplayName", profile => profile.DisplayName),
                ("Email", profile => profile.Email ?? string.Empty),
                ("Phone", profile => profile.Phone ?? string.Empty),
                ("Languages", profile => string.Join(", ", profile.LanguageTags)),
                ("Status", profile => profile.Status.ToString()),
                ("LastChangedAtUtc", profile => profile.LastChangedAtUtc.ToString("O", CultureInfo.InvariantCulture))
            ]);

    private static void WriteProfiles(IReadOnlyCollection<GuestProfileDto> profiles, string output) =>
        AdminCliOutput.WriteRows(
            profiles,
            output,
            [
                ("GuestId", profile => profile.GuestId.ToString()),
                ("PropertyId", profile => profile.OriginPropertyId.ToString()),
                ("DisplayName", profile => profile.DisplayName),
                ("Email", profile => profile.Email ?? string.Empty),
                ("Phone", profile => profile.Phone ?? string.Empty),
                ("Languages", profile => string.Join(", ", profile.LanguageTags)),
                ("Status", profile => profile.Status.ToString()),
                ("Version", profile => profile.Version.ToString(CultureInfo.InvariantCulture))
            ]);

    private static void WriteMutationReceipts(
        IReadOnlyCollection<GuestMutationReceiptDto> receipts,
        string output) => AdminCliOutput.WriteRows(
        receipts,
        output,
        [
            ("GuestId", receipt => receipt.GuestId.ToString()),
            ("Status", receipt => receipt.Status.ToString()),
            ("Version", receipt => receipt.Version.ToString(CultureInfo.InvariantCulture)),
            ("LastChangedAtUtc", receipt => receipt.LastChangedAtUtc.ToString("O", CultureInfo.InvariantCulture))
        ]);

    private static string Output(ParseResult parse, AdminCliGlobalOptions global) =>
        parse.GetValue(global.OutputOption) ?? AdminCliOutput.Table;

    private static string ResolveActor(ParseResult parse, AdminCliGlobalOptions global) =>
        string.IsNullOrWhiteSpace(parse.GetValue(global.ActorOption))
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : parse.GetValue(global.ActorOption)!.Trim();

    private static Option<Guid> PropertyOption() => new("--property-id") { Required = true };
    private static Option<Guid> GuestOption() => new("--guest-id") { Required = true };

    private static readonly Error InvalidDateError = new(
        "Guests.DateOfBirthInvalid",
        "Date of birth must use yyyy-MM-dd format.");

    private sealed record GuestProfileValues(
        string DisplayName,
        string? LegalName,
        string? Email,
        string? Phone,
        DateOnly? DateOfBirth,
        string? NationalityCountryCode,
        string? PreferredLanguageTag,
        string? Notes,
        string[]? LanguageTags);

    private sealed class ProfileOptions
    {
        public Option<string> DisplayName { get; } = new("--display-name") { Required = true };
        public Option<string?> LegalName { get; } = new("--legal-name");
        public Option<string?> Email { get; } = new("--email");
        public Option<string?> Phone { get; } = new("--phone");
        public Option<string?> DateOfBirth { get; } = new("--date-of-birth");
        public Option<string?> Nationality { get; } = new("--nationality");
        public Option<string?> Language { get; } = new("--language");
        public Option<string[]> Languages { get; } = new("--languages")
        {
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true,
            Description = "Replace all languages; omit to preserve, or supply without values to clear."
        };
        public Option<string?> Notes { get; } = new("--notes");
        public Option<long> ExpectedVersion { get; } = new("--expected-version") { Required = true };

        public void AddTo(Command command, bool includeVersion)
        {
            command.Options.Add(this.DisplayName);
            command.Options.Add(this.LegalName);
            command.Options.Add(this.Email);
            command.Options.Add(this.Phone);
            command.Options.Add(this.DateOfBirth);
            command.Options.Add(this.Nationality);
            command.Options.Add(this.Language);
            command.Options.Add(this.Languages);
            command.Options.Add(this.Notes);
            if (includeVersion)
            {
                command.Options.Add(this.ExpectedVersion);
            }
        }
    }
}
