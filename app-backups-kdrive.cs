using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.WebUtilities;
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

                // 3) Create file (temporal)
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var fileName  = $"backup_{timestamp}.txt";
                var payload   = Encoding.UTF8.GetBytes($"Backup created at {timestamp} UTC");
                var totalSize = payload.Length;

                // 4) Prepare upload URL (< 1 GB)
                var uploadUrl =
                    $"https://api.infomaniak.com/3/drive/{driveId}/upload" +
                    $"?directory_id={Uri.EscapeDataString(folderId)}" +
                    $"&file_name={Uri.EscapeDataString(fileName)}" +
                    $"&total_size={totalSize}";


                // 4) Create request with file in body
                using var content = new ByteArrayContent(payload);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

                using var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl)
                {
                    Content = content
                };
                uploadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);

                // 5) Send and wait for answer
                var uploadResp    = await _http.SendAsync(uploadReq);
                var uploadContent = await uploadResp.Content.ReadAsStringAsync();
                _logger.LogInformation("Upload response: {Json}", uploadContent);

                if (!uploadResp.IsSuccessStatusCode)
                {
                    _logger.LogError("Error uploading file: {Json}", uploadContent);
                    var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                    await errorResponse.WriteStringAsync("Error uploading backup.");
                    return errorResponse;
                }

                // 6) Ready
                _logger.LogInformation("✅ Backup '{FileName}' uploaded with succeess.", fileName);
                var ok = req.CreateResponse(HttpStatusCode.OK);
                await ok.WriteStringAsync($"Backup '{fileName}' uploaded with succeess.");
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
