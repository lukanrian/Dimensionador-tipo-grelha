using System.Drawing.Drawing2D;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Grelha.Shared;

namespace Grelha.IfcInspector.UI;

// Seleção da bomba: grupo (bocais) e peça do catálogo, curva da bomba sobre a curva do sistema e dados do ponto de funcionamento.
public sealed class PumpSelectionForm : Form
{
    private sealed record CurvePoint(double Head_m, double Flow_m3_h);
    private sealed record CatalogPump(string Id, string Manufacturer, string Model, string Family, string Role, int CatalogPage, double Power_cv,
        double? Stages, double? Suction_in, double? Discharge_in, double? ShutoffHead_m, double? MaxSuctionLift_m, double? Impeller_mm, CurvePoint[] Curve);
    private sealed record Evaluation(string Id, bool Meets, bool Pending, double Flow, double Head, double MinPressure, double Suction, double Discharge,
        double Shutoff, double Limit140, double Head150, double Limit65, string Notes);

    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    private readonly Dictionary<string, CatalogPump> catalog = new();
    private readonly List<Evaluation> evaluations = [];
    private readonly List<(double Flow, double Head)> systemCurve = [];
    private (double Flow, double Head)? required;

    private readonly ComboBox group = new();
    private readonly ComboBox piece = new();
    private readonly Label model = new();
    private readonly CheckBox onlyMeeting = new();
    private readonly Panel chart = new DoubleBufferedPanel();
    private readonly ListView properties = new();
    private readonly Button okButton = new();

    public string? SelectedPumpId { get; private set; }

