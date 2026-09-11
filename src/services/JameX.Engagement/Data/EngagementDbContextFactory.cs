using JameX.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace JameX.Engagement.Data;

/// <summary>Used only by <c>dotnet ef</c> at design time — see Identity's equivalent.</summary>
internal sealed class EngagementDbContextFactory : IDesignTimeDbContextFactory<EngagementDbContext>
{
    public EngagementDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<EngagementDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=jamex_engagement;Username=jamex;Password=jamex",
                PostgresExtensions.ConfigureNpgsql)
            .Options;

        return new EngagementDbContext(options);
    }
}
