using System.Threading.Channels;

namespace Boxy.Web.Services;

/// <summary>
/// In-process hand-off from upload requests to the background <see cref="MediaProcessingWorker"/>.
/// Uploads stay fast; probing/poster/transcode happen out of band.
/// </summary>
public class MediaProcessingQueue
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>(
        new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(int mediaItemId)
    {
        _channel.Writer.TryWrite(mediaItemId);
    }

    public IAsyncEnumerable<int> ReadAllAsync(CancellationToken ct)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }
}
