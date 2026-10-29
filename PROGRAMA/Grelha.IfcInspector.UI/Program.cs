namespace Grelha.IfcInspector.UI;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Sem o WebView2 Runtime do Edge, abre a janela clássica (sem o visualizador 3D).
        Application.Run(MainWindow.WebViewAvailable() ? new MainWindow(args) : new MainForm(args));
    }
}
