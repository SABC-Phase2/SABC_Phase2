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
            var safeFileName = SanitizeHelper.ToSharePointSafeFileName(fileName);

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

        /// <summary>
        /// NEW: Upload document for draft applications
        /// Creates: Tender Number/Tender Applications/Company Name/Draft Docs/documents
        /// </summary>
        public async Task<string> UploadDraftApplicationDocumentAsync(string tenderNumber, string companyName, Stream fileStream, string fileName)
        {
            var safeTenderNumber = SanitizeHelper.ToSharePointSafeFolderName(tenderNumber);
            var safeCompanyName = SanitizeHelper.ToSharePointSafeFolderName(companyName);
            var safeFileName = SanitizeHelper.ToSharePointSafeFileName(fileName);

            // 1. Ensure Tender folder exists
            var tenderFolder = await EnsureFolderAsync(_driveId, safeTenderNumber, null);
            // 2. Ensure "Tender Applications" subfolder exists
            var applicationsFolder = await EnsureFolderAsync(_driveId, "Tender Applications", tenderFolder.Id);
            // 3. Ensure {companyName} folder exists
            var companyFolder = await EnsureFolderAsync(_driveId, safeCompanyName, applicationsFolder.Id);
            // 4. NEW: Ensure "Draft Docs" subfolder exists under company folder
            var draftDocsFolder = await EnsureFolderAsync(_driveId, "Draft Docs", companyFolder.Id);

            // 5. Upload the file to the Draft Docs folder
            var uploadedItem = await _graphClient.Drives[_driveId]
                .Items[draftDocsFolder.Id]
                .ItemWithPath(safeFileName)
                .Content
                .PutAsync(fileStream);

            var fileMeta = await _graphClient.Drives[_driveId].Items[uploadedItem.Id].GetAsync();
            return fileMeta?.WebUrl;
        }

        /// <summary>
        /// UPDATED: Upload document for submitted applications
        /// Creates: Tender Number/Tender Applications/Company Name/Application Documents/documents
        /// </summary>
        public async Task<string> UploadUserApplicationDocumentAsync(string tenderNumber, string companyName, Stream fileStream, string fileName)
        {
            var safeTenderNumber = SanitizeHelper.ToSharePointSafeFolderName(tenderNumber);
            var safeCompanyName = SanitizeHelper.ToSharePointSafeFolderName(companyName);
            var safeFileName = SanitizeHelper.ToSharePointSafeFileName(fileName);

            // 1. Ensure Tender folder exists
            var tenderFolder = await EnsureFolderAsync(_driveId, safeTenderNumber, null);
            // 2. Ensure "Tender Applications" subfolder exists
            var applicationsFolder = await EnsureFolderAsync(_driveId, "Tender Applications", tenderFolder.Id);
            // 3. Ensure {companyName} folder exists
            var companyFolder = await EnsureFolderAsync(_driveId, safeCompanyName, applicationsFolder.Id);
            // 4. NEW: Ensure "Application Documents" subfolder exists under company folder
            var applicationDocsFolder = await EnsureFolderAsync(_driveId, "Application Documents", companyFolder.Id);

            // 5. Upload the file to the Application Documents folder
            var uploadedItem = await _graphClient.Drives[_driveId]
                .Items[applicationDocsFolder.Id]
                .ItemWithPath(safeFileName)
                .Content
                .PutAsync(fileStream);

            var fileMeta = await _graphClient.Drives[_driveId].Items[uploadedItem.Id].GetAsync();
            return fileMeta?.WebUrl;
        }

        /// <summary>
        /// NEW: Move documents from Draft Docs to Application Documents folder
        /// This is called when a draft is submitted as a final application
        /// </summary>
        public async Task<string> MoveDraftDocumentToApplicationAsync(string sharePointPath, string tenderNumber, string companyName)
        {
            var safeTenderNumber = SanitizeHelper.ToSharePointSafeFolderName(tenderNumber);
            var safeCompanyName = SanitizeHelper.ToSharePointSafeFolderName(companyName);

            // 1. Download the file from the current location
            var fileStream = await GetFileStreamFromSharePointPathAsync(sharePointPath);

            // 2. Get the filename from the path
            var fileName = GetFileNameFromSharePointPath(sharePointPath);
            var safeFileName = SanitizeHelper.ToSharePointSafeFileName(fileName);

            // 3. Upload to Application Documents folder
            var newSharePointUrl = await UploadUserApplicationDocumentAsync(tenderNumber, companyName, fileStream, fileName);

            // 4. Delete the file from Draft Docs folder
            await DeleteDocumentAsync(sharePointPath);

            return newSharePointUrl;
        }

        /// <summary>
        /// NEW: Helper method to download file content from SharePoint path
        /// </summary>
        private async Task<Stream> GetFileStreamFromSharePointPathAsync(string sharePointPath)
        {
            var uri = new Uri(sharePointPath);
            var path = uri.AbsolutePath;

            // Find the SABC Phase 2 segment
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var docLibName = "SABC  Phase 2"; // Use your config or table if this can change
            var libraryIdx = Array.FindIndex(segments, s => Uri.UnescapeDataString(s).Equals(docLibName, StringComparison.OrdinalIgnoreCase));

            if (libraryIdx == -1)
                throw new Exception($"SharePoint path does not contain document library '{docLibName}'");

            // Path after the docLibName
            var pathParts = segments.Skip(libraryIdx + 1).Select(Uri.UnescapeDataString);
            var relativePath = string.Join("/", pathParts);

            // Get the file content using Graph API
            var fileContent = await _graphClient.Drives[_driveId]
                .Root
                .ItemWithPath(relativePath)
                .Content
                .GetAsync();

            return fileContent;
        }

        /// <summary>
        /// NEW: Helper method to extract filename from SharePoint path
        /// </summary>
        private string GetFileNameFromSharePointPath(string sharePointPath)
        {
            var uri = new Uri(sharePointPath);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return Uri.UnescapeDataString(segments.Last());
        }

        /// <summary>
        /// NEW: Clean up Draft Docs folder when all documents are moved or deleted
        /// </summary>
        public async Task DeleteDraftDocsFolderIfEmptyAsync(string tenderNumber, string companyName)
        {
            try
            {
                var safeTenderNumber = SanitizeHelper.ToSharePointSafeFolderName(tenderNumber);
                var safeCompanyName = SanitizeHelper.ToSharePointSafeFolderName(companyName);

                // Navigate to the Draft Docs folder
                var tenderFolder = await GetFolderByNameAsync(safeTenderNumber, null);
                if (tenderFolder == null) return;

                var applicationsFolder = await GetFolderByNameAsync("Tender Applications", tenderFolder.Id);
                if (applicationsFolder == null) return;

                var companyFolder = await GetFolderByNameAsync(safeCompanyName, applicationsFolder.Id);
                if (companyFolder == null) return;

                var draftDocsFolder = await GetFolderByNameAsync("Draft Docs", companyFolder.Id);
                if (draftDocsFolder == null) return;

                // Check if folder is empty
                var folderContents = await _graphClient.Drives[_driveId]
                    .Items[draftDocsFolder.Id]
                    .Children
                    .GetAsync();

                if (folderContents.Value == null || !folderContents.Value.Any())
                {
                    // Delete empty Draft Docs folder
                    await _graphClient.Drives[_driveId]
                        .Items[draftDocsFolder.Id]
                        .DeleteAsync();
                }
            }
            catch (Exception ex)
            {
                // Log error but don't throw to avoid breaking the submission process
                Console.WriteLine($"Error deleting empty Draft Docs folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Helper method to get folder by name
        /// </summary>
        private async Task<DriveItem> GetFolderByNameAsync(string folderName, string parentId)
        {
            List<DriveItem> children;

            if (parentId == null)
            {
                var root = await _graphClient.Drives[_driveId].Root.GetAsync(r =>
                {
                    r.QueryParameters.Expand = new[] { "children" };
                });
                children = root?.Children?.ToList() ?? new List<DriveItem>();
            }
            else
            {
                var folder = await _graphClient.Drives[_driveId].Items[parentId].GetAsync(r =>
                {
                    r.QueryParameters.Expand = new[] { "children" };
                });
                children = folder?.Children?.ToList() ?? new List<DriveItem>();
            }

            return children.FirstOrDefault(x => x.Folder != null && x.Name == folderName);
        }

        public async Task DeleteDocumentAsync(string sharePointPath)
        {
            var uri = new Uri(sharePointPath);
            var path = uri.AbsolutePath;

            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var docLibName = "SABC  Phase 2";
            var libraryIdx = Array.FindIndex(segments, s => Uri.UnescapeDataString(s).Equals(docLibName, StringComparison.OrdinalIgnoreCase));

            if (libraryIdx == -1)
                throw new Exception($"SharePoint path does not contain document library '{docLibName}'");

            var pathParts = segments.Skip(libraryIdx + 1).Select(Uri.UnescapeDataString);
            var relativePath = string.Join("/", pathParts);

            await _graphClient.Drives[_driveId]
                .Root
                .ItemWithPath(relativePath)
                .DeleteAsync();
        }

        public async Task RenameTenderFolderAsync(string oldTenderNumber, string newTenderNumber)
        {
            var oldSafe = SanitizeHelper.ToSharePointSafeFolderName(oldTenderNumber);
            var newSafe = SanitizeHelper.ToSharePointSafeFolderName(newTenderNumber);

            var rootChildren = await _graphClient.Drives[_driveId]
                .Items["root"]
                .Children
                .GetAsync();

            var folder = rootChildren.Value.FirstOrDefault(x => x.Name == oldSafe && x.Folder != null);

            if (folder == null)
                throw new Exception($"Tender folder '{oldTenderNumber}' not found in SharePoint.");

            var update = new DriveItem
            {
                Name = newSafe
            };
            await _graphClient.Drives[_driveId].Items[folder.Id].PatchAsync(update);
        }

        public async Task DeleteTenderFolderAsync(string tenderNumber)
        {
            try
            {
                var safeTenderNumber = SanitizeHelper.ToSharePointSafeFolderName(tenderNumber);

                var rootChildren = await _graphClient.Drives[_driveId]
                    .Items["root"]
                    .Children
                    .GetAsync();

                var tenderFolder = rootChildren.Value.FirstOrDefault(x =>
                    x.Name == safeTenderNumber && x.Folder != null);

                if (tenderFolder != null)
                {
                    await _graphClient.Drives[_driveId]
                        .Items[tenderFolder.Id]
                        .DeleteAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting tender folder from SharePoint: {ex.Message}");
            }
        }

        public async Task DeleteAdminDocsFolder(string tenderNumber)
        {
            try
            {
                var safeTenderNumber = SanitizeHelper.ToSharePointSafeFolderName(tenderNumber);

                var rootChildren = await _graphClient.Drives[_driveId]
                    .Items["root"]
                    .Children
                    .GetAsync();

                var tenderFolder = rootChildren.Value.FirstOrDefault(x =>
                    x.Name == safeTenderNumber && x.Folder != null);

                if (tenderFolder != null)
                {
                    var tenderChildren = await _graphClient.Drives[_driveId]
                        .Items[tenderFolder.Id]
                        .Children
                        .GetAsync();

                    var adminDocsFolder = tenderChildren.Value.FirstOrDefault(x =>
                        x.Name == "Admin docs" && x.Folder != null);

                    if (adminDocsFolder != null)
                    {
                        await _graphClient.Drives[_driveId]
                            .Items[adminDocsFolder.Id]
                            .DeleteAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error deleting Admin docs folder from SharePoint: {ex.Message}");
            }
        }
    }
}