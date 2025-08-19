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


public async Task<string> UploadDocumentAsync(string tenderNumber, Stream fileStream, string fileName, bool isAwarded = false)
    {
        // Sanitize all folder and file names before calling SharePoint API
        var safeTenderNumber = SanitizeHelper.ToSharePointSafeFolderName(tenderNumber);
        var safeFileName = SanitizeHelper.ToSharePointSafeFolderName(fileName);

        var tenderFolder = await EnsureFolderAsync(_driveId, safeTenderNumber, null);
        var adminDocsFolder = await EnsureFolderAsync(_driveId, "Admin docs", tenderFolder.Id);

        string parentId = adminDocsFolder.Id;
        if (isAwarded)
        {
            // Ensure "Awarded Tender Documents" subfolder exists under Admin docs
            var awardedDocsFolder = await EnsureFolderAsync(_driveId, "Awarded Tender Documents", adminDocsFolder.Id);
            parentId = awardedDocsFolder.Id;
        }

        var uploadedItem = await _graphClient.Drives[_driveId]
            .Items[parentId]
            .ItemWithPath(safeFileName)
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

        public async Task DeleteDocumentAsync(string sharePointPath)
        {
            // Example: 
            // https://providencesoft.sharepoint.com/sites/ProvidenceInternal/SABC%20%20Phase%202/Tender5/Admin%20docs/file.pdf
            // You want: Tender5/Admin docs/file.pdf

            var uri = new Uri(sharePointPath);
            // Get the segments after the site name
            // Find the index of your document library name in the URL (likely "SABC  Phase 2")
            var path = uri.AbsolutePath; // /sites/ProvidenceInternal/SABC%20%20Phase%202/Tender5/Admin%20docs/file.pdf

            // Find the SABC  Phase 2 segment
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var docLibName = "SABC  Phase 2"; // Use your config or table if this can change
            var libraryIdx = Array.FindIndex(segments, s => Uri.UnescapeDataString(s).Equals(docLibName, StringComparison.OrdinalIgnoreCase));

            if (libraryIdx == -1)
                throw new Exception($"SharePoint path does not contain document library '{docLibName}'");

            // Path after the docLibName
            var pathParts = segments.Skip(libraryIdx + 1).Select(Uri.UnescapeDataString);
            var relativePath = string.Join("/", pathParts);

            // Now delete using Graph API using driveId and path
            await _graphClient.Drives[_driveId]
                .Root
                .ItemWithPath(relativePath)
                .DeleteAsync();
        }
    }
}