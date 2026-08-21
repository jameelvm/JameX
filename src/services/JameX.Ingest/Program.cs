using JameX.Ingest;
using JameX.ServiceDefaults.Hosting;

// Ingest owns the upload path: presigned multipart URLs, resumable session
// state in DynamoDB, and the raw S3 bucket. Video bytes never pass through this
// process — at the doc's ~480 Gbps ingest that would make the service the
// bottleneck. It issues credentials and tracks progress; S3 takes the bytes.
const string ServiceName = "Ingest";

var builder = WebApplication.CreateBuilder(args);

builder.AddJameXServiceDefaults(ServiceName);
builder.AddJameXApiDefaults();
builder.Services.AddIngestServices(builder.Configuration);

var app = builder.Build();

app.UseJameXExceptionHandling();
app.UseCors();

app.MapJameXDefaultEndpoints(ServiceName);

app.Run();
