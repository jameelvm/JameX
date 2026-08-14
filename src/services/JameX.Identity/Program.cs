using JameX.Identity.Data;
using JameX.ServiceDefaults.Data;
using JameX.ServiceDefaults.Hosting;

// Identity owns jamex_users. Chapter 2 keeps user data strongly consistent
// while video metadata is allowed to lag, which is why this is a separate
// service with a separate database rather than a table alongside the catalogue.
const string ServiceName = "Identity";

var builder = WebApplication.CreateBuilder(args);

builder.AddJameXServiceDefaults(ServiceName);
builder.AddJameXApiDefaults();
builder.AddJameXPostgres<IdentityDbContext>("Identity");

var app = builder.Build();

app.UseCors();
app.MapJameXDefaultEndpoints(ServiceName);

// Development convenience only — see MigrateJameXDatabaseAsync.
await app.MigrateJameXDatabaseAsync<IdentityDbContext>();

app.Run();
