namespace Integration.Tests;

using System.Text;
using BunkFy.Modules.DataRights.Application.Models;
using BunkFy.Modules.DataRights.Application.Ports;
using BunkFy.Modules.DataRights.Contracts;
using BunkFy.Modules.DataRights.Domain.Aggregates;
using BunkFy.Modules.DataRights.Domain.Models;
using BunkFy.Modules.DataRights.Domain.ValueObjects;
using BunkFy.Modules.DataRights.Persistence;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Gma.Framework.FileManagement;
using Gma.Framework.FileManagement.Minio;
using Gma.Framework.Runtime.Time;
using Gma.Framework.Scoping;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;
using Xunit;

[Trait("Category", "Integration")]
public sealed class DataRightsProtectedExportIntegrationTests
{
    private const string TenantId = "tenant-protected-export";
    private static readonly DateTimeOffset Now =
        new(2026, 7, 27, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ExpiresAt = Now.AddHours(24);

    [DockerFact]
    [Trait("Category", "Docker")]
    public async Task Protected_export_round_trips_through_postgresql_and_minio()
    {
        const string accessKey = "minioadmin";
        const string secretKey = "minioadmin";
        string bucketName = $"bunkfy-data-rights-{Guid.NewGuid():N}";
        byte[] plaintext = Encoding.UTF8.GetBytes(
            """
            {"format":"bunkfy.data-rights.export","subject":"confidential-staff-record"}
            """);

        await using PostgreSqlContainer postgreSql =
            new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase("bunkfy_data_rights_protected_export_tests")
                .Build();
        await using IContainer minio =
            ProviderTestContainers.Minio()
                .WithEnvironment("MINIO_ROOT_USER", accessKey)
                .WithEnvironment("MINIO_ROOT_PASSWORD", secretKey)
                .WithPortBinding(9000, assignRandomHostPort: true)
                .WithCommand("server", "/data", "--console-address", ":9001")
                .WithWaitStrategy(
                    Wait.ForUnixContainer()
                        .UntilInternalTcpPortIsAvailable(9000))
                .Build();

        await Task.WhenAll(
            postgreSql.StartAsync(),
            minio.StartAsync()).ConfigureAwait(false);

        using ServiceProvider provider = BuildProvider(
            postgreSql.GetConnectionString(),
            $"localhost:{minio.GetMappedPublicPort(9000)}",
            accessKey,
            secretKey,
            bucketName,
            plaintext);

        Guid caseId = Guid.NewGuid();
        Guid artifactId = Guid.NewGuid();
        Guid runId = Guid.NewGuid();
        Guid staffProfileId = Guid.NewGuid();
        DataRightsCase dataRightsCase =
            CreateApprovedStaffCase(caseId, staffProfileId);
        long decisionRevision = dataRightsCase.DecisionRevision!.Value;
        DataRightsExportGenerationRequest generationRequest = new(
            artifactId,
            TenantId,
            caseId,
            DataRightsCaseType.StaffRights,
            PropertyId: null,
            decisionRevision,
            [
                new DataRightsSubjectCoordinate(
                    "staff",
                    "staff-profile",
                    staffProfileId,
                    RecordVersion: 3)
            ],
            Now.AddMinutes(-9),
            ExpiresAt);

        string? storageKey = null;
        try
        {
            await using (AsyncServiceScope scope =
                         provider.CreateAsyncScope())
            {
                DataRightsDbContext dbContext =
                    scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
                await dbContext.Database.MigrateAsync();

                DataRightsExportArtifact artifact = CreateArtifact(
                    artifactId,
                    caseId,
                    decisionRevision);
                dbContext.Cases.Add(dataRightsCase);
                await scope.ServiceProvider
                    .GetRequiredService<IDataRightsExportArtifactRepository>()
                    .AddAsync(artifact, CancellationToken.None);
                await dbContext.SaveChangesAsync();

                Assert.True(artifact.BeginGeneration(
                    runId,
                    attempt: 1,
                    "system:data-rights-export",
                    Now.AddMinutes(-5)).IsSuccess);
                await dbContext.SaveChangesAsync();

                DataRightsProtectedExportArtifact generated =
                    await scope.ServiceProvider
                        .GetRequiredService<IDataRightsExportArtifactGenerator>()
                        .GenerateAsync(
                            generationRequest,
                            CancellationToken.None);
                storageKey = generated.StorageKey;

                Assert.True(artifact.MarkAvailable(
                    runId,
                    attempt: 1,
                    generated.StorageKey,
                    generated.EncryptedByteLength,
                    generated.PlaintextSha256,
                    generated.EncryptionKeyVersion,
                    generated.FormatVersion,
                    generated.AvailableAtUtc,
                    generated.ExpiresAtUtc).IsSuccess);
                Assert.True(dataRightsCase.CompleteAccessExport(
                    decisionRevision,
                    "system:data-rights-export",
                    generated.AvailableAtUtc).IsSuccess);
                await dbContext.SaveChangesAsync();
            }

            await using (AsyncServiceScope scope =
                         provider.CreateAsyncScope())
            {
                DataRightsDbContext dbContext =
                    scope.ServiceProvider.GetRequiredService<DataRightsDbContext>();
                IDataRightsExportArtifactRepository repository =
                    scope.ServiceProvider
                        .GetRequiredService<IDataRightsExportArtifactRepository>();
                DataRightsExportArtifact reloaded = Assert.IsType<
                    DataRightsExportArtifact>(
                    await repository.GetAsync(
                        DataRightsCaseScope.Staff,
                        artifactId,
                        CancellationToken.None));

                Assert.Equal(
                    DataRightsExportArtifactState.Available,
                    reloaded.State);
                DataRightsCase reloadedCase = await dbContext.Cases
                    .SingleAsync(item => item.Id == caseId);
                Assert.Equal(
                    DataRightsCaseState.Completed,
                    reloadedCase.Status);
                Assert.Null(reloadedCase.ExecutionRevision);
                Assert.NotNull(reloaded.StorageKey);
                Assert.DoesNotContain(
                    TenantId,
                    reloaded.StorageKey,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    caseId.ToString("N"),
                    reloaded.StorageKey,
                    StringComparison.OrdinalIgnoreCase);

                DataRightsExportDownload download =
                    await scope.ServiceProvider
                        .GetRequiredService<IDataRightsExportArtifactReader>()
                        .OpenVerifiedAsync(
                            reloaded,
                            CancellationToken.None);
                await using (download.Content)
                await using (MemoryStream restored = new())
                {
                    await download.Content.CopyToAsync(restored);
                    Assert.Equal(plaintext, restored.ToArray());
                    Assert.Equal(plaintext.LongLength, download.ContentLength);
                }
            }
        }
        finally
        {
            if (storageKey is not null)
            {
                IFileStorage storage = provider.GetRequiredService<IFileStorage>();
                _ = await storage.DeleteAsync(
                    new FileStorageObjectKey(storageKey));
            }

            Array.Clear(plaintext);
        }
    }

    private static ServiceProvider BuildProvider(
        string connectionString,
        string endpoint,
        string accessKey,
        string secretKey,
        string bucketName,
        byte[] plaintext)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration["Persistence:Provider"] = "PostgreSql";
        builder.Configuration["ConnectionStrings:PostgreSql"] = connectionString;
        builder.Configuration["FileManagement:Enabled"] = "true";
        builder.Configuration["FileManagement:Provider"] = "Minio";
        builder.Configuration["FileManagement:MaximumObjectBytes"] = "2097152";
        builder.Configuration["FileManagement:AllowedContentTypes:0"] =
            "application/octet-stream";
        builder.Configuration["FileManagement:Minio:Endpoint"] = endpoint;
        builder.Configuration["FileManagement:Minio:AccessKey"] = accessKey;
        builder.Configuration["FileManagement:Minio:SecretKey"] = secretKey;
        builder.Configuration["FileManagement:Minio:BucketName"] = bucketName;
        builder.Configuration["FileManagement:Minio:UseSsl"] = "false";
        builder.Configuration["FileManagement:Minio:CreateBucketIfMissing"] =
            "true";
        builder.Configuration["DataRights:ExportArtifacts:ActiveKeyVersion"] =
            "7";
        builder.Configuration["DataRights:ExportArtifacts:Keys:7"] =
            Convert.ToBase64String(
                Enumerable.Range(1, 32)
                    .Select(value => (byte)value)
                    .ToArray());
        builder.Configuration[
            "DataRights:ExportArtifacts:MaximumPlaintextBytes"] = "1048576";
        builder.Configuration["DataRights:ExportArtifacts:ChunkSizeBytes"] =
            "16384";
        builder.Configuration["DataRights:ExportArtifacts:ArtifactLifetime"] =
            "1.00:00:00";

        builder.Services.AddSingleton<IScopeContext>(
            new TestScopeContext());
        builder.Services.AddSingleton<ISystemClock>(
            new TestClock());
        builder.Services.AddScoped<IDataRightsExportAssembler>(
            _ => new StaticAssembler(plaintext));
        builder.AddMinioFileStorage();
        builder.AddDataRightsPersistence();

        return builder.Services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateScopes = true
            });
    }

    private static DataRightsCase CreateApprovedStaffCase(
        Guid caseId,
        Guid staffProfileId)
    {
        DataRightsCaseRequest request = DataRightsCaseRequest.Create(
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            DataRightsCaseOperation.AccessExport,
            DataRightsRequesterRelation.DataSubject).Value;
        DataRightsCase dataRightsCase = DataRightsCase.Create(
            caseId,
            TenantId,
            request,
            "staff:privacy",
            Now.AddHours(-1)).Value;
        Assert.True(dataRightsCase.RecordRequesterVerification(
            verified: true,
            dataRightsCase.Version,
            "staff:privacy",
            Now.AddMinutes(-55)).IsSuccess);
        Assert.True(dataRightsCase.RecordControllerRouting(
            dataRightsCase.Version,
            "staff:privacy",
            Now.AddMinutes(-52)).IsSuccess);
        Assert.True(dataRightsCase.BeginDiscovery(
            dataRightsCase.Version,
            "staff:privacy",
            Now.AddMinutes(-50)).IsSuccess);
        Assert.True(dataRightsCase.SelectSubject(
            "staff",
            "staff-profile",
            staffProfileId,
            recordVersion: 3,
            dataRightsCase.Version,
            "staff:privacy",
            Now.AddMinutes(-40)).IsSuccess);
        Assert.True(dataRightsCase.RequireReview(
            dataRightsCase.Version,
            "staff:privacy",
            Now.AddMinutes(-30)).IsSuccess);
        Assert.True(dataRightsCase.BeginDecision(
            dataRightsCase.Version,
            "staff:decision-maker",
            Now.AddMinutes(-20)).IsSuccess);
        Assert.True(dataRightsCase.RecordDecision(
            DataRightsCaseDecision.Approved,
            DataRightsCaseDecisionReason.RequestValidated,
            dataRightsCase.Version,
            "staff:decision-maker",
            Now.AddMinutes(-10)).IsSuccess);
        return dataRightsCase;
    }

    private static DataRightsExportArtifact CreateArtifact(
        Guid artifactId,
        Guid caseId,
        long decisionRevision) =>
        DataRightsExportArtifact.Request(
            artifactId,
            TenantId,
            Guid.NewGuid(),
            caseId,
            propertyId: null,
            DataRightsCaseKind.StaffRights,
            decisionRevision,
            selectedSubjectCount: 1,
            new string('a', DataRightsExportArtifact.Sha256Length),
            "staff:privacy",
            Now.AddMinutes(-9),
            ExpiresAt).Value;

    private sealed class StaticAssembler(byte[] content)
        : IDataRightsExportAssembler
    {
        public async Task<DataRightsExportAssemblyResult> AssembleAsync(
            DataRightsExportGenerationRequest request,
            Stream destination,
            CancellationToken cancellationToken)
        {
            await destination.WriteAsync(
                content,
                cancellationToken);
            return new(SubjectCount: 1, RecordCount: 1);
        }
    }

    private sealed class TestClock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class TestScopeContext : IScopeContext
    {
        public bool IsEnabled => true;

        public string ScopeId => TenantId;
    }
}
