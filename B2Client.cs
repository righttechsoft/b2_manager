using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace B2Manager;

public sealed class B2ApiException : Exception
{
    public int Status { get; }
    public string Code { get; }

    public B2ApiException(int status, string code, string message) : base(message)
    {
        Status = status;
        Code = code;
    }
}

public sealed class B2LifecycleRule
{
    public string FileNamePrefix { get; set; } = "";
    public int? DaysFromUploadingToHiding { get; set; }
    public int? DaysFromHidingToDeleting { get; set; }
}

public sealed class B2Bucket
{
    public string BucketId { get; set; } = "";
    public string BucketName { get; set; } = "";
    public string BucketType { get; set; } = "";
    public List<B2LifecycleRule> LifecycleRules { get; set; } = new();
}

public sealed class B2File
{
    public string FileId { get; set; } = "";
    public string FileName { get; set; } = "";
    public long ContentLength { get; set; }
    public string ContentType { get; set; } = "";
    public long UploadTimestamp { get; set; }
    public string Action { get; set; } = "";
}

public class B2Key
{
    public string ApplicationKeyId { get; set; } = "";
    public string KeyName { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
    public List<string> BucketIds { get; set; } = new();
    public string? NamePrefix { get; set; }
    public long? ExpirationTimestamp { get; set; }
}

public sealed class B2CreatedKey : B2Key
{
    public string ApplicationKey { get; set; } = "";
}

public sealed class B2Client
{
    private const int MaxAttempts = 5;
    private const long LargeFileThreshold = 200L * 1024 * 1024;
    private const int ParallelWorkerCount = 4;

