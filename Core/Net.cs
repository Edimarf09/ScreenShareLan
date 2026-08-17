using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ScreenShareLan;

/// <summary>Tipos de mensagem do protocolo UDP.</summary>
public enum MsgType : byte
{
    Join       = 1, // cliente -> servidor : [name UTF8]
    Welcome    = 2, // servidor -> cliente : [int id]
    Hello      = 3, // cliente -> servidor : [int id]  (keepalive)
    Leave      = 4, // cliente -> servidor : [int id]
    Roster     = 5, // servidor -> cliente : lista da sala + quem compartilha
    StartShare = 6, // cliente -> servidor : [int id][byte preset]
    StopShare  = 7, // cliente -> servidor : [int id]
    Video      = 8, // video fragmentado (cliente->servidor e relay servidor->clientes)
}

/// <summary>Os 4 unicos modos de compartilhamento (travados pra otimizar).</summary>
public enum SharePreset : byte
{
    P720_30  = 0,
    P720_60  = 1,
    P1080_30 = 2,
    P1080_60 = 3,
}

public readonly record struct PresetInfo(int Width, int Height, int Fps, int Quality, string Label);

public static class Presets
{
    // Largura/altura = teto do frame (mantem proporcao). Quality = JPEG.
    public static PresetInfo Get(SharePreset p) => p switch
    {
        SharePreset.P720_30  => new PresetInfo(1280,  720, 30, 65, "720p 30FPS"),
        SharePreset.P720_60  => new PresetInfo(1280,  720, 60, 58, "720p 60FPS"),
        SharePreset.P1080_30 => new PresetInfo(1920, 1080, 30, 62, "1080p 30FPS"),
        SharePreset.P1080_60 => new PresetInfo(1920, 1080, 60, 55, "1080p 60FPS"),
        _                    => new PresetInfo(1280,  720, 30, 65, "720p 30FPS"),
    };

    public static readonly SharePreset[] All =
        { SharePreset.P720_30, SharePreset.P720_60, SharePreset.P1080_30, SharePreset.P1080_60 };
}

public static class Protocol
{
    public const int DiscoveryPort   = 45678;  // UDP: anuncio das salas (broadcast)
    public const int DefaultRoomPort = 45679;  // UDP: sala (controle + video)

    public const int MaxUdpPayload   = 1200;   // bytes de JPEG por pacote (fica < MTU)
    public const int VideoHeaderSize = 15;     // type(1)+sender(4)+frame(4)+idx(2)+count(2)+len(2)

    public const string Magic = "SCRNSHR";
    public const string Version = "1";

    public static string BuildAnnounce(string hostName, int roomPort)
        => $"{Magic}|{Version}|{hostName}|{roomPort}";

    public static bool TryParseAnnounce(string s, out string hostName, out int roomPort)
    {
        hostName = string.Empty;
        roomPort = 0;
        var parts = s.Split('|');
        if (parts.Length != 4) return false;
        if (parts[0] != Magic || parts[1] != Version) return false;
        hostName = parts[2];
        return int.TryParse(parts[3], out roomPort);
    }
}

public static class NetworkUtils
{
    /// <summary>
    /// Broadcast de todas as interfaces IPv4 ativas + 255.255.255.255.
    /// Cobre a LAN fisica e a LAN virtual (ex.: Radmin VPN 26.x.x.x -> 26.255.255.255).
    /// </summary>
    public static List<IPAddress> GetBroadcastAddresses()
    {
        var result = new List<IPAddress> { IPAddress.Broadcast };

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask?.GetAddressBytes();
                if (mask is not { Length: 4 }) continue;

                var bcast = new byte[4];
                for (int i = 0; i < 4; i++)
                    bcast[i] = (byte)(ip[i] | (mask[i] ^ 0xFF));

                var addr = new IPAddress(bcast);
                if (!result.Contains(addr)) result.Add(addr);
            }
        }
        return result;
    }

    public static List<string> GetLocalIPv4()
    {
        var list = new List<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ua.Address))
                    list.Add(ua.Address.ToString());
            }
        }
        if (list.Count == 0) list.Add("127.0.0.1");
        return list;
    }
}
