using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Daktonis.Function
{
    public class AppBackupsKDrive
    {
        private readonly ILogger _logger;
        private readonly HttpClient _http;

        public AppBackupsKDrive(ILogger<AppBackupsKDrive> logger)
        {
            _logger = logger;
            _http   = new HttpClient(new HttpClientHandler { UseProxy = false });
        }

        [Function("app_backups_kdrive")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestData req)
        {
            _logger.LogInformation("=== Starting KDrive backup of all containers ===");

            try
            {
                // 1) Parse required parameters from query string
                var qs = QueryHelpers.ParseQuery(req.Url.Query);

                if (!qs.TryGetValue("KDRIVE_API_TOKEN", out StringValues tokenVals) 
                    || StringValues.IsNullOrEmpty(tokenVals))
                {
                    _logger.LogWarning("Missing KDRIVE_API_TOKEN in URL.");
                    var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badReq.WriteStringAsync("Mandatory parameter `KDRIVE_API_TOKEN`.");
                    return badReq;
                }
                var apiToken = tokenVals.ToString();

                if (!qs.TryGetValue("KDRIVE_ID", out StringValues idVals) 
                    || StringValues.IsNullOrEmpty(idVals))
                {
                    _logger.LogWarning("Missing KDRIVE_ID in URL.");
                    var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badReq.WriteStringAsync("Mandatory parameter `KDRIVE_ID`.");
                    return badReq;
                }
                var driveId = idVals.ToString();

                if (!qs.TryGetValue("KDRIVE_FOLDER_ID", out StringValues folderVals) 
                    || StringValues.IsNullOrEmpty(folderVals))
                {
                    _logger.LogWarning("Missing KDRIVE_FOLDER_ID in URL.");
                    var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badReq.WriteStringAsync("Mandatory parameter `KDRIVE_FOLDER_ID`.");
                    return badReq;
                }
                var folderId = folderVals.ToString();

                // ─────────────────────────────────────────────────────────────────────
                // DOWNLOAD & UPLOAD: iterate all Azure blob containers
                string connStr = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")!;
                var serviceClient = new BlobServiceClient(connStr);

                await foreach (BlobContainerItem containerItem in serviceClient.GetBlobContainersAsync())
                {
                    string containerName = containerItem.Name;
                    var containerClient = serviceClient.GetBlobContainerClient(containerName);

                    // Temporary folder per container
                    string tempDir = Path.Combine(Path.GetTempPath(), $"kdrive_{driveId}_{containerName}");
                    Directory.CreateDirectory(tempDir);

                    // Download & upload each blob
                    await foreach (BlobItem blobItem in containerClient.GetBlobsAsync())
                    {
                        string downloadPath = Path.Combine(tempDir, blobItem.Name);
                        var blobClient = containerClient.GetBlobClient(blobItem.Name);

                        // Download blob to local file
                        await blobClient.DownloadToAsync(downloadPath);
                        _logger.LogInformation("Downloaded {Blob} from {Container}", blobItem.Name, containerName);

                        // Read file bytes
                        byte[] fileBytes = await File.ReadAllBytesAsync(downloadPath);

                        // Prepare KDrive upload URL, preserving container name in path
                        string uploadUrl =
                            $"https://api.infomaniak.com/3/drive/{driveId}/upload" +
                            $"?directory_id={Uri.EscapeDataString(folderId)}" +
                            $"&file_name={Uri.EscapeDataString($"{containerName}/{blobItem.Name}")}" +
                            $"&total_size={fileBytes.Length}";

                        using var content = new ByteArrayContent(fileBytes);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                        using var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl)
                        {
                            Content = content
                        };
                        uploadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

                        var resp = await _http.SendAsync(uploadReq);
                        var body = await resp.Content.ReadAsStringAsync();
                        _logger.LogInformation("Uploaded {Blob}→KDrive: {Status}", blobItem.Name, resp.StatusCode);
                        if (!resp.IsSuccessStatusCode)
                        {
                            _logger.LogError("Failed uploading {Blob}: {Body}", blobItem.Name, body);
                        }
                    }
                }
                // ─────────────────────────────────────────────────────────────────────

                _logger.LogInformation("✅ All blobs from all containers backed up.");
                var ok = req.CreateResponse(HttpStatusCode.OK);
                await ok.WriteStringAsync("All Azure blobs have been backed up to KDrive.");
                return ok;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during backup process.");
                var unexpected = req.CreateResponse(HttpStatusCode.InternalServerError);
                await unexpected.WriteStringAsync("Unexpected error occurred.");
                return unexpected;
            }
        }
    }
}
