using System.Diagnostics;

namespace ScreenShareLan;

/// <summary>Encoder JPEG (o esquema antigo, atras da interface). Todo frame e keyframe.</summary>
public sealed class JpegEncoder : IVideoEncoder
{
    private readonly PresetInfo _info;
    private CancellationTokenSource? _cts;

    public event Action<EncodedFrame>? FrameReady;

    public JpegEncoder(SharePreset preset) => _info = Presets.Get(preset);

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(() => Loop(ct));
    }

    private void Loop(CancellationToken ct)
    {
        int frameMs = 1000 / _info.Fps;
        var sw = new Stopwatch();
        try
        {
            using var cap = new ScreenCapture(_info.Width, _info.Height, _info.Quality, drawCursor: true);
            while (!ct.IsCancellationRequested)
            {
                sw.Restart();
                byte[] jpeg;
                try { jpeg = cap.CaptureJpeg(); } catch { break; }
                FrameReady?.Invoke(new EncodedFrame(jpeg, true));
                int wait = frameMs - (int)sw.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
            }
        }
        catch { }
    }

    public void Stop() { try { _cts?.Cancel(); } catch { } }
    public void Dispose() => Stop();
}
