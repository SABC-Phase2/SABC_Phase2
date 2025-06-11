using Azure.Storage.Sas;
using Azure.Storage.Blobs;
using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs.Models;

public class BlobStorageService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly string _containerName = "tender-documents";

    public BlobStorageService(string connectionString)
    {
        _blobServiceClient = new BlobServiceClient(connectionString);
    }

    public async Task<string> UploadFileAsync(Stream stream, string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        await containerClient.CreateIfNotExistsAsync();

        var blobClient = containerClient.GetBlobClient(fileName);
        await blobClient.UploadAsync(stream, overwrite: true);

        return blobClient.Uri.ToString();
    }

    public string GetBlobSasUri(string blobName, int expiryMinutes = 30)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        if (!blobClient.CanGenerateSasUri)
            throw new InvalidOperationException("SAS URI cannot be generated. Check permissions.");

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _containerName,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes)
        };

        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        Uri sasUri = blobClient.GenerateSasUri(sasBuilder);
        return sasUri.ToString();
    }

    public async Task DeleteFileAsync(string blobName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blob = containerClient.GetBlobClient(blobName);
        await blob.DeleteIfExistsAsync();
    }

    public async Task CopyBlobAsync(string sourceBlobName, string destinationBlobName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var sourceBlob = containerClient.GetBlobClient(sourceBlobName);
        var destinationBlob = containerClient.GetBlobClient(destinationBlobName);

        // Check if source exists
        if (!await sourceBlob.ExistsAsync())
            throw new FileNotFoundException("Source blob not found");

        await destinationBlob.StartCopyFromUriAsync(sourceBlob.Uri);

        // Wait for the copy to complete
        while (true)
        {
            var properties = await destinationBlob.GetPropertiesAsync();
            if (properties.Value.CopyStatus != CopyStatus.Pending)
            {
                if (properties.Value.CopyStatus == CopyStatus.Failed)
                {
                    throw new Exception($"Blob copy failed: {properties.Value.CopyStatusDescription}");
                }
                break;
            }
            await Task.Delay(100);
        }
    }
}