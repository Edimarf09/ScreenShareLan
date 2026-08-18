using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenShareLan;

/// <summary>
/// Decoder H.264 via ffmpeg (subprocesso). Recebe Access Units (Annex-B) no stdin e
/// le frames rawvideo BGRA no stdout. O tamanho e fixo (o encoder faz pad pro tamanho
/// exato do preset), entao a gente sabe quantos bytes ler por frame.
///
/// NOTA: parte que precisa de teste no Windows. Se falhar, use o codec JPEG.
/// </summary>
public sealed class FfmpegDecoder : IVideoDecoder
{
    private readonly int _w, _h;
    private Process? _proc;
    private CancellationTokenSource? _cts;
    private Thread? _reader;
    private Stream? _stdin;
    private readonly object _writeLock = new();

    public event Action<Image>? FrameDecoded;

    public FfmpegDecoder(SharePreset preset)
    {
        var info = Presets.Get(preset);
        _w = info.Width; _h = info.Height;
        Start();
    }

    private void Start()
    {
        _cts = new CancellationTokenSource();
        string args =
            "-hide_banner -loglevel error -fflags nobuffer -flags low_delay " +
            "-analyzeduration 0 -probesize 32 " +
            "-f h264 -i - -f rawvideo -pix_fmt bgra -";
        _proc = Ffmpeg.StartPipe(args);
        _stdin = _proc.StandardInput.BaseStream;

        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "dec-reader" };
        _reader.Start();
    }

    public void Push(EncodedFrame frame)
    {
        try { lock (_writeLock) { _stdin!.Write(frame.Data, 0, frame.Data.Length); _stdin.Flush(); } }
        catch { /* ffmpeg saiu */ }
    }

    private void ReaderLoop()
    {
        var ct = _cts!.Token;
        int frameBytes = _w * _h * 4;
        var frame = new byte[frameBytes];
        try
        {
            var stdout = _proc!.StandardOutput.BaseStream;
            while (!ct.IsCancellationRequested)
            {
                if (!ReadFull(stdout, frame, frameBytes)) break;

                var bmp = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, _w, _h),
                                        ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int rowBytes = _w * 4;
                    if (data.Stride == rowBytes)
                        Marshal.Copy(frame, 0, data.Scan0, frameBytes);
                    else
                        for (int y = 0; y < _h; y++)
                            Marshal.Copy(frame, y * rowBytes, data.Scan0 + y * data.Stride, rowBytes);
                }
                finally { bmp.UnlockBits(data); }

                FrameDecoded?.Invoke(bmp);
            }
        }
        catch { }
    }

    private static bool ReadFull(Stream s, byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            int r = s.Read(buf, off, count - off);
            if (r <= 0) return false;
            off += r;
        }
        return true;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _stdin?.Close(); } catch { }
        try { if (_proc is { HasExited: false }) _proc.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
    }
}
