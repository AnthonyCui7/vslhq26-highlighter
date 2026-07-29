using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace Highlighter.Pipeline;

/// <summary>Port of highlighter_pipeline/storage.py (boto3 → AWSSDK.S3; the
/// default credential chain behaves the same way).</summary>
public class S3Storage
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ts"] = "video/mp2t",
        [".wav"] = "audio/wav",
        [".mp4"] = "video/mp4",
    };

    public string Bucket { get; }
    public string Region { get; }

    private readonly AmazonS3Client _client;

    public S3Storage(string bucket, string region)
    {
        Bucket = bucket;
        Region = region;
        _client = new AmazonS3Client(RegionEndpoint.GetBySystemName(region));
    }

    public string UploadFile(string path, string key)
    {
        var contentType = ContentTypes.TryGetValue(Path.GetExtension(path), out var mapped)
            ? mapped
            : MimeTypes.Guess(path) ?? "application/octet-stream";
        var request = new PutObjectRequest
        {
            BucketName = Bucket,
            Key = key,
            FilePath = path,
            ContentType = contentType,
        };
        _client.PutObjectAsync(request).GetAwaiter().GetResult();
        return UrlFor(key);
    }

    public string UrlFor(string key) => $"https://{Bucket}.s3.{Region}.amazonaws.com/{key}";

    public string DownloadFile(string key, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var response = _client
            .GetObjectAsync(new GetObjectRequest { BucketName = Bucket, Key = key })
            .GetAwaiter().GetResult();
        response.WriteResponseStreamToFileAsync(path, append: false, CancellationToken.None)
            .GetAwaiter().GetResult();
        return path;
    }

    public void DeleteObject(string key) =>
        _client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = key })
            .GetAwaiter().GetResult();

    /// <summary>Delete every object under a prefix. Returns the number deleted.</summary>
    public int DeletePrefix(string prefix)
    {
        var deleted = 0;
        string? continuationToken = null;
        do
        {
            var page = _client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = Bucket,
                Prefix = prefix.TrimEnd('/') + "/",
                ContinuationToken = continuationToken,
            }).GetAwaiter().GetResult();
            var keys = (page.S3Objects ?? new List<S3Object>())
                .Select(item => new KeyVersion { Key = item.Key })
                .ToList();
            if (keys.Count > 0)
            {
                _client.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = Bucket,
                    Objects = keys,
                }).GetAwaiter().GetResult();
                deleted += keys.Count;
            }
            continuationToken = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (continuationToken is not null);
        return deleted;
    }

    /// <summary>S3 storage from S3_BUCKET/AWS_REGION. Empty S3_BUCKET disables uploads.</summary>
    public static S3Storage? StorageFromEnv()
    {
        var bucket = Config.Env("S3_BUCKET", Defaults.DEFAULT_S3_BUCKET);
        if (string.IsNullOrEmpty(bucket)) return null;
        return new S3Storage(bucket, Config.Env("AWS_REGION", Defaults.DEFAULT_AWS_REGION));
    }
}
