using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.S3;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using JameX.ServiceDefaults.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JameX.ServiceDefaults.Aws;

/// <summary>
/// Marker for the second S3 client, the one bound to the browser-facing
/// endpoint and used only to presign URLs. Resolved via a keyed service so
/// callers cannot accidentally get the wrong one.
/// </summary>
public static class AwsClientKeys
{
    public const string PublicS3 = "public-s3";
}

public static class AwsClientFactory
{
    /// <summary>
    /// Registers the AWS clients every service may need. All are singletons —
    /// the SDK clients are thread-safe and hold connection pools, so creating
    /// them per request is a known source of socket exhaustion.
    /// </summary>
    public static IServiceCollection AddJameXAwsClients(this IServiceCollection services)
    {
        services.AddSingleton<IAmazonS3>(sp =>
            new AmazonS3Client(Credentials(sp), Configure(sp, new AmazonS3Config
            {
                // LocalStack is addressed as host/bucket/key rather than
                // bucket.host/key, so virtual-host addressing must be off.
                ForcePathStyle = true
            })));

        // Presigning client: identical except that it signs for the address the
        // browser will actually call. See AwsOptions.PublicServiceUrl.
        services.AddKeyedSingleton<IAmazonS3>(AwsClientKeys.PublicS3, (sp, _) =>
        {
            var options = sp.GetRequiredService<IOptions<AwsOptions>>().Value;
            var config = new AmazonS3Config { ForcePathStyle = true };
            var publicUrl = options.PublicServiceUrl ?? options.ServiceUrl;

            if (!string.IsNullOrWhiteSpace(publicUrl))
            {
                config.ServiceURL = publicUrl;
                config.AuthenticationRegion = options.Region;
            }
            else
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
            }

            return new AmazonS3Client(Credentials(sp), config);
        });

        services.AddSingleton<IAmazonSQS>(sp =>
            new AmazonSQSClient(Credentials(sp), Configure(sp, new AmazonSQSConfig())));

        services.AddSingleton<IAmazonSimpleNotificationService>(sp =>
            new AmazonSimpleNotificationServiceClient(
                Credentials(sp), Configure(sp, new AmazonSimpleNotificationServiceConfig())));

        services.AddSingleton<IAmazonDynamoDB>(sp =>
            new AmazonDynamoDBClient(Credentials(sp), Configure(sp, new AmazonDynamoDBConfig())));

        return services;
    }

    private static AWSCredentials Credentials(IServiceProvider sp)
    {
        var options = sp.GetRequiredService<IOptions<AwsOptions>>().Value;

        // Real AWS: fall through to the default chain (instance role, IRSA,
        // environment, SSO). Static keys exist only for LocalStack.
        if (string.IsNullOrWhiteSpace(options.AccessKey))
            return DefaultAWSCredentialsIdentityResolver.GetCredentials();

        return new BasicAWSCredentials(options.AccessKey, options.SecretKey);
    }

    private static TConfig Configure<TConfig>(IServiceProvider sp, TConfig config)
        where TConfig : ClientConfig
    {
        var options = sp.GetRequiredService<IOptions<AwsOptions>>().Value;

        if (options.UsesCustomEndpoint)
        {
            // Setting ServiceURL and RegionEndpoint together throws in SDK v4.
            // AuthenticationRegion supplies the region the signature needs.
            config.ServiceURL = options.ServiceUrl;
            config.AuthenticationRegion = options.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        return config;
    }
}
