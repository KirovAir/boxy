using System.Diagnostics;
using Boxy.Web.Models;
using Microsoft.Extensions.Options;

namespace Boxy.Web.Services;

/// <summary>
/// What the ffmpeg on THIS machine can actually do. Asked once, at boot, because the answer is a property
/// of the deployment and not of any one video: a stock Homebrew build has no <c>zscale</c>, the Docker
/// image does, and only a machine with a working render node can encode on the GPU.
///
/// The VAAPI answer is not taken from a list of compiled-in encoders, and not from the presence of a
/// device node either. Both can be true on a box that still cannot encode a single frame - the driver may
/// be missing, the container may not have been given the device, the node may be owned by a group we are
/// not in. So we ask the only question that means anything and try to encode one frame. If that fails,
/// hardware is simply reported as unavailable, the admin is told so, and everything runs on the CPU.
/// </summary>
public class FfmpegCapabilities(IOptions<FfmpegSettings> ffmpeg, ILogger<FfmpegCapabilities> logger)
{
    private readonly FfmpegSettings options = ffmpeg.Value;

    /// <summary>Tone-mapping HDR down to SDR needs zscale (libzimg) for the linear-light conversion, and
    /// tonemap for the curve. Without both, an HDR source can only be labelled, not converted.</summary>
    public bool CanToneMap { get; private set; }

    /// <summary>True only if this machine encoded a real frame on the GPU during startup.</summary>
    public bool CanEncodeOnGpu { get; private set; }

    /// <summary>Why hardware encoding is off, for the settings page to show. Null when it works.</summary>
    public string? GpuUnavailableReason { get; private set; }

    public string VaapiDevice => options.VaapiDevice;

    /// <summary>The subset of this that the (pure, testable) argument builder needs.</summary>
    public EncoderCapabilities ForEncoding()
    {
        return new EncoderCapabilities(CanToneMap, CanEncodeOnGpu, options.VaapiDevice);
    }

    public async Task DetectAsync(CancellationToken ct = default)
    {
        var filters = await RunAsync("-hide_banner -filters", ct);
        CanToneMap = filters.Contains(" zscale ", StringComparison.Ordinal)
                     && filters.Contains(" tonemap ", StringComparison.Ordinal);

        await DetectGpuAsync(ct);

        logger.LogInformation("ffmpeg capabilities: HDR tone-mapping={ToneMap}, GPU encoding={Gpu}{Why}",
            CanToneMap, CanEncodeOnGpu, GpuUnavailableReason is null ? "" : $" ({GpuUnavailableReason})");
    }

    private async Task DetectGpuAsync(CancellationToken ct)
    {
        var encoders = await RunAsync("-hide_banner -encoders", ct);
        if (!encoders.Contains("h264_vaapi", StringComparison.Ordinal))
        {
            GpuUnavailableReason = "this ffmpeg build has no VAAPI encoder";
            return;
        }

        if (!File.Exists(options.VaapiDevice))
        {
            // The usual cause in a container: the device was never passed through.
            GpuUnavailableReason = $"no render device at {options.VaapiDevice}";
            return;
        }

        // The only test that proves anything: encode a frame. A colour source straight into the GPU, one
        // frame, discarded. Costs well under a second and saves every later transcode from finding out the
        // hard way, one item at a time.
        var probe = $"-hide_banner -loglevel error -vaapi_device {options.VaapiDevice} "
                    + "-f lavfi -i color=black:size=320x240:rate=1 -frames:v 1 "
                    + "-vf \"format=nv12,hwupload\" -c:v h264_vaapi -f null -";
        var (code, stderr) = await RunWithCodeAsync(probe, ct);
        if (code == 0)
        {
            CanEncodeOnGpu = true;
            return;
        }

        GpuUnavailableReason = "the render device is there but would not encode a frame";
        logger.LogWarning("VAAPI probe failed on {Device}: {Error}", options.VaapiDevice, Tail(stderr));
    }

    private async Task<string> RunAsync(string args, CancellationToken ct)
    {
        var (_, output) = await RunWithCodeAsync(args, ct, wantStdout: true);
        return output;
    }

    private async Task<(int Code, string Output)> RunWithCodeAsync(string args, CancellationToken ct, bool wantStdout = false)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = options.FfmpegPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (proc is null)
            {
                return (-1, "");
            }

            var stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);

            // Bounded: a wedged probe must never hold up startup.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(true);
                }
                catch
                {
                    /* already gone */
                }

                return (-1, "");
            }

            return (proc.ExitCode, wantStdout ? await stdout : await stderr);
        }
        catch (Exception ex)
        {
            // No ffmpeg at all is a real deployment problem, but it is the transcode's job to report it -
            // capability detection just answers "no".
            logger.LogWarning(ex, "Could not run ffmpeg to detect capabilities");
            return (-1, "");
        }
    }

    private static string Tail(string s)
    {
        return string.IsNullOrEmpty(s) ? "" : s[Math.Max(0, s.Length - 300)..];
    }
}

/// <summary>The deployment facts the argument builder needs, passed in so it stays pure and testable.</summary>
public readonly record struct EncoderCapabilities(bool CanToneMap, bool CanEncodeOnGpu, string VaapiDevice);
