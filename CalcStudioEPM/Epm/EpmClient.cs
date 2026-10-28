using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Newtonsoft.Json;
using CalcStudioEPM.Config;

namespace CalcStudioEPM.Epm
{
    /// <summary>
    /// HTTP client wrapper for Oracle EPM Cloud REST APIs.
    /// Handles Basic authentication, JSON serialization, and file downloads.
    /// </summary>
    public class EpmClient : IDisposable
    {
        // ------------------------------------------------------------------ //
        //  API version constants — update here if Oracle releases a new version
        // ------------------------------------------------------------------ //

        private const string PlanningApiVersion  = "v3";
        private const string InteropApiV2Version = "v2";
        private const string InteropApiV1Version = "v1";

        // ------------------------------------------------------------------ //
        //  Timeout / poll constants
        // ------------------------------------------------------------------ //

        /// <summary>Timeout for standard API (non-download) requests.</summary>
        private static readonly TimeSpan ApiRequestTimeout = TimeSpan.FromSeconds(30);

        /// <summary>Timeout for file-download requests (ZIP can be large).</summary>
        private static readonly TimeSpan DownloadRequestTimeout = TimeSpan.FromSeconds(120);

        /// <summary>Interval between job-status poll attempts.</summary>
        private const int PollIntervalMs = 3_000;

        private readonly ConnectionConfig _config;
        private readonly HttpClient _apiClient;
        private readonly HttpClient _downloadClient;
        private string _basicAuthHeader; // "Basic <base64>" — reused by HttpWebRequest
        private readonly Uri _serviceUri;   // https base every request must stay on
        private bool _disposed;

        // Derived URL bases
        private string PlanningBase =>
            $"{_config.ServiceBaseUrl.TrimEnd('/')}/HyperionPlanning/rest/{PlanningApiVersion}/applications/{Uri.EscapeDataString(_config.Application ?? "")}";

        private string InteropBase =>
            $"{_config.ServiceBaseUrl.TrimEnd('/')}/interop/rest/{InteropApiV1Version}";

        public EpmClient(ConnectionConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));

            // Checked here, not only in the connection dialog: a connection can also come from
            // an imported file or a hand-edited connections.json, and the password goes with
            // every request.
            if (!Uri.TryCreate(_config.ServiceBaseUrl?.Trim(), UriKind.Absolute, out _serviceUri) ||
                _serviceUri.Scheme != Uri.UriSchemeHttps)
                throw new EpmException(
                    $"The service URL of the connection '{_config.Name}' must be an https:// address " +
                    $"(it is '{_config.ServiceBaseUrl}'). Correct it in Configure EPM Connection.");

            // NOTE: TLS protocol is set once at plugin load time in Plugin.cs:SetInfo()
            // (ServicePointManager.SecurityProtocol is a process-wide setting and must
            // not be set inside a per-request or per-client constructor).

