using System.Drawing;

namespace ScreenShareLan;

public sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "ScreenShareLan";
        Width = 420;
        Height = 340;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        BackColor = Color.FromArgb(24, 24, 28);

        var title = new Label
        {
            Text = "ScreenShareLan",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 20f),
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Top,
            Height = 70
        };

        Controls.Add(MakeButton("Hostear", 100, OnHost));
        Controls.Add(MakeButton("Lista da LAN", 160, OnLanList));
        Controls.Add(MakeButton("Conexao direta", 220, OnDirect));
        Controls.Add(title);
    }

    private Button MakeButton(string text, int top, EventHandler onClick)
    {
        var b = new Button
        {
            Text = text,
            Left = 60, Top = top, Width = 300, Height = 46,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Color.White,
            BackColor = Color.FromArgb(48, 48, 56),
            Font = new Font("Segoe UI", 12f),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += onClick;
        return b;
    }

    private void OnHost(object? sender, EventArgs e)
    {
        var server = new RoomServer(Protocol.DefaultRoomPort);
        server.Start();
        // host tambem participa: conecta na propria maquina
        new RoomForm(server, "127.0.0.1", Protocol.DefaultRoomPort).Show();
    }

    private void OnLanList(object? sender, EventArgs e) => new LanListForm().Show();

    private void OnDirect(object? sender, EventArgs e)
    {
        string input = Prompt(
            "Digite o IP:porta do host (ex: 26.10.20.30:45679)",
            "Conexao direta",
            $"127.0.0.1:{Protocol.DefaultRoomPort}");

        if (string.IsNullOrWhiteSpace(input)) return;
        if (!TryParseHostPort(input, out var host, out var port))
        {
            MessageBox.Show("Formato invalido. Use ip:porta.",
                "Erro", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        new RoomForm(null, host, port).Show();
    }

    internal static bool TryParseHostPort(string input, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        input = input.Trim();
        int idx = input.LastIndexOf(':');
        if (idx <= 0 || idx == input.Length - 1) return false;
        host = input[..idx];
        return int.TryParse(input[(idx + 1)..], out port) && port is > 0 and <= 65535;
    }

    internal static string Prompt(string text, string caption, string def = "")
    {
        using var f = new Form
        {
            Width = 430, Height = 170, Text = caption,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false
        };
        var lbl = new Label { Left = 12, Top = 14, Width = 400, Text = text };
        var tb = new TextBox { Left = 12, Top = 42, Width = 400, Text = def };
        var ok = new Button { Text = "OK", Left = 246, Width = 75, Top = 84, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancelar", Left = 327, Width = 85, Top = 84, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { lbl, tb, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? tb.Text : string.Empty;
    }
}