    private static readonly HttpClient Http = new(new HttpClientHandler())
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    static B2Client()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "b2_manager/1.0.0+dotnet/8.0 (+https://github.com/righttechsoft/b2_manager)");
    }

    private string _keyId = "";
    private string _applicationKey = "";
    private string _accountId = "";
    private string _authToken = "";
    private string _apiUrl = "";
    private string _downloadUrl = "";

    public string CurrentKeyId => _keyId;
    public long RecommendedPartSize { get; private set; } = 100L * 1024 * 1024;
    public long AbsoluteMinimumPartSize { get; private set; } = 5L * 1024 * 1024;

    public async Task AuthorizeAsync(string keyId, string applicationKey)
    {
        _keyId = keyId;
        _applicationKey = applicationKey;
        await DoAuthorizeAsync();
    }

    private async Task DoAuthorizeAsync()
    {
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_keyId}:{_applicationKey}"));
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.backblazeb2.com/b2api/v4/b2_authorize_account");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw ParseError(resp.StatusCode, body);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        _accountId = root.GetProperty("accountId").GetString() ?? "";
        _authToken = root.GetProperty("authorizationToken").GetString() ?? "";
        var storageApi = root.GetProperty("apiInfo").GetProperty("storageApi");
        _apiUrl = storageApi.GetProperty("apiUrl").GetString() ?? "";
        _downloadUrl = storageApi.GetProperty("downloadUrl").GetString() ?? "";

        if (storageApi.TryGetProperty("recommendedPartSize", out var rps) && rps.ValueKind == JsonValueKind.Number)
            RecommendedPartSize = rps.GetInt64();
        if (storageApi.TryGetProperty("absoluteMinimumPartSize", out var amps) && amps.ValueKind == JsonValueKind.Number)
            AbsoluteMinimumPartSize = amps.GetInt64();
    }

    private static B2ApiException ParseError(System.Net.HttpStatusCode status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            string code = root.TryGetProperty("code", out var c) ? c.GetString() ?? "" : "";
            string message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (status == System.Net.HttpStatusCode.Forbidden && code == "cap_exceeded")
                message = "Your Backblaze B2 account cap has been reached. Review Caps & Alerts in the Backblaze console.";
            return new B2ApiException((int)status, code, string.IsNullOrEmpty(message) ? body : message);
        }
        catch (JsonException)
        {
            return new B2ApiException((int)status, "", body);
        }
    }

    // ---- Retry / backoff helpers ----

    private static TimeSpan? TryGetRetryAfter(HttpResponseMessage resp) => resp.Headers.RetryAfter?.Delta;

    private static TimeSpan ExponentialBackoff(int attempt)
    {
        double seconds = Math.Min(30, Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 251));
    }

    private static Task DelayBeforeRetryAsync(HttpResponseMessage? resp, int attempt)
    {
        var delay = (resp != null ? TryGetRetryAfter(resp) : null) ?? ExponentialBackoff(attempt);
        return Task.Delay(delay);
    }

    private async Task<JsonDocument> PostAsync(string op, object requestBody)
    {
        var json = JsonSerializer.Serialize(requestBody);
        int reauthCount = 0;
        Exception? lastError = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_apiUrl}/b2api/v4/{op}");
                req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                req.Headers.TryAddWithoutValidation("Authorization", _authToken);

                using var resp = await Http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                    return JsonDocument.Parse(body);

                var status = resp.StatusCode;
                var error = ParseError(status, body);

                if (status == System.Net.HttpStatusCode.Unauthorized)
                {
                    if ((error.Code == "expired_auth_token" || error.Code == "bad_auth_token") && reauthCount < 2)
                    {
                        reauthCount++;
                        lastError = error;
                        await DoAuthorizeAsync();
                        continue;
                    }
                    throw error;
                }

                if (status == System.Net.HttpStatusCode.Forbidden)
                    throw error;

                if (status == System.Net.HttpStatusCode.RequestTimeout || (int)status == 429 || (int)status >= 500)
                {
                    lastError = error;
                    if (attempt == MaxAttempts) throw error;
                    await DelayBeforeRetryAsync(resp, attempt);
                    continue;
                }

                // any other 4xx: not retryable
                throw error;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
            {
                lastError = ex;
                if (attempt == MaxAttempts) throw;
                await DelayBeforeRetryAsync(null, attempt);
            }
        }

        throw lastError ?? new InvalidOperationException("b2 request exhausted retries without a result.");
    }

    private static string EncodeFileName(string name) =>
        string.Join("/", name.Split('/').Select(Uri.EscapeDataString));

    // ---- Buckets ----

    public async Task<List<B2Bucket>> ListBucketsAsync()
    {
        using var doc = await PostAsync("b2_list_buckets", new { accountId = _accountId });
        var buckets = new List<B2Bucket>();
        foreach (var el in doc.RootElement.GetProperty("buckets").EnumerateArray())
            buckets.Add(ParseBucket(el));
        return buckets;
    }

    public async Task<B2Bucket> CreateBucketAsync(string bucketName, string bucketType)
    {
        using var doc = await PostAsync("b2_create_bucket", new { accountId = _accountId, bucketName, bucketType });
        return ParseBucket(doc.RootElement);
    }

    private static B2Bucket ParseBucket(JsonElement el)
    {
        var bucket = new B2Bucket
        {
            BucketId = el.GetProperty("bucketId").GetString() ?? "",
            BucketName = el.GetProperty("bucketName").GetString() ?? "",
            BucketType = el.GetProperty("bucketType").GetString() ?? ""
        };
        if (el.TryGetProperty("lifecycleRules", out var rules))
        {
            foreach (var ruleEl in rules.EnumerateArray())
            {
                bucket.LifecycleRules.Add(new B2LifecycleRule
                {
                    FileNamePrefix = ruleEl.TryGetProperty("fileNamePrefix", out var fnp) ? fnp.GetString() ?? "" : "",
                    DaysFromUploadingToHiding = ruleEl.TryGetProperty("daysFromUploadingToHiding", out var dfu) && dfu.ValueKind != JsonValueKind.Null ? dfu.GetInt32() : null,
                    DaysFromHidingToDeleting = ruleEl.TryGetProperty("daysFromHidingToDeleting", out var dfh) && dfh.ValueKind != JsonValueKind.Null ? dfh.GetInt32() : null
                });
            }
        }
        return bucket;
    }

    public async Task UpdateBucketAsync(string bucketId, string bucketType, List<B2LifecycleRule>? lifecycleRules = null)
    {
        var bodyDict = new Dictionary<string, object?>
        {
            ["accountId"] = _accountId,
            ["bucketId"] = bucketId,
            ["bucketType"] = bucketType
        };
        if (lifecycleRules != null)
        {
            bodyDict["lifecycleRules"] = lifecycleRules.Select(r => new Dictionary<string, object?>
            {
                ["fileNamePrefix"] = r.FileNamePrefix,
                ["daysFromUploadingToHiding"] = r.DaysFromUploadingToHiding,
                ["daysFromHidingToDeleting"] = r.DaysFromHidingToDeleting
            }).ToList();
        }
        using var doc = await PostAsync("b2_update_bucket", bodyDict);
    }

    public async Task DeleteBucketAsync(string bucketId)
    {
        using var doc = await PostAsync("b2_delete_bucket", new { accountId = _accountId, bucketId });
    }

    // ---- Files ----

    public async Task<List<B2File>> ListFileVersionsAsync(string bucketId, string? prefix = null)
    {
        var files = new List<B2File>();
        string? startFileName = null;
        string? startFileId = null;

        while (true)
        {
            var bodyDict = new Dictionary<string, object?> { ["bucketId"] = bucketId, ["maxFileCount"] = 1000 };
            if (prefix != null)
                bodyDict["prefix"] = prefix;
            if (startFileName != null)
                bodyDict["startFileName"] = startFileName;
            if (startFileId != null)
                bodyDict["startFileId"] = startFileId;

            using var doc = await PostAsync("b2_list_file_versions", bodyDict);
            var root = doc.RootElement;

            foreach (var el in root.GetProperty("files").EnumerateArray())
            {
                files.Add(new B2File
                {
                    FileId = el.GetProperty("fileId").GetString() ?? "",
                    FileName = el.GetProperty("fileName").GetString() ?? "",
                    ContentLength = el.TryGetProperty("contentLength", out var cl) ? cl.GetInt64() : 0,
                    ContentType = el.TryGetProperty("contentType", out var ct) ? ct.GetString() ?? "" : "",
                    UploadTimestamp = el.TryGetProperty("uploadTimestamp", out var ut) ? ut.GetInt64() : 0,
                    Action = el.TryGetProperty("action", out var ac) ? ac.GetString() ?? "" : ""
                });
            }

            var nextFileName = root.TryGetProperty("nextFileName", out var nf) && nf.ValueKind != JsonValueKind.Null ? nf.GetString() : null;
            var nextFileId = root.TryGetProperty("nextFileId", out var ni) && ni.ValueKind != JsonValueKind.Null ? ni.GetString() : null;
            if (string.IsNullOrEmpty(nextFileName))
                break;
            startFileName = nextFileName;
            startFileId = nextFileId;
        }

        return files;
    }

    public async Task UploadFileAsync(string bucketId, string localPath, string remoteName, IProgress<long>? progress = null)
    {
        string sha1Hex;
        using (var sha1 = SHA1.Create())
        using (var fs = File.OpenRead(localPath))
        {
            var hash = await sha1.ComputeHashAsync(fs);
            sha1Hex = Convert.ToHexString(hash).ToLowerInvariant();
        }

        long fileSize = new FileInfo(localPath).Length;
        if (fileSize > LargeFileThreshold)
        {
            await UploadLargeFileAsync(bucketId, localPath, remoteName, sha1Hex, fileSize, progress);
            return;
        }

        await UploadSingleFileAsync(bucketId, localPath, remoteName, sha1Hex, progress);
    }

    private async Task UploadSingleFileAsync(string bucketId, string localPath, string remoteName, string sha1Hex, IProgress<long>? progress)
    {
        long lastModifiedMillis = new DateTimeOffset(File.GetLastWriteTimeUtc(localPath), TimeSpan.Zero).ToUnixTimeMilliseconds();
        Exception? lastError = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // The stream restarts from zero on retry, so reset the reported progress to match.
            progress?.Report(0);
            try
            {
                using var uploadUrlDoc = await PostAsync("b2_get_upload_url", new { bucketId });
                var uploadUrlEl = uploadUrlDoc.RootElement;
                string uploadUrl = uploadUrlEl.GetProperty("uploadUrl").GetString() ?? "";
                string uploadToken = uploadUrlEl.GetProperty("authorizationToken").GetString() ?? "";

                using var fs = File.OpenRead(localPath);
                using var req = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
                req.Headers.TryAddWithoutValidation("Authorization", uploadToken);
                req.Headers.TryAddWithoutValidation("X-Bz-File-Name", EncodeFileName(remoteName));
                req.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", sha1Hex);
                req.Headers.TryAddWithoutValidation("X-Bz-Info-src_last_modified_millis", lastModifiedMillis.ToString());

                Stream uploadStream = progress != null ? new ProgressStream(fs, progress) : fs;
                var content = new StreamContent(uploadStream);
                content.Headers.ContentType = MediaTypeHeaderValue.Parse("b2/x-auto");
                content.Headers.ContentLength = fs.Length;
                req.Content = content;

                using var resp = await Http.SendAsync(req);

                if (resp.IsSuccessStatusCode)
                    return;

                var body = await resp.Content.ReadAsStringAsync();
                var status = resp.StatusCode;
                var error = ParseError(status, body);

                if (status == System.Net.HttpStatusCode.RequestTimeout || (int)status >= 500 || status == System.Net.HttpStatusCode.Unauthorized)
                {
                    lastError = error;
                    if (attempt == MaxAttempts) throw error;
                    await DelayBeforeRetryAsync(resp, attempt);
                    continue;
                }

                throw error;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
            {
                lastError = ex;
                if (attempt == MaxAttempts) throw;
                await DelayBeforeRetryAsync(null, attempt);
            }
        }

        throw lastError ?? new InvalidOperationException("Upload exhausted retries without a result.");
    }

    private async Task UploadLargeFileAsync(string bucketId, string localPath, string remoteName, string sha1Hex, long fileSize, IProgress<long>? progress)
    {
        long lastModifiedMillis = new DateTimeOffset(File.GetLastWriteTimeUtc(localPath), TimeSpan.Zero).ToUnixTimeMilliseconds();

        using var startDoc = await PostAsync("b2_start_large_file", new
        {
            bucketId,
            fileName = remoteName,
            contentType = "b2/x-auto",
            fileInfo = new
            {
                src_last_modified_millis = lastModifiedMillis.ToString(),
                large_file_sha1 = sha1Hex
            }
        });
        string fileId = startDoc.RootElement.GetProperty("fileId").GetString() ?? "";

        long partSize = Math.Max(RecommendedPartSize, AbsoluteMinimumPartSize);
        while ((long)Math.Ceiling((double)fileSize / partSize) > 10000)
            partSize *= 2;
        int totalParts = (int)Math.Ceiling((double)fileSize / partSize);

        var partShas = new ConcurrentDictionary<int, string>();
        int nextPartNumber = 0;
        long uploadedBytes = 0;
        long lastReportedBytes = 0;
        var reportLock = new object();
        using var cts = new CancellationTokenSource();

        void ReportPartProgress(long delta)
        {
            if (progress == null) return;
            long total = Interlocked.Add(ref uploadedBytes, delta);
            lock (reportLock)
            {
                if (total - lastReportedBytes >= (1 << 20) || total == fileSize)
                {
                    lastReportedBytes = total;
                    progress.Report(total);
                }
            }
        }

        async Task WorkerBodyAsync()
        {
            string uploadUrl = "";
            string uploadToken = "";
            using var fs = File.OpenRead(localPath);
            // One buffer per worker, reused across parts: parts are typically 100MB, so allocating
            // per part would churn the large object heap on a multi-GB file.
            var buffer = new byte[partSize];

            while (true)
            {
                if (cts.IsCancellationRequested) return;
                int partNumber = Interlocked.Increment(ref nextPartNumber);
                if (partNumber > totalParts) return;

                long start = (long)(partNumber - 1) * partSize;
                int length = (int)Math.Min(partSize, fileSize - start);

                fs.Seek(start, SeekOrigin.Begin);
                int readTotal = 0;
                while (readTotal < length)
                {
                    int read = await fs.ReadAsync(buffer, readTotal, length - readTotal);
                    if (read == 0) break;
                    readTotal += read;
                }

                string partSha1;
                using (var sha1 = SHA1.Create())
                    partSha1 = Convert.ToHexString(sha1.ComputeHash(buffer, 0, length)).ToLowerInvariant();

                Exception? lastError = null;
                bool uploaded = false;

                for (int attempt = 1; attempt <= MaxAttempts && !uploaded; attempt++)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(uploadUrl) || string.IsNullOrEmpty(uploadToken))
                        {
                            using var partUrlDoc = await PostAsync("b2_get_upload_part_url", new { fileId });
                            uploadUrl = partUrlDoc.RootElement.GetProperty("uploadUrl").GetString() ?? "";
                            uploadToken = partUrlDoc.RootElement.GetProperty("authorizationToken").GetString() ?? "";
                        }

                        using var req = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
                        req.Headers.TryAddWithoutValidation("Authorization", uploadToken);
                        req.Headers.TryAddWithoutValidation("X-Bz-Part-Number", partNumber.ToString());
                        req.Headers.TryAddWithoutValidation("X-Bz-Content-Sha1", partSha1);
                        var content = new ByteArrayContent(buffer, 0, length);
                        content.Headers.ContentLength = length;
                        req.Content = content;

                        using var resp = await Http.SendAsync(req);

                        if (resp.IsSuccessStatusCode)
                        {
                            partShas[partNumber] = partSha1;
                            ReportPartProgress(length);
                            uploaded = true;
                            break;
                        }

                        var body = await resp.Content.ReadAsStringAsync();
                        var status = resp.StatusCode;
                        var error = ParseError(status, body);

                        if (status == System.Net.HttpStatusCode.RequestTimeout || (int)status >= 500 || status == System.Net.HttpStatusCode.Unauthorized)
                        {
                            lastError = error;
                            uploadUrl = "";
                            uploadToken = "";
                            if (attempt == MaxAttempts) throw error;
                            await DelayBeforeRetryAsync(resp, attempt);
                            continue;
                        }

                        throw error;
                    }
                    catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
                    {
                        lastError = ex;
                        uploadUrl = "";
                        uploadToken = "";
                        if (attempt == MaxAttempts) throw;
                        await DelayBeforeRetryAsync(null, attempt);
                    }
                }

                if (!uploaded)
                    throw lastError ?? new InvalidOperationException("Part upload exhausted retries without a result.");
            }
        }

        // Any part failing aborts the whole file, so stop the other workers instead of letting
        // them upload the remaining parts of an upload that is already doomed.
        async Task WorkerAsync()
        {
            try { await WorkerBodyAsync(); }
            catch { cts.Cancel(); throw; }
        }

        var workers = Enumerable.Range(0, ParallelWorkerCount).Select(_ => WorkerAsync()).ToArray();
        try
        {
            await Task.WhenAll(workers);
        }
        catch
        {
            try { using var _ = await PostAsync("b2_cancel_large_file", new { fileId }); }
            catch { /* best-effort: the original failure below is what the user needs to see */ }
            throw;
        }

        var orderedShas = Enumerable.Range(1, totalParts).Select(n => partShas[n]).ToList();
        using var finishDoc = await PostAsync("b2_finish_large_file", new { fileId, partSha1Array = orderedShas });
    }

    /// <summary>Wraps a source stream to report cumulative bytes read as HttpClient pulls from it while sending.</summary>
    private sealed class ProgressStream : Stream
    {
        // ponytail: report at most once per MB — HttpClient pulls in 80KB chunks, and marshalling
        // every one of those to the UI thread swamps the dispatcher on a multi-GB file.
        private const long ReportThreshold = 1 << 20;

        private readonly Stream _inner;
        private readonly IProgress<long> _progress;
        private long _totalRead;
        private long _lastReported;

        public ProgressStream(Stream inner, IProgress<long> progress)
        {
            _inner = inner;
            _progress = progress;
        }

        private void Advance(int read)
        {
            if (read > 0)
                _totalRead += read;
            // read == 0 means EOF: flush the final total so the bar lands on 100%.
            bool done = read == 0;
            if ((done || _totalRead - _lastReported >= ReportThreshold) && _totalRead != _lastReported)
            {
                _lastReported = _totalRead;
                _progress.Report(_totalRead);
            }
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            Advance(read);
            return read;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, System.Threading.CancellationToken cancellationToken)
        {
            int read = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            Advance(read);
            return read;
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public async Task DownloadFileAsync(string bucketName, string remoteName, string localPath, IProgress<long>? progress = null, long knownSize = 0)
    {
        if (knownSize > LargeFileThreshold)
        {
            bool usedParallel = await TryParallelDownloadAsync(bucketName, remoteName, localPath, knownSize, progress);
            if (usedParallel) return;
            // The probe chunk already counted bytes; the fallback restarts from zero.
            progress?.Report(0);
        }

        await DownloadFileAsync(bucketName, remoteName, localPath, allowRetry: true, progress);
    }

    private async Task DownloadFileAsync(string bucketName, string remoteName, string localPath, bool allowRetry, IProgress<long>? progress)
    {
        string url = $"{_downloadUrl}/file/{bucketName}/{EncodeFileName(remoteName)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Authorization", _authToken);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && allowRetry)
        {
            await DoAuthorizeAsync();
            await DownloadFileAsync(bucketName, remoteName, localPath, allowRetry: false, progress);
            return;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync();
            throw ParseError(resp.StatusCode, errBody);
        }

        await using var httpStream = await resp.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(localPath);

        var buffer = new byte[81920];
        long totalBytesWritten = 0;
        long lastReported = 0;
        const long reportThreshold = 1 << 20; // see ProgressStream: throttle UI updates to once per MB
        int read;
        while ((read = await httpStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, read);
            totalBytesWritten += read;
            if (totalBytesWritten - lastReported >= reportThreshold)
            {
                lastReported = totalBytesWritten;
                progress?.Report(totalBytesWritten);
            }
        }
        if (totalBytesWritten != lastReported)
            progress?.Report(totalBytesWritten);
    }

    private async Task<bool> TryParallelDownloadAsync(string bucketName, string remoteName, string localPath, long knownSize, IProgress<long>? progress)
    {
        string url = $"{_downloadUrl}/file/{bucketName}/{EncodeFileName(remoteName)}";

        long partSize = Math.Max(RecommendedPartSize, AbsoluteMinimumPartSize);
        while ((long)Math.Ceiling((double)knownSize / partSize) > 10000)
            partSize *= 2;
        int totalChunks = (int)Math.Ceiling((double)knownSize / partSize);

        using (var create = File.Create(localPath))
            create.SetLength(knownSize);

        long downloadedBytes = 0;
        long lastReportedBytes = 0;
        var reportLock = new object();

        void ReportChunkProgress(long delta)
        {
            if (progress == null) return;
            long total = Interlocked.Add(ref downloadedBytes, delta);
            lock (reportLock)
            {
                if (total - lastReportedBytes >= (1 << 20) || total == knownSize)
                {
                    lastReportedBytes = total;
                    progress.Report(total);
                }
            }
        }

        // Probe the first chunk: if the server ignores Range and returns the whole file, abandon the parallel path.
        long firstChunkEnd = Math.Min(partSize, knownSize) - 1;
        var firstStatus = await DownloadRangeAsync(url, localPath, 0, firstChunkEnd, ReportChunkProgress);
        if (firstStatus != System.Net.HttpStatusCode.PartialContent)
            return false;

        if (totalChunks > 1)
        {
            int nextChunkIndex = 0;

            async Task WorkerAsync()
            {
                while (true)
                {
                    int idx = Interlocked.Increment(ref nextChunkIndex);
                    if (idx > totalChunks - 1) return;

                    long start = (long)idx * partSize;
                    long end = Math.Min(start + partSize, knownSize) - 1;
                    await DownloadRangeAsync(url, localPath, start, end, ReportChunkProgress);
                }
            }

            var workers = Enumerable.Range(0, ParallelWorkerCount).Select(_ => WorkerAsync()).ToArray();
            await Task.WhenAll(workers);
        }

        return true;
    }

    private async Task<System.Net.HttpStatusCode> DownloadRangeAsync(string url, string localPath, long start, long end, Action<long> onBytesWritten)
    {
        Exception? lastError = null;

        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            // A retry re-fetches this chunk from the start, so undo the bytes the failed
            // attempt already counted — otherwise the progress bar overshoots 100%.
            long writtenThisAttempt = 0;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", _authToken);
                req.Headers.Range = new RangeHeaderValue(start, end);

                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

                if (resp.IsSuccessStatusCode)
                {
                    await using var httpStream = await resp.Content.ReadAsStreamAsync();
                    await using var fileStream = new FileStream(localPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                    fileStream.Seek(start, SeekOrigin.Begin);

                    var buffer = new byte[81920];
                    int read;
                    while ((read = await httpStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        writtenThisAttempt += read;
                        onBytesWritten(read);
                    }
                    return resp.StatusCode;
                }

                var body = await resp.Content.ReadAsStringAsync();
                var status = resp.StatusCode;
                var error = ParseError(status, body);

                if (status == System.Net.HttpStatusCode.Unauthorized)
                {
                    await DoAuthorizeAsync();
                    lastError = error;
                    if (attempt == MaxAttempts) throw error;
                    continue;
                }

                if (status == System.Net.HttpStatusCode.RequestTimeout || (int)status >= 500)
                {
                    lastError = error;
                    if (attempt == MaxAttempts) throw error;
                    await DelayBeforeRetryAsync(resp, attempt);
                    continue;
                }

                throw error;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is TaskCanceledException)
            {
                lastError = ex;
                if (writtenThisAttempt > 0) onBytesWritten(-writtenThisAttempt);
                if (attempt == MaxAttempts) throw;
                await DelayBeforeRetryAsync(null, attempt);
            }
        }

        throw lastError ?? new InvalidOperationException("Chunk download exhausted retries without a result.");
    }

    public async Task DeleteFileVersionAsync(string fileName, string fileId)
    {
        using var doc = await PostAsync("b2_delete_file_version", new { fileName, fileId });
    }

    // ---- Keys ----

    public async Task<List<B2Key>> ListKeysAsync()
    {
        var keys = new List<B2Key>();
        string? startApplicationKeyId = null;

        while (true)
        {
            object body = startApplicationKeyId == null
                ? new { accountId = _accountId }
                : new { accountId = _accountId, startApplicationKeyId };

            using var doc = await PostAsync("b2_list_keys", body);
            var root = doc.RootElement;

            foreach (var el in root.GetProperty("keys").EnumerateArray())
            {
                keys.Add(ParseKey(el));
            }

            var next = root.TryGetProperty("nextApplicationKeyId", out var nk) ? nk.GetString() : null;
            if (string.IsNullOrEmpty(next))
                break;
            startApplicationKeyId = next;
        }

        return keys;
    }

    private static B2Key ParseKey(JsonElement el)
    {
        var key = new B2Key
        {
            ApplicationKeyId = el.GetProperty("applicationKeyId").GetString() ?? "",
            KeyName = el.GetProperty("keyName").GetString() ?? "",
            NamePrefix = el.TryGetProperty("namePrefix", out var np) && np.ValueKind != JsonValueKind.Null ? np.GetString() : null,
            ExpirationTimestamp = el.TryGetProperty("expirationTimestamp", out var e) && e.ValueKind != JsonValueKind.Null ? e.GetInt64() : null
        };
        if (el.TryGetProperty("capabilities", out var caps))
        {
            foreach (var c in caps.EnumerateArray())
                key.Capabilities.Add(c.GetString() ?? "");
        }
        if (el.TryGetProperty("bucketIds", out var bids) && bids.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in bids.EnumerateArray())
            {
                var id = b.GetString();
                if (id != null)
                    key.BucketIds.Add(id);
            }
        }
        return key;
    }

    public async Task<B2CreatedKey> CreateKeyAsync(string keyName, List<string> capabilities, string? bucketId, long? validDurationInSeconds)
    {
        var bodyDict = new Dictionary<string, object?>
        {
            ["accountId"] = _accountId,
            ["capabilities"] = capabilities,
            ["keyName"] = keyName
        };
        if (!string.IsNullOrEmpty(bucketId))
            bodyDict["bucketIds"] = new List<string> { bucketId };
        if (validDurationInSeconds.HasValue)
            bodyDict["validDurationInSeconds"] = validDurationInSeconds.Value;

        using var doc = await PostAsync("b2_create_key", bodyDict);
        var el = doc.RootElement;
        var key = ParseKey(el);
        return new B2CreatedKey
        {
            ApplicationKeyId = key.ApplicationKeyId,
            KeyName = key.KeyName,
            Capabilities = key.Capabilities,
            BucketIds = key.BucketIds,
            NamePrefix = key.NamePrefix,
            ExpirationTimestamp = key.ExpirationTimestamp,
            ApplicationKey = el.GetProperty("applicationKey").GetString() ?? ""
        };
    }

    public async Task DeleteKeyAsync(string applicationKeyId)
    {
        using var doc = await PostAsync("b2_delete_key", new { applicationKeyId });
    }
}
