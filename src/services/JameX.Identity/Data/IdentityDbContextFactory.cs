using JameX.ServiceDefaults.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace JameX.Identity.Data;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time.
/// <para>
/// Without this, the tooling boots the real application host to find the
/// context — which fails on a developer machine, because the host expects
/// compose-supplied configuration and a hostname (<c>postgres</c>) that only
/// resolves inside the compose network. Generating a migration needs the model,
/// not a running database, so this hands EF the provider and nothing else.
/// </para>
/// </summary>
internal sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            // localhost, not `postgres`: design-time commands run on the host,
            // where the database is only reachable via the published port.
            .UseNpgsql(
                "Host=localhost;Database=jamex_users;Username=jamex;Password=jamex",
                PostgresExtensions.ConfigureNpgsql)
            .Options;

        return new IdentityDbContext(options);
    }
}
