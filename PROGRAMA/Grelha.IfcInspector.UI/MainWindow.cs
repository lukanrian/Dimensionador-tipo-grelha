using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Grelha.IfcInspector.UI;

// Janela principal: interface web (painel, visualizador 3D da rede) numa WebView2; o cálculo roda no leitor (processo separado).
internal sealed class MainWindow : Form
{
    private const string Version = "0.37";
    private readonly WebView2 web = new() { Dock = DockStyle.Fill };
    private readonly string projectRoot;
    private readonly string cliProject;
    private readonly string? bundledReader;
    private readonly string logDirectory;
    private string lastLogPath;
    private string ifcPath = "";
    private Process? activeProcess;
    private bool useChosenArea;

    public MainWindow(string[] args)
    {
        var installedReader = Path.Combine(AppContext.BaseDirectory, "Leitor", "Grelha.IfcInspector.exe");
        bundledReader = File.Exists(installedReader) ? installedReader : null;
        projectRoot = Environment.GetEnvironmentVariable("GRELHA_PROJECT_ROOT") ?? (bundledReader is null ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")) : AppContext.BaseDirectory);
        cliProject = Environment.GetEnvironmentVariable("GRELHA_CLI_PROJECT") ?? Path.Combine(projectRoot, "Grelha.IfcInspector", "Grelha.IfcInspector.csproj");
        logDirectory = Environment.GetEnvironmentVariable("GRELHA_LOG_DIR") ?? (bundledReader is null ? Path.GetFullPath(Path.Combine(projectRoot, "..", "logs"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dimensionador Tipo Grelha", "logs"));
        Directory.CreateDirectory(logDirectory);
        lastLogPath = Path.Combine(logDirectory, $"Grelha_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}.log");
        File.WriteAllText(lastLogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} Programa iniciado. Versão {Version}.{Environment.NewLine}", Encoding.UTF8);

        var defaultFile = Environment.GetEnvironmentVariable("GRELHA_DEFAULT_IFC") ?? Path.GetFullPath(Path.Combine(projectRoot, "..", "IFC", "786-INC-PRE-0000-MOD-GERL-AC-MODELO_IFC-00.ifc"));
        ifcPath = args.FirstOrDefault(File.Exists) ?? (File.Exists(defaultFile) ? defaultFile : "");

        Text = $"Dimensionador Tipo Grelha {Version}";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1100, 700);
        ClientSize = new Size(1600, 950);
        WindowState = FormWindowState.Maximized;
        Controls.Add(web);
        Load += async (_, _) => await InitializeWebAsync();
        FormClosing += (_, _) => { StopChildProcess(); WriteSessionLog("Janela do programa encerrada."); };
    }

    public static bool WebViewAvailable()
    {
        try { return !string.IsNullOrEmpty(CoreWebView2Environment.GetAvailableBrowserVersionString()); }
        catch { return false; }
    }

    private async Task InitializeWebAsync()
    {
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dimensionador Tipo Grelha", "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(null, userData);
        await web.EnsureCoreWebView2Async(environment);
        var core = web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("GRELHA_DEVTOOLS") == "1";
        core.Settings.IsStatusBarEnabled = false;
        core.SetVirtualHostNameToFolderMapping("app.grelha", Path.Combine(AppContext.BaseDirectory, "Web"), CoreWebView2HostResourceAccessKind.Allow);
        MapData();
        core.WebMessageReceived += async (_, e) => await OnMessageAsync(e.WebMessageAsJson);
        core.Navigate("https://app.grelha/index.html");
    }

    // A pasta do IFC fica acessível à página como https://dados.grelha/ (resultados .json/.csv).
    private void MapData()
    {
        if (web.CoreWebView2 is null || ifcPath.Length == 0) return;
        web.CoreWebView2.ClearVirtualHostNameToFolderMapping("dados.grelha");
        web.CoreWebView2.SetVirtualHostNameToFolderMapping("dados.grelha", Path.GetDirectoryName(ifcPath)!, CoreWebView2HostResourceAccessKind.Allow);
    }

    private string? DataBase() => ifcPath.Length == 0 ? null : "https://dados.grelha/" + Uri.EscapeDataString(Path.GetFileNameWithoutExtension(ifcPath));

    private void Post(object message)
    {
        if (InvokeRequired) { BeginInvoke(() => Post(message)); return; }
        web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async Task OnMessageAsync(string json)
    {
        using var document = JsonDocument.Parse(json);
        var m = document.RootElement;
        string S(string name) => m.TryGetProperty(name, out var value) ? value.GetString() ?? "" : "";
        switch (S("cmd"))
        {
            case "ready":
                Post(new { type = "init", version = Version, path = ifcPath, memorial = HasMemorial(), @base = File.Exists(Path.ChangeExtension(ifcPath, ".network-3d.json")) ? DataBase() : null });
                break;
            case "browse":
                using (var dialog = new OpenFileDialog { Title = "Selecionar modelo IFC", Filter = "Modelos IFC (*.ifc;*.ifczip)|*.ifc;*.ifczip|Todos os arquivos (*.*)|*.*", CheckFileExists = true })
                {
                    if (File.Exists(ifcPath)) dialog.FileName = ifcPath;
                    if (dialog.ShowDialog(this) != DialogResult.OK) break;
                    ifcPath = dialog.FileName;
                    MapData();
                    Post(new { type = "file", path = ifcPath, memorial = HasMemorial() });
                    if (File.Exists(Path.ChangeExtension(ifcPath, ".network-3d.json"))) Post(new { type = "done", @base = DataBase() });
                    else _ = RunReaderAsync("", "", null, geometryOnly: true);
                }
                break;
            case "analyze":
                await RunReaderAsync(S("source"), S("tank"), null);
                break;
            case "analyzeMemorial":
                useChosenArea = false;
                await RunReaderAsync(S("source"), S("tank"), null);
                break;
            case "memorial":
                await RunReaderAsync(S("source"), S("tank"), S("id"));
                break;
            case "selectPump":
                await SelectPumpAsync(S("id"));
                break;
            case "areaSource":
                useChosenArea = m.TryGetProperty("manual", out var manual) && manual.GetBoolean();
                break;
            case "analyzeAuto":
                // Área mais desfavorável: critérios gravados ao lado do IFC; o leitor calcula as candidatas e grava a escolhida.
                var criteriaFile = Path.ChangeExtension(ifcPath, ".area-criteria.json");
                await File.WriteAllTextAsync(criteriaFile, m.GetProperty("area").GetRawText(), Encoding.UTF8);
                await RunReaderAsync(S("source"), S("tank"), null, criteriaFile);
                useChosenArea = true;
                break;
            case "analyzeArea":
                // Área de operação escolhida no 2D: gravada ao lado do IFC e usada nas próximas análises e memoriais.
                await File.WriteAllTextAsync(AreaFile(), m.GetProperty("area").GetRawText(), Encoding.UTF8);
                useChosenArea = true;
                await RunReaderAsync(S("source"), S("tank"), null);
                break;
            case "open":
                OpenOutput(S("kind"));
                break;
        }
    }

    private string AreaFile() => Path.ChangeExtension(ifcPath, ".operation-area.json");

    private async Task SelectPumpAsync(string? initialPumpId)
    {
        var csv = Path.ChangeExtension(ifcPath, ".pump-selection.csv");
        var catalogJson = new[] { Path.Combine(AppContext.BaseDirectory, "Leitor", "Catalogo", "bombas_incendio.json"), Path.Combine(projectRoot, "Catalogo", "bombas_incendio.json") }
            .FirstOrDefault(File.Exists);
        if (!File.Exists(csv) || catalogJson is null)
        {
            MessageBox.Show(this, catalogJson is null ? "Catálogo de bombas (bombas_incendio.json) não encontrado." : "Analise o IFC antes de selecionar a bomba.", "Seleção de bomba", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var form = new PumpSelectionForm(csv, catalogJson, Path.ChangeExtension(ifcPath, ".system-curve.csv"), string.IsNullOrEmpty(initialPumpId) ? null : initialPumpId);
        if (form.ShowDialog(this) != DialogResult.OK || form.SelectedPumpId is not { } id) return;
        Post(new { type = "pumpChosen", id });
        await RunReaderAsync("", "", id);
    }

    // Memorial do projeto (PDF com MEM e SPK no nome) ao lado do IFC, o mesmo critério do leitor.
    private bool HasMemorial() => Path.GetDirectoryName(ifcPath) is { } folder && Directory.Exists(folder)
        && Directory.GetFiles(folder, "*.pdf").Any(file => Path.GetFileName(file).Contains("MEM", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(file).Contains("SPK", StringComparison.OrdinalIgnoreCase));

    private async Task RunReaderAsync(string source, string tank, string? memorialPumpId, string? autoAreaCriteria = null, bool geometryOnly = false)
    {
        if (!File.Exists(ifcPath)) { MessageBox.Show(this, "Selecione um arquivo IFC existente.", "Arquivo não encontrado", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (bundledReader is null && !File.Exists(cliProject)) { MessageBox.Show(this, $"Não encontrei o projeto de leitura do IFC:\n{cliProject}", "Projeto ausente", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (activeProcess is not null) return;

        var startedAt = DateTime.Now;
        var text = geometryOnly ? "Lendo o IFC para exibir a rede…" : autoAreaCriteria is not null ? "Montando as áreas de operação candidatas e calculando cada uma na rede (cerca de 2 minutos)…" : memorialPumpId is null ? "Lendo o IFC, calculando a rede e avaliando as bombas do catálogo (cerca de 1 minuto)…" : $"Recalculando com a bomba {memorialPumpId} e gerando o memorial…";
        Post(new { type = "busy", on = true, text });
        Post(new { type = "status", text });
        var startInfo = new ProcessStartInfo(bundledReader ?? "dotnet")
        {
            WorkingDirectory = bundledReader is null ? projectRoot : Path.GetDirectoryName(bundledReader)!,
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true
        };
        if (bundledReader is null)
            foreach (var argument in new[] { "run", "--project", cliProject, "--configuration", "Release", "--" }) startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(ifcPath);
        if (source.Length > 0) { startInfo.ArgumentList.Add("--alimentacao"); startInfo.ArgumentList.Add(source); }
        if (tank.Length > 0 && tank != "0") { startInfo.ArgumentList.Add("--nivel-reservatorio"); startInfo.ArgumentList.Add(tank); }
        if (memorialPumpId is not null) { startInfo.ArgumentList.Add("--memorial"); startInfo.ArgumentList.Add(memorialPumpId); }
        if (autoAreaCriteria is not null) { startInfo.ArgumentList.Add("--area-auto"); startInfo.ArgumentList.Add(autoAreaCriteria); }
        if (geometryOnly) startInfo.ArgumentList.Add("--geometria");
        else if (useChosenArea && File.Exists(AreaFile())) { startInfo.ArgumentList.Add("--area"); startInfo.ArgumentList.Add(AreaFile()); }
        startInfo.Environment["GRELHA_LOG_DIR"] = logDirectory;
        startInfo.Environment["GRELHA_LOG_PATH"] = lastLogPath;

        try
        {
            activeProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            activeProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Post(new { type = "log", line = e.Data }); };
            activeProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Post(new { type = "log", line = e.Data }); };
            activeProcess.Start();
            activeProcess.BeginOutputReadLine();
            activeProcess.BeginErrorReadLine();
            await activeProcess.WaitForExitAsync();
            activeProcess.WaitForExit();
            var exitCode = activeProcess.ExitCode;
            activeProcess.Dispose();
            activeProcess = null;
            Post(new { type = "busy", on = false });
            if (exitCode != 0) { Post(new { type = "status", text = $"A análise terminou com erro ({exitCode}). Veja a saída e o log." }); return; }
            Post(new { type = "done", @base = DataBase() });
            if (geometryOnly) Post(new { type = "status", text = "IFC carregado. Preencha os critérios e clique em Analisar para dimensionar." });
            else if (memorialPumpId is null) Post(new { type = "status", text = $"Análise concluída às {DateTime.Now:HH:mm}." });
            else
            {
                var memorial = LatestMemorial(startedAt);
                Post(new { type = "status", text = memorial is null ? "O memorial não foi gerado; veja o log." : $"Memorial gerado: {memorial.Name}" });
                if (memorial is not null) Shell(memorial.FullName);
            }
        }
        catch (Exception exception)
        {
            StopChildProcess();
            activeProcess = null;
            Post(new { type = "busy", on = false });
            Post(new { type = "status", text = "Não foi possível iniciar o leitor." });
            WriteSessionLog($"Falha ao iniciar o leitor IFC: {exception}");
        }
    }

    private FileInfo? LatestMemorial(DateTime? since = null) => ifcPath.Length == 0 ? null
        : new DirectoryInfo(Path.GetDirectoryName(ifcPath)!).GetFiles("Memorial_Calculo_*.docx")
            .Where(file => since is null || file.LastWriteTime >= since.Value.AddSeconds(-2)).OrderByDescending(file => file.LastWriteTime).FirstOrDefault();

    private void OpenOutput(string kind)
    {
        if (kind == "log") { Shell(lastLogPath); return; }
        if (ifcPath.Length == 0) return;
        if (kind == "folder") { Shell(Path.GetDirectoryName(ifcPath)!); return; }
        if (kind == "memorial") { if (LatestMemorial() is { } file) Shell(file.FullName); else Post(new { type = "status", text = "Nenhum memorial gerado ainda para este IFC." }); return; }
        var extension = kind switch
        {
            "selection" => ".pump-selection.csv", "segments" => ".hydraulic-segments.csv", "sprinklers" => ".hydraulic-sprinklers.csv",
            "pipes" => ".hydraulic-pipes.csv", "inventory" => ".inventory.json", "pumpcurve" => ".pump-curve.csv", _ => null
        };
        if (extension is null) return;
        var path = Path.ChangeExtension(ifcPath, extension);
        if (kind == "pumpcurve" && !File.Exists(path)) File.WriteAllText(path, "Flow_l_s,Head_m" + Environment.NewLine, Encoding.UTF8);
        if (File.Exists(path)) Shell(path); else Post(new { type = "status", text = "Arquivo ainda não gerado; analise o IFC." });
    }

    private void Shell(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Não foi possível abrir", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void WriteSessionLog(string message) =>
        File.AppendAllText(lastLogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}", Encoding.UTF8);

    private void StopChildProcess()
    {
        try { if (activeProcess is { HasExited: false }) activeProcess.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }
}
