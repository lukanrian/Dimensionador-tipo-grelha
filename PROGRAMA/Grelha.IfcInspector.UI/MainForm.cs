using System.Diagnostics;
using System.Text;

namespace Grelha.IfcInspector.UI;

internal sealed class MainForm : Form
{
    private readonly TextBox filePath = new();
    private readonly Button browseButton = new();
    private readonly Button analyzeButton = new();
    private readonly Button inventoryButton = new();
    private readonly Button auditButton = new();
    private readonly Button candidatesButton = new();
    private readonly Button componentsButton = new();
    private readonly Button operationAreaButton = new();
    private readonly Button confirmedTopologyButton = new();
    private readonly Button topologyAuditButton = new();
    private readonly Button branchReviewButton = new();
    private readonly Button hydraulicHeadsButton = new();
    private readonly Button hydraulicPipesButton = new();
    private readonly TextBox sourcePipe = new();
    private readonly TextBox tankLevel = new();
    private readonly Button pumpCurveButton = new();
    private readonly Button pumpSelectionButton = new();
    private readonly Button logButton = new();
    private readonly Label status = new();
    private readonly ProgressBar progress = new();
    private readonly RichTextBox output = new();
    private readonly string projectRoot;
    private readonly string cliProject;
    private readonly string? bundledReader;
    private Process? activeProcess;
    private string? lastLogPath;

