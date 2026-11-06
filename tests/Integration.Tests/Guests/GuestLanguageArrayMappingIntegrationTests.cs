namespace Integration.Tests;

using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class GuestLanguageArrayMappingIntegrationTests
{
    private static readonly string[] LegacyTags = ["EN-gB"];
    private static readonly string[] UnknownTags = ["unknown-١"];
    private static readonly string[] RemainingTags = ["fr", "unknown-١"];

    [DockerFact]
    [Trait("Category", "Docker")]
    [Trait("Category", "Integration")]
    public async Task Text_array_backfill_preserves_scalar_bytes_and_tracks_copied_collections()
    {
        await using PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("bunkfy_guest_language_array_mapping_tests")
            .Build();
        await postgres.StartAsync();

        DbContextOptions<LanguageArrayContext> options = new DbContextOptionsBuilder<LanguageArrayContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options;
        await using LanguageArrayContext db = new(options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE language_profiles (
                "Id" integer PRIMARY KEY,
                "PreferredLanguageTag" text NULL
            );
            INSERT INTO language_profiles VALUES
                (1, 'EN-gB'), (2, NULL), (3, 'unknown-١');
            ALTER TABLE language_profiles
                ADD COLUMN "LanguageTags" text[] NOT NULL DEFAULT ARRAY[]::text[];
            UPDATE language_profiles
                SET "LanguageTags" = ARRAY["PreferredLanguageTag"]
                WHERE "PreferredLanguageTag" IS NOT NULL;
            """);

        LanguageArrayProfile[] backfilled = await db.Profiles.OrderBy(p => p.Id).ToArrayAsync();
        Assert.Equal("EN-gB", backfilled[0].PreferredLanguageTag);
        Assert.Equal(LegacyTags, backfilled[0].LanguageTags);
        Assert.Empty(backfilled[1].LanguageTags);
        Assert.Equal(UnknownTags, backfilled[2].LanguageTags);

        LanguageArrayProfile profile = backfilled[0];
        string[] input = ["EN-gB", "fr", "unknown-١"];
        profile.ReplaceLanguages(input);
        input[0] = "caller-input-mutation";
        string[] returned = profile.LanguageTags;
        returned[0] = "caller-output-mutation";
        Assert.Equal("EN-gB", profile.LanguageTags[0]);
        db.ChangeTracker.DetectChanges();
        Assert.True(db.Entry(profile).Property(p => p.LanguageTags).IsModified);
        Assert.Equal(1, await db.SaveChangesAsync());

        profile.ReplaceLanguages(["EN-gB", "fr", "unknown-١"]);
        db.ChangeTracker.DetectChanges();
        Assert.False(db.Entry(profile).Property(p => p.LanguageTags).IsModified);
        Assert.Equal(0, await db.SaveChangesAsync());

        profile.ReplaceLanguages(["fr", "unknown-١"]);
        Assert.Equal(1, await db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        profile = await db.Profiles.SingleAsync(p => p.Id == 1);
        Assert.Equal(RemainingTags, profile.LanguageTags);
        Assert.Equal("EN-gB", profile.PreferredLanguageTag);

        profile.ReplaceLanguages([]);
        Assert.Equal(1, await db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        Assert.Empty((await db.Profiles.SingleAsync(p => p.Id == 1)).LanguageTags);
    }

    private sealed class LanguageArrayContext(DbContextOptions<LanguageArrayContext> options) : DbContext(options)
    {
        public DbSet<LanguageArrayProfile> Profiles => this.Set<LanguageArrayProfile>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LanguageArrayProfile>(entity =>
            {
                entity.ToTable("language_profiles");
                entity.HasKey(p => p.Id);
                entity.Property(p => p.PreferredLanguageTag);
                entity.Property(p => p.LanguageTags)
                    .HasField("languageTags")
                    .UsePropertyAccessMode(PropertyAccessMode.Field)
                    .HasColumnType("text[]")
                    .HasDefaultValueSql("ARRAY[]::text[]")
                    .IsRequired();
            });
        }
    }

    private sealed class LanguageArrayProfile
    {
        private string[] languageTags = [];

        public int Id { get; private set; }
        public string? PreferredLanguageTag { get; private set; }
        public string[] LanguageTags => [.. this.languageTags];

        public void ReplaceLanguages(string[] values) => this.languageTags = [.. values];
    }
}
