using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Semantic_Text_to_Vector.Services
{
    public class R2StorageService : IR2StorageService
    {
        private readonly IAmazonS3 _s3Client;
        private readonly R2StorageSettings _settings;
        private readonly ILogger<R2StorageService> _logger;

        public R2StorageService(IOptions<R2StorageSettings> settings, ILogger<R2StorageService> logger)
        {
            _settings = settings.Value;
            _logger = logger;

            var credentials = new Amazon.Runtime.BasicAWSCredentials(
                _settings.AccessKeyId,
                _settings.SecretAccessKey);

            var config = new AmazonS3Config
            {
                ServiceURL = $"https://{_settings.AccountId}.r2.cloudflarestorage.com",
                ForcePathStyle = true
            };

            _s3Client = new AmazonS3Client(credentials, config);
        }

        public async Task<IEnumerable<string>> GetFoldersAsync()
        {
            var folders = new HashSet<string>();

            try
            {
                var request = new ListObjectsV2Request
                {
                    BucketName = _settings.BucketName,
                    Delimiter = "/"
                };

                ListObjectsV2Response response;
                do
                {
                    response = await _s3Client.ListObjectsV2Async(request);

                    foreach (var commonPrefix in response.CommonPrefixes)
                    {
                        var folderName = commonPrefix.TrimEnd('/');
                        folders.Add(folderName);
                    }

                    request.ContinuationToken = response.NextContinuationToken;
                }
                while (response.IsTruncated);

                _logger.LogInformation("Retrieved {Count} folders from R2 bucket {BucketName}", 
                    folders.Count, _settings.BucketName);
            }
            catch (AmazonS3Exception ex)
            {
                _logger.LogError(ex, "Error retrieving folders from R2 bucket {BucketName}", _settings.BucketName);
                throw;
            }

            return folders.OrderBy(f => f);
        }
    }
}
