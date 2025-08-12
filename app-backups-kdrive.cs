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
            _logger.LogInformation("=== Starting KDrive backup of all containers (async fire-and-forget) ===");

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

            // Works on background so we dont get 502/timeouts
            _ = Task.Run(async () =>
            {
                try
                {
                    string connStr = Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")!;
                    var serviceClient = new BlobServiceClient(connStr);

                    await foreach (BlobContainerItem containerItem in serviceClient.GetBlobContainersAsync())
                    {
                        string containerName = containerItem.Name;
                        var containerClient = serviceClient.GetBlobContainerClient(containerName);

                        string tempDir = Path.Combine(Path.GetTempPath(), $"kdrive_{driveId}_{containerName}");
                        Directory.CreateDirectory(tempDir);

                        await foreach (BlobItem blobItem in containerClient.GetBlobsAsync())
                        {
                            string downloadPath = Path.Combine(tempDir, blobItem.Name);
                            var blobClient = containerClient.GetBlobClient(blobItem.Name);

                            // subdirectories if blobs has '/'
                            var parentDir = Path.GetDirectoryName(downloadPath);
                            if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);

                            await blobClient.DownloadToAsync(downloadPath);
                            _logger.LogInformation("Downloaded {Blob} from {Container}", blobItem.Name, containerName);

                            byte[] fileBytes = await File.ReadAllBytesAsync(downloadPath);

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

                    _logger.LogInformation("✅ All blobs from all containers backed up (background job).");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unexpected error during background backup process.");
                }
            });

            var accepted = req.CreateResponse(HttpStatusCode.Accepted);
            await accepted.WriteStringAsync("Backup started (running in background).");
            return accepted;
        }
    }
}
