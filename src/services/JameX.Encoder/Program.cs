using JameX.Encoder;
using JameX.ServiceDefaults.Hosting;

// Encoder is the doc's encoder tier. It consumes VideoUploaded, transcodes to
// an adaptive bitrate ladder with FFmpeg, writes renditions and thumbnails to
// the media bucket, and publishes VideoEncoded.
//
// It scales on queue depth, not request rate — which is precisely why it is a
// separate service from Ingest despite sitting next to it in the pipeline.
const string ServiceName = "Encoder";

var builder = WebApplication.CreateBuilder(args);

builder.AddJameXServiceDefaults(ServiceName);

// Encoder is a worker, but it still exposes HTTP — health probes, and in
// Development a debug endpoint that runs the ladder over a generated clip so
// the transcode path can be exercised without publishing an event.
builder.AddJameXApiDefaults();

builder.Services.AddEncoderServices(builder.Configuration);
builder.Services.AddJameXEventConsumer();

var app = builder.Build();

app.UseJameXExceptionHandling();
app.MapJameXDefaultEndpoints(ServiceName);

app.Run();
