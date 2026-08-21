using JameX.Ingest.Configuration;
using JameX.Ingest.Repositories;
using JameX.Ingest.Services;
using JameX.Ingest.Storage;

namespace JameX.Ingest;

public static class IngestRegistrationExtensions
{
    public static IServiceCollection AddIngestServices(
        this IServiceCollection services, IConfiguration configuration)
    {
        // Bound here rather than in ServiceDefaults: these settings belong to
        // the upload feature and no other service should see them.
        services.Configure<UploadOptions>(configuration.GetSection(UploadOptions.SectionName));

        services.AddScoped<IUploadSessionRepository, UploadSessionRepository>();
        services.AddScoped<IRawUploadStore, RawUploadStore>();
        services.AddScoped<IUploadService, UploadService>();

        return services;
    }
}
