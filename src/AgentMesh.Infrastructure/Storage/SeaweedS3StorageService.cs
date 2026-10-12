using System.Text;
using AgentMesh.Core.Services;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentMesh.Infrastructure.Storage;

public class SeaweedS3StorageService(
    IAmazonS3 s3Client,
    IConfiguration configuration,
    ILogger<SeaweedS3StorageService> logger) : IStorageService
{
    private readonly string _defaultBucket = configuration["Storage:S3:BucketName"] ?? "agent-mesh-artifacts";
    private bool _bucketChecked;

    public async Task EnsureBucketExistsAsync(CancellationToken ct = default)
    {
        if (_bucketChecked) return;

        try
        {
            var exists = await AmazonS3Util.DoesS3BucketExistV2Async(s3Client, _defaultBucket);
            if (!exists)
            {
                logger.LogInformation("Creating SeaweedFS S3 bucket: {Bucket}", _defaultBucket);
                await s3Client.PutBucketAsync(new PutBucketRequest { BucketName = _defaultBucket }, ct);
            }
            _bucketChecked = true;
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyOwnedByYou" || ex.ErrorCode == "BucketAlreadyExists")
        {
            _bucketChecked = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to verify or auto-create S3 bucket {Bucket}. Operations will proceed assuming bucket exists.", _defaultBucket);
        }
    }

    public async Task<string> UploadTextAsync(string key, string content, string contentType = "application/json", CancellationToken ct = default)
    {
        await EnsureBucketExistsAsync(ct);

        var (bucket, cleanKey) = ParseUriOrKey(key);
        var putRequest = new PutObjectRequest
        {
            BucketName = bucket,
            Key = cleanKey,
            ContentBody = content,
            ContentType = contentType
        };

        await s3Client.PutObjectAsync(putRequest, ct);
        logger.LogInformation("Uploaded text artifact to SeaweedFS: s3://{Bucket}/{Key} ({Bytes} chars)", bucket, cleanKey, content.Length);

        return $"s3://{bucket}/{cleanKey}";
    }

    public async Task<string> UploadStreamAsync(string key, Stream stream, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketExistsAsync(ct);

        var (bucket, cleanKey) = ParseUriOrKey(key);
        var putRequest = new PutObjectRequest
        {
            BucketName = bucket,
            Key = cleanKey,
            InputStream = stream,
            ContentType = contentType
        };

        await s3Client.PutObjectAsync(putRequest, ct);
        logger.LogInformation("Uploaded stream artifact to SeaweedFS: s3://{Bucket}/{Key}", bucket, cleanKey);

        return $"s3://{bucket}/{cleanKey}";
    }

    public async Task<string> DownloadTextAsync(string s3UriOrKey, CancellationToken ct = default)
    {
        var (bucket, cleanKey) = ParseUriOrKey(s3UriOrKey);
        using var response = await s3Client.GetObjectAsync(bucket, cleanKey, ct);
        using var reader = new StreamReader(response.ResponseStream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    public async Task<Stream> DownloadStreamAsync(string s3UriOrKey, CancellationToken ct = default)
    {
        var (bucket, cleanKey) = ParseUriOrKey(s3UriOrKey);
        var response = await s3Client.GetObjectAsync(bucket, cleanKey, ct);
        return response.ResponseStream;
    }

    public Task<string> GetPresignedUrlAsync(string s3UriOrKey, TimeSpan duration)
    {
        var (bucket, cleanKey) = ParseUriOrKey(s3UriOrKey);
        var request = new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = cleanKey,
            Expires = DateTime.UtcNow.Add(duration)
        };

        var url = s3Client.GetPreSignedURL(request);
        return Task.FromResult(url);
    }

    private (string Bucket, string Key) ParseUriOrKey(string input)
    {
        if (input.StartsWith("s3://", StringComparison.OrdinalIgnoreCase))
        {
            var withoutScheme = input["s3://".Length..];
            var slashIndex = withoutScheme.IndexOf('/');
            if (slashIndex > 0)
            {
                var bucket = withoutScheme[..slashIndex];
                var key = withoutScheme[(slashIndex + 1)..];
                return (bucket, key);
            }
            return (withoutScheme, string.Empty);
        }

        var normalizedKey = input.TrimStart('/');
        return (_defaultBucket, normalizedKey);
    }
}