    public MainForm(string[] args)
    {
        var installedReader = Path.Combine(AppContext.BaseDirectory, "Leitor", "Grelha.IfcInspector.exe");
        bundledReader = File.Exists(installedReader) ? installedReader : null;
        projectRoot = Environment.GetEnvironmentVariable("GRELHA_PROJECT_ROOT") ?? (bundledReader is null ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")) : AppContext.BaseDirectory);
        cliProject = Environment.GetEnvironmentVariable("GRELHA_CLI_PROJECT") ?? Path.Combine(projectRoot, "Grelha.IfcInspector", "Grelha.IfcInspector.csproj");
        var logDirectory = Environment.GetEnvironmentVariable("GRELHA_LOG_DIR") ?? (bundledReader is null ? Path.GetFullPath(Path.Combine(projectRoot, "..", "logs")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dimensionador Tipo Grelha", "logs"));
        Directory.CreateDirectory(logDirectory);
        lastLogPath = Path.Combine(logDirectory, $"Grelha_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Environment.ProcessId}.log");
        File.WriteAllText(lastLogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} Programa iniciado. Versão 0.34.{Environment.NewLine}", Encoding.UTF8);

        Text = "Dimensionador Tipo Grelha 0.34";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(980, 430);
        ClientSize = new Size(980, 558);
        Font = new Font("Segoe UI", 9F);

        var title = new Label
        {
            Text = "Dimensionador Tipo Grelha",
            Font = new Font("Segoe UI Semibold", 16F),
            AutoSize = true,
            Location = new Point(24, 18)
        };
        var subtitle = new Label
        {
            Text = "Leitura, conectividade e dimensionamento de redes de sprinklers · versão 0.34",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Location = new Point(26, 52)
        };
        var fileLabel = new Label { Text = "Arquivo IFC", AutoSize = true, Location = new Point(24, 91) };
        filePath.SetBounds(24, 113, 810, 28);
        filePath.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        filePath.AccessibleName = "Caminho do arquivo IFC";

        browseButton.Text = "Selecionar...";
        browseButton.SetBounds(846, 111, 110, 30);
        browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        browseButton.Click += BrowseForIfc;

        analyzeButton.Text = "Analisar IFC";
        analyzeButton.SetBounds(24, 157, 140, 34);
        analyzeButton.Click += async (_, _) => await AnalyzeAsync();

        var sourceLabel = new Label { Text = "Tubo de alimentação (GlobalId, opcional)", AutoSize = true, Location = new Point(180, 150) };
        sourcePipe.SetBounds(180, 168, 300, 24);
        sourcePipe.AccessibleName = "GlobalId do tubo de alimentação";
        var tankLabel = new Label { Text = "Nível do reservatório (m)", AutoSize = true, Location = new Point(496, 150) };
        tankLevel.SetBounds(496, 168, 150, 24);
        tankLevel.Text = "0";
        tankLevel.AccessibleName = "Nível da água acima da tomada, em metros";
        pumpCurveButton.Text = "Curva da bomba";
        pumpCurveButton.SetBounds(662, 165, 130, 30);
        pumpCurveButton.Click += (_, _) =>
        {
            var curve = Path.ChangeExtension(filePath.Text, ".pump-curve.csv");
            if (!File.Exists(curve) && File.Exists(filePath.Text)) File.WriteAllText(curve, "Flow_l_s,Head_m" + Environment.NewLine, Encoding.UTF8);
            OpenOutput(curve);
        };

        progress.SetBounds(806, 174, 150, 14);
        progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        progress.Style = ProgressBarStyle.Marquee;
        progress.MarqueeAnimationSpeed = 24;
        progress.Visible = false;

        status.Text = "Selecione um IFC para iniciar a leitura.";
        status.SetBounds(24, 201, 932, 24);
        status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        output.SetBounds(24, 231, 932, 210);
        output.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        output.ReadOnly = true;
        output.WordWrap = false;
        output.BackColor = SystemColors.Window;
        output.Font = new Font("Consolas", 9F);
        output.AccessibleName = "Andamento e resultado da análise";

        inventoryButton.Text = "Abrir inventário";
        inventoryButton.SetBounds(24, 462, 130, 30);
        inventoryButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        inventoryButton.Enabled = false;
        inventoryButton.Click += (_, _) => OpenOutput(Path.ChangeExtension(filePath.Text, ".inventory.json"));

        auditButton.Text = "Abrir auditoria";
        auditButton.SetBounds(160, 462, 130, 30);
        auditButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        auditButton.Enabled = false;
        auditButton.Click += (_, _) => OpenOutput(Path.ChangeExtension(filePath.Text, ".connectivity-audit.csv"));

        candidatesButton.Text = "Ligações candidatas";
        candidatesButton.SetBounds(392, 462, 138, 30);
        candidatesButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        candidatesButton.Enabled = false;
        candidatesButton.Click += (_, _) => OpenOutput(Path.ChangeExtension(filePath.Text, ".network-candidates.csv"));

        componentsButton.Text = "Grupos de tubos";
        operationAreaButton.Text = "Revisar conexões";
        operationAreaButton.SetBounds(538, 462, 130, 30);
        operationAreaButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        operationAreaButton.Enabled = false;
        operationAreaButton.Click += (_, _) => OpenOutput(Path.ChangeExtension(filePath.Text, ".operation-area-topology-review.csv"));

        componentsButton.SetBounds(810, 462, 146, 30);
        componentsButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        componentsButton.Enabled = false;
        componentsButton.Click += (_, _) => OpenOutput(Path.ChangeExtension(filePath.Text, ".network-components.csv"));

        confirmedTopologyButton.Text = "Topologia confirmada";
        confirmedTopologyButton.SetBounds(674, 462, 130, 30);
        confirmedTopologyButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        confirmedTopologyButton.Enabled = false;
        confirmedTopologyButton.Click += (_, _) => OpenOutput(Path.ChangeExtension(filePath.Text, ".operation-area-confirmed-topology.csv"));

        topologyAuditButton.Text = "Auditoria da topologia revisada";
        topologyAuditButton.SetBounds(24, 500, 220, 30);
        topologyAuditButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        topologyAuditButton.Enabled = false;
        topologyAuditButton.Click += (_, _) => OpenOutput(Path.ChangeExtension(filePath.Text, ".operation-area-topology-audit.csv"));

        branchReviewButton.Text = "Revisar bicos e ramais";
        branchReviewButton.SetBounds(254, 500, 180, 30);
        branchReviewButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        branchReviewButton.Enabled = false;
        branchReviewButton.Click += (_, _) => OpenOutput(Path.ChangeExtension(filePath.Text, ".operation-area-branch-review.csv"));

        hydraulicHeadsButton.Text = "Dimensionamento: bicos";
        hydraulicHeadsButton.SetBounds(444, 500, 170, 30);
        hydraulicHeadsButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        hydraulicHeadsButton.Enabled = false;
        hydraulicHeadsButton.Click += (_, _) => OpenOutput(Path.ChangeExtension(filePath.Text, ".hydraulic-sprinklers.csv"));

        hydraulicPipesButton.Text = "Dimensionamento: tubos";
        hydraulicPipesButton.SetBounds(624, 500, 170, 30);
        hydraulicPipesButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        hydraulicPipesButton.Enabled = false;
        hydraulicPipesButton.Click += (_, _) => OpenOutput(Path.ChangeExtension(filePath.Text, ".hydraulic-pipes.csv"));

        pumpSelectionButton.Text = "Selecionar bomba";
        pumpSelectionButton.SetBounds(804, 500, 152, 30);
        pumpSelectionButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        pumpSelectionButton.Enabled = false;
        pumpSelectionButton.Click += async (_, _) =>
        {
            var csv = Path.ChangeExtension(filePath.Text, ".pump-selection.csv");
            if (!File.Exists(csv)) return;
            var catalogJson = new[] { Path.Combine(AppContext.BaseDirectory, "Leitor", "Catalogo", "bombas_incendio.json"), Path.Combine(projectRoot, "Catalogo", "bombas_incendio.json") }
                .FirstOrDefault(File.Exists);
            if (catalogJson is null)
            {
                MessageBox.Show(this, "Catálogo de bombas (bombas_incendio.json) não encontrado.", "Catálogo ausente", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            using var form = new PumpSelectionForm(csv, catalogJson, Path.ChangeExtension(filePath.Text, ".system-curve.csv"));
            if (form.ShowDialog(this) == DialogResult.OK && form.SelectedPumpId is { } id) await AnalyzeAsync(id);
        };

        logButton.Text = "Abrir log";
        logButton.SetBounds(304, 462, 100, 30);
        logButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        logButton.Enabled = File.Exists(lastLogPath);
        logButton.Click += (_, _) => OpenOutput(lastLogPath);

        Controls.AddRange([title, subtitle, fileLabel, filePath, browseButton, analyzeButton, sourceLabel, sourcePipe, tankLabel, tankLevel, pumpCurveButton, progress, status, output, inventoryButton, auditButton, logButton, candidatesButton, operationAreaButton, confirmedTopologyButton, topologyAuditButton, branchReviewButton, hydraulicHeadsButton, hydraulicPipesButton, pumpSelectionButton, componentsButton]);

        var defaultFile = Environment.GetEnvironmentVariable("GRELHA_DEFAULT_IFC") ?? Path.GetFullPath(Path.Combine(projectRoot, "..", "IFC", "786-INC-PRE-0000-MOD-GERL-AC-MODELO_IFC-00.ifc"));
        filePath.Text = args.FirstOrDefault() ?? (File.Exists(defaultFile) ? defaultFile : "");
        AppendOutput($"Log: {lastLogPath}");
        FormClosing += (_, _) =>
        {
            StopChildProcess();
            WriteSessionLog("Janela do programa encerrada.");
        };
    }

    private void BrowseForIfc(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Selecionar modelo IFC",
            Filter = "Modelos IFC (*.ifc;*.ifczip)|*.ifc;*.ifczip|Todos os arquivos (*.*)|*.*",
            CheckFileExists = true
        };
        if (File.Exists(filePath.Text)) dialog.FileName = filePath.Text;
        if (dialog.ShowDialog(this) == DialogResult.OK) filePath.Text = dialog.FileName;
    }

    private async Task AnalyzeAsync(string? memorialPumpId = null)
    {
        var input = filePath.Text.Trim();
        if (!File.Exists(input))
        {
            MessageBox.Show(this, "Selecione um arquivo IFC existente.", "Arquivo não encontrado", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            filePath.Focus();
            return;
        }
        if (bundledReader is null && !File.Exists(cliProject))
        {
            MessageBox.Show(this, $"Não encontrei o projeto de leitura do IFC:\n{cliProject}", "Projeto ausente", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        output.Clear();
        inventoryButton.Enabled = auditButton.Enabled = candidatesButton.Enabled = operationAreaButton.Enabled = confirmedTopologyButton.Enabled = topologyAuditButton.Enabled = branchReviewButton.Enabled = hydraulicHeadsButton.Enabled = hydraulicPipesButton.Enabled = pumpSelectionButton.Enabled = componentsButton.Enabled = logButton.Enabled = false;
        AppendOutput($"Log: {lastLogPath}");
        analyzeButton.Enabled = browseButton.Enabled = filePath.Enabled = false;
        progress.Visible = true;
        status.Text = memorialPumpId is null ? "Lendo o modelo IFC e calculando a rede. Arquivos grandes podem levar cerca de um minuto."
            : $"Recalculando a rede com a bomba {memorialPumpId} e gerando o memorial de cálculo.";
        var startedAt = DateTime.Now;

        var startInfo = new ProcessStartInfo(bundledReader ?? "dotnet")
        {
            WorkingDirectory = bundledReader is null ? projectRoot : Path.GetDirectoryName(bundledReader)!,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };
        if (bundledReader is null)
        {
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(cliProject);
            startInfo.ArgumentList.Add("--configuration");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("--");
        }
        startInfo.ArgumentList.Add(input);
        if (sourcePipe.Text.Trim().Length > 0)
        {
            startInfo.ArgumentList.Add("--alimentacao");
            startInfo.ArgumentList.Add(sourcePipe.Text.Trim());
        }
        if (memorialPumpId is not null)
        {
            startInfo.ArgumentList.Add("--memorial");
            startInfo.ArgumentList.Add(memorialPumpId);
        }
        if (tankLevel.Text.Trim() is { Length: > 0 } level && level != "0")
        {
            startInfo.ArgumentList.Add("--nivel-reservatorio");
            startInfo.ArgumentList.Add(level);
        }
        startInfo.Environment["GRELHA_LOG_DIR"] = Environment.GetEnvironmentVariable("GRELHA_LOG_DIR") ?? (bundledReader is null ? Path.GetFullPath(Path.Combine(projectRoot, "..", "logs")) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Dimensionador Tipo Grelha", "logs"));
        startInfo.Environment["GRELHA_LOG_PATH"] = lastLogPath!;

        try
        {
            activeProcess = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            activeProcess.OutputDataReceived += (_, e) => AppendOutput(e.Data);
            activeProcess.ErrorDataReceived += (_, e) => AppendOutput(e.Data);
            if (!activeProcess.Start()) throw new InvalidOperationException("Não foi possível iniciar o leitor IFC.");
            activeProcess.BeginOutputReadLine();
            activeProcess.BeginErrorReadLine();
            await activeProcess.WaitForExitAsync();
            activeProcess.WaitForExit();

            var exitCode = activeProcess.ExitCode;
            activeProcess.Dispose();
            activeProcess = null;
            progress.Visible = false;
            analyzeButton.Enabled = browseButton.Enabled = filePath.Enabled = true;

            if (exitCode == 0)
            {
                status.Text = "Análise concluída. Use Selecionar bomba para ver as bombas do catálogo que atendem e gerar o memorial.";
                inventoryButton.Enabled = File.Exists(Path.ChangeExtension(input, ".inventory.json"));
                auditButton.Enabled = File.Exists(Path.ChangeExtension(input, ".connectivity-audit.csv"));
                candidatesButton.Enabled = File.Exists(Path.ChangeExtension(input, ".network-candidates.csv"));
                operationAreaButton.Enabled = File.Exists(Path.ChangeExtension(input, ".operation-area-topology-review.csv"));
                confirmedTopologyButton.Enabled = File.Exists(Path.ChangeExtension(input, ".operation-area-confirmed-topology.csv"));
                topologyAuditButton.Enabled = File.Exists(Path.ChangeExtension(input, ".operation-area-topology-audit.csv"));
                branchReviewButton.Enabled = File.Exists(Path.ChangeExtension(input, ".operation-area-branch-review.csv"));
                hydraulicHeadsButton.Enabled = File.Exists(Path.ChangeExtension(input, ".hydraulic-sprinklers.csv"));
                hydraulicPipesButton.Enabled = File.Exists(Path.ChangeExtension(input, ".hydraulic-pipes.csv"));
                componentsButton.Enabled = File.Exists(Path.ChangeExtension(input, ".network-components.csv"));
                pumpSelectionButton.Enabled = File.Exists(Path.ChangeExtension(input, ".pump-selection.csv"));
                if (memorialPumpId is not null)
                {
                    var memorialFile = new DirectoryInfo(Path.GetDirectoryName(input)!).GetFiles("Memorial_Calculo_*.docx")
                        .Where(file => file.LastWriteTime >= startedAt.AddSeconds(-2)).OrderByDescending(file => file.LastWriteTime).FirstOrDefault();
                    status.Text = memorialFile is null ? "O memorial não foi gerado; consulte o log." : $"Memorial gerado: {memorialFile.Name}";
                    if (memorialFile is not null) OpenOutput(memorialFile.FullName);
                }
                logButton.Enabled = File.Exists(lastLogPath);
            }
            else
            {
                status.Text = $"A leitura terminou com erro ({exitCode}). Consulte o log.";
                logButton.Enabled = File.Exists(lastLogPath);
            }
        }
        catch (Exception exception)
        {
            StopChildProcess();
            progress.Visible = false;
            analyzeButton.Enabled = browseButton.Enabled = filePath.Enabled = true;
            status.Text = "Não foi possível iniciar a leitura.";
            AppendOutput(exception.ToString());
            WriteSessionLog($"Falha ao iniciar o leitor IFC: {exception}");
            MessageBox.Show(this, bundledReader is null ? "Não foi possível iniciar o leitor IFC. Confira se o .NET 10 SDK está instalado." : "Não foi possível iniciar o leitor IFC.", "Falha ao abrir o leitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AppendOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendOutput(line));
            return;
        }

        if (line.StartsWith("Log: ", StringComparison.Ordinal)) lastLogPath = line[5..].Trim();
        output.AppendText(line + Environment.NewLine);
    }

    private void OpenOutput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Não foi possível abrir o arquivo", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void WriteSessionLog(string message) =>
        File.AppendAllText(lastLogPath!, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}", Encoding.UTF8);

    private void StopChildProcess()
    {
        try
        {
            if (activeProcess is { HasExited: false }) activeProcess.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
    }
}
