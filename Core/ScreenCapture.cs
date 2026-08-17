using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ScreenShareLan;

/// <summary>
/// Captura a tela primaria (GDI), reduz pra resolucao do preset (mantendo
/// proporcao, sem dar upscale) e devolve um JPEG. Desenha o cursor por cima.
///
/// Simples e sem dependencia externa. Pra 1080p60 de verdade o ideal e trocar
/// por Desktop Duplication (DXGI) + encoder por hardware (NVENC/QuickSync);
/// esta versao usa GDI+JPEG e da conta tranquilo de 720p e 1080p30.
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private readonly Rectangle _bounds;
    private readonly bool _drawCursor;
    private readonly int _outW;
    private readonly int _outH;
    private readonly bool _needsScale;

    private readonly Bitmap _full;
    private readonly Graphics _fullG;
    private readonly Bitmap? _scaled;
    private readonly Graphics? _scaledG;

    private readonly ImageCodecInfo _jpeg;
    private readonly EncoderParameters _encParams;

    public ScreenCapture(int targetW, int targetH, int quality, bool drawCursor = true)
    {
        _bounds = Screen.PrimaryScreen!.Bounds;
        _drawCursor = drawCursor;

        // Escala pra caber na caixa alvo, sem ampliar (max 1.0).
        double scale = Math.Min(1.0, Math.Min(
            (double)targetW / _bounds.Width,
            (double)targetH / _bounds.Height));
        _outW = Math.Max(1, (int)Math.Round(_bounds.Width * scale));
        _outH = Math.Max(1, (int)Math.Round(_bounds.Height * scale));
        _needsScale = _outW != _bounds.Width || _outH != _bounds.Height;

        _full = new Bitmap(_bounds.Width, _bounds.Height, PixelFormat.Format24bppRgb);
        _fullG = Graphics.FromImage(_full);

        if (_needsScale)
        {
            _scaled = new Bitmap(_outW, _outH, PixelFormat.Format24bppRgb);
            _scaledG = Graphics.FromImage(_scaled);
            _scaledG.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            _scaledG.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        }

        _jpeg = GetEncoder(ImageFormat.Jpeg);
        _encParams = new EncoderParameters(1);
        _encParams.Param[0] = new EncoderParameter(
            Encoder.Quality, (long)Math.Clamp(quality, 10, 95));
    }

    public byte[] CaptureJpeg()
    {
        _fullG.CopyFromScreen(_bounds.Location, Point.Empty, _bounds.Size);
        if (_drawCursor) DrawCursor(_fullG, _bounds);

        using var ms = new MemoryStream(64 * 1024);
        if (_needsScale)
        {
            _scaledG!.DrawImage(_full, 0, 0, _outW, _outH);
            _scaled!.Save(ms, _jpeg, _encParams);
        }
        else
        {
            _full.Save(ms, _jpeg, _encParams);
        }
        return ms.ToArray();
    }

    private static ImageCodecInfo GetEncoder(ImageFormat format)
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.FormatID == format.Guid) return c;
        throw new InvalidOperationException("Codec JPEG nao encontrado.");
    }

    // ---------- cursor via Win32 ----------
    private static void DrawCursor(Graphics g, Rectangle bounds)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || ci.flags != CURSOR_SHOWING) return;

        IntPtr hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc,
                ci.ptScreenPos.x - bounds.X,
                ci.ptScreenPos.y - bounds.Y,
                ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally { g.ReleaseHdc(hdc); }
    }

    private const int CURSOR_SHOWING = 0x00000001;
    private const int DI_NORMAL = 0x0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hDC, int x, int y, IntPtr hIcon,
        int cxWidth, int cyHeight, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, int diFlags);

    public void Dispose()
    {
        _fullG.Dispose();
        _full.Dispose();
        _scaledG?.Dispose();
        _scaled?.Dispose();
        _encParams.Dispose();
    }
}
