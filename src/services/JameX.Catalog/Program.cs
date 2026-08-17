using JameX.Catalog.Data;
using JameX.ServiceDefaults.Data;
using JameX.ServiceDefaults.Hosting;

// Catalog owns video metadata: the jamex_catalog database and nothing else.
// It is the system of record for what a video *is* — title, tags, privacy,
// status, renditions — and the only service permitted to write that data.
const string ServiceName = "Catalog";

var builder = WebApplication.CreateBuilder(args);

builder.AddJameXServiceDefaults(ServiceName);
builder.AddJameXApiDefaults();
builder.AddJameXPostgres<CatalogDbContext>("Catalog");

// Consumes VideoUploaded (create the metadata row) and VideoEncoded /
// VideoEncodingFailed (advance status, record the ladder). Handlers arrive in
// phase 3.
builder.Services.AddJameXEventConsumer();

var app = builder.Build();

app.UseJameXExceptionHandling();
app.UseCors();

app.MapJameXDefaultEndpoints(ServiceName);

// Development convenience only — see MigrateJameXDatabaseAsync.
await app.MigrateJameXDatabaseAsync<CatalogDbContext>();

app.Run();
