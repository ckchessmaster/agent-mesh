namespace AgentMesh.Core.Services;

public interface IStorageService
{
    Task EnsureBucketExistsAsync(CancellationToken ct = default);
    Task<string> UploadTextAsync(string key, string content, string contentType = "application/json", CancellationToken ct = default);
    Task<string> UploadStreamAsync(string key, Stream stream, string contentType, CancellationToken ct = default);
    Task<string> DownloadTextAsync(string s3UriOrKey, CancellationToken ct = default);
    Task<Stream> DownloadStreamAsync(string s3UriOrKey, CancellationToken ct = default);
    Task<string> GetPresignedUrlAsync(string s3UriOrKey, TimeSpan duration);
}
