using JameX.ServiceDefaults.Hosting;

// Search owns the inverted index described in chapter 3: term -> videos, with
// frequency and the field the term matched on.
//
// It is separate so the query engine can be replaced without touching the rest
// of the system — the DynamoDB index here could become OpenSearch behind the
// same API. It rebuilds itself purely from events, so it can be dropped and
// replayed from the topic without any other service knowing.
const string ServiceName = "Search";

var builder = WebApplication.CreateBuilder(args);

builder.AddJameXServiceDefaults(ServiceName);
builder.AddJameXApiDefaults();

// Consumes VideoEncoded (index it — nothing unplayable should be findable)
// and VideoDeleted (remove it).
builder.Services.AddJameXEventConsumer();

var app = builder.Build();

app.UseCors();
app.MapJameXDefaultEndpoints(ServiceName);

app.Run();
