using System.Buffers.Binary;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScreenShareLan;

public sealed record RosterEntry(int Id, string Name);

/// <summary>
/// Participante da sala (host e convidados usam esta mesma classe).
/// Fala tudo UDP com o servidor: entra, manda keepalive, pode compartilhar a
/// tela (captura -> JPEG -> fragmenta -> envia) e recebe o video de quem estiver
/// compartilhando (remonta os fragmentos -> decodifica -> dispara FrameReceived).
/// </summary>
public sealed class RoomClient : IDisposable
{
    private readonly string _serverHost;
    private readonly int _serverPort;
    private readonly string _name;

    private Socket? _sock;
    private IPEndPoint? _serverEp;
    private CancellationTokenSource? _cts;

    private int _myId = -1;
    private volatile bool _sharing;
    private CancellationTokenSource? _captureCts;
    private uint _frameId;
    private readonly object _sendLock = new();

    private readonly Reassembler _reasm = new();

    public int MyId => _myId;
    public bool IsSharing => _sharing;

    public event Action<string>? StatusChanged;
    public event Action<IReadOnlyList<RosterEntry>, int, SharePreset>? RosterUpdated; // (pessoas, broadcasterId(-1=ninguem), preset)
    public event Action<int, Image>? FrameReceived; // (senderId, frame)
    public event Action<bool>? ShareStateChanged;

    public RoomClient(string serverHost, int serverPort, string name)
    {
        _serverHost = serverHost;
        _serverPort = serverPort;
        _name = string.IsNullOrWhiteSpace(name) ? "user" : name;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        var addr = ResolveHost(_serverHost);
        _serverEp = new IPEndPoint(addr, _serverPort);

        _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try { _sock.ReceiveBufferSize = 1 << 20; _sock.SendBufferSize = 1 << 20; } catch { }
        _sock.Bind(new IPEndPoint(IPAddress.Any, 0));

        _ = Task.Run(() => ReceiveLoop(_cts.Token));
        _ = Task.Run(() => KeepAliveLoop(_cts.Token));

        StatusChanged?.Invoke($"Entrando na sala {_serverHost}:{_serverPort}...");
        SendJoin();
    }

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        var addrs = Dns.GetHostAddresses(host);
        foreach (var a in addrs)
            if (a.AddressFamily == AddressFamily.InterNetwork) return a;
        return IPAddress.Loopback;
    }

    // ---------- envio de controle ----------
    private void SendRaw(byte[] data, int len)
    {
        try { lock (_sendLock) _sock!.SendTo(data, 0, len, SocketFlags.None, _serverEp!); }
        catch { }
    }

    private void SendJoin()
    {
        var nb = Encoding.UTF8.GetBytes(_name);
        var b = new byte[1 + nb.Length];
        b[0] = (byte)MsgType.Join;
        Array.Copy(nb, 0, b, 1, nb.Length);
        SendRaw(b, b.Length);
    }

    private void SendIdMsg(MsgType type)
    {
        var b = new byte[5];
        b[0] = (byte)type;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), _myId);
        SendRaw(b, b.Length);
    }

    private async Task KeepAliveLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_myId < 0) SendJoin();
                else SendIdMsg(MsgType.Hello);
                await Task.Delay(1500, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------- recepcao ----------
    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[2048];
        var from = new IPEndPoint(IPAddress.Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var r = await _sock!.ReceiveFromAsync(buf, SocketFlags.None, from, ct);
                Handle(buf, r.ReceivedBytes);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { StatusChanged?.Invoke($"Conexao caiu: {ex.Message}"); }
    }

    private void Handle(byte[] buf, int len)
    {
        if (len < 1) return;
        switch ((MsgType)buf[0])
        {
            case MsgType.Welcome:
                if (len >= 5)
                {
                    _myId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(1));
                    StatusChanged?.Invoke("Conectado na sala.");
                }
                break;

            case MsgType.Roster:
                ParseRoster(buf, len);
                break;

            case MsgType.Video:
                HandleVideo(buf, len);
                break;
        }
    }

    private void ParseRoster(byte[] buf, int len)
    {
        try
        {
            int o = 1;
            bool present = buf[o++] != 0;
            int bId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(o)); o += 4;
            var preset = (SharePreset)buf[o++];
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(o)); o += 2;

            var people = new List<RosterEntry>(count);
            for (int i = 0; i < count && o + 5 <= len; i++)
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(o)); o += 4;
                int nameLen = buf[o++];
                if (o + nameLen > len) break;
                string name = Encoding.UTF8.GetString(buf, o, nameLen); o += nameLen;
                people.Add(new RosterEntry(id, name));
            }
            RosterUpdated?.Invoke(people, present ? bId : -1, preset);
        }
        catch { /* pacote malformado, ignora */ }
    }

    private void HandleVideo(byte[] buf, int len)
    {
        if (len < Protocol.VideoHeaderSize) return;
        int senderId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(1));
        if (senderId == _myId) return; // nao ecoa o proprio

        uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(5));
        ushort idx = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(9));
        ushort cnt = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(11));
        ushort plen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(13));
        if (Protocol.VideoHeaderSize + plen > len) return;

        var complete = _reasm.Add(senderId, frameId, idx, cnt,
            buf.AsSpan(Protocol.VideoHeaderSize, plen));
        if (complete is null) return;

        try
        {
            using var ms = new MemoryStream(complete, writable: false);
            using var decoded = Image.FromStream(ms);
            var copy = new Bitmap(decoded);
            FrameReceived?.Invoke(senderId, copy);
        }
        catch { /* frame corrompido por perda de pacote: ignora */ }
    }

    // ---------- compartilhamento ----------
    public void StartShare(SharePreset preset)
    {
        if (_sharing || _myId < 0) return;
        _sharing = true;
        ShareStateChanged?.Invoke(true);

        var b = new byte[6];
        b[0] = (byte)MsgType.StartShare;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), _myId);
        b[5] = (byte)preset;
        SendRaw(b, b.Length);

        _captureCts = new CancellationTokenSource();
        var ct = _captureCts.Token;
        _ = Task.Run(() => CaptureLoop(preset, ct));
    }

    public void StopShare()
    {
        if (!_sharing) return;
        _sharing = false;
        try { _captureCts?.Cancel(); } catch { }
        SendIdMsg(MsgType.StopShare);
        ShareStateChanged?.Invoke(false);
    }

    private void CaptureLoop(SharePreset preset, CancellationToken ct)
    {
        var info = Presets.Get(preset);
        int frameMs = 1000 / info.Fps;
        var sw = new System.Diagnostics.Stopwatch();
        var header = new byte[Protocol.VideoHeaderSize];

        try
        {
            using var cap = new ScreenCapture(info.Width, info.Height, info.Quality, drawCursor: true);
            while (!ct.IsCancellationRequested)
            {
                sw.Restart();
                byte[] jpeg;
                try { jpeg = cap.CaptureJpeg(); }
                catch { break; }

                uint fid = unchecked(_frameId++);
                int total = jpeg.Length;
                int count = (total + Protocol.MaxUdpPayload - 1) / Protocol.MaxUdpPayload;
                if (count > ushort.MaxValue) continue; // frame absurdo, pula

                for (int i = 0; i < count; i++)
                {
                    int off = i * Protocol.MaxUdpPayload;
                    int plen = Math.Min(Protocol.MaxUdpPayload, total - off);

                    header[0] = (byte)MsgType.Video;
                    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), _myId);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(5), fid);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(9), (ushort)i);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(11), (ushort)count);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(13), (ushort)plen);

                    // monta o datagrama (header + pedaco) e envia
                    var packet = new byte[Protocol.VideoHeaderSize + plen];
                    Buffer.BlockCopy(header, 0, packet, 0, Protocol.VideoHeaderSize);
                    Buffer.BlockCopy(jpeg, off, packet, Protocol.VideoHeaderSize, plen);
                    SendRaw(packet, packet.Length);
                }

                int wait = frameMs - (int)sw.ElapsedMilliseconds;
                if (wait > 0) Thread.Sleep(wait);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { StopShare(); } catch { }
        try { if (_myId >= 0) SendIdMsg(MsgType.Leave); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _sock?.Close(); } catch { }
        try { _sock?.Dispose(); } catch { }
    }
}

