using System.Drawing;

namespace ScreenShareLan;

public sealed class RoomForm : Form
{
    private readonly RoomServer? _ownedServer;
    private readonly string _host;
    private readonly int _port;
    private RoomClient _client = null!;
    private bool _started;

    // UI
    private readonly ListBox _people = new();
    private readonly PictureBox _pic = new();
    private readonly Label _overlay = new();
    private readonly Button _shareBtn = new();
    private readonly Label _status = new();

    // estado
    private List<RosterEntry> _roster = new();
    private int _broadcasterId = -1;
    private SharePreset _presetInRoom = SharePreset.P720_30;
    private SharePreset _mySelected = SharePreset.P720_30;

    // coalescing de frames pra UI nao entupir
    private Image? _pending;
    private bool _invokeQueued;
    private readonly object _frameLock = new();

    public RoomForm(RoomServer? ownedServer, string host, int port)
    {
        _ownedServer = ownedServer;
        _host = host;
        _port = port;

        Text = ownedServer != null
            ? $"Sala (hospedando) - {port}"
            : $"Sala - {host}:{port}";
        Width = 1120; Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 20, 24);

        BuildUi();
    }

    private void BuildUi()
    {
        // ----- barra de baixo -----
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Color.FromArgb(28, 28, 34) };
        _shareBtn.Text = "Compartilhar tela";
        _shareBtn.SetBounds(12, 10, 220, 38);
        _shareBtn.FlatStyle = FlatStyle.Flat;
        _shareBtn.ForeColor = Color.White;
        _shareBtn.BackColor = Color.FromArgb(60, 110, 200);
        _shareBtn.Font = new Font("Segoe UI Semibold", 11f);
        _shareBtn.FlatAppearance.BorderSize = 0;
        _shareBtn.Cursor = Cursors.Hand;
        _shareBtn.Click += OnShareClick;

        _status.AutoSize = false;
        _status.SetBounds(250, 0, 820, 58);
        _status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.Gainsboro;
        _status.Text = "Conectando...";
        bottom.Controls.Add(_shareBtn);
        bottom.Controls.Add(_status);

        // ----- lista de participantes (esquerda) -----
        var left = new Panel { Dock = DockStyle.Left, Width = 220, BackColor = Color.FromArgb(24, 24, 30) };
        var peopleTitle = new Label
        {
            Dock = DockStyle.Top, Height = 34, Text = "  Na sala", AutoSize = false,
            ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 11f),
            TextAlign = ContentAlignment.MiddleLeft
        };
        _people.Dock = DockStyle.Fill;
        _people.BackColor = Color.FromArgb(24, 24, 30);
        _people.ForeColor = Color.White;
        _people.BorderStyle = BorderStyle.None;
        _people.Font = new Font("Segoe UI", 10.5f);
        left.Controls.Add(_people);
        left.Controls.Add(peopleTitle);

        // ----- video (centro) -----
        var video = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
        _pic.Dock = DockStyle.Fill;
        _pic.SizeMode = PictureBoxSizeMode.Zoom;
        _pic.BackColor = Color.Black;
        _pic.Visible = false;

        _overlay.Dock = DockStyle.Fill;
        _overlay.AutoSize = false;
        _overlay.TextAlign = ContentAlignment.MiddleCenter;
        _overlay.ForeColor = Color.Gainsboro;
        _overlay.Font = new Font("Segoe UI", 14f);
        _overlay.Text = "Ninguem esta compartilhando";
        video.Controls.Add(_pic);
        video.Controls.Add(_overlay);

        // ordem importa no dock: o Fill (video) tem que entrar PRIMEIRO,
        // pra os controles de borda (left/bottom) recortarem o espaco e o
        // video ficar com o que sobra.
        Controls.Add(video);
        Controls.Add(left);
        Controls.Add(bottom);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_started) return;
        _started = true;

        _client = new RoomClient(_host, _port, Environment.UserName);
        _client.StatusChanged += s => Ui(() => _status.Text = s);
        _client.RosterUpdated += (people, bId, preset) => Ui(() =>
        {
            _roster = new List<RosterEntry>(people);
            _broadcasterId = bId;
            _presetInRoom = preset;
            RefreshPeople();
            ApplyState();
        });
        _client.ShareStateChanged += _ => Ui(ApplyState);
        _client.FrameReceived += OnFrame;
        _client.Start();
    }

    private void OnShareClick(object? sender, EventArgs e)
    {
        if (_client.IsSharing)
        {
            _client.StopShare();
            return;
        }
        var chosen = ChoosePreset();
        if (chosen is null) return;
        _mySelected = chosen.Value;
        _client.StartShare(chosen.Value);
    }

    private void OnFrame(int senderId, Image img)
    {
        if (IsDisposed) { img.Dispose(); return; }
        Image? toDispose;
        lock (_frameLock)
        {
            toDispose = _pending;
            _pending = img;
            if (!_invokeQueued)
            {
                _invokeQueued = true;
                try { BeginInvoke(ShowPending); } catch { _invokeQueued = false; }
            }
        }
        toDispose?.Dispose();
    }

    private void ShowPending()
    {
        Image? img;
        lock (_frameLock) { img = _pending; _pending = null; _invokeQueued = false; }
        if (img is null) return;

        // so mostra se for de outra pessoa compartilhando
        if (_broadcasterId >= 0 && _broadcasterId != _client.MyId)
        {
            var old = _pic.Image;
            _pic.Image = img;
            old?.Dispose();
        }
        else img.Dispose();
    }

    private void RefreshPeople()
    {
        _people.BeginUpdate();
        _people.Items.Clear();
        foreach (var p in _roster)
        {
            string tag = "";
            if (p.Id == _broadcasterId) tag = "  \u25CF compartilhando";
            if (p.Id == _client.MyId) tag += "  (voce)";
            _people.Items.Add(p.Name + tag);
        }
        _people.EndUpdate();
    }

    private void ApplyState()
    {
        bool someoneElse = _broadcasterId >= 0 && _broadcasterId != _client.MyId;
        bool meBroadcasting = _client.IsSharing || _broadcasterId == _client.MyId;

        if (someoneElse)
        {
            _overlay.Visible = false;
            _pic.Visible = true;
            _shareBtn.Enabled = false;
            _shareBtn.Text = "Compartilhar tela";
            var name = _roster.FirstOrDefault(x => x.Id == _broadcasterId)?.Name ?? "alguem";
            _status.Text = $"Assistindo {name} — {Presets.Get(_presetInRoom).Label}";
        }
        else if (meBroadcasting)
        {
            _pic.Visible = false;
            ClearPic();
            _overlay.Visible = true;
            _overlay.Text = $"Voce esta compartilhando\n{Presets.Get(_mySelected).Label}";
            _shareBtn.Enabled = true;
            _shareBtn.Text = "Parar de compartilhar";
            _status.Text = $"Transmitindo — {_roster.Count - 1} espectador(es)";
        }
        else
        {
            _pic.Visible = false;
            ClearPic();
            _overlay.Visible = true;
            _overlay.Text = "Ninguem esta compartilhando";
            _shareBtn.Enabled = true;
            _shareBtn.Text = "Compartilhar tela";
            _status.Text = _ownedServer != null ? "Voce esta hospedando esta sala." : "Conectado.";
        }
    }

    private void ClearPic()
    {
        var old = _pic.Image;
        _pic.Image = null;
        old?.Dispose();
    }

    private SharePreset? ChoosePreset()
    {
        using var f = new Form
        {
            Text = "Qualidade do compartilhamento",
            Width = 320, Height = 300,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(28, 28, 34)
        };

        SharePreset? result = null;
        int top = 20;
        foreach (var p in Presets.All)
        {
            var info = Presets.Get(p);
            var b = new Button { Text = info.Label };
            b.SetBounds(30, top, 250, 46);
            b.FlatStyle = FlatStyle.Flat;
            b.ForeColor = Color.White;
            b.BackColor = Color.FromArgb(60, 110, 200);
            b.Font = new Font("Segoe UI Semibold", 11f);
            b.FlatAppearance.BorderSize = 0;
            b.Cursor = Cursors.Hand;
            var captured = p;
            b.Click += (_, _) => { result = captured; f.DialogResult = DialogResult.OK; };
            f.Controls.Add(b);
            top += 56;
        }
        f.ShowDialog(this);
        return result;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _client?.Dispose(); } catch { }
        try { _ownedServer?.Dispose(); } catch { }
        ClearPic();
        base.OnFormClosed(e);
    }

    private void Ui(Action a)
    {
        if (IsDisposed) return;
        try { BeginInvoke(a); } catch { }
    }
}
