using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace LunaMultiplayer.PlayerUpdater.Core
{
    // Streams an HTTP(S) URL to a local file with progress callbacks and
    // cancellation support. Used by InstallPipeline to fetch the release zip
    // from GitHub's `browser_download_url`.
    //
    // Why a dedicated class instead of `HttpClient.GetByteArrayAsync` + a
    // single `File.WriteAllBytes`? The release zip is 1.3 MB on the
    // -Client-Release flavour and up to 70 MB on the -selfcontained-AdminGui
    // flavour. Loading 70 MB into a single byte array stutters the UI
    // thread; streaming in 64 KB chunks lets the Forms-layer progress bar
    // update smoothly + supports mid-download cancel via the
    // CancellationToken parameter.
    //
    // Progress reporting: callers pass an `IProgress<DownloadProgress>`
    // which `Progress<T>.Report` marshals to the UI thread automatically.
    // We throttle reports to one per ~64 KB chunk so we don't flood the
    // Forms message queue on fast localhost / disk-cache reads.
    //
    // Test seam: pass a custom HttpMessageHandler via the constructor to
    // inject canned responses without real network traffic — same pattern
    // as GitHubClient.
    public sealed class HttpDownloader : IDisposable
    {
        // Download streaming chunk size. 64 KB matches Windows' default
        // FileStream buffer and is a sweet spot between syscall overhead
        // and progress-reporting granularity. Used on both the read (from
        // network) and write (to file) sides.
        private const int ChunkSize = 64 * 1024;

        // GitHub's CDN can return 403 on requests without a User-Agent
        // header. Match GitHubClient's UA so we present a consistent
        // identity across the two clients.
        private const string UserAgentValue = "LunaMultiplayer-PlayerUpdater";

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;

        // Default construction wires HttpClient against a fresh handler and
        // owns its lifetime — Dispose() will tear it down. Tests inject a
        // FakeHttpMessageHandler via the handler param and we still own
        // the wrapping HttpClient (the test fixture disposes us).
        public HttpDownloader(HttpMessageHandler? handler = null)
        {
            _ownsHttpClient = true;
            _httpClient = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null);
            _httpClient.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(UserAgentValue, "1.0"));
            // Long timeout — release zip + slow connection could be a few
            // minutes. Cancellation token is the right per-call gate.
            _httpClient.Timeout = TimeSpan.FromMinutes(30);
        }

        // Streams the URL to destPath. Returns the number of bytes written.
        // Calls progress.Report after each chunk with current bytes-read +
        // total bytes (from Content-Length; 0 if the server omits it —
        // Forms layer renders an indeterminate progress bar in that case).
        //
        // Throws:
        //  - HttpRequestException on non-2xx responses or transport errors.
        //  - IOException / UnauthorizedAccessException on file system errors.
        //  - OperationCanceledException when cancellationToken fires
        //    (cooperative; we check between chunks).
        //
        // The destPath is OVERWRITTEN if it exists — caller is responsible
        // for not pointing at a file they want to keep. InstallPipeline
        // writes to a staging path under Path.GetTempPath() so this is
        // safe by design.
        public async Task<long> DownloadAsync(
            string url,
            string destPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("url must be non-empty.", nameof(url));
            }
            if (string.IsNullOrWhiteSpace(destPath))
            {
                throw new ArgumentException("destPath must be non-empty.", nameof(destPath));
            }

            // HttpCompletionOption.ResponseHeadersRead so we get Content-
            // Length up front without buffering the whole body in memory.
            using var response = await _httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? 0L;
            var bytesRead = 0L;

            // Initial report so the Forms layer can size the progress bar
            // before the first chunk lands.
            progress?.Report(new DownloadProgress(0, totalBytes));

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            using (var dest = new FileStream(
                destPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: ChunkSize,
                useAsync: true))
            {
                var buffer = new byte[ChunkSize];
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var read = await source.ReadAsync(buffer.AsMemory(0, ChunkSize), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) break;

                    await dest.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);

                    bytesRead += read;
                    progress?.Report(new DownloadProgress(bytesRead, totalBytes));
                }
            }

            return bytesRead;
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }

    // BytesRead is monotonically increasing across a single DownloadAsync
    // call. TotalBytes is 0 when the server omitted Content-Length; Forms
    // should render an indeterminate progress bar in that case.
    public readonly record struct DownloadProgress(long BytesRead, long TotalBytes);
}