/// <summary>Remonta os fragmentos de um frame. Frame incompleto e descartado quando chega um novo.</summary>
internal sealed class Reassembler
{
    private int _senderId = -1;
    private uint _frameId;
    private int _count;
    private int _received;
    private byte[]?[] _parts = Array.Empty<byte[]?>();
    private readonly object _lock = new();

    public byte[]? Add(int senderId, uint frameId, int idx, int count, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            bool sameFrame = senderId == _senderId && frameId == _frameId;
            if (!sameFrame)
            {
                // ignora sobra de frame antigo do mesmo sender
                if (senderId == _senderId && (int)(frameId - _frameId) < 0) return null;
                _senderId = senderId;
                _frameId = frameId;
                _count = count;
                _received = 0;
                _parts = new byte[count][];
            }

            if (idx < 0 || idx >= _count) return null;
            if (_parts[idx] != null) return null; // duplicado

            _parts[idx] = data.ToArray();
            _received++;

            if (_received != _count) return null;

            int total = 0;
            for (int i = 0; i < _count; i++) total += _parts[i]!.Length;
            var full = new byte[total];
            int o = 0;
            for (int i = 0; i < _count; i++)
            {
                var part = _parts[i]!;
                Buffer.BlockCopy(part, 0, full, o, part.Length);
                o += part.Length;
            }
            // zera pra nao remontar de novo
            _parts = Array.Empty<byte[]?>();
            _count = 0;
            _received = 0;
            return full;
        }
    }
}
