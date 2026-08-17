using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScreenShareLan;

/// <summary>
/// Servidor da sala (roda no host). Tudo UDP:
///  - controle: JOIN/HELLO/LEAVE/START/STOP
///  - video: recebe do broadcaster ativo e faz relay pra todos os outros
///  - envia ROSTER periodico e anuncia a sala por broadcast (pra "Lista da LAN")
/// </summary>
public sealed class RoomServer : IDisposable
{
    private sealed class Participant
    {
        public int Id;
        public string Name = "";
        public IPEndPoint EndPoint = null!;
        public DateTime LastSeen;
    }

    private readonly int _port;
    private Socket? _sock;
    private CancellationTokenSource? _cts;

    private readonly object _lock = new();
    private readonly Dictionary<IPEndPoint, Participant> _byEp = new();
    private int _nextId = 1;
    private int _broadcasterId = -1;
    private SharePreset _preset = SharePreset.P720_30;

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
        // buffers maiores ajudam no video em rajada
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
                var ep = (IPEndPoint)r.RemoteEndPoint;
                Handle(buf, r.ReceivedBytes, ep);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { Log?.Invoke($"Erro no receive: {ex.Message}"); }
    }

    private void Handle(byte[] buf, int len, IPEndPoint ep)
    {
        if (len < 1) return;
        var type = (MsgType)buf[0];

        switch (type)
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
            {
                lock (_lock)
                    if (_byEp.TryGetValue(ep, out var p)) p.LastSeen = DateTime.UtcNow;
                break;
            }
            case MsgType.Leave:
            {
                RemoveParticipant(ep);
                BroadcastRoster();
                break;
            }
            case MsgType.StartShare:
            {
                if (len < 6) break;
                var preset = (SharePreset)buf[5];
                lock (_lock)
                {
                    if (_byEp.TryGetValue(ep, out var p))
                    {
                        _broadcasterId = p.Id;
                        _preset = preset;
                        p.LastSeen = DateTime.UtcNow;
                    }
                }
                BroadcastRoster();
                Log?.Invoke($"Compartilhando: {Presets.Get(preset).Label}");
                break;
            }
            case MsgType.StopShare:
            {
                lock (_lock)
                {
                    if (_byEp.TryGetValue(ep, out var p) && p.Id == _broadcasterId)
                        _broadcasterId = -1;
                }
                BroadcastRoster();
                break;
            }
            case MsgType.Video:
            {
                RelayVideo(buf, len, ep);
                break;
            }
        }
    }

    private void RelayVideo(byte[] buf, int len, IPEndPoint sender)
    {
        Participant[] targets;
        lock (_lock)
        {
            if (!_byEp.TryGetValue(sender, out var sp) || sp.Id != _broadcasterId)
                return; // so o broadcaster atual pode transmitir
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

    private void BroadcastRoster()
    {
        Participant[] list;
        int bId; SharePreset preset;
        lock (_lock)
        {
            list = _byEp.Values.ToArray();
            bId = _broadcasterId;
            preset = _preset;
        }

        // [type][broadcasterPresent][int bId][preset][ushort count] then per p: [int id][byte nameLen][name]
        using var ms = new MemoryStream(256);
        ms.WriteByte((byte)MsgType.Roster);
        ms.WriteByte((byte)(bId >= 0 ? 1 : 0));
        WriteInt(ms, bId);
        ms.WriteByte((byte)preset);
        WriteUShort(ms, (ushort)list.Length);
        foreach (var p in list)
        {
            WriteInt(ms, p.Id);
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

    private void RemoveParticipant(IPEndPoint ep)
    {
        lock (_lock)
        {
            if (_byEp.Remove(ep, out var p) && p.Id == _broadcasterId)
                _broadcasterId = -1;
        }
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
                // timeout de quem sumiu (>5s)
                bool changed = false;
                lock (_lock)
                {
                    var dead = _byEp.Where(k => (DateTime.UtcNow - k.Value.LastSeen).TotalSeconds > 5)
                                    .Select(k => k.Key).ToList();
                    foreach (var k in dead)
                    {
                        if (_byEp.Remove(k, out var p) && p.Id == _broadcasterId)
                            _broadcasterId = -1;
                        changed = true;
                    }
                }
                if (changed) BroadcastRoster();

                // roster periodico
                if (tick % 2 == 0) BroadcastRoster();

                // anuncio na LAN (a cada ~1.5s)
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
