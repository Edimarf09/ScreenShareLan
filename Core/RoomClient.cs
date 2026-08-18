using System.Buffers.Binary;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScreenShareLan;

// Cada participante carrega seu estado de compartilhamento + o codec que esta usando.
public sealed record RosterEntry(int Id, string Name, bool Sharing, SharePreset Preset, CodecKind Codec);

/// <summary>
/// Participante da sala (host e convidados usam a mesma classe). Tudo UDP.
/// Varios broadcasters ao mesmo tempo: 1 reassembler + 1 decoder por sender.
/// O encoder e escolhido por quem compartilha; o codec vai no protocolo pra quem
/// assiste abrir o decoder certo.
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
    private IVideoEncoder? _encoder;
    private uint _frameId;
    private readonly object _sendLock = new();

    // recepcao (tudo acessado pela thread do ReceiveLoop; locks cobrem o Dispose da UI)
    private readonly Dictionary<int, Reassembler> _reasm = new();
    private readonly object _reasmLock = new();

    private readonly Dictionary<int, IVideoDecoder> _decoders = new();
    private readonly Dictionary<int, (SharePreset preset, CodecKind codec)> _decoderCfg = new();
    private readonly Dictionary<int, (SharePreset preset, CodecKind codec)> _senderInfo = new();
    private readonly HashSet<int> _keyed = new();
    private readonly object _decLock = new();

    public int MyId => _myId;
    public bool IsSharing => _sharing;

    public event Action<string>? StatusChanged;
    public event Action<IReadOnlyList<RosterEntry>>? RosterUpdated;
    public event Action<int, Image>? FrameReceived;   // (senderId, frame)
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
        _serverEp = new IPEndPoint(ResolveHost(_serverHost), _serverPort);

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
        foreach (var a in Dns.GetHostAddresses(host))
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

    // [type][ushort count] then per p: [int id][byte sharing][byte preset][byte codec][byte nameLen][name]
    private void ParseRoster(byte[] buf, int len)
    {
        try
        {
            int o = 1;
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(o)); o += 2;

            var people = new List<RosterEntry>(count);
            for (int i = 0; i < count && o + 8 <= len; i++)
            {
                int id = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(o)); o += 4;
                bool sharing = buf[o++] != 0;
                var preset = (SharePreset)buf[o++];
                var codec = (CodecKind)buf[o++];
                int nameLen = buf[o++];
                if (o + nameLen > len) break;
                string name = Encoding.UTF8.GetString(buf, o, nameLen); o += nameLen;
                people.Add(new RosterEntry(id, name, sharing, preset, codec));
            }

            var active = people.Where(p => p.Sharing)
                               .ToDictionary(p => p.Id, p => (p.Preset, p.Codec));

            lock (_decLock)
            {
                _senderInfo.Clear();
                foreach (var kv in active) _senderInfo[kv.Key] = kv.Value;

                foreach (var id in _decoders.Keys.Where(k => !active.ContainsKey(k)).ToList())
                {
                    if (_decoders.Remove(id, out var d)) { try { d.Dispose(); } catch { } }
                    _decoderCfg.Remove(id);
                    _keyed.Remove(id);
                }
            }
            lock (_reasmLock)
            {
                foreach (var k in _reasm.Keys.Where(k => !active.ContainsKey(k)).ToList())
                    _reasm.Remove(k);
            }

            RosterUpdated?.Invoke(people);
        }
        catch { /* pacote malformado */ }
    }

    private void HandleVideo(byte[] buf, int len)
    {
        if (len < Protocol.VideoHeaderSize) return;
        int senderId = BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(1));
        if (senderId == _myId) return;

        uint frameId = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(5));
        ushort idx = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(9));
        ushort cnt = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(11));
        ushort plen = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(13));
        byte flags = buf[15];
        if (Protocol.VideoHeaderSize + plen > len) return;
        bool keyPacket = (flags & Protocol.FlagKeyFrame) != 0;

        Reassembler r;
        lock (_reasmLock)
        {
            if (!_reasm.TryGetValue(senderId, out var ex)) { r = new Reassembler(); _reasm[senderId] = r; }
            else r = ex;
        }

        var ef = r.Add(frameId, idx, cnt, keyPacket, buf.AsSpan(Protocol.VideoHeaderSize, plen));
        if (ef is null) return;
        var frame = ef.Value;

        lock (_decLock)
        {
            if (!_senderInfo.TryGetValue(senderId, out var info)) return; // ainda sem roster desse sender

            // mudou codec/preset -> reinicia decoder e exige keyframe de novo
            if (_decoderCfg.TryGetValue(senderId, out var cfg) && !cfg.Equals(info))
            {
                if (_decoders.Remove(senderId, out var old)) { try { old.Dispose(); } catch { } }
                _decoderCfg.Remove(senderId);
                _keyed.Remove(senderId);
            }

            // so comeca a decodificar a partir de um keyframe (evita lixo)
            if (!_keyed.Contains(senderId))
            {
                if (!frame.KeyFrame) return;
                _keyed.Add(senderId);
            }

            if (!_decoders.TryGetValue(senderId, out var dec))
            {
                int sid = senderId;
                dec = VideoFactory.CreateDecoder(info.codec, info.preset);
                dec.FrameDecoded += img => FrameReceived?.Invoke(sid, img);
                _decoders[senderId] = dec;
                _decoderCfg[senderId] = info;
            }
            dec.Push(frame);
        }
    }

    // ---------- compartilhamento ----------
    public void StartShare(SharePreset preset, CodecKind codec)
    {
        if (_sharing || _myId < 0) return;
        _sharing = true;
        ShareStateChanged?.Invoke(true);

        // msg: [type][int id][byte preset][byte codec]
        var b = new byte[7];
        b[0] = (byte)MsgType.StartShare;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(1), _myId);
        b[5] = (byte)preset;
        b[6] = (byte)codec;
        SendRaw(b, b.Length);

        _encoder = VideoFactory.CreateEncoder(codec, preset);
        _encoder.FrameReady += SendEncoded;
        _encoder.Start();
    }

    public void StopShare()
    {
        if (!_sharing) return;
        _sharing = false;
        try { _encoder?.Dispose(); } catch { }
        _encoder = null;
        SendIdMsg(MsgType.StopShare);
        ShareStateChanged?.Invoke(false);
    }

    // fragmenta 1 frame codificado em pacotes UDP e envia pro servidor (que faz relay)
    private void SendEncoded(EncodedFrame ef)
    {
        int total = ef.Data.Length;
        if (total == 0) return;

        uint fid = unchecked(_frameId++);
        int count = (total + Protocol.MaxUdpPayload - 1) / Protocol.MaxUdpPayload;
        if (count > ushort.MaxValue) return;
        byte flags = ef.KeyFrame ? Protocol.FlagKeyFrame : (byte)0;

        for (int i = 0; i < count; i++)
        {
            int off = i * Protocol.MaxUdpPayload;
            int plen = Math.Min(Protocol.MaxUdpPayload, total - off);

            var packet = new byte[Protocol.VideoHeaderSize + plen];
            packet[0] = (byte)MsgType.Video;
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(1), _myId);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), fid);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(9), (ushort)i);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(11), (ushort)count);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(13), (ushort)plen);
            packet[15] = flags;
            Buffer.BlockCopy(ef.Data, off, packet, Protocol.VideoHeaderSize, plen);
            SendRaw(packet, packet.Length);
        }
    }

    public void Dispose()
    {
        try { StopShare(); } catch { }
        try { if (_myId >= 0) SendIdMsg(MsgType.Leave); } catch { }
        try { _cts?.Cancel(); } catch { }
        lock (_decLock)
        {
            foreach (var d in _decoders.Values) { try { d.Dispose(); } catch { } }
            _decoders.Clear();
        }
        try { _sock?.Close(); } catch { }
        try { _sock?.Dispose(); } catch { }
    }
}

/// <summary>Remonta os fragmentos de um frame de UM sender. Frame incompleto e descartado quando chega um novo.</summary>
internal sealed class Reassembler
{
    private uint _frameId;
    private bool _has;
    private bool _key;
    private int _count;
    private int _received;
    private byte[]?[] _parts = Array.Empty<byte[]?>();
    private readonly object _lock = new();

    public EncodedFrame? Add(uint frameId, int idx, int count, bool keyFrame, ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (!_has || frameId != _frameId)
            {
                if (_has && (int)(frameId - _frameId) < 0) return null; // sobra de frame antigo
                _has = true;
                _frameId = frameId;
                _count = count;
                _received = 0;
                _key = keyFrame;
                _parts = new byte[count][];
            }

            if (idx < 0 || idx >= _count) return null;
            if (_parts[idx] != null) return null;

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
            bool key = _key;
            _parts = Array.Empty<byte[]?>();
            _count = 0; _received = 0; _has = false;
            return new EncodedFrame(full, key);
        }
    }
}
