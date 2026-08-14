using JameX.Identity.Domain;
using JameX.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;

namespace JameX.Identity.Data;

/// <summary>
/// The only class in the system permitted to touch <c>jamex_users</c>.
/// </summary>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Channel> Channels => Set<Channel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(user =>
        {
            user.ToTable("users");
            user.HasKey(u => u.Id);

            user.Property(u => u.Email).HasMaxLength(320).IsRequired();
            user.Property(u => u.DisplayName).HasMaxLength(100).IsRequired();
            user.Property(u => u.CreatedAt).IsRequired();

            // The strong-consistency guarantee, expressed as a constraint.
            // Checking "does this email exist?" before inserting is a race:
            // two concurrent registrations both read absent and both insert.
            // Only the unique index actually prevents the duplicate — the API
            // layer's job is just to turn the resulting violation into a 409.
            user.HasIndex(u => u.Email).IsUnique().HasDatabaseName("ix_users_email");
        });

        modelBuilder.Entity<Channel>(channel =>
        {
            channel.ToTable("channels");
            channel.HasKey(c => c.Id);

            channel.Property(c => c.Name).HasMaxLength(100).IsRequired();
            channel.Property(c => c.Handle).HasMaxLength(30).IsRequired();
            channel.Property(c => c.AvatarUrl).HasMaxLength(500);
            channel.Property(c => c.SubscriberCount).HasDefaultValue(0L);
            channel.Property(c => c.CreatedAt).IsRequired();

            channel.HasIndex(c => c.Handle).IsUnique().HasDatabaseName("ix_channels_handle");

            // Listing a user's channels is the only access path that is not by
            // primary key, so it is the only one that needs its own index.
            channel.HasIndex(c => c.OwnerUserId).HasDatabaseName("ix_channels_owner_user_id");

            // A foreign key is available here precisely because both tables are
            // inside this service's database. Compare with Catalog, which
            // stores ChannelId with no constraint behind it.
            channel.HasOne(c => c.Owner)
                .WithMany(u => u.Channels)
                .HasForeignKey(c => c.OwnerUserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.UseSnakeCaseNames();
    }
}
