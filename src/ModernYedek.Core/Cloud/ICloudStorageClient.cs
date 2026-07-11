using ModernYedek.Core.Models;

namespace ModernYedek.Core.Cloud;

public interface ICloudStorageClient
{
    Task<CloudUploadResult> TestConnectionAsync(string bucketName, CancellationToken cancellationToken = default);
    Task<CloudUploadResult> UploadAsync(CloudUploadRequest request, CancellationToken cancellationToken = default);
}