    public PumpSelectionForm(string selectionCsv, string catalogJson, string? systemCurveCsv, string? initialPumpId = null)
    {
        Text = "Bomba";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(800, 940);
        MinimumSize = new Size(700, 760);
        Font = new Font("Segoe UI", 9F);

        ReadCatalog(catalogJson);
        ReadEvaluations(selectionCsv);
        if (systemCurveCsv is not null && File.Exists(systemCurveCsv)) ReadSystemCurve(systemCurveCsv);

        var header = new TableLayoutPanel { Location = new Point(10, 10), Size = new Size(780, 128), ColumnCount = 2, RowCount = 5, Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, CellBorderStyle = TableLayoutPanelCellBorderStyle.Single };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Label Caption(string text) => new() { Text = text, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0) };
        header.Controls.Add(Caption("Classe"), 0, 0);
        header.Controls.Add(Caption("Bomba de incêndio – recalque (principal)"), 1, 0);
        header.Controls.Add(Caption("Grupo"), 0, 1);
        group.DropDownStyle = ComboBoxStyle.DropDownList;
        group.Dock = DockStyle.Fill;
        header.Controls.Add(group, 1, 1);
        header.Controls.Add(Caption("Peça"), 0, 2);
        piece.DropDownStyle = ComboBoxStyle.DropDownList;
        piece.Dock = DockStyle.Fill;
        header.Controls.Add(piece, 1, 2);
        header.Controls.Add(Caption("Modelo"), 0, 3);
        model.Dock = DockStyle.Fill;
        model.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(model, 1, 3);
        onlyMeeting.Text = "Mostrar só as bombas que atendem (✔) ou ficam a confirmar (?) pela NBR 16704";
        onlyMeeting.Checked = true;
        onlyMeeting.Dock = DockStyle.Fill;
        header.Controls.Add(onlyMeeting, 1, 4);
        for (var i = 0; i < 5; i++) header.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));

        chart.SetBounds(10, 146, 780, 520);
        chart.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        chart.BackColor = Color.White;
        chart.BorderStyle = BorderStyle.FixedSingle;
        chart.Paint += (_, e) => DrawChart(e.Graphics);
        chart.Resize += (_, _) => chart.Invalidate();

        properties.SetBounds(10, 674, 780, 210);
        properties.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        properties.View = View.Details;
        properties.FullRowSelect = true;
        properties.HeaderStyle = ColumnHeaderStyle.None;
        properties.Columns.Add("Propriedade", 330);
        properties.Columns.Add("Valor", 420);

        okButton.Text = "Gerar memorial";
        okButton.SetBounds(260, 896, 130, 32);
        okButton.Anchor = AnchorStyles.Bottom;
        okButton.Click += (_, _) => Choose();
        var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel };
        cancel.SetBounds(410, 896, 130, 32);
        cancel.Anchor = AnchorStyles.Bottom;
        CancelButton = cancel;
        AcceptButton = okButton;

        Controls.AddRange([header, chart, properties, okButton, cancel]);
        group.SelectedIndexChanged += (_, _) => FillPieces();
        piece.SelectedIndexChanged += (_, _) => ShowPump();
        onlyMeeting.CheckedChanged += (_, _) => FillGroups();
        FillGroups();
        if (initialPumpId is not null && catalog.TryGetValue(initialPumpId, out var initial))
        {
            if (!evaluations.Any(e => e.Id == initialPumpId && (e.Meets || e.Pending))) onlyMeeting.Checked = false;
            group.SelectedItem = GroupOf(initial);
            foreach (var item in piece.Items.OfType<PieceItem>()) if (item.Id == initialPumpId) piece.SelectedItem = item;
        }
    }

    private CatalogPump? Current => piece.SelectedItem is PieceItem item ? catalog.GetValueOrDefault(item.Id) : null;
    private Evaluation? CurrentEvaluation => piece.SelectedItem is PieceItem item ? evaluations.FirstOrDefault(e => e.Id == item.Id) : null;

    private sealed record PieceItem(string Id, string Text) { public override string ToString() => Text; }

    private static string Inches(double? value) => value is null ? "?" : value.Value switch
    {
        var v when Math.Abs(v - Math.Floor(v)) < 1e-6 => $"{v:0}\"",
        var v when Math.Floor(v) == 0 => $"{Fraction(v)}\"",
        var v => $"{Math.Floor(v):0} {Fraction(v - Math.Floor(v))}\""
    };

    private static string Fraction(double value) => value switch { 0.25 => "1/4", 0.5 => "1/2", 0.75 => "3/4", _ => value.ToString("0.##", Br) };

    private string GroupOf(CatalogPump pump) => $"{Inches(pump.Suction_in)} x {Inches(pump.Discharge_in)}";

    private IEnumerable<Evaluation> Listed => evaluations.Where(e => catalog.ContainsKey(e.Id) && (!onlyMeeting.Checked || e.Meets || e.Pending));

    private void FillGroups()
    {
        var previous = group.SelectedItem as string;
        group.Items.Clear();
        foreach (var name in Listed.Select(e => GroupOf(catalog[e.Id])).Distinct().OrderBy(name => catalog.Values.First(p => GroupOf(p) == name).Suction_in ?? 0)
                     .ThenBy(name => catalog.Values.First(p => GroupOf(p) == name).Discharge_in ?? 0))
            group.Items.Add(name);
        // Grupo inicial: o da primeira bomba da lista (melhor classificada).
        var best = Listed.FirstOrDefault();
        var target = previous is not null && group.Items.Contains(previous) ? previous : best is null ? null : GroupOf(catalog[best.Id]);
        if (target is not null) group.SelectedItem = target;
        else { piece.Items.Clear(); ShowPump(); }
    }

    private void FillPieces()
    {
        piece.Items.Clear();
        foreach (var e in Listed.Where(e => GroupOf(catalog[e.Id]) == group.SelectedItem as string))
        {
            var p = catalog[e.Id];
            piece.Items.Add(new PieceItem(p.Id, $"{(e.Meets ? "✔" : e.Pending ? "?" : "✖")}  {p.Model} – {p.Power_cv.ToString("0.##", Br)} cv" +
                (p.Impeller_mm is double r ? $" – rotor {r:0} mm" : "") + $"  ({e.Flow.ToString("0.0", Br)} m³/h a {e.Head.ToString("0.0", Br)} m)"));
        }
        if (piece.Items.Count > 0) piece.SelectedIndex = 0;
        else ShowPump();
    }

    private void ShowPump()
    {
        var pump = Current;
        var e = CurrentEvaluation;
        okButton.Enabled = pump is not null;
        model.Text = pump is null ? "Nenhuma bomba nesta seleção." : $"{pump.Manufacturer} {pump.Model} – {pump.Family} (catálogo, p. {pump.CatalogPage})";
        properties.Items.Clear();
        properties.Groups.Clear();
        ListViewGroup Group(string name) { var g = new ListViewGroup(name); properties.Groups.Add(g); return g; }
        void Row(ListViewGroup g, string name, string value) => properties.Items.Add(new ListViewItem([name, value], g));
        string N(double value, string format = "0.00") => double.IsNaN(value) ? "—" : value.ToString(format, Br);

        var network = Group("Rede");
        Row(network, "Vazão mínima (m³/h)", required is { } r1 ? N(r1.Flow) : "—");
        Row(network, "Altura mínima (m.c.a.)", required is { } r2 ? N(r2.Head) : "—");
        if (pump is not null && e is not null)
        {
            var b = Group("Bomba");
            Row(b, "Vazão de funcionamento (m³/h)", N(e.Flow));
            Row(b, "Altura de funcionamento (m.c.a.)", N(e.Head));
            Row(b, "Menor pressão nos chuveiros (m.c.a.)", N(e.MinPressure));
            Row(b, "Pressão na sucção / no recalque (m.c.a.)", $"{N(e.Suction)} / {N(e.Discharge)}");
            Row(b, "Potência do motor (cv)", N(pump.Power_cv, "0.##"));
            // Potência hidráulica = ρ g Q H; 1 cv = 735,5 W.
            Row(b, "Potência hidráulica no ponto (cv)", N(1000 * 9.80665 * e.Flow / 3600 * e.Head / 735.5));
            Row(b, "Rotor (mm)", pump.Impeller_mm is double rotor ? N(rotor, "0") : "—");
            Row(b, "Bocais sucção x recalque", GroupOf(pump));
            Row(b, "Pressão sem vazão (m.c.a.)", N(pump.ShutoffHead_m ?? double.NaN));
            Row(b, "Altura máxima de sucção (m.c.a.)", N(pump.MaxSuctionLift_m ?? double.NaN));
            Row(b, "NPSH requerido (m.c.a.)", "não informado no catálogo");
            Row(b, "Rendimento (%)", "não informado no catálogo");
            var n = Group("Verificações – ABNT NBR 16704");
            Row(n, "Pressão sem vazão ≤ 140% da nominal", $"{N(e.Shutoff)} ≤ {N(e.Limit140)} m");
            Row(n, "150% da vazão com ≥ 65% da pressão", $"{N(e.Head150)} ≥ {N(e.Limit65)} m a {N(1.5 * e.Flow)} m³/h");
            Row(n, "Situação", e.Notes);
        }
        chart.Invalidate();
    }

    private void Choose()
    {
        var e = CurrentEvaluation;
        if (e is null) return;
        if (e.Pending && MessageBox.Show(this, "O ponto de 150% da vazão desta bomba fica além da curva do catálogo; a altura foi extrapolada e precisa ser confirmada na curva de ensaio do fabricante. Gerar o memorial mesmo assim?", "Bomba a confirmar",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        if (!e.Meets && !e.Pending && MessageBox.Show(this, "Esta bomba não atende a todas as verificações. Gerar o memorial mesmo assim?", "Bomba não atende",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        SelectedPumpId = e.Id;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void DrawChart(Graphics g)
    {
        var pump = Current;
        var e = CurrentEvaluation;
        PumpChart.Draw(g, chart.Width, chart.Height, pump is null ? null : new PumpChart.Data(
            pump.Curve.Select(p => (p.Flow_m3_h, p.Head_m)).ToArray(), pump.ShutoffHead_m, systemCurve.ToArray(), required,
            e is null ? null : (e.Flow, e.Head), e is null ? null : (1.5 * e.Flow, e.Head150)));
    }

    private void ReadCatalog(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var pumps = document.RootElement.GetProperty("Pumps").Deserialize<CatalogPump[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        foreach (var pump in pumps.Where(p => p.Curve.Length >= 2)) catalog[pump.Id] = pump;
    }

    private void ReadEvaluations(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length == 0) return;
        var header = Split(lines[0]);
        int Col(string name) => Array.IndexOf(header, name);
        double D(string[] row, string name) => Col(name) is >= 0 and var i && i < row.Length && double.TryParse(row[i], NumberStyles.Float, Br, out var v) ? v : double.NaN;
        foreach (var row in lines.Skip(1).Where(line => line.Length > 0).Select(Split))
            evaluations.Add(new Evaluation(row[Col("Id")], row[Col("Atende")] == "Sim", row[Col("Atende")] == "A confirmar", D(row, "Vazao_operacao_m3h"), D(row, "Altura_operacao_m"),
                D(row, "Pressao_min_bicos_mca"), D(row, "Succao_mca"), D(row, "Recalque_mca"), D(row, "Pressao_sem_vazao_m"), D(row, "Limite_140_m"),
                D(row, "Altura_150_m"), D(row, "Limite_65_m"), row[Col("Observacoes")]));
    }

    private void ReadSystemCurve(string path)
    {
        foreach (var parts in File.ReadAllLines(path).Skip(1).Select(line => line.Split(',')).Where(parts => parts.Length == 3))
        {
            var point = (double.Parse(parts[1], CultureInfo.InvariantCulture), double.Parse(parts[2], CultureInfo.InvariantCulture));
            if (parts[0] == "Required") required = point; else systemCurve.Add(point);
        }
    }

    private static string[] Split(string line)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted && c == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ';' && !quoted) { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private sealed class DoubleBufferedPanel : Panel
    {
        public DoubleBufferedPanel() => DoubleBuffered = true;
    }
}
