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
    private readonly TableLayoutPanel _grid = new();
    private readonly Label _overlay = new();
    private readonly Button _shareBtn = new();
    private readonly Label _status = new();

    // estado
    private List<RosterEntry> _roster = new();
    private SharePreset _mySelected = SharePreset.P720_30;
    private CodecKind _myCodec = VideoFactory.Default;

    // um tile de video por sender (varios simultaneos)
    private readonly Dictionary<int, PictureBox> _tiles = new();
    private readonly HashSet<int> _shownIds = new();

    // coalescing de frames (o mais recente por sender)
    private readonly Dictionary<int, Image> _pending = new();
    private bool _invokeQueued;
    private readonly object _frameLock = new();

    public RoomForm(RoomServer? ownedServer, string host, int port)
    {
        _ownedServer = ownedServer;
        _host = host;
        _port = port;

        Text = ownedServer != null ? $"Sala (hospedando) - {port}" : $"Sala - {host}:{port}";
        Width = 1120; Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 20, 24);

        BuildUi();
    }

    private void BuildUi()
    {
        // barra de baixo
        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Color.FromArgb(28, 28, 34) };
        _shareBtn.Text = "Compartilhar minha tela";
        _shareBtn.SetBounds(12, 10, 240, 38);
        _shareBtn.FlatStyle = FlatStyle.Flat;
        _shareBtn.ForeColor = Color.White;
        _shareBtn.BackColor = Color.FromArgb(60, 110, 200);
        _shareBtn.Font = new Font("Segoe UI Semibold", 11f);
        _shareBtn.FlatAppearance.BorderSize = 0;
        _shareBtn.Cursor = Cursors.Hand;
        _shareBtn.Click += OnShareClick;

        _status.AutoSize = false;
        _status.SetBounds(270, 0, 800, 58);
        _status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.ForeColor = Color.Gainsboro;
        _status.Text = "Conectando...";
        bottom.Controls.Add(_shareBtn);
        bottom.Controls.Add(_status);

        // lista de participantes (esquerda)
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

        // area de video (centro): grid de tiles + overlay
        var video = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
        _grid.Dock = DockStyle.Fill;
        _grid.BackColor = Color.Black;
        _grid.Padding = new Padding(2);

        _overlay.Dock = DockStyle.Fill;
        _overlay.AutoSize = false;
        _overlay.TextAlign = ContentAlignment.MiddleCenter;
        _overlay.ForeColor = Color.Gainsboro;
        _overlay.Font = new Font("Segoe UI", 14f);
        _overlay.Text = "Ninguem esta compartilhando";
        video.Controls.Add(_grid);
        video.Controls.Add(_overlay);

        // Fill primeiro
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
        _client.RosterUpdated += list => Ui(() => OnRoster(list));
        _client.ShareStateChanged += _ => Ui(UpdateShareButton);
        _client.FrameReceived += OnFrame;
        _client.Start();
    }

    private void OnShareClick(object? sender, EventArgs e)
    {
        if (_client.IsSharing) { _client.StopShare(); return; }
        var chosen = ChoosePreset();
        if (chosen is null) return;
        _mySelected = chosen.Value.preset;
        _myCodec = chosen.Value.codec;
        _client.StartShare(chosen.Value.preset, chosen.Value.codec);
    }

    private void OnRoster(IReadOnlyList<RosterEntry> list)
    {
        _roster = new List<RosterEntry>(list);

        // lista lateral
        _people.BeginUpdate();
        _people.Items.Clear();
        foreach (var p in _roster)
        {
            string tag = p.Sharing ? "  \u25CF" : "";
            if (p.Id == _client.MyId) tag += "  (voce)";
            _people.Items.Add(p.Name + tag);
        }
        _people.EndUpdate();

        // quem eu preciso renderizar = quem compartilha e nao sou eu
        var want = _roster.Where(p => p.Sharing && p.Id != _client.MyId).Select(p => p.Id).ToHashSet();
        if (!want.SetEquals(_shownIds))
            RebuildTiles(want);

        // atualiza rotulos dos tiles (nome + preset podem mudar)
        foreach (var p in _roster)
            if (_tiles.TryGetValue(p.Id, out var pb) && pb.Parent is Panel tile && tile.Controls.Count > 1
                && tile.Controls[1] is Label lab)
                lab.Text = $" {p.Name} — {Presets.Get(p.Preset).Label}";

        UpdateShareButton();
        UpdateOverlayAndStatus();
    }

    private void RebuildTiles(HashSet<int> want)
    {
        _grid.SuspendLayout();

        // remove tiles que sairam
        foreach (var id in _shownIds.Where(id => !want.Contains(id)).ToList())
        {
            if (_tiles.TryGetValue(id, out var pb))
            {
                pb.Image?.Dispose();
                pb.Parent?.Dispose();
                _tiles.Remove(id);
            }
        }

        _grid.Controls.Clear();
        _shownIds.Clear();
        foreach (var id in want) _shownIds.Add(id);

        int n = want.Count;
        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(n)));
        int rows = Math.Max(1, (int)Math.Ceiling(n / (double)cols));
        _grid.ColumnStyles.Clear();
        _grid.RowStyles.Clear();
        _grid.ColumnCount = cols;
        _grid.RowCount = rows;
        for (int c = 0; c < cols; c++) _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / cols));
        for (int rr = 0; rr < rows; rr++) _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));

        int idxCell = 0;
        foreach (var id in want)
        {
            if (!_tiles.TryGetValue(id, out var pb))
            {
                var tile = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black, Margin = new Padding(2) };
                pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
                var lab = new Label
                {
                    Dock = DockStyle.Bottom, Height = 22, AutoSize = false,
                    BackColor = Color.FromArgb(30, 30, 36), ForeColor = Color.Gainsboro,
                    TextAlign = ContentAlignment.MiddleLeft, Text = " ..."
                };
                tile.Controls.Add(pb);
                tile.Controls.Add(lab);
                _tiles[id] = pb;
            }
            var host = pb.Parent as Panel ?? new Panel { Dock = DockStyle.Fill };
            _grid.Controls.Add(host, idxCell % cols, idxCell / cols);
            idxCell++;
        }

        _grid.ResumeLayout();
    }

    private void OnFrame(int senderId, Image img)
    {
        if (IsDisposed) { img.Dispose(); return; }
        lock (_frameLock)
        {
            if (_pending.TryGetValue(senderId, out var old)) old.Dispose();
            _pending[senderId] = img;
            if (!_invokeQueued)
            {
                _invokeQueued = true;
                try { BeginInvoke(FlushPending); } catch { _invokeQueued = false; }
            }
        }
    }

    private void FlushPending()
    {
        List<KeyValuePair<int, Image>> batch;
        lock (_frameLock)
        {
            batch = _pending.ToList();
            _pending.Clear();
            _invokeQueued = false;
        }
        foreach (var kv in batch)
        {
            if (_tiles.TryGetValue(kv.Key, out var pb))
            {
                var old = pb.Image;
                pb.Image = kv.Value;
                old?.Dispose();
            }
            else kv.Value.Dispose();
        }
    }

    private void UpdateShareButton()
    {
        _shareBtn.Enabled = true; // agora da pra compartilhar mesmo com outros compartilhando
        _shareBtn.Text = _client.IsSharing ? "Parar de compartilhar" : "Compartilhar minha tela";
    }

    private void UpdateOverlayAndStatus()
    {
        int others = _shownIds.Count;
        int totalSharers = _roster.Count(p => p.Sharing);

        if (others == 0)
        {
            _overlay.Visible = true;
            _overlay.Text = _client.IsSharing
                ? $"Voce esta compartilhando: {Presets.Get(_mySelected).Label}\n(ninguem mais esta compartilhando)"
                : "Ninguem esta compartilhando";
        }
        else _overlay.Visible = false;

        string me = _client.IsSharing ? $"Voce compartilhando ({Presets.Get(_mySelected).Label}). " : "";
        _status.Text = $"{me}{totalSharers} compartilhando · {_roster.Count} na sala"
                       + (_ownedServer != null ? " · hospedando" : "");
    }

    private (SharePreset preset, CodecKind codec)? ChoosePreset()
    {
        using var f = new Form
        {
            Text = "Compartilhar tela",
            Width = 340, Height = 400,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false,
            BackColor = Color.FromArgb(28, 28, 34)
        };

        // ----- seletor de encoder -----
        var codecLabel = new Label { Text = "  Encoder", AutoSize = false, ForeColor = Color.Gainsboro };
        codecLabel.SetBounds(30, 12, 250, 20);

        var rbFfmpeg = new RadioButton
        {
            Text = "FFmpeg (H.264) — recomendado", ForeColor = Color.White, AutoSize = false,
            Checked = _myCodec == CodecKind.Ffmpeg
        };
        rbFfmpeg.SetBounds(30, 34, 270, 22);
        var rbJpeg = new RadioButton
        {
            Text = "JPEG (antigo, sempre funciona)", ForeColor = Color.White, AutoSize = false,
            Checked = _myCodec == CodecKind.Jpeg
        };
        rbJpeg.SetBounds(30, 58, 270, 22);
        if (!rbFfmpeg.Checked && !rbJpeg.Checked) rbFfmpeg.Checked = true;

        var qualLabel = new Label { Text = "  Qualidade", AutoSize = false, ForeColor = Color.Gainsboro };
        qualLabel.SetBounds(30, 90, 250, 20);

        f.Controls.Add(codecLabel);
        f.Controls.Add(rbFfmpeg);
        f.Controls.Add(rbJpeg);
        f.Controls.Add(qualLabel);

        (SharePreset preset, CodecKind codec)? result = null;
        int top = 114;
        foreach (var p in Presets.All)
        {
            var info = Presets.Get(p);
            var b = new Button { Text = info.Label };
            b.SetBounds(30, top, 270, 42);
            b.FlatStyle = FlatStyle.Flat;
            b.ForeColor = Color.White;
            b.BackColor = Color.FromArgb(60, 110, 200);
            b.Font = new Font("Segoe UI Semibold", 11f);
            b.FlatAppearance.BorderSize = 0;
            b.Cursor = Cursors.Hand;
            var captured = p;
            b.Click += (_, _) =>
            {
                var codec = rbJpeg.Checked ? CodecKind.Jpeg : CodecKind.Ffmpeg;
                result = (captured, codec);
                f.DialogResult = DialogResult.OK;
            };
            f.Controls.Add(b);
            top += 50;
        }
        f.ShowDialog(this);
        return result;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        try { _client?.Dispose(); } catch { }
        try { _ownedServer?.Dispose(); } catch { }
        foreach (var pb in _tiles.Values) pb.Image?.Dispose();
        base.OnFormClosed(e);
    }

    private void Ui(Action a)
    {
        if (IsDisposed) return;
        try { BeginInvoke(a); } catch { }
    }
}
