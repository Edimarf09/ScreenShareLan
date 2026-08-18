using System.Drawing;

namespace ScreenShareLan;

/// <summary>Decoder JPEG (esquema antigo, atras da interface). Decodifica na hora.</summary>
public sealed class JpegDecoder : IVideoDecoder
{
    public event Action<Image>? FrameDecoded;

    public void Push(EncodedFrame frame)
    {
        try
        {
            using var ms = new MemoryStream(frame.Data, writable: false);
            using var dec = Image.FromStream(ms);
            FrameDecoded?.Invoke(new Bitmap(dec)); // copia independente do stream
        }
        catch { /* frame corrompido por perda de pacote: ignora */ }
    }

    public void Dispose() { }
}
