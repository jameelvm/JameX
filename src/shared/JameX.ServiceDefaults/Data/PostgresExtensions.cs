using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace JameX.ServiceDefaults.Data;

/// <summary>
/// Shared Postgres wiring for the three services that own a relational store —
/// Identity (<c>jamex_users</c>), Catalog (<c>jamex_catalog</c>) and Engagement
/// (<c>jamex_engagement</c>).
/// <para>
/// Each still gets its own <see cref="DbContext"/> and its own database; what is
/// shared is only the *policy* — retry behaviour, naming, health checks and how
/// migrations are applied. Centralising that is what stops three services
/// drifting into three different answers for "what happens when Postgres blips".
/// </para>
/// </summary>
public static class PostgresExtensions
{
    /// <summary>
    /// Registers a service's own <typeparamref name="TContext"/> against the
    /// connection string named after it, and adds a readiness health check for
    /// that database.
    /// </summary>
    /// <param name="connectionStringName">
    /// Key under <c>ConnectionStrings</c>, e.g. <c>Identity</c> — supplied by
    /// compose as <c>ConnectionStrings__Identity</c>.
    /// </param>
    public static WebApplicationBuilder AddJameXPostgres<TContext>(
        this WebApplicationBuilder builder, string connectionStringName)
        where TContext : DbContext
    {
        var connectionString = builder.Configuration.GetConnectionString(connectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{connectionStringName}' is not configured. " +
                $"Set ConnectionStrings__{connectionStringName}.");

        builder.Services.AddDbContext<TContext>(options =>
            options.UseNpgsql(connectionString, ConfigureNpgsql));

        // Readiness — not liveness. A service whose database is unreachable
        // should be pulled out of the load balancer, not killed and restarted:
        // restarting it does nothing to fix Postgres, and a restart loop removes
        // the instance that would have recovered on its own.
        builder.Services.AddHealthChecks()
            .AddDbContextCheck<TContext>($"postgres:{connectionStringName.ToLowerInvariant()}");

        return builder;
    }

    /// <summary>
    /// Provider settings that must be identical at run time and at design time.
    /// <para>
    /// The history table especially: if <c>dotnet ef database update</c> records
    /// applied migrations in one table and the running service looks in another,
    /// the service concludes nothing has been applied and tries to create tables
    /// that already exist. Sharing one method is what keeps the two in step.
    /// </para>
    /// </summary>
    public static void ConfigureNpgsql(NpgsqlDbContextOptionsBuilder npgsql) => npgsql
        // Postgres failovers, restarts and transient network faults are normal,
        // not exceptional. Retrying in the data layer keeps them from surfacing
        // as 500s. The cost is that this installs an execution strategy, so any
        // code opening its *own* transaction must run through it — see the
        // outbox in Catalog.
        .EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), null)
        .MigrationsHistoryTable("__jamex_migrations");

    /// <summary>
    /// Applies pending migrations at startup.
    /// <para>
    /// Convenient for a compose stack and wrong for production, where migrations
    /// run as a deliberate deployment step: several replicas starting at once
    /// would otherwise race, and an automatic migration gives you no chance to
    /// review a destructive change before it executes.
    /// </para>
    /// </summary>
    public static async Task<WebApplication> MigrateJameXDatabaseAsync<TContext>(this WebApplication app)
        where TContext : DbContext
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrations");

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToArray();
        if (pending.Length == 0)
        {
            logger.LogInformation("{Context}: database is up to date", typeof(TContext).Name);
            return app;
        }

        logger.LogInformation(
            "{Context}: applying {Count} migration(s): {Migrations}",
            typeof(TContext).Name, pending.Length, string.Join(", ", pending));

        await db.Database.MigrateAsync();
        return app;
    }

    /// <summary>
    /// Rewrites every table, column, key, index and constraint name to
    /// snake_case.
    /// <para>
    /// EF's default is the CLR name — <c>DisplayName</c> — which Postgres folds
    /// to lower case unless quoted, so the column becomes something you must
    /// write as <c>"DisplayName"</c> in every hand-written query. Since a large
    /// part of verifying this build is reading tables in <c>psql</c>, the schema
    /// is worth keeping idiomatic to the database rather than to C#.
    /// </para>
    /// </summary>
    public static ModelBuilder UseSnakeCaseNames(this ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            if (entity.GetTableName() is { } table)
                entity.SetTableName(ToSnakeCase(table));

            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.GetColumnName()));

            foreach (var key in entity.GetKeys())
                if (key.GetName() is { } name) key.SetName(ToSnakeCase(name));

            foreach (var foreignKey in entity.GetForeignKeys())
                if (foreignKey.GetConstraintName() is { } name) foreignKey.SetConstraintName(ToSnakeCase(name));

            foreach (var index in entity.GetIndexes())
                if (index.GetDatabaseName() is { } name) index.SetDatabaseName(ToSnakeCase(name));
        }

        return modelBuilder;
    }

    /// <summary>
    /// <c>DisplayName</c> to <c>display_name</c>, but also <c>PK_users</c> to
    /// <c>pk_users</c> rather than <c>p_k_users</c>: a boundary exists only
    /// where an upper-case letter follows a lower-case one, or begins a word
    /// inside a run of capitals (<c>HTTPServer</c> to <c>http_server</c>).
    /// </summary>
    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);

        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];

            if (char.IsUpper(current) && i > 0 && value[i - 1] != '_')
            {
                var followsLowerOrDigit = char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1]);
                var startsWordInCapsRun = char.IsUpper(value[i - 1])
                    && i + 1 < value.Length && char.IsLower(value[i + 1]);

                if (followsLowerOrDigit || startsWordInCapsRun)
                    builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder.ToString();
    }
}
