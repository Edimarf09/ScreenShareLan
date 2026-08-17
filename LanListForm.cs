using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScreenShareLan;

public sealed class LanListForm : Form
{
    private readonly ListBox _list = new();
    private readonly Dictionary<string, (string name, DateTime seen)> _found = new();
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private readonly System.Windows.Forms.Timer _prune = new() { Interval = 2000 };

    public LanListForm()
    {
        Text = "Salas na LAN - ScreenShareLan";
        Width = 540; Height = 420;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 24, 28);

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 44,
            ForeColor = Color.Gainsboro, Padding = new Padding(12, 10, 12, 0),
            Text = "Procurando salas na LAN / LAN virtual... de dois cliques pra entrar."
        };
        _list.Dock = DockStyle.Fill;
        _list.BackColor = Color.FromArgb(16, 16, 20);
        _list.ForeColor = Color.White;
        _list.BorderStyle = BorderStyle.None;
        _list.Font = new Font("Segoe UI", 11f);
        _list.DoubleClick += OnConnect;

        Controls.Add(_list);
        Controls.Add(hint);

        StartListening();
        _prune.Tick += (_, _) => Prune();
        _prune.Start();
    }

    private void StartListening()
    {
        _cts = new CancellationTokenSource();
        _udp = new UdpClient();
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
        _ = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var res = await _udp!.ReceiveAsync(ct);
                var text = Encoding.UTF8.GetString(res.Buffer);
                if (!Protocol.TryParseAnnounce(text, out var name, out var roomPort)) continue;

                var ip = res.RemoteEndPoint.Address.ToString();
                var key = $"{ip}:{roomPort}";
                bool isNew;
                lock (_found)
                {
                    isNew = !_found.ContainsKey(key);
                    _found[key] = (name, DateTime.UtcNow);
                }
                if (isNew && !IsDisposed) try { BeginInvoke(Refresh); } catch { }
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private void Refresh()
    {
        _list.Items.Clear();
        lock (_found)
            foreach (var kv in _found)
                _list.Items.Add($"{kv.Value.name}   ({kv.Key})");
    }

    private void Prune()
    {
        bool changed = false;
        lock (_found)
        {
            var dead = _found.Where(k => (DateTime.UtcNow - k.Value.seen).TotalSeconds > 6)
                             .Select(k => k.Key).ToList();
            foreach (var k in dead) { _found.Remove(k); changed = true; }
        }
        if (changed) Refresh();
    }

    private void OnConnect(object? sender, EventArgs e)
    {
        if (_list.SelectedItem is null) return;
        var text = _list.SelectedItem.ToString()!;
        int a = text.LastIndexOf('(');
        int b = text.LastIndexOf(')');
        if (a < 0 || b < 0 || b <= a) return;

        var hp = text.Substring(a + 1, b - a - 1);
        if (MainForm.TryParseHostPort(hp, out var host, out var port))
            new RoomForm(null, host, port).Show();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _prune.Dispose();
        base.OnFormClosed(e);
    }
}
