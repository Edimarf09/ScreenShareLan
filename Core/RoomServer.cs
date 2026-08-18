using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScreenShareLan;

/// <summary>
/// Servidor da sala (roda no host). So roteia bytes (relay), nao decodifica video.
/// Agora suporta VARIOS broadcasters ao mesmo tempo: cada participante tem seu
/// proprio estado de compartilhamento (Sharing + Preset).
/// </summary>
public sealed class RoomServer : IDisposable
{
    private sealed class Participant
    {
        public int Id;
        public string Name = "";
        public IPEndPoint EndPoint = null!;
        public DateTime LastSeen;
        public bool Sharing;                       // <-- por participante
        public SharePreset Preset;                 // <-- por participante
        public CodecKind Codec;                    // <-- codec que essa pessoa esta usando
    }

    private readonly int _port;
    private Socket? _sock;
    private CancellationTokenSource? _cts;

    private readonly object _lock = new();
    private readonly Dictionary<IPEndPoint, Participant> _byEp = new();
    private int _nextId = 1;

    public event Action<string>? Log;
    public int Port => _port;

    public RoomServer(int port) => _port = port;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true
        };
        try { _sock.ReceiveBufferSize = 1 << 20; _sock.SendBufferSize = 1 << 20; } catch { }
        _sock.Bind(new IPEndPoint(IPAddress.Any, _port));

        _ = Task.Run(() => ReceiveLoop(_cts.Token));
        _ = Task.Run(() => MaintenanceLoop(_cts.Token));
        Log?.Invoke($"Servidor da sala no ar na porta {_port}.");
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[2048];
        var from = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await _sock!.ReceiveFromAsync(buf, SocketFlags.None, from, ct);
                Handle(buf, r.ReceivedBytes, (IPEndPoint)r.RemoteEndPoint);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log?.Invoke($"Erro no receive: {ex.Message}"); }
    }

    private void Handle(byte[] buf, int len, IPEndPoint ep)
    {
        if (len < 1) return;

        switch ((MsgType)buf[0])
        {
            case MsgType.Join:
            {
                string name = len > 1 ? Encoding.UTF8.GetString(buf, 1, len - 1) : "?";
                int id;
                lock (_lock)
                {
                    if (!_byEp.TryGetValue(ep, out var p))
                    {
                        p = new Participant { Id = _nextId++, EndPoint = ep };
                        _byEp[ep] = p;
                    }
                    p.Name = string.IsNullOrWhiteSpace(name) ? $"user{p.Id}" : name;
                    p.LastSeen = DateTime.UtcNow;
                    id = p.Id;
                }
                SendWelcome(ep, id);
                BroadcastRoster();
                Log?.Invoke($"Entrou: {name} ({ep})");
                break;
            }
            case MsgType.Hello:
                lock (_lock)
                    if (_byEp.TryGetValue(ep, out var p)) p.LastSeen = DateTime.UtcNow;
                break;

            case MsgType.Leave:
                lock (_lock) _byEp.Remove(ep);
                BroadcastRoster();
                break;

            case MsgType.StartShare:
            {
                if (len < 7) break;
                var preset = (SharePreset)buf[5];
                var codec = (CodecKind)buf[6];
                lock (_lock)
                {
                    if (_byEp.TryGetValue(ep, out var p))
                    {
                        p.Sharing = true;
                        p.Preset = preset;
                        p.Codec = codec;
                        p.LastSeen = DateTime.UtcNow;
                    }
                }
                BroadcastRoster();
                break;
            }
            case MsgType.StopShare:
                lock (_lock)
                    if (_byEp.TryGetValue(ep, out var p)) p.Sharing = false;
                BroadcastRoster();
                break;

            case MsgType.Video:
                RelayVideo(buf, len, ep);
                break;
        }
    }

    // Agora relaya o video de QUALQUER participante que esteja compartilhando.
    private void RelayVideo(byte[] buf, int len, IPEndPoint sender)
    {
        Participant[] targets;
        lock (_lock)
        {
            if (!_byEp.TryGetValue(sender, out var sp) || !sp.Sharing)
                return; // so quem esta compartilhando pode transmitir
            sp.LastSeen = DateTime.UtcNow;
            targets = _byEp.Values.Where(p => !p.EndPoint.Equals(sender)).ToArray();
        }
        foreach (var t in targets) SendTo(buf, len, t.EndPoint);
    }

    // ---------- envio ----------
    private readonly object _sendLock = new();
    private void SendTo(byte[] data, int len, IPEndPoint ep)
    {
        try { lock (_sendLock) _sock!.SendTo(data, 0, len, SocketFlags.None, ep); }
        catch { }
    }

    private void SendWelcome(IPEndPoint ep, int id)
    {
        var b = new byte[5];
        b[0] = (byte)MsgType.Welcome;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), id);
        SendTo(b, b.Length, ep);
    }

    // Roster: cada participante carrega sharing + preset + codec.
    // [type][ushort count] then per p: [int id][byte sharing][byte preset][byte codec][byte nameLen][name]
    private void BroadcastRoster()
    {
        Participant[] list;
        lock (_lock) list = _byEp.Values.ToArray();

        using var ms = new MemoryStream(256);
        ms.WriteByte((byte)MsgType.Roster);
        WriteUShort(ms, (ushort)list.Length);
        foreach (var p in list)
        {
            WriteInt(ms, p.Id);
            ms.WriteByte((byte)(p.Sharing ? 1 : 0));
            ms.WriteByte((byte)p.Preset);
            ms.WriteByte((byte)p.Codec);
            var nb = Encoding.UTF8.GetBytes(p.Name);
            if (nb.Length > 255) nb = nb[..255];
            ms.WriteByte((byte)nb.Length);
            ms.Write(nb, 0, nb.Length);
        }
        var packet = ms.ToArray();
        foreach (var p in list) SendTo(packet, packet.Length, p.EndPoint);
    }

    private static void WriteInt(Stream s, int v)
    {
        Span<byte> t = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(t, v);
        s.Write(t);
    }
    private static void WriteUShort(Stream s, ushort v)
    {
        Span<byte> t = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(t, v);
        s.Write(t);
    }

    private async Task MaintenanceLoop(CancellationToken ct)
    {
        var announce = Encoding.UTF8.GetBytes(
            Protocol.BuildAnnounce(Environment.MachineName, _port));
        int tick = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool changed = false;
                lock (_lock)
                {
                    var dead = _byEp.Where(k => (DateTime.UtcNow - k.Value.LastSeen).TotalSeconds > 5)
                                    .Select(k => k.Key).ToList();
                    foreach (var k in dead) { _byEp.Remove(k); changed = true; }
                }
                if (changed) BroadcastRoster();
                if (tick % 2 == 0) BroadcastRoster();

                foreach (var b in NetworkUtils.GetBroadcastAddresses())
                    SendTo(announce, announce.Length, new IPEndPoint(b, Protocol.DiscoveryPort));

                tick++;
                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { _sock?.Close(); } catch { }
        try { _sock?.Dispose(); } catch { }
    }
}
