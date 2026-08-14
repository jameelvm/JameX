using JameX.ServiceDefaults.Hosting;

// Engagement owns the write-heavy half of the system: sharded view counters and
// reactions in DynamoDB, comments in its own relational database.
//
// It is separate because its write profile is unlike anything else here. A
// viral video's view counter is the hottest key in the system, and isolating it
// means a counter hot-partition problem cannot degrade playback or search.
const string ServiceName = "Engagement";

var builder = WebApplication.CreateBuilder(args);

builder.AddJameXServiceDefaults(ServiceName);
builder.AddJameXApiDefaults();

// Consumes VideoEncoded (initialise counters) and VideoDeleted (drop them).
builder.Services.AddJameXEventConsumer();

var app = builder.Build();

app.UseCors();
app.MapJameXDefaultEndpoints(ServiceName);

app.Run();
