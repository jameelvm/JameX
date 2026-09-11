using JameX.Engagement.Domain;
using JameX.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;

namespace JameX.Engagement.Data;

/// <summary>
/// The only class permitted to touch <c>jamex_engagement</c>.
/// <para>
/// Comments live here because they are read as an ordered, paginated list —
/// a relational access pattern. Counters and reactions do not: they live in
/// DynamoDB, reached through their own repositories, never through this
/// context. One service, two stores, because the access patterns genuinely
/// differ — the same split the design doc draws between the metadata database
/// and the counter store.
/// </para>
/// </summary>
public sealed class EngagementDbContext(DbContextOptions<EngagementDbContext> options)
    : DbContext(options)
{
    public DbSet<Comment> Comments => Set<Comment>();

    /// <summary>Inbox — see <see cref="ProcessedEvent"/>.</summary>
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Comment>(comment =>
        {
            comment.ToTable("comments");
            comment.HasKey(c => c.Id);

            comment.Property(c => c.Text).HasMaxLength(10_000).IsRequired();
            comment.Property(c => c.CreatedAt).IsRequired();
            comment.Property(c => c.UpdatedAt).IsRequired();

            // The comment list for a video: newest first, paged. This is the
            // only access path that is not "by id", so it is the only one
            // that needs its own index.
            comment.HasIndex(c => new { c.VideoId, c.CreatedAt })
                .HasDatabaseName("ix_comments_video_id_created_at")
                .IsDescending(false, true);

            // Fetching a top-level comment's replies.
            comment.HasIndex(c => c.ParentCommentId)
                .HasDatabaseName("ix_comments_parent_comment_id");

            // A real foreign key: parent and child are the same table in the
            // same database, so the constraint is available — contrast with
            // VideoId, which references a row this service cannot see.
            // Restrict, not cascade: deleting a comment with replies is a
            // decision the service layer must make explicitly (Module 5),
            // not something the database should do silently.
            comment.HasOne<Comment>()
                .WithMany()
                .HasForeignKey(c => c.ParentCommentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Inbox only — Engagement has no outbox. It never announces its own
        // changes; nothing downstream needs to react to a view or a like.
        modelBuilder.AddJameXInboxTable();
        modelBuilder.UseSnakeCaseNames();
    }
}
