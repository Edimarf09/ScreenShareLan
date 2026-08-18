using System.Diagnostics;

namespace ScreenShareLan;

/// <summary>Utilitarios do ffmpeg (localizar o exe, escolher encoder, abrir processo em pipe).</summary>
public static class Ffmpeg
{
    /// <summary>Caminho do ffmpeg. Ordem: este valor -> ffmpeg.exe ao lado do app -> PATH.</summary>
    public static string ExePath { get; set; } = ResolveDefault();

    /// <summary>
    /// Encoder H.264 a usar. Padrao "libx264" (CPU, sempre funciona).
    /// Pra usar GPU, troque por "h264_nvenc" (NVIDIA), "h264_qsv" (Intel) ou "h264_amf" (AMD).
    /// Use PickH264Encoder() pra detectar automaticamente.
    /// </summary>
    public static string H264Encoder = "libx264";

    private static string ResolveDefault()
    {
        try
        {
            var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
        }
        catch { }
        return "ffmpeg";
    }

    private static string? _picked;
    /// <summary>Detecta o melhor encoder disponivel: nvenc > qsv > amf > libx264.</summary>
    public static string PickH264Encoder()
    {
        if (_picked != null) return _picked;
        _picked = "libx264";
        try
        {
            var psi = new ProcessStartInfo(ExePath, "-hide_banner -encoders")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit(3000);
                if (o.Contains("h264_nvenc")) _picked = "h264_nvenc";
                else if (o.Contains("h264_qsv")) _picked = "h264_qsv";
                else if (o.Contains("h264_amf")) _picked = "h264_amf";
            }
        }
        catch { }
        return _picked;
    }

    public static Process StartPipe(string args)
    {
        var psi = new ProcessStartInfo(ExePath, args)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var p = new Process { StartInfo = psi };
        p.Start();
        // dreno do stderr pra nao encher o buffer e travar o ffmpeg
        _ = Task.Run(() =>
        {
            try { while (p.StandardError.ReadLine() is not null) { } }
            catch { }
        });
        return p;
    }
}
