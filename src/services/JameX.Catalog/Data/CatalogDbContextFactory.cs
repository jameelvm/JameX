using JameX.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace JameX.Catalog.Data;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time — see the Identity equivalent
/// for why the tooling must not boot the real host.
/// </summary>
internal sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            // localhost, not `postgres`: design-time commands run on the host,
            // where the database is only reachable via the published port.
            .UseNpgsql(
                "Host=localhost;Database=jamex_catalog;Username=jamex;Password=jamex",
                PostgresExtensions.ConfigureNpgsql)
            .Options;

        return new CatalogDbContext(options);
    }
}
