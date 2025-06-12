using Azure.Storage.Sas;
using Azure.Storage.Blobs;
using System;
using System.IO;
using System.Threading.Tasks;
using Azure.Storage.Blobs.Models;

public class BlobStorageService
{
    /// <summary>
    /// Service responsible for Azure Blob Storage interactions:
    /// - Uploading documents
    /// - Generating time-limited SAS URIs
    /// - Deleting blobs
    /// - Copying blobs asynchronously
    /// </summary>
    private readonly BlobServiceClient _blobServiceClient;
    // Container name used for all tender-related documents.
    private readonly string _containerName = "tender-documents-tashlyn";

    /// <summary>
    /// Constructor that initializes the BlobServiceClient using the provided connection string.
    /// </summary>
    /// <param name="connectionString">Azure Storage connection string (should be kept secure)</param>
    public BlobStorageService(string connectionString)
    {
        _blobServiceClient = new BlobServiceClient(connectionString);
    }

    /// <summary>
    /// Uploads a file stream to Azure Blob Storage under the specified file name.
    /// Creates the container if it does not exist.
    /// </summary>
    /// <param name="stream">File stream to upload</param>
    /// <param name="fileName">Name of the blob to be created</param>
    /// <returns>Public URI to the blob (not SAS)</returns>
    public async Task<string> UploadFileAsync(Stream stream, string fileName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        await containerClient.CreateIfNotExistsAsync();

        var blobClient = containerClient.GetBlobClient(fileName);
        // Upload the file with overwrite enabled
        await blobClient.UploadAsync(stream, overwrite: true);

        return blobClient.Uri.ToString();
    }

    /// <summary>
    /// Generates a time-limited SAS URI for the specified blob, allowing read access.
    /// </summary>
    /// <param name="blobName">Name of the blob</param>
    /// <param name="expiryMinutes">How long the SAS URI is valid for (default: 30 minutes)</param>
    /// <returns>SAS URI string</returns>
    /// <exception cref="InvalidOperationException">Thrown if SAS URI generation is not supported</exception>
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

    /// <summary>
    /// Deletes the specified blob if it exists.
    /// </summary>
    /// <param name="blobName">Name of the blob to delete</param>
    public async Task DeleteFileAsync(string blobName)
    {
        var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
        var blob = containerClient.GetBlobClient(blobName);
        await blob.DeleteIfExistsAsync();
    }


    /// <summary>
    /// Copies a blob from one name to another within the same container.
    /// Waits for the asynchronous copy to complete and throws if it fails.
    /// </summary>
    /// <param name="sourceBlobName">Source blob name</param>
    /// <param name="destinationBlobName">Destination blob name</param>
    /// <exception cref="FileNotFoundException">If source blob doesn't exist</exception>
    /// <exception cref="Exception">If copy operation fails</exception>
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