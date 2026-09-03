using JameX.ServiceDefaults.Aws;
using JameX.ServiceDefaults.Configuration;
using JameX.ServiceDefaults.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Scalar.AspNetCore;
using StackExchange.Redis;

namespace JameX.ServiceDefaults.Hosting;

/// <summary>
/// Everything seven service hosts would otherwise each reimplement: options
/// binding, AWS clients, the event publisher, Redis, health checks, OpenAPI and
/// CORS. Keeping it here is what stops the services drifting apart.
/// </summary>
public static class JameXHostingExtensions
{
    /// <summary>
    /// Wiring common to every JameX service, API or worker.
    /// </summary>
    public static WebApplicationBuilder AddJameXServiceDefaults(
        this WebApplicationBuilder builder, string serviceName)
    {
        builder.Services.AddSingleton<IServiceIdentity>(new ServiceIdentity(serviceName));

        builder.Services
            .Configure<AwsOptions>(builder.Configuration.GetSection(AwsOptions.SectionName))
            .Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName))
            .Configure<MessagingOptions>(builder.Configuration.GetSection(MessagingOptions.SectionName));

        builder.Services.AddJameXAwsClients();
        builder.Services.AddSingleton<IEventPublisher, SnsEventPublisher>();

        var redisConnection = builder.Configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConnection))
        {
            builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var configuration = ConfigurationOptions.Parse(redisConnection);
                // Never take a service down because the cache is unreachable —
                // a cache miss must degrade latency, not availability.
                configuration.AbortOnConnectFail = false;
                configuration.ConnectRetry = 5;
                return ConnectionMultiplexer.Connect(configuration);
            });

            builder.Services.AddSingleton<IEventDeduplicator, RedisEventDeduplicator>();
        }
        else
        {
            // No Redis configured: fall back to a filter that lets everything
            // through. Handlers are required to be idempotent regardless, and a
            // service must not fail to start because a cache is absent.
            builder.Services.AddSingleton<IEventDeduplicator, NoOpEventDeduplicator>();
        }

        builder.Services.AddHealthChecks();

        // Identifies which service produced a log line once seven of them are
        // interleaved in `docker compose logs`.
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HeaderCurrentUser>();

        return builder;
    }

    /// <summary>
    /// Extra wiring for the services that expose an HTTP API.
    /// </summary>
    public static WebApplicationBuilder AddJameXApiDefaults(this WebApplicationBuilder builder)
    {
        // Controllers rather than minimal APIs. The trade is ceremony for
        // enforced structure: [ApiController] supplies automatic model-state
        // validation, binding-source inference and ProblemDetails responses
        // that minimal APIs would have each service hand-roll. It also brings
        // ApiExplorer, which is what AddOpenApi documents the routes from.
        builder.Services.AddControllers();

        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<UnauthorizedExceptionHandler>();

        // The browser talks to the Gateway, but during development it is useful
        // to be able to hit a service directly from the Next.js origin.
        builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
            // 3000 — the Next.js frontend (phase 6).
            // 3100 — the static upload/playback harness in web/debug.
            // 8080 — the Gateway, for calls proxied through it.
            .WithOrigins("http://localhost:3000", "http://localhost:3100", "http://localhost:8080")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .WithExposedHeaders("ETag", "X-JameX-Cache")));

        return builder;
    }

    /// <summary>
    /// Registers the SQS consumer loop plus one handler per subscribed event.
    /// </summary>
    public static IServiceCollection AddJameXEventConsumer(this IServiceCollection services)
    {
        services.AddHostedService<SqsEventConsumerService>();
        return services;
    }

    /// <summary>Registers a handler for one event type.</summary>
    public static IServiceCollection AddEventHandler<THandler>(this IServiceCollection services)
        where THandler : class, IEventHandler
    {
        services.AddScoped<IEventHandler, THandler>();
        return services;
    }

    /// <summary>
    /// Translates exceptions that carry an HTTP meaning into the status code
    /// they mean, so a missing caller identity is a 401 rather than a 500.
    /// Register early: it only catches what is thrown after it.
    /// </summary>
    public static WebApplication UseJameXExceptionHandling(this WebApplication app)
    {
        app.UseExceptionHandler();
        return app;
    }

    /// <summary>
    /// Endpoints every service exposes: liveness, readiness and API docs.
    /// </summary>
    public static WebApplication MapJameXDefaultEndpoints(this WebApplication app, string serviceName)
    {
        // Liveness: is the process up? Never touch a dependency here, or a
        // database blip restarts every healthy service that talks to it.
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive", service = serviceName }))
            .ExcludeFromDescription();

        // Readiness: should traffic be routed here? This one may check
        // dependencies, because failing it removes the instance from the load
        // balancer rather than killing it.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = _ => true
        }).ExcludeFromDescription();

        app.MapGet("/", () => Results.Ok(new
        {
            service = serviceName,
            docs = "/scalar",
            health = new[] { "/health/live", "/health/ready" }
        })).ExcludeFromDescription();

        // Attribute-routed controllers, but only for hosts that asked for an
        // API. Encoder is a pure worker: it calls AddJameXServiceDefaults and
        // never AddJameXApiDefaults, so MVC was never registered and mapping
        // controllers would throw at startup. Feature-detect rather than assume.
        if (app.Services.GetService<IActionDescriptorCollectionProvider>() is not null)
            app.MapControllers();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference(options => options.WithTitle($"JameX {serviceName}"));
        }

        return app;
    }
}

/// <summary>
/// Caller identity.
/// <para>
/// Deliberately stubbed: the design doc's functional requirements do not
/// include authentication, and building real identity would add a lot of code
/// that teaches nothing about video delivery. In production this is where a
/// validated JWT's subject claim would come from — the Gateway would
/// authenticate once and forward a signed identity downstream, so individual
/// services never re-validate credentials.
/// </para>
/// </summary>
public interface ICurrentUser
{
    Guid? UserId { get; }
    Guid RequireUserId();
}

/// <summary>
/// <see cref="ICurrentUser.RequireUserId"/> throws when the caller is anonymous.
/// Without this that surfaces as a 500 — an authentication problem reported as a
/// server fault, which is both wrong and unactionable for the client.
/// </summary>
internal sealed class UnauthorizedExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not UnauthorizedAccessException) return false;

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new { error = exception.Message }, ct);
        return true;
    }
}

internal sealed class HeaderCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string HeaderName = "X-JameX-User";

    public Guid? UserId =>
        accessor.HttpContext?.Request.Headers.TryGetValue(HeaderName, out var raw) == true
        && Guid.TryParse(raw.ToString(), out var id)
            ? id
            : null;

    public Guid RequireUserId() =>
        UserId ?? throw new UnauthorizedAccessException(
            $"This endpoint requires a caller identity. Send the {HeaderName} header.");
}
