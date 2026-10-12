using AgentMesh.Core.Services;
using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMesh.Infrastructure.Storage;

public static class StorageServiceExtensions
{
    public static IServiceCollection AddSeaweedS3Storage(this IServiceCollection services, IConfiguration configuration)
    {
        var serviceUrl = configuration["Storage:S3:ServiceUrl"] ?? "http://localhost:8333";
        var accessKey = configuration["Storage:S3:AccessKey"] ?? "agentmesh-storage-key";
        var secretKey = configuration["Storage:S3:SecretKey"] ?? "AgentMesh#SecureS3Pass2026!";
        var forcePathStyle = !bool.TryParse(configuration["Storage:S3:ForcePathStyle"], out var parsed) || parsed;

        var s3Config = new AmazonS3Config
        {
            ServiceURL = serviceUrl,
            ForcePathStyle = forcePathStyle,
            UseHttp = serviceUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        };

        var credentials = new BasicAWSCredentials(accessKey, secretKey);
        services.AddSingleton<IAmazonS3>(new AmazonS3Client(credentials, s3Config));
        services.AddSingleton<IStorageService, SeaweedS3StorageService>();

        return services;
    }
}
