using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Boxy.Web.Services;

/// <summary>
/// Turns a <see cref="BlobServe"/> into an HTTP response. Centralises the per-backend serving fork so
/// controllers stay backend-agnostic: a local blob is served by the framework (native Range/ETag), and a
/// remote blob is streamed range-by-range by <see cref="RemoteBlobResult"/>. Callers set their own
/// response headers (cache-control, nosniff, CSP) before returning this.
/// </summary>
public static class BlobServing
{
    public static IActionResult Serve(BlobServe serve, string contentType, string? downloadName, bool enableRange)
    {
        return serve switch
        {
            LocalBlobServe local => new PhysicalFileResult(local.Path, contentType)
            {
                FileDownloadName = downloadName,
                EnableRangeProcessing = enableRange
            },
            RemoteBlobServe remote => new RemoteBlobResult(remote, contentType, downloadName, enableRange),
            _ => throw new NotSupportedException($"Unhandled blob serve type {serve.GetType().Name}")
        };
    }
}

/// <summary>
/// Streams a remote blob to the response, honouring a single HTTP Range and the conditional headers
/// (If-None-Match / If-Modified-Since / If-Range). Mirrors what the framework does for a local file, but
/// fetches the requested byte range from the backend instead of a seekable local stream.
/// </summary>
public sealed class RemoteBlobResult(RemoteBlobServe blob, string contentType, string? downloadName, bool enableRange) : IActionResult
{
    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var request = http.Request;
        var response = http.Response;
        var ct = http.RequestAborted;

        response.GetTypedHeaders().LastModified = blob.LastModified;
        response.Headers.ETag = blob.ETag;
        response.ContentType = contentType;
        if (downloadName is not null)
        {
            response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
            {
                FileNameStar = downloadName
            }.ToString();
        }

        if (enableRange)
        {
            response.Headers.AcceptRanges = "bytes";
        }

        // Not-modified: a matching validator means the client already has these bytes.
        if (IsNotModified(request))
        {
            response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        long start = 0;
        var length = blob.Length;
        var partial = false;

        if (enableRange && request.Headers.ContainsKey(HeaderNames.Range) && RangeApplies(request))
        {
            var parsed = ParseSingleRange(request.Headers.Range.ToString(), blob.Length);
            if (parsed is null)
            {
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = $"bytes */{blob.Length}";
                return;
            }

            (start, length) = parsed.Value;
            partial = true;
        }

        response.ContentLength = length;
        if (partial)
        {
            response.StatusCode = StatusCodes.Status206PartialContent;
            response.Headers.ContentRange = $"bytes {start}-{start + length - 1}/{blob.Length}";
        }

        if (HttpMethods.IsHead(request.Method) || length == 0)
        {
            return;
        }

        await using var stream = await blob.OpenRange(start, length, ct);
        await stream.CopyToAsync(response.Body, ct);
    }

    private bool IsNotModified(HttpRequest request)
    {
        var ifNoneMatch = request.Headers.IfNoneMatch;
        if (ifNoneMatch.Count > 0)
        {
            return ifNoneMatch.Any(v => v == "*" || TagsEqual(v, blob.ETag));
        }

        var ifModifiedSince = request.GetTypedHeaders().IfModifiedSince;
        // Compare at second precision - HTTP dates carry no sub-second component.
        return ifModifiedSince is not null && blob.LastModified <= ifModifiedSince.Value.AddSeconds(1);
    }

    // A range only applies when there's no If-Range, or its validator still matches (else serve the whole
    // thing so the client doesn't stitch a stale range onto changed bytes).
    private bool RangeApplies(HttpRequest request)
    {
        var ifRange = request.Headers.IfRange.ToString();
        if (string.IsNullOrEmpty(ifRange))
        {
            return true;
        }

        // If-Range holds either an entity-tag or an HTTP-date.
        if (ifRange.StartsWith('"') || ifRange.StartsWith("W/", StringComparison.Ordinal))
        {
            return TagsEqual(ifRange, blob.ETag);
        }

        return DateTimeOffset.TryParse(ifRange, out var date) && blob.LastModified <= date.AddSeconds(1);
    }

    private static bool TagsEqual(string? a, string? b)
    {
        static string Normalize(string? t)
        {
            return (t ?? "").Trim().Replace("W/", "", StringComparison.Ordinal);
        }

        return !string.IsNullOrEmpty(a) && Normalize(a) == Normalize(b);
    }

    // Parse a single byte range ("bytes=start-end", "bytes=start-", "bytes=-suffix"). Returns the
    // resolved (start, length), or null when unsatisfiable. Multi-range requests fall back to the full
    // body since browsers only send single ranges for media.
    private static (long Start, long Length)? ParseSingleRange(string header, long total)
    {
        const string prefix = "bytes=";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return (0, total);
        }

        var spec = header[prefix.Length..].Trim();
        if (spec.Contains(','))
        {
            return (0, total); // multi-range not supported; serve full
        }

        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var startText = spec[..dash];
        var endText = spec[(dash + 1)..];

        long start;
        long end;
        if (startText.Length == 0)
        {
            // Suffix range: last N bytes.
            if (!long.TryParse(endText, out var suffix) || suffix <= 0)
            {
                return null;
            }

            start = Math.Max(0, total - suffix);
            end = total - 1;
        }
        else
        {
            if (!long.TryParse(startText, out start) || start < 0 || start >= total)
            {
                return null;
            }

            end = endText.Length == 0 ? total - 1 : long.TryParse(endText, out var e) ? Math.Min(e, total - 1) : total - 1;
        }

        if (end < start)
        {
            return null;
        }

        return (start, end - start + 1);
    }
}
