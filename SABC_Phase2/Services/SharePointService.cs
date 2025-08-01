using Microsoft.Graph;
using Microsoft.Graph.Models;
using Azure.Identity;

namespace SABC_Phase2.Services
{
    public class SharePointService
    {
        private readonly IConfiguration _config;
        private readonly GraphServiceClient _graphClient;
        private readonly string _siteId;
        private readonly string _driveId;

        public SharePointService(IConfiguration config)
        {
            _config = config;
            _siteId = _config["SharePoint:SiteId"];
            _driveId = _config["SharePoint:DriveId"];

            var tenantId = _config["SharePoint:TenantId"];
            var clientId = _config["SharePoint:ClientId"];
            var clientSecret = _config["SharePoint:ClientSecret"];

            var clientSecretCredential = new ClientSecretCredential(
                tenantId, clientId, clientSecret);

            _graphClient = new GraphServiceClient(clientSecretCredential);
        }

        public async Task<string> UploadDocumentAsync(string tenderNumber, Stream fileStream, string fileName)
        {
            var tenderFolder = await EnsureFolderAsync(_driveId, tenderNumber, null);
            var adminDocsFolder = await EnsureFolderAsync(_driveId, "Admin docs", tenderFolder.Id);

            var uploadedItem = await _graphClient.Drives[_driveId]
                .Items[adminDocsFolder.Id]
                .ItemWithPath(fileName)
                .Content
                .PutAsync(fileStream);

            var fileMeta = await _graphClient.Drives[_driveId].Items[uploadedItem.Id].GetAsync();

            return fileMeta?.WebUrl;
        }

        private async Task<DriveItem> EnsureFolderAsync(string driveId, string folderName, string parentId)
        {
            List<DriveItem> children;

            if (parentId == null)
            {
                // Get root and list children
                var root = await _graphClient.Drives[driveId].Root.GetAsync(r =>
                {
                    r.QueryParameters.Expand = new[] { "children" };
                });

                children = root?.Children?.ToList() ?? new List<DriveItem>();
            }
            else
            {
                // Get parent and list children
                var folder = await _graphClient.Drives[driveId].Items[parentId].GetAsync(r =>
                {
                    r.QueryParameters.Expand = new[] { "children" };
                });

                children = folder?.Children?.ToList() ?? new List<DriveItem>();
            }

            // Check if folder exists
            var targetFolder = children.FirstOrDefault(x => x.Folder != null && x.Name == folderName);
            if (targetFolder != null)
                return targetFolder;

            // Folder to create
            var folderToCreate = new DriveItem
            {
                Name = folderName,
                Folder = new Folder(),
                AdditionalData = new Dictionary<string, object>
        {
            { "@microsoft.graph.conflictBehavior", "rename" }
        }
            };

            DriveItem createdFolder;

            if (parentId == null)
            {
                // Create under root using /items/root/children
                createdFolder = await _graphClient.Drives[driveId]
                    .Items["root"]
                    .Children
                    .PostAsync(folderToCreate);
            }
            else
            {
                // Create under specified parent
                createdFolder = await _graphClient.Drives[driveId]
                    .Items[parentId]
                    .Children
                    .PostAsync(folderToCreate);
            }

            return createdFolder;
        }

        /// <summary>
        /// Downloads a file stream from a given URI (can be a blob URL or other accessible location).
        /// </summary>
        /// <param name="filePath">URL or local path of the file.</param>
        /// <returns>Stream of the file's content.</returns>
        public async Task<Stream> GetFileStreamAsync(string filePath)
        {
            // For a web URL (e.g., Azure Blob or SharePoint), use HttpClient
            if (Uri.TryCreate(filePath, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                var httpClient = new HttpClient();
                var response = await httpClient.GetAsync(filePath);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStreamAsync();
            }
            // For a local file path
            if (File.Exists(filePath))
            {
                return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            throw new FileNotFoundException($"File not found or inaccessible: {filePath}");
        }

        public async Task<string> UploadUserApplicationDocumentAsync(string tenderNumber, string companyName, Stream fileStream, string fileName)
        {
            // 1. Ensure Tender folder exists
            var tenderFolder = await EnsureFolderAsync(_driveId, tenderNumber, null);
            // 2. Ensure "Tender Applications" subfolder exists
            var applicationsFolder = await EnsureFolderAsync(_driveId, "Tender Applications", tenderFolder.Id);
            // 3. Ensure {companyName} folder exists
            var companyFolder = await EnsureFolderAsync(_driveId, companyName, applicationsFolder.Id);

            // 4. Upload the file to the company folder
            var uploadedItem = await _graphClient.Drives[_driveId]
                .Items[companyFolder.Id]
                .ItemWithPath(fileName)
                .Content
                .PutAsync(fileStream);

            var fileMeta = await _graphClient.Drives[_driveId].Items[uploadedItem.Id].GetAsync();
            return fileMeta?.WebUrl;
        }

        public async Task DeleteDocumentAsync(string sharePointUrl)
        {
            // Extract the item ID from the SharePoint URL if possible, or use Graph API to locate and delete
            // Example implementation, you may need to adjust parsing for your URL structure

            // If your URLs are like https://.../drives/{driveId}/items/{itemId}
            // You might parse itemId out, or query by path, etc.

            // For demonstration, assuming you know how to get itemId:
            // var itemId = ExtractItemIdFromUrl(sharePointUrl);

            // await _graphClient.Drives[_driveId].Items[itemId].DeleteAsync();

            // If you store the item's unique ID, use that instead of URL.
            // If not, you may need to search by path or name (advanced: use List children API and match by name).

            // This is a stub. You must implement actual logic to delete from SharePoint using Microsoft Graph.
            throw new NotImplementedException("Implement SharePoint document deletion based on your URL/itemId strategy.");
        }
    }
}