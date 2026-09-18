using System.IdentityModel.Tokens.Jwt;
using JameX.Gateway;
using JameX.ServiceDefaults.Hosting;

// The Gateway is the only address the browser knows. It exists for three
// reasons that matter at this scale:
//
//  1. One origin for the client, so services can be split, renamed or moved
//     without the frontend or its CORS configuration changing.
//  2. One place to authenticate. The Gateway validates the caller once and
//     forwards a trusted identity, so seven services do not each re-implement
//     token validation.
//  3. Aggregation. A watch page needs metadata (Catalog), counters and the
//     viewer's own reaction (Engagement) and the channel name (Identity).
//     Making the browser issue three requests over a mobile connection is
//     worse than making one server-side fan-out on a fast internal network.
//
// Routing is table-driven from appsettings so the topology is data, not code.
const string ServiceName = "Gateway";

var builder = WebApplication.CreateBuilder(args);

builder.AddJameXServiceDefaults(ServiceName);
builder.AddJameXApiDefaults();
builder.Services.AddGatewayServices(builder.Configuration);

builder.Services
    .AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseJameXExceptionHandling();
app.UseCors();

app.UseAuthentication();

// The trust boundary this whole design has pointed at since phase 2: strip
// whatever the client sent (a browser could set X-JameX-User to anyone's id
// directly — see PROGRESS.md's phase 6 notes on this exact gap), then, only
// if a valid JWT was presented, replace it with the subject that token's
// signature actually vouches for. Every downstream service still just reads
// X-JameX-User unchanged; only what's allowed to set it has changed.
app.Use(async (context, next) =>
{
    context.Request.Headers.Remove(HeaderCurrentUser.HeaderName);

    var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
    if (subject is not null)
        context.Request.Headers[HeaderCurrentUser.HeaderName] = subject;

    await next();
});

app.UseAuthorization();

app.MapJameXDefaultEndpoints(ServiceName);

app.MapReverseProxy();

app.Run();
