using Boxy.Data.Entities;
using Boxy.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Boxy.Tests;

[TestClass]
public class MediaMetadataTests
{
    [TestMethod]
    public void ParseProbe_ReadsCreationTime_FromFormatTags()
    {
        const string json = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080 } ],
          "format": { "duration": "12.5", "tags": { "creation_time": "2023-06-15T10:30:00.000000Z" } } }
        """;

        var probe = MediaProcessor.ParseProbe(json);

        Assert.AreEqual(new DateTime(2023, 6, 15, 10, 30, 0, DateTimeKind.Utc), probe.CreationTimeUtc!.Value.ToUniversalTime());
    }

    [TestMethod]
    public void ParseProbe_NoCreationTime_IsNull()
    {
        const string json = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 640, "height": 480 } ],
          "format": { "duration": "3.0" } }
        """;

        Assert.IsNull(MediaProcessor.ParseProbe(json).CreationTimeUtc);
    }

    [TestMethod]
    public void ParseProbe_PortraitRotation_SwapsToDisplayDimensions()
    {
        // A portrait phone clip: encoded 1920x1080 with a 90° rotation tag -> the viewer sees 1080x1920.
        const string json = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
                         "tags": { "rotate": "90" } } ], "format": { "duration": "5.0" } }
        """;

        var probe = MediaProcessor.ParseProbe(json);

        Assert.AreEqual(1080, probe.Width);
        Assert.AreEqual(1920, probe.Height);
        Assert.AreEqual(90, probe.Rotation);
    }

    [TestMethod]
    public void ParseProbe_RotationFromDisplayMatrixSideData_IsRead()
    {
        const string json = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080,
                         "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ] } ],
          "format": { "duration": "5.0" } }
        """;

        var probe = MediaProcessor.ParseProbe(json);

        Assert.AreEqual(1080, probe.Width); // -90 also swaps
        Assert.AreEqual(1920, probe.Height);
        Assert.AreEqual(90, probe.Rotation); // normalised to 0..359
    }

    [TestMethod]
    public void ParseProbe_LandscapeVideo_KeepsDimensions()
    {
        const string json = """
        { "streams": [ { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080 } ],
          "format": { "duration": "5.0" } }
        """;

        var probe = MediaProcessor.ParseProbe(json);

        Assert.AreEqual(1920, probe.Width);
        Assert.AreEqual(1080, probe.Height);
    }

    private static FileMetadataExtractor NewExtractor()
    {
        return new FileMetadataExtractor(NullLogger<FileMetadataExtractor>.Instance);
    }

    [TestMethod]
    public void CaptureDate_Video_UsesProbeCreationTime()
    {
        var probe = new ProbeResult(1920, 1080, 10, "h264", "aac", CreationTimeUtc: new DateTime(2022, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var taken = NewExtractor().CaptureDate(MediaKind.Video, "irrelevant.mp4", probe);

        Assert.IsNotNull(taken);
        Assert.AreEqual(new DateTime(2022, 1, 2, 3, 4, 5), taken!.Value);
    }

    [TestMethod]
    public void CaptureDate_RejectsSentinelEpochs()
    {
        // ffmpeg-produced / re-muxed files carry 1904 (QuickTime) or 1970 (Unix) "unset" times.
        var epoch1904 = new ProbeResult(1, 1, 1, "h264", null, CreationTimeUtc: new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var epoch1970 = new ProbeResult(1, 1, 1, "h264", null, CreationTimeUtc: new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.IsNull(NewExtractor().CaptureDate(MediaKind.Video, "x.mp4", epoch1904));
        Assert.IsNull(NewExtractor().CaptureDate(MediaKind.Video, "x.mp4", epoch1970));
    }

    [TestMethod]
    public void CaptureDate_NonMediaKind_IsNull()
    {
        Assert.IsNull(NewExtractor().CaptureDate(MediaKind.File, "x.zip", null));
        Assert.IsNull(NewExtractor().CaptureDate(MediaKind.Pdf, "x.pdf", null));
    }

    [TestMethod]
    public void CaptureDate_Image_MissingFile_IsFailSafe()
    {
        // A missing/unreadable image must never throw (extraction runs post-persist and must never fail an upload).
        Assert.IsNull(NewExtractor().CaptureDate(MediaKind.Image, "does-not-exist.jpg", null));
    }
}
