using System.Diagnostics;

namespace ScreenShareLan;

/// <summary>
/// Encoder H.264 via ffmpeg (subprocesso). Captura a tela em BGRA (tamanho exato do
/// preset), joga no stdin do ffmpeg e le o H.264 Annex-B do stdout, fatiando em frames.
///
/// NOTA: esta e a parte que precisa de teste no Windows (nao da pra compilar/rodar aqui).
/// Se algo nao funcionar, troque o codec pra JPEG que continua 100%.
/// </summary>
public sealed class FfmpegEncoder : IVideoEncoder
{
    private readonly PresetInfo _info;
    private Process? _proc;
    private CancellationTokenSource? _cts;
    private Thread? _captureThread;
    private Thread? _readerThread;

    public event Action<EncodedFrame>? FrameReady;

    public FfmpegEncoder(SharePreset preset) => _info = Presets.Get(preset);

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _proc = Ffmpeg.StartPipe(BuildArgs());

        _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "enc-capture" };
        _readerThread = new Thread(ReaderLoop) { IsBackground = true, Name = "enc-reader" };
        _captureThread.Start();
        _readerThread.Start();
    }

    private string BuildArgs()
    {
        int w = _info.Width, h = _info.Height, fps = _info.Fps, br = _info.BitrateKbps;

        string input =
            $"-hide_banner -loglevel error " +
            $"-f rawvideo -pixel_format bgra -video_size {w}x{h} -framerate {fps} -i -";

        string common = $"-an -pix_fmt yuv420p -b:v {br}k -maxrate {br}k -bufsize {br}k -flush_packets 1";

        string enc;
        if (Ffmpeg.H264Encoder.StartsWith("libx264"))
        {
            // CPU: parametros confiaveis. repeat-headers=1 -> SPS/PPS em todo IDR; aud=1 -> AUD (fatiar frames).
            enc = "-c:v libx264 -preset ultrafast -tune zerolatency " +
                  $"-x264-params \"repeat-headers=1:aud=1:keyint={fps}:min-keyint={fps}:scenecut=0:bframes=0\"";
        }
        else
        {
            // GPU (nvenc/qsv/amf): AUD via bitstream filter. GOP ~1s.
            enc = $"-c:v {Ffmpeg.H264Encoder} -g {fps} -bf 0 -bsf:v h264_metadata=aud=insert";
        }

        return $"{input} {enc} {common} -f h264 -";
    }

    private void CaptureLoop()
    {
        var ct = _cts!.Token;
        int frameMs = 1000 / _info.Fps;
        var sw = new Stopwatch();
        try
        {
            using var cap = new ScreenCapture(_info.Width, _info.Height, _info.Quality,
                                              drawCursor: true, padToExact: true);
            var stdin = _proc!.StandardInput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                sw.Restart();
                byte[] bgra;
                try { bgra = cap.CaptureRawBgra(); } catch { break; }
                try { stdin.Write(bgra, 0, bgra.Length); stdin.Flush(); }
                catch { break; } // ffmpeg saiu
                int wait = frameMs - (int)sw.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
            }
        }
        catch { }
        try { _proc!.StandardInput.Close(); } catch { }
    }

    private void ReaderLoop()
    {
        var ct = _cts!.Token;
        var splitter = new AnnexBSplitter();
        var buf = new byte[64 * 1024];
        try
        {
            var stdout = _proc!.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                int n = stdout.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                splitter.Append(buf, n, (au, key) => FrameReady?.Invoke(new EncodedFrame(au, key)));
            }
        }
        catch { }
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _proc?.StandardInput.Close(); } catch { }
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
    }

    public void Dispose()
    {
        Stop();
        try { _proc?.Dispose(); } catch { }
    }
}
