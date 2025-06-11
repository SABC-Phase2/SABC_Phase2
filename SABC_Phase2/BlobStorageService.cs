using Azure.Storage.Sas;
using Azure.Storage.Blobs;
using System;
using System.IO;
using System.Threading.Tasks;

public class BlobStorageService
{
    private readonly string _connectionString;
    private readonly string _containerName = "tender-documents";

    public BlobStorageService(string connectionString)
    {
        _connectionString = connectionString;
    }

    public async Task<string> UploadFileAsync(Stream stream, string fileName)
    {
        var blobServiceClient = new BlobServiceClient(_connectionString);
        var containerClient = blobServiceClient.GetBlobContainerClient(_containerName);
        await containerClient.CreateIfNotExistsAsync();

        var blobClient = containerClient.GetBlobClient(fileName);
        await blobClient.UploadAsync(stream, overwrite: true);

        return blobClient.Uri.ToString(); // Save this URL in your database
    }

    public string GetBlobSasUri(string blobName, int expiryMinutes = 30)
    {
        var blobServiceClient = new BlobServiceClient(_connectionString);
        var containerClient = blobServiceClient.GetBlobContainerClient(_containerName);
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
        var blobClient = new BlobServiceClient(_connectionString);
        var containerClient = blobClient.GetBlobContainerClient("tender-documents");
        var blob = containerClient.GetBlobClient(blobName);

        await blob.DeleteIfExistsAsync();
    }

}