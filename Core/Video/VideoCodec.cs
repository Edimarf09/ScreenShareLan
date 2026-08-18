using System.Drawing;

namespace ScreenShareLan;

/// <summary>Um frame ja codificado (JPEG, ou 1 Access Unit H.264).</summary>
public readonly struct EncodedFrame
{
    public readonly byte[] Data;
    public readonly bool KeyFrame;
    public EncodedFrame(byte[] data, bool keyFrame) { Data = data; KeyFrame = keyFrame; }
}

/// <summary>
/// Encoder de video: captura + codifica em background e dispara FrameReady por frame.
/// (Assim JPEG sincrono e FFmpeg em pipeline cabem na mesma interface.)
/// </summary>
public interface IVideoEncoder : IDisposable
{
    event Action<EncodedFrame>? FrameReady;
    void Start();
    void Stop();
}

/// <summary>Decoder de video: recebe frames codificados e dispara FrameDecoded com a imagem.</summary>
public interface IVideoDecoder : IDisposable
{
    event Action<Image>? FrameDecoded;
    void Push(EncodedFrame frame);
}

/// <summary>
/// Escolhe a implementacao pelo CodecKind. Pra adicionar um codec novo (ex.: WindowsApi
/// na 2.2), basta criar as classes e adicionar um case aqui — nada mais muda.
/// </summary>
public static class VideoFactory
{
    // Padrao da 2.1 = FFmpeg (o JPEG continua disponivel).
    public static CodecKind Default = CodecKind.Ffmpeg;

    public static IVideoEncoder CreateEncoder(CodecKind kind, SharePreset preset) => kind switch
    {
        CodecKind.Jpeg   => new JpegEncoder(preset),
        CodecKind.Ffmpeg => new FfmpegEncoder(preset),
        // CodecKind.WindowsApi => new WindowsApiEncoder(preset), // 2.2
        _                => new FfmpegEncoder(preset),
    };

    public static IVideoDecoder CreateDecoder(CodecKind kind, SharePreset preset) => kind switch
    {
        CodecKind.Jpeg   => new JpegDecoder(),
        CodecKind.Ffmpeg => new FfmpegDecoder(preset),
        // CodecKind.WindowsApi => new WindowsApiDecoder(preset), // 2.2
        _                => new FfmpegDecoder(preset),
    };
}