            // Saved on the connection, or entered for this session (Forms.PasswordForm.Ensure
            // asks before any EPM action when neither is available)
            string plainPassword = Credentials.GetPassword(_config)
                ?? throw new EpmException(
                    $"No password is available for the connection '{_config.Name}'. " +
                    "Run the action again and enter it when asked.");
            string credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_config.Username}:{plainPassword}"));
            _basicAuthHeader = "Basic " + credentials;

            _apiClient = new HttpClient(new SameServiceHandler(_serviceUri))
            {
                Timeout = ApiRequestTimeout
            };
            _apiClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
            _apiClient.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            _downloadClient = new HttpClient(new SameServiceHandler(_serviceUri))
            {
                Timeout = DownloadRequestTimeout
            };
            _downloadClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", credentials);
        }

        // ------------------------------------------------------------------ //
        //  Generic GET
        // ------------------------------------------------------------------ //

        /// <summary>GET returning the raw JSON, with the long download timeout — for big
        /// responses such as a dimension's whole hierarchy.</summary>
        public async Task<string> GetLargeJsonAsync(string relativeUrl)
        {
            string url = BuildUrl(relativeUrl);
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    HttpResponseMessage response = await _downloadClient.SendAsync(request).ConfigureAwait(false);
                    await EnsureSuccessAsync(response).ConfigureAwait(false);
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (EpmException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new EpmException($"HTTP error calling GET {url}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new EpmException($"Request timed out calling GET {url}.", ex);
            }
        }

        /// <summary>Issues a GET request and deserializes the JSON response to T.</summary>
        public async Task<T> GetAsync<T>(string relativeUrl)
        {
            string url = BuildUrl(relativeUrl);
            try
            {
                HttpResponseMessage response = await _apiClient.GetAsync(url).ConfigureAwait(false);
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch (EpmException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new EpmException($"HTTP error calling GET {url}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new EpmException($"Request timed out calling GET {url}.", ex);
            }
        }

        // ------------------------------------------------------------------ //
        //  Generic POST
        // ------------------------------------------------------------------ //

        /// <summary>Issues a POST request with a JSON body and deserializes the response.</summary>
        public async Task<TRes> PostAsync<TReq, TRes>(string relativeUrl, TReq body)
        {
            string url = BuildUrl(relativeUrl);
            try
            {
                string requestJson = JsonConvert.SerializeObject(body);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await _apiClient.PostAsync(url, content).ConfigureAwait(false);
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<TRes>(json);
            }
            catch (EpmException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new EpmException($"HTTP error calling POST {url}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new EpmException($"Request timed out calling POST {url}.", ex);
            }
        }

        // ------------------------------------------------------------------ //
        //  File download
        // ------------------------------------------------------------------ //

        /// <summary>Streams the response body of a GET request directly to a file on disk.</summary>
        public async Task DownloadFileAsync(string url, string destPath)
        {
            try
            {
                using (HttpResponseMessage response = await _downloadClient.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    await EnsureSuccessAsync(response).ConfigureAwait(false);
                    string dir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    using (Stream responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (FileStream fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await responseStream.CopyToAsync(fileStream).ConfigureAwait(false);
                    }
                }
            }
            catch (EpmException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new EpmException($"HTTP error downloading file from {url}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new EpmException($"Download timed out for {url}.", ex);
            }
        }

        // ------------------------------------------------------------------ //
        //  Convenience helpers
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Tests connectivity by probing the configured application directly.
        /// Uses <c>GET /HyperionPlanning/rest/v3/applications/{app}</c> which requires
        /// only that the user has read access to their own application (much less
        /// restrictive than listing all applications).
        /// Throws <see cref="EpmException"/> on any error.
        /// </summary>
        public async Task TestConnectionAsync()
        {
            string url = PlanningBase;
            try
            {
                HttpResponseMessage response = await _apiClient.GetAsync(url).ConfigureAwait(false);
                await EnsureSuccessAsync(response).ConfigureAwait(false);
            }
            catch (EpmException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new EpmException("Connection test failed: " + ex.Message, ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new EpmException("Connection test timed out (30 s). Check URL and network.", ex);
            }
        }

        /// <summary>Delay between Planning job status polls.</summary>
        public static TimeSpan JobPollInterval => TimeSpan.FromMilliseconds(PollIntervalMs);

        /// <summary>
        /// Reads a Planning job's status once — GET …/applications/{app}/jobs/{jobId}.
        /// Returns the parsed response plus the raw JSON (for the details log).
        /// </summary>
        public async Task<(PlanningJobResponse Job, string Json)> GetJobStatusAsync(long jobId)
        {
            Newtonsoft.Json.Linq.JObject raw =
                await GetAsync<Newtonsoft.Json.Linq.JObject>($"jobs/{jobId}").ConfigureAwait(false);
            if (raw == null)
                throw new EpmException($"Received an empty status response for job {jobId}.");
            return (raw.ToObject<PlanningJobResponse>(), raw.ToString(Formatting.None));
        }

        // ------------------------------------------------------------------ //
        //  URL builder
        // ------------------------------------------------------------------ //

        private string BuildUrl(string relativeUrl)
        {
            // If the caller already provided a full URL, use it as-is
            if (relativeUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                relativeUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return relativeUrl;
            }

            // Routes starting with "/interop" are on the interop base
            if (relativeUrl.StartsWith("/interop", StringComparison.OrdinalIgnoreCase))
            {
                return _config.ServiceBaseUrl.TrimEnd('/') + relativeUrl;
            }

            return PlanningBase.TrimEnd('/') + "/" + relativeUrl.TrimStart('/');
        }

        // ------------------------------------------------------------------ //
        //  Error handling
        // ------------------------------------------------------------------ //

        private async Task EnsureSuccessAsync(HttpResponseMessage response)
        {
            if (response.IsSuccessStatusCode)
                return;

            // Wrong password: forget a session-only password so the next action asks again
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                Credentials.ForgetSession(_config);

            string body = string.Empty;
            try { body = await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch { /* ignore read errors */ }

            string detail = string.IsNullOrWhiteSpace(body)
                ? response.ReasonPhrase
                : body;

            throw new EpmException(
                $"EPM API returned HTTP {(int)response.StatusCode}: {detail}",
                (int)response.StatusCode);
        }

        // ------------------------------------------------------------------ //
        //  Interop v2 Migration API
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Submits an asynchronous export job for all artifacts in the given category.
        /// POST /interop/rest/v2/migration/categories/artifacts/export
        /// <paramref name="snapshotName"/> becomes the file name of the resulting ZIP.
        /// </summary>
        public async Task<MigrationExportResponse> ExportMigrationCategoryAsync(
            string snapshotName, string categoryName)
        {
            var body = new
            {
                snapshotName = snapshotName,
                categories   = new[] { new { categoryName = categoryName } }
            };
            try
            {
                return await PostAsync<object, MigrationExportResponse>(
                    $"/interop/rest/{InteropApiV2Version}/migration/categories/artifacts/export", body)
                    .ConfigureAwait(false);
            }
            catch (EpmException) { throw; }
            catch (Exception ex)
            {
                throw new EpmException("Failed to submit migration export: " + ex.Message, ex);
            }
        }

        /// <summary>
        /// Submits an export of only some artifacts of a category (an incremental snapshot):
        /// POST /interop/rest/v2/migration/categories/artifacts/export with
        /// <c>selectedArtifacts</c> — artifact paths as the category's artifact list shows them,
        /// a folder's ending in "/". Poll the response's Job Status link like a category export.
        /// </summary>
        public async Task<MigrationExportResponse> ExportMigrationArtifactsAsync(
            string snapshotName, string categoryName, IEnumerable<string> selectedArtifacts)
        {
            var body = new
            {
                snapshotName = snapshotName,
                categories   = new[] { new { categoryName = categoryName, selectedArtifacts = selectedArtifacts.ToArray() } }
            };
            try
            {
                return await PostAsync<object, MigrationExportResponse>(
                    $"/interop/rest/{InteropApiV2Version}/migration/categories/artifacts/export", body)
                    .ConfigureAwait(false);
            }
            catch (EpmException) { throw; }
            catch (Exception ex)
            {
                throw new EpmException("Failed to submit migration export: " + ex.Message, ex);
            }
        }

        /// <summary>POST with a JSON body returning the raw JSON, with the long download timeout —
        /// for big responses such as a migration category's artifact tree.</summary>
        public async Task<string> PostLargeJsonAsync(string relativeUrl, object body)
        {
            string url = BuildUrl(relativeUrl);
            try
            {
                var content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content })
                {
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    HttpResponseMessage response = await _downloadClient.SendAsync(request).ConfigureAwait(false);
                    await EnsureSuccessAsync(response).ConfigureAwait(false);
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (EpmException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new EpmException($"HTTP error calling POST {url}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new EpmException($"Request timed out calling POST {url}.", ex);
            }
        }

        /// <summary>
        /// Step 1 of 2 for downloading a snapshot.
        /// POST /interop/rest/v2/files/download  body: { "fileName": "&lt;snapshotName&gt;.zip" }
        /// The server queues an async download job and returns status=-1 plus a "Job Status" link.
        /// Poll that link with <see cref="PollInteropJobByUrlAsync"/> until status==0,
        /// then call <see cref="FetchSnapshotBinaryAsync"/> to retrieve the file.
        /// </summary>
        public async Task<MigrationExportResponse> InitiateSnapshotDownloadAsync(string snapshotName)
        {
            string url = $"{_config.ServiceBaseUrl.TrimEnd('/')}/interop/rest/{InteropApiV2Version}/files/download";
            string requestJson = JsonConvert.SerializeObject(new { fileName = snapshotName + ".zip" });
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            try
            {
                HttpResponseMessage response =
                    await _apiClient.PostAsync(url, content).ConfigureAwait(false);
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<MigrationExportResponse>(json);
            }
            catch (EpmException) { throw; }
            catch (Exception ex)
            {
                throw new EpmException(
                    $"Failed to initiate snapshot download for '{snapshotName}': {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Downloads a file via HTTP GET and streams the response body directly to disk.
        /// Used after a download job completes to retrieve the actual ZIP content
        /// from whatever URL the completed job status response provides.
        /// </summary>
        public async Task GetFileAsync(string url, string destPath)
        {
            try
            {
                using (HttpResponseMessage response = await _downloadClient.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    await EnsureSuccessAsync(response).ConfigureAwait(false);

                    string dir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    using (Stream rs = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (FileStream fs = new FileStream(
                        destPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await rs.CopyToAsync(fs).ConfigureAwait(false);
                    }
                }
            }
            catch (EpmException) { throw; }
            catch (HttpRequestException ex)
            {
                throw new EpmException($"HTTP error downloading from {url}: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new EpmException($"Download timed out from {url}.", ex);
            }
        }

        /// <summary>
        /// Polls an interop v2 job using the exact status URL returned by the export response
        /// (the "Job Status" link href, e.g. …/interop/rest/v2/status/migration/44).
        /// Polls every 3 seconds.  status == 0 → success; status != -1 → error.
        /// </summary>
        public async Task<InteropJobStatusResponse> PollInteropJobByUrlAsync(
            string statusUrl, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;

            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(PollIntervalMs).ConfigureAwait(false);

                InteropJobStatusResponse status;
                try
                {
                    HttpResponseMessage response =
                        await _apiClient.GetAsync(statusUrl).ConfigureAwait(false);
                    await EnsureSuccessAsync(response).ConfigureAwait(false);
                    string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    status = JsonConvert.DeserializeObject<InteropJobStatusResponse>(json);
                }
                catch (EpmException) { throw; }
                catch (Exception ex)
                {
                    throw new EpmException(
                        "Error polling interop job status: " + ex.Message, ex);
                }

                if (status == null)
                    throw new EpmException(
                        "Received empty status response while polling interop job.");

                // -1 = still running, 0 = completed, anything else = error
                if (status.Status == 0)
                    return status;

                if (status.Status != -1)
                    throw new EpmException(
                        $"Interop job ended with status {status.Status}: " +
                        (status.Details ?? "(no details)"));
            }

            throw new EpmException(
                $"Interop job did not complete within {timeout.TotalMinutes:F0} minutes.");
        }

        // ------------------------------------------------------------------ //
        //  Deploy helpers — upload, delete, import
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Uploads a ZIP snapshot to EPM Cloud using the 4-step v2 protocol:
        ///   1. POST  /interop/rest/v2/files/upload            → create session, receive jobId
        ///   2. PATCH /interop/rest/v2/files/upload/{jobId}   → stream the ZIP bytes
        ///   3. POST  /interop/rest/v2/files/upload/{jobId}/complete → trigger extraction
        ///   4. GET   /interop/rest/v2/status/jobs/{jobId}    → poll until extracted
        /// </summary>
        public async Task UploadSnapshotAsync(
            string snapshotName,
            byte[] zipBytes,
            IProgress<string> progress = null)
        {
            string zipFileName = snapshotName + ".zip";
            string uploadBase  =
                $"{_config.ServiceBaseUrl.TrimEnd('/')}/interop/rest/{InteropApiV2Version}/files/upload";

            // ── Step 1: Create upload session ──────────────────────────────
            progress?.Report("Creating upload session…");

            UploadSessionResponse session;
            string sessionRawJson = string.Empty;
            try
            {
                string body = JsonConvert.SerializeObject(
                    new { fileName = zipFileName, fileSize = zipBytes.LongLength });

                HttpResponseMessage r = await _apiClient
                    .PostAsync(uploadBase, new StringContent(body, Encoding.UTF8, "application/json"))
                    .ConfigureAwait(false);
                await EnsureSuccessAsync(r).ConfigureAwait(false);

                sessionRawJson = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                session = JsonConvert.DeserializeObject<UploadSessionResponse>(sessionRawJson);

                // Oracle returns HTTP 200 even for business errors; check the JSON status.
                // status 0 or -1 = OK; status 1 = error (e.g. "File already exists").
                if (session != null && session.Status == 1)
                {
                    string detail = session.Details ?? "unknown error";
                    throw new EpmException(
                        $"EPM rejected upload session creation: {detail}\n" +
                        "The previous deploy may have left a partial upload. " +
                        "Try again — the timestamp-based name ensures a fresh session.");
                }
            }
            catch (EpmException) { throw; }
            catch (Exception ex)
            {
                throw new EpmException("Failed to create upload session: " + ex.Message, ex);
            }

            // Extract the jobId — may be a direct field or embedded in the PATCH link URL.
            // Also try parsing the raw JSON for alternate field names Oracle might use.
            long jobId = session?.JobId ?? 0;
            if (jobId <= 0)
            {
                // Try dynamic parse for alternate field names (id, uploadId, jobID, etc.)
                try
                {
                    var raw = JsonConvert.DeserializeObject<Dictionary<string, object>>(sessionRawJson);
                    foreach (string key in new[] { "id", "uploadId", "jobID", "job_id", "sessionId" })
                    {
                        if (raw != null && raw.TryGetValue(key, out object val) &&
                            long.TryParse(val?.ToString(), out long alt) && alt > 0)
                        {
                            jobId = alt;
                            break;
                        }
                    }
                }
                catch { /* ignore parse errors */ }
            }
            if (jobId <= 0)
            {
                // Last resort: extract a numeric ID from any PATCH link URL
                string patchHref = session?.Links?
                    .FirstOrDefault(l => string.Equals(l.Action, "PATCH",
                        StringComparison.OrdinalIgnoreCase))?.Href ?? string.Empty;

                string tail = patchHref.TrimEnd('/');
                int slash = tail.LastIndexOf('/');
                if (slash >= 0) long.TryParse(tail.Substring(slash + 1), out jobId);
            }
            if (jobId <= 0)
                throw new EpmException(
                    "Upload session did not return a job ID.\n" +
                    "Raw session response: " + sessionRawJson);

            // ── Step 2: Upload content via PATCH ────────────────────────────
            // HttpClient (in all configurations we've tried) triggers Oracle's
            // "bytes lost during upload" 409.  HttpWebRequest lets us set
            // SendChunked = false and ContentLength explicitly, which produces
            // a clean Content-Length request body that Oracle accepts.
            progress?.Report(
                $"Uploading {zipBytes.Length / 1024} KB (session {jobId})…");

            try
            {
                string patchUrl = session?.Links?
                    .FirstOrDefault(l => string.Equals(l.Action, "PATCH",
                        StringComparison.OrdinalIgnoreCase))?.Href
                    ?? $"{uploadBase}/{jobId}";
                EnsureSameService(_serviceUri, new Uri(patchUrl));   // the link comes from EPM's response

                var webReq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(patchUrl);
                webReq.Method          = "PATCH";
                webReq.ContentType     = "application/octet-stream";
                webReq.ContentLength   = zipBytes.LongLength;
                webReq.SendChunked     = false;
                webReq.Timeout         = (int)DownloadRequestTimeout.TotalMilliseconds;
                webReq.Headers["Authorization"] = _basicAuthHeader;

                // Oracle EPM Cloud v2 uses a proprietary Chunk-Range header holding just
                // "{start}-{end}" — no "bytes " prefix, no total; end = fileSize - 1.
                // (Upload Application Snapshot (v2): "Chunk-Range: 0-2468889".)
                // A "bytes 0-N" value is mis-parsed and the server answers
                // 409 "bytes lost during upload".
                string chunkRange = $"0-{zipBytes.LongLength - 1}";
                webReq.Headers["Chunk-Range"] = chunkRange;
                progress?.Report($"  PATCH {patchUrl}  Chunk-Range: {chunkRange}");

                // CRITICAL: disable Expect: 100-continue.
                // .NET's default behaviour sends PATCH headers first and waits for the
                // server to reply "100 Continue" before sending the body.  Oracle EPM
                // evaluates the headers and immediately returns 409 ("bytes lost during
                // upload") without ever signalling 100-continue, so the body never
                // arrives.  Setting this to false makes .NET send headers + body together
                // in one shot — exactly what curl does by default.
                webReq.ServicePoint.Expect100Continue = false;

                // Write the ZIP bytes
                using (var reqStream = await Task.Factory.FromAsync(
                    webReq.BeginGetRequestStream, webReq.EndGetRequestStream, null)
                    .ConfigureAwait(false))
                {
                    await reqStream.WriteAsync(zipBytes, 0, zipBytes.Length)
                        .ConfigureAwait(false);
                }

                // Read the response
                try
                {
                    using (var resp = (System.Net.HttpWebResponse)await Task.Factory.FromAsync(
                        webReq.BeginGetResponse, webReq.EndGetResponse, null)
                        .ConfigureAwait(false))
                    using (var s = new System.IO.StreamReader(resp.GetResponseStream()))
                    {
                        // HTTP 2xx can still carry a business error in the JSON status.
                        string body = s.ReadToEnd();
                        InteropJobStatusResponse patchResult = null;
                        try { patchResult = JsonConvert.DeserializeObject<InteropJobStatusResponse>(body); }
                        catch { /* non-JSON body — treat the 2xx as success */ }

                        progress?.Report(
                            $"  → HTTP {(int)resp.StatusCode}, status={patchResult?.Status.ToString() ?? "?"}");
                        if (patchResult != null && patchResult.Status != 0)
                            throw new EpmException(
                                $"EPM rejected the uploaded content (status {patchResult.Status}): " +
                                (patchResult.Details ?? body));
                    }
                }
                catch (System.Net.WebException ex)
                    when (ex.Response is System.Net.HttpWebResponse errResp)
                {
                    string body;
                    using (var s = new System.IO.StreamReader(errResp.GetResponseStream()))
                        body = s.ReadToEnd();
                    throw new EpmException(
                        $"EPM API returned HTTP {(int)errResp.StatusCode}: {body}");
                }
            }
            catch (EpmException) { throw; }
            catch (Exception ex)
            {
                throw new EpmException("Failed to upload file content: " + ex.Message, ex);
            }

            // ── Step 3: Complete — trigger extraction from the ZIP ──────────
            progress?.Report("Triggering extraction…");

            UploadSessionResponse completeResp;
            try
            {
                HttpResponseMessage r = await _apiClient
                    .PostAsync($"{uploadBase}/{jobId}/complete",
                        new StringContent("{}", Encoding.UTF8, "application/json"))
                    .ConfigureAwait(false);
                await EnsureSuccessAsync(r).ConfigureAwait(false);

                string json = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                completeResp = JsonConvert.DeserializeObject<UploadSessionResponse>(json);
            }
            catch (EpmException) { throw; }
            catch (Exception ex)
            {
                throw new EpmException("Failed to complete upload: " + ex.Message, ex);
            }

            // ── Step 4: Poll extraction status ──────────────────────────────
            long extractJobId = completeResp?.JobId > 0 ? completeResp.JobId : jobId;
            string statusUrl = completeResp?.JobStatusUrl
                ?? $"{_config.ServiceBaseUrl.TrimEnd('/')}/interop/rest/{InteropApiV2Version}/status/jobs/{extractJobId}";

            progress?.Report($"Waiting for extraction (job {extractJobId})…");

            await PollInteropJobByUrlAsync(statusUrl, TimeSpan.FromMinutes(5))
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Deletes a snapshot (or file) from EPM Cloud using the Delete Files v2 REST API:
        ///   DELETE /interop/rest/v2/files/delete   body: { "fileName": "{name}" }
        /// (https://docs.oracle.com/en/cloud/saas/enterprise-performance-management-common/prest/delete_files_v2.html)
        ///
        /// Snapshots are addressed by name without extension — the same thing
        /// "epmautomate deleteFile {name}" does. (Asking for "{name}.zip" instead is
        /// rejected with status 4 "Invalid file", so there is no point trying it.)
        ///
        /// Returns <c>true</c> when EPM confirms the delete (status 0), <c>false</c> when
        /// it doesn't exist (status 8 "Resource does not exist"), and throws
        /// <see cref="EpmException"/> with EPM's own "details" text on any other failure.
        /// </summary>
        public async Task<bool> DeleteSnapshotAsync(string snapshotName, IProgress<string> progress = null)
        {
            string fileName = snapshotName;
            string url  = $"{_config.ServiceBaseUrl.TrimEnd('/')}/interop/rest/{InteropApiV2Version}/files/delete";
            string body = JsonConvert.SerializeObject(new { fileName });

            HttpResponseMessage response;
            string json;
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Delete, url)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                response = await _apiClient.SendAsync(request).ConfigureAwait(false);
                json     = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new EpmException($"HTTP error deleting '{fileName}': {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new EpmException($"Delete request timed out for '{fileName}'.", ex);
            }

            InteropJobStatusResponse result = null;
            try { result = JsonConvert.DeserializeObject<InteropJobStatusResponse>(json); }
            catch { /* non-JSON body — handled below */ }

            progress?.Report(
                $"  DELETE {url} {body} → HTTP {(int)response.StatusCode}, " +
                $"status={result?.Status.ToString() ?? "?"}, details='{result?.Details}'");

            if (response.IsSuccessStatusCode && result != null && result.Status == 0)
                return true;

            string details = result?.Details ?? json;
            if ((int)response.StatusCode == 404 || result?.Status == 8 || LooksLikeNotFound(details))
                return false;

            throw new EpmException(
                $"Could not delete '{fileName}' (HTTP {(int)response.StatusCode}, " +
                $"status {result?.Status.ToString() ?? "?"}): {details}",
                (int)response.StatusCode);
        }

        private static bool LooksLikeNotFound(string details) =>
            !string.IsNullOrEmpty(details) &&
            (details.IndexOf("not found",      StringComparison.OrdinalIgnoreCase) >= 0 ||
             details.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
             details.IndexOf("not exist",      StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>
        /// Starts an EPM Cloud migration import for an already-uploaded snapshot (LCM Import v2):
        /// POST /interop/rest/v2/snapshots/import  body: { "snapshotName": "{name}" }
        /// (https://docs.oracle.com/en/cloud/saas/enterprise-performance-management-common/prest/lcm_import_v2.html)
        /// Users are not imported (importUsers defaults to false).
        /// Returns the response (status=-1 with a Job Status link while running).
        /// Callers must poll <see cref="PollInteropJobByUrlAsync"/> using the JobStatusUrl.
        /// </summary>
        public async Task<MigrationExportResponse> ImportSnapshotAsync(string snapshotName)
        {
            string url = $"{_config.ServiceBaseUrl.TrimEnd('/')}/interop/rest/{InteropApiV2Version}/snapshots/import";
            var body = new { snapshotName = snapshotName };
            try
            {
                string requestJson = JsonConvert.SerializeObject(body);
                var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                HttpResponseMessage response =
                    await _apiClient.PostAsync(url, content).ConfigureAwait(false);
                await EnsureSuccessAsync(response).ConfigureAwait(false);
                string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return JsonConvert.DeserializeObject<MigrationExportResponse>(json);
            }
            catch (EpmException) { throw; }
            catch (HttpRequestException ex)
            {
                throw new EpmException($"HTTP error starting import for '{snapshotName}': {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new EpmException($"Import request timed out for '{snapshotName}'.", ex);
            }
        }

        // ------------------------------------------------------------------ //
        //  Exposed URL properties for services that need to build their own URLs
        // ------------------------------------------------------------------ //

        public string GetPlanningBase() => PlanningBase;
        public string GetInteropBase()  => InteropBase;
        public string GetServiceBaseUrl() => _config.ServiceBaseUrl.TrimEnd('/');

        // ------------------------------------------------------------------ //
        //  IDisposable
        // ------------------------------------------------------------------ //

        public void Dispose()
        {
            if (_disposed) return;
            _apiClient?.Dispose();
            _downloadClient?.Dispose();
            _disposed = true;
        }

        // ------------------------------------------------------------------ //
        //  Credentials stay on the connection's own service
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Throws unless <paramref name="target"/> is https on the same host and port as the
        /// connection's service URL. Every request carries the Basic credentials, and some URLs
        /// (job status, downloads, upload sessions) are links taken from EPM's responses.
        /// </summary>
        internal static void EnsureSameService(Uri service, Uri target)
        {
            if (target != null && target.IsAbsoluteUri && target.Scheme == Uri.UriSchemeHttps &&
                string.Equals(target.Host, service.Host, StringComparison.OrdinalIgnoreCase) &&
                target.Port == service.Port)
                return;
            string where = target != null && target.IsAbsoluteUri ? target.GetLeftPart(UriPartial.Authority) : "(no address)";
            throw new EpmException(
                $"Refused to send the EPM credentials to {where}: it is not the connection's service " +
                $"{service.GetLeftPart(UriPartial.Authority)}.");
        }

        /// <summary>Applies <see cref="EnsureSameService"/> to every request of an HttpClient.</summary>
        private sealed class SameServiceHandler : DelegatingHandler
        {
            private readonly Uri _service;

            public SameServiceHandler(Uri service) : base(new HttpClientHandler()) { _service = service; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                EnsureSameService(_service, request.RequestUri);
                return base.SendAsync(request, cancellationToken);
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  JSON response models
    // ------------------------------------------------------------------ //

    /// <summary>
    /// Planning job response — returned both when a job is submitted
    /// (POST …/jobs) and when its status is read (GET …/jobs/{id}).
    /// Status codes: -1 in progress, 0 success, 1 error, 2 cancel pending,
    /// 3 cancelled, 4 invalid parameter.
    /// </summary>
    public class PlanningJobResponse
    {
        [JsonProperty("jobId")]
        public long JobId { get; set; }

        [JsonProperty("jobName")]
        public string JobName { get; set; }

        /// <summary>Kept as text: documented as an integer, but read leniently.</summary>
        [JsonProperty("status")]
        public string StatusRaw { get; set; }

        [JsonProperty("details")]
        public string Details { get; set; }

        [JsonProperty("descriptiveStatus")]
        public string DescriptiveStatus { get; set; }

        /// <summary>Some error responses carry the status text here instead.</summary>
        [JsonProperty("jobStatus")]
        public string JobStatus { get; set; }

        [JsonProperty("links")]
        public List<EpmLink> Links { get; set; }

        public int StatusCode
        {
            get
            {
                if (int.TryParse(StatusRaw, out int n)) return n;
                string s = StatusRaw ?? DescriptiveStatus ?? JobStatus ?? "";
                if (s.IndexOf("progress", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    s.IndexOf("process",  StringComparison.OrdinalIgnoreCase) >= 0) return -1;
                if (s.IndexOf("complete", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
                return 1;
            }
        }

        public bool IsRunning => StatusCode == -1 || StatusCode == 2;

        public string StatusText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(DescriptiveStatus)) return DescriptiveStatus;
                if (!string.IsNullOrWhiteSpace(JobStatus))         return JobStatus;
                switch (StatusCode)
                {
                    case -1: return "Processing";
                    case 0:  return "Completed";
                    case 2:  return "Cancel pending";
                    case 3:  return "Cancelled";
                    case 4:  return "Invalid parameter";
                    default: return "Error";
                }
            }
        }

        /// <summary>The "job-details" link, when EPM provides one.</summary>
        public string DetailsUrl =>
            Links?.FirstOrDefault(l => string.Equals(l.Rel, "job-details", StringComparison.OrdinalIgnoreCase))?.Href;
    }

    // ------------------------------------------------------------------ //
    //  Interop v2 Migration response models
    // ------------------------------------------------------------------ //

    public class MigrationCategoryListResponse
    {
        [JsonProperty("status")]
        public int Status { get; set; }

        [JsonProperty("details")]
        public string Details { get; set; }

        [JsonProperty("items")]
        public System.Collections.Generic.List<MigrationCategoryItem> Items { get; set; }
    }

    public class MigrationCategoryItem
    {
        /// <summary>Some EPM versions use "name", others "categoryName".</summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("categoryName")]
        public string CategoryName { get; set; }

        /// <summary>Returns whichever field is populated.</summary>
        public string DisplayName => Name ?? CategoryName;
    }

    /// <summary>
    /// Response from POST /interop/rest/v2/files/upload (create session)
    /// and POST /interop/rest/v2/files/upload/{jobId}/complete (trigger extraction).
    /// jobId identifies both the PATCH target and the extraction status job.
    /// </summary>
    public class UploadSessionResponse
    {
        [JsonProperty("status")]
        public int Status { get; set; }

        [JsonProperty("jobId")]
        public long JobId { get; set; }

        [JsonProperty("details")]
        public string Details { get; set; }

        [JsonProperty("links")]
        public List<EpmLink> Links { get; set; }

        /// <summary>Job Status polling URL from the links array.</summary>
        public string JobStatusUrl =>
            Links?.FirstOrDefault(l =>
                string.Equals(l.Rel, "Job Status",
                    StringComparison.OrdinalIgnoreCase))?.Href;
    }

    /// <summary>
    /// Response from POST /interop/rest/v2/migration/categories/artifacts/export.
    /// status == -1 means the job was accepted and is running.
    /// The "Job Status" link href is the URL to poll for completion.
    /// </summary>
    public class MigrationExportResponse
    {
        [JsonProperty("status")]
        public int Status { get; set; }

        [JsonProperty("items")]
        public object Items { get; set; }

        [JsonProperty("details")]
        public string Details { get; set; }

        [JsonProperty("links")]
        public List<EpmLink> Links { get; set; }

        /// <summary>
        /// The full URL to poll for job status, extracted from the "Job Status" link.
        /// e.g. https://…/interop/rest/v2/status/migration/44
        /// </summary>
        public string JobStatusUrl =>
            Links?.FirstOrDefault(l =>
                string.Equals(l.Rel, "Job Status", StringComparison.OrdinalIgnoreCase))?.Href;
    }

    /// <summary>A single entry in an EPM REST API "links" array.</summary>
    public class EpmLink
    {
        [JsonProperty("href")]
        public string Href { get; set; }

        [JsonProperty("action")]
        public string Action { get; set; }

        [JsonProperty("rel")]
        public string Rel { get; set; }
    }

    /// <summary>
    /// Response from GET …/interop/rest/v2/status/migration/{id}.
    /// status == -1 = running, 0 = completed, > 0 = error.
    /// </summary>
    public class InteropJobStatusResponse
    {
        [JsonProperty("status")]
        public int Status { get; set; }

        [JsonProperty("details")]
        public string Details { get; set; }

        [JsonProperty("links")]
        public List<EpmLink> Links { get; set; }
    }
}
