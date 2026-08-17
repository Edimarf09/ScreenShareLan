namespace ScreenShareLan;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Inicializa o WinForms (usa ApplicationHighDpiMode do .csproj).
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
