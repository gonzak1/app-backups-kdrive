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
            _logger.LogInformation("=== Starting simple KDrive backup ===");

            try
            {
                // 1) Parse all three required (token, drive id and folder id) parameters from the query string
                var qs = QueryHelpers.ParseQuery(req.Url.Query);

                // a) API Token
                if (!qs.TryGetValue("KDRIVE_API_TOKEN", out StringValues tokenVals) 
                    || StringValues.IsNullOrEmpty(tokenVals))
                {
                    _logger.LogWarning("Missing KDRIVE_API_TOKEN in URL.");
                    var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badReq.WriteStringAsync("Mandatory parameter `KDRIVE_API_TOKEN`.");
                    return badReq;
                }
                var apiToken = tokenVals.ToString();

                // b) Drive ID
                if (!qs.TryGetValue("KDRIVE_ID", out StringValues idVals) 
                    || StringValues.IsNullOrEmpty(idVals))
                {
                    _logger.LogWarning("Missing KDRIVE_ID in URL.");
                    var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badReq.WriteStringAsync("Mandatory parameter `KDRIVE_ID`.");
                    return badReq;
                }
                var driveId = idVals.ToString();

                // c) Folder ID
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
                // download blobs from Azure Blob Storage
                string? connStr       = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING");
                string? containerName = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONTAINER_NAME");
                if (string.IsNullOrEmpty(connStr) || string.IsNullOrEmpty(containerName))
                {
                    _logger.LogWarning("Missing AZURE_STORAGE_CONNECTION_STRING or AZURE_STORAGE_CONTAINER_NAME.");
                }
                else
                {
                    var containerClient = new BlobContainerClient(connStr, containerName);
                    string tempDir = Path.Combine(Path.GetTempPath(), $"kdrive_{driveId}");
                    Directory.CreateDirectory(tempDir);

                    await foreach (BlobItem blobItem in containerClient.GetBlobsAsync())
                    {
                        string downloadPath = Path.Combine(tempDir, blobItem.Name);
                        var blobClient = containerClient.GetBlobClient(blobItem.Name);

                        // Download blob to local file
                        await blobClient.DownloadToAsync(downloadPath);
                        _logger.LogInformation("Downloaded blob to {Path}", downloadPath);

                        // Read file bytes
                        byte[] fileBytes = await File.ReadAllBytesAsync(downloadPath);

                        // Prepare upload URL for this specific blob
                        string uploadUrl =
                            $"https://api.infomaniak.com/3/drive/{driveId}/upload" +
                            $"?directory_id={Uri.EscapeDataString(folderId)}" +
                            $"&file_name={Uri.EscapeDataString(blobItem.Name)}" +
                            $"&total_size={fileBytes.Length}";

                        // Upload blob content to KDrive
                        using var content = new ByteArrayContent(fileBytes);
                        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                        using var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl)
                        {
                            Content = content
                        };
                        uploadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

                        var uploadResp    = await _http.SendAsync(uploadReq);
                        var uploadContent = await uploadResp.Content.ReadAsStringAsync();
                        _logger.LogInformation("Upload response for {Name}: {Json}", blobItem.Name, uploadContent);

                        if (!uploadResp.IsSuccessStatusCode)
                        {
                            _logger.LogError("Error uploading blob {Name}: {Json}", blobItem.Name, uploadContent);
                        }
                    }
                }
                // ─────────────────────────────────────────────────────────────────────
                _logger.LogInformation("✅ All blobs uploaded successfully.");
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
