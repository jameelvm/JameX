using JameX.Catalog.Domain;
using JameX.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;

namespace JameX.Catalog.Data;

/// <summary>
/// The only class permitted to touch <c>jamex_catalog</c>.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<Rendition> Renditions => Set<Rendition>();

    /// <summary>Inbox — see <see cref="ProcessedEvent"/>.</summary>
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    /// <summary>Outbox — see <see cref="OutboxMessage"/>.</summary>
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Declared in the model so the migration creates it, rather than
        // depending on the container init script having run first.
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.Entity<Video>(video =>
        {
            video.ToTable("videos");
            video.HasKey(v => v.Id);

            // No ValueGeneratedOnAdd: the id arrives on the event and must be
            // used verbatim. Letting the database mint one would silently
            // decouple this row from the S3 object that already embeds the id.
            video.Property(v => v.Id).ValueGeneratedNever();

            video.Property(v => v.Title).HasMaxLength(200).IsRequired();
            video.Property(v => v.Description).HasMaxLength(5000);
            video.Property(v => v.CategoryId).HasMaxLength(50);
            video.Property(v => v.DefaultLanguage).HasMaxLength(20).IsRequired();
            video.Property(v => v.RawBucket).HasMaxLength(100).IsRequired();
            video.Property(v => v.RawObjectKey).HasMaxLength(500).IsRequired();
            video.Property(v => v.ContentType).HasMaxLength(100);
            video.Property(v => v.MediaBucket).HasMaxLength(100);
            video.Property(v => v.MasterPlaylistKey).HasMaxLength(500);
            video.Property(v => v.PosterThumbnailKey).HasMaxLength(500);
            video.Property(v => v.EncoderProvider).HasMaxLength(50);
            video.Property(v => v.FailureReason).HasMaxLength(2000);
            video.Property(v => v.FailureStage).HasMaxLength(100);

            // Enums as int. Their numeric values are already part of the wire
            // contract (see JameX.Contracts.Enums), so storing the name would
            // give two independent representations to keep in step. Costs
            // readability in psql; buys a 4-byte, index-friendly column.
            video.Property(v => v.Status).HasConversion<int>();
            video.Property(v => v.Privacy).HasConversion<int>();
            video.Property(v => v.PopularityTier).HasConversion<int>();

            // A channel page: this channel's videos, newest first. The composite
            // index answers both the filter and the ordering, so Postgres reads
            // it in order and stops at the page size instead of sorting the
            // channel's entire history.
            video.HasIndex(v => new { v.ChannelId, v.CreatedAt })
                .HasDatabaseName("ix_videos_channel_id_created_at")
                .IsDescending(false, true);

            // The public feed. Partial, because it only ever serves rows that
            // are both public and playable — which excludes every private,
            // queued, transcoding and failed video from the index entirely.
            video.HasIndex(v => v.PublishedAt)
                .HasDatabaseName("ix_videos_published")
                .IsDescending(true)
                .HasFilter("privacy = 2 AND status = 3");

            // Operational: "what is stuck in Transcoding?" — the query a
            // stalled-pipeline alert runs.
            video.HasIndex(v => v.Status).HasDatabaseName("ix_videos_status");

            // Tag lookup over the text[] column. GIN is the index type for
            // "does this array contain X" — a B-tree cannot answer that.
            video.HasIndex(v => v.Tags)
                .HasDatabaseName("ix_videos_tags")
                .HasMethod("gin");

            // Trigram index on the title, for the Postgres full-text path that
            // phase 5 compares against the DynamoDB inverted index. Also what
            // makes near-duplicate title detection (chapter 4) cheap.
            video.HasIndex(v => v.Title)
                .HasDatabaseName("ix_videos_title_trgm")
                .HasMethod("gin")
                .HasOperators("gin_trgm_ops");
        });

        modelBuilder.Entity<Rendition>(rendition =>
        {
            rendition.ToTable("renditions");
            rendition.HasKey(r => r.Id);

            rendition.Property(r => r.Label).HasMaxLength(20).IsRequired();
            rendition.Property(r => r.Codec).HasMaxLength(30).IsRequired();
            rendition.Property(r => r.PlaylistKey).HasMaxLength(500).IsRequired();

            // Makes the VideoEncoded handler idempotent at the schema level: a
            // redelivered event cannot insert a second "720p" for the same
            // video, whatever the handler does.
            rendition.HasIndex(r => new { r.VideoId, r.Label })
                .HasDatabaseName("ix_renditions_video_id_label")
                .IsUnique();

            // A real foreign key, because both tables are in this service's
            // database. Cascade: renditions have no meaning without their video.
            rendition.HasOne(r => r.Video)
                .WithMany(v => v.Renditions)
                .HasForeignKey(r => r.VideoId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.AddJameXEventTables();
        modelBuilder.UseSnakeCaseNames();
    }
}
