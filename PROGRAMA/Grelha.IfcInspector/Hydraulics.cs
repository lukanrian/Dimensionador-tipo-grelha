using System.Globalization;
using System.Text.RegularExpressions;
using Xbim.Common.Geometry;

// Dimensionamento hidráulico da área de operação por Hazen-Williams (método do gradiente global, Todini).
// Topologia montada automaticamente pela geometria IFC; ligações marcadas "Rejeitada" na revisão são excluídas.

internal sealed record HydraulicPipe(string Id, string Type, double InnerDiameter_m, double Length_m, double C, XbimPoint3D Start, XbimPoint3D End);
internal enum HydraulicDeviceKind { Fitting, Valve, Pump }
internal sealed record HydraulicDevice(string Id, HydraulicDeviceKind Kind, string Type, string Indication, XbimPoint3D Origin);
internal sealed record HydraulicSprinkler(string Id, int Number, double K, XbimPoint3D Origin);
internal sealed record EquivalentLengthEntry(string Item, double Length_m);

internal sealed record HydraulicInput(IReadOnlyList<HydraulicPipe> Pipes, IReadOnlyList<HydraulicDevice> Devices, IReadOnlyList<HydraulicSprinkler> Heads,
    double UnitsPerMeter, double TargetMinimumPressure_mca, ISet<string> RejectedEdges, string? SourcePipeId,
    (double Flow_l_s, double Head_m)[]? PumpCurve, double TankLevel_m, IReadOnlyList<EquivalentLengthEntry> MemorialEquivalents,
    IReadOnlyList<PumpCandidate> Candidates, ISet<string>? ActiveHeads = null, IReadOnlyList<string[]>? AreaCandidates = null, IReadOnlyList<double>? AreaTargets = null);
// Área candidata avaliada na mesma rede: exigência na bomba (ou na alimentação) para a pressão mínima nos seus bicos.
internal sealed record AreaCandidateResult(int Index, double RequiredHead_m, double Flow_l_s, string? Error);
internal sealed record PumpCandidate(string Id, (double Flow_l_s, double Head_m)[] Points);
internal sealed record PumpCandidateResult(string Id, double Flow_l_s, double Head_m, double MinimumSprinklerPressure_mca,
    double SuctionPressure_mca, double DischargePressure_mca, string? Error);
internal sealed record HydraulicSegmentResult(int Number, string FromNode, string ToNode, double Flow_l_s, string Diameters_mm, double Length_m,
    double EquivalentLength_m, double HeadLoss_m, double Velocity_m_s, double FromElevation_m, double ToElevation_m, double FromPressure_mca, double ToPressure_mca);

internal sealed record HydraulicSprinklerResult(int Number, string Id, double Elevation_m, double Pressure_mca, double Flow_l_s);
internal sealed record HydraulicPipeResult(string Id, string Type, double InnerDiameter_mm, double Length_m, double EquivalentLength_m, double C,
    double Flow_l_s, double Velocity_m_s, double HeadLoss_m, string FromNode, string ToNode);
internal sealed record HydraulicPumpResult(string Id, string Type, string Mode, double Flow_l_s, double Head_m, double SuctionElevation_m, double SuctionPressure_mca,
    double DischargeElevation_m, double DischargePressure_mca, double TankLevelElevation_m, int TankOutlets);
internal sealed record HydraulicResult(string Mode, string SourceNode, string SourcePipeId, double SourceElevation_m, double SourcePressure_mca,
    double TotalFlow_l_s, double TargetMinimumPressure_mca, double MinimumSprinklerPressure_mca, int NetworkNodes, int SolverNodes, int SolverLinks, int Iterations,
    HydraulicPumpResult? Pump, string EquivalentLengthSource, int FittingsWithMemorialEquivalent, int FittingsWithTableEquivalent,
    HydraulicSprinklerResult[] Sprinklers, HydraulicPipeResult[] Pipes, HydraulicSegmentResult[] Segments, PumpCandidateResult[] Candidates, (double Flow_l_s, double Head_m)[] SystemCurve, string[] Warnings)
{
    public AreaCandidateResult[] Areas { get; init; } = [];
    /// <summary>Ligações deduzidas por proximidade (além do contato de 1 mm): Id do elemento → como foi ligado e a distância (m).</summary>
    public Dictionary<string, (string How, double Distance_m)> Joins { get; init; } = [];
    /// <summary>Pontas de tubo que ficaram sem nada ligado ("Id:0" ou "Id:1").</summary>
    public string[] DeadEnds { get; init; } = [];
}

internal static class Hydraulics
{
    const double HazenExponent = 1.852;
    const double BarPerMca = 0.0980665;

    // Comprimentos equivalentes (m) para aço galvanizado/carbono (tabela usual da literatura brasileira):
    // cotovelo 90° raio curto, tê passagem direta, tê saída lateral, registro de gaveta aberto, válvula de retenção vertical.
    static readonly (double MaxDiameter_mm, double Elbow, double TeeRun, double TeeBranch, double Gate, double Check)[] Table =
    [
        (17, 0.5, 0.3, 1.0, 0.1, 1.6), (22, 0.7, 0.4, 1.4, 0.1, 2.4), (28, 0.9, 0.5, 1.7, 0.2, 3.2), (37, 1.1, 0.7, 2.3, 0.2, 4.0),
        (43, 1.3, 0.9, 2.8, 0.3, 4.8), (55, 1.7, 1.1, 3.5, 0.4, 6.4), (68, 2.0, 1.3, 4.3, 0.4, 8.1), (82, 2.5, 1.6, 5.2, 0.5, 9.7),
        (108, 3.4, 2.1, 6.7, 0.7, 12.9), (double.MaxValue, 4.9, 3.2, 10.0, 1.1, 19.3)
    ];

    static (double Elbow, double TeeRun, double TeeBranch, double Gate, double Check) Standard(double diameter_m)
    {
        var row = Table.First(item => diameter_m * 1000 <= item.MaxDiameter_mm);
        return (row.Elbow, row.TeeRun, row.TeeBranch, row.Gate, row.Check);
    }

    // Bitola nominal (pol., formato de Sizes) a partir do diâmetro interno.
    static string NominalSize(double diameter_m) => new[] { (17, "0.5"), (22, "0.75"), (28, "1"), (37, "1.25"), (43, "1.5"), (55, "2"), (68, "2.5"), (82, "3"), (108, "4") }
        .FirstOrDefault(item => diameter_m * 1000 <= item.Item1, (0, "6")).Item2;

    static bool IsElbow(string type) => type.Contains("cotovelo", StringComparison.OrdinalIgnoreCase) || type.Contains("curva", StringComparison.OrdinalIgnoreCase);
    static bool IsTee(string type) => type.StartsWith("Tê", StringComparison.OrdinalIgnoreCase) || type.StartsWith("Te ", StringComparison.OrdinalIgnoreCase);
    static bool IsCheck(string type) => type.Contains("reten", StringComparison.OrdinalIgnoreCase);
    static bool IsTankOutlet(string type) => type.Contains("Tomada", StringComparison.OrdinalIgnoreCase);

    // Bitolas em polegadas na ordem em que aparecem: 1.1/4" = 1 1/4" = 1,25.
    public static string Sizes(string text) => string.Join("x", Regex.Matches(text, @"(?:(\d+)[.\s]+)?(\d+)/(\d+)\s*""|(\d+)\s*""")
        .Select(match => match.Groups[4].Success
            ? double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture)
            : (match.Groups[1].Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0)
                + double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) / double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture))
        .Select(value => value.ToString("0.###", CultureInfo.InvariantCulture)));

    static string NormalizeType(string text) => new string(Regex.Replace(Regex.Match(text, @"^[^\d""]+").Value, @"\s+", " ").Trim().ToLowerInvariant()
        .Normalize(System.Text.NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());

    static double SegmentDistance(XbimPoint3D point, XbimPoint3D a, XbimPoint3D b)
    {
        var ab = b - a;
        var t = Math.Clamp((point - a).DotProduct(ab) / Math.Max(ab.DotProduct(ab), 1e-12), 0, 1);
        return (point - (a + ab * t)).Length;
    }

    public static double Resistance(double length_m, double diameter_m, double c) =>
        10.67 * length_m / (Math.Pow(c, HazenExponent) * Math.Pow(diameter_m, 4.87));

    sealed class UnionFind(int size)
    {
        readonly int[] parent = Enumerable.Range(0, size).ToArray();
        public int Find(int item) => parent[item] == item ? item : parent[item] = Find(parent[item]);
        public void Union(int left, int right) => parent[Find(left)] = Find(right);
    }

    enum LinkKind { Chain, Emitter, Pump }

    sealed class Link(LinkKind kind, int from, int to, double resistance)
    {
        public LinkKind Kind = kind;
        public int From = from, To = to;
        public double Resistance = resistance;
        public List<(int PipeIndex, bool Forward)> Pipes = [];
        public double Flow = 0.0005;
        public (double A, double B, double C) Curve; // H = A + B Q + C Q² (m, m³/s)

        public (double Loss, double Derivative) Evaluate()
        {
            var q = Flow;
            var magnitude = Math.Max(Math.Abs(q), 1e-7);
            return Kind switch
            {
                LinkKind.Chain => (Resistance * q * Math.Pow(magnitude, HazenExponent - 1), HazenExponent * Resistance * Math.Pow(magnitude, HazenExponent - 1)),
                LinkKind.Emitter => (Resistance * q * magnitude, 2 * Resistance * magnitude),
                _ => (-(Curve.A + Curve.B * q + Curve.C * q * q), Math.Max(-(Curve.B + 2 * Curve.C * q), 0.5))
            };
        }
    }

    public static HydraulicResult Solve(HydraulicInput input)
    {
        var (pipes, devices, heads, unitsPerMeter) = (input.Pipes, input.Devices, input.Heads, input.UnitsPerMeter);
        var warnings = new List<string>();
        var directTolerance = 0.001 * unitsPerMeter;
        var fittingTolerance = 0.05 * unitsPerMeter;
        var valveReach = 0.40 * unitsPerMeter;
        var pumpReach = 1.00 * unitsPerMeter;
        bool Rejected(string a, string aEnd, string b, string bEnd) =>
            input.RejectedEdges.Contains(string.Join('\u001f', a, aEnd, b, bEnd)) || input.RejectedEdges.Contains(string.Join('\u001f', b, bEnd, a, aEnd));

        // Pontos: 2 por tubo, depois dispositivos (conexões, válvulas, bombas), depois bicos.
        var deviceBase = pipes.Count * 2;
        var headBase = deviceBase + devices.Count;
        var unionFind = new UnionFind(headBase + heads.Count);
        var joins = new Dictionary<string, (string How, double Distance_m)>();
        void Join(string id, string how, double distance) => joins[id] = (how, distance / unitsPerMeter);
        XbimPoint3D EndPoint(int index) => index % 2 == 0 ? pipes[index / 2].Start : pipes[index / 2].End;
        var endIndex = new SpatialIndex<int>(Enumerable.Range(0, deviceBase).Select(index => (index, EndPoint(index))), fittingTolerance);
        var fittingIndex = new SpatialIndex<int>(devices.Select((device, index) => (index, device.Origin)).Where(item => devices[item.index].Kind == HydraulicDeviceKind.Fitting), fittingTolerance);
        var fittingSizes = devices.Select(device => Sizes(device.Indication).Split('x', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        var fittingReach = fittingSizes.Select(sizes => Math.Max(fittingTolerance,
            2.5 * 0.0254 * unitsPerMeter * sizes.Select(size => double.Parse(size, CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max())).ToArray();
        var fittingReachMax = fittingReach.DefaultIfEmpty(fittingTolerance).Max();

        for (var index = 0; index < deviceBase; index++)
        {
            var pipe = pipes[index / 2];
            foreach (var match in endIndex.FindWithin(EndPoint(index), directTolerance, other => other / 2 != index / 2 && other > index))
            {
                var other = pipes[match.Item / 2];
                if (!Rejected(pipe.Id, (index % 2).ToString(), other.Id, (match.Item % 2).ToString())) unionFind.Union(index, match.Item);
            }
            if (fittingIndex.TryNearest(EndPoint(index), fittingTolerance, _ => true, out var fitting, out _)
                && !Rejected(pipe.Id, (index % 2).ToString(), devices[fitting].Id, ""))
                unionFind.Union(index, deviceBase + fitting);
        }

        // Válvulas e bombas não têm portas no IFC: ligam-se às extremidades que ficaram soltas perto da origem.
        bool[] LooseEnds()
        {
            var counts = new Dictionary<int, int>();
            // Ponta livre = única extremidade de tubo no seu nó (pode estar presa só a uma conexão, como flange ou redução).
            for (var point = 0; point < deviceBase; point++) counts[unionFind.Find(point)] = counts.GetValueOrDefault(unionFind.Find(point)) + 1;
            return Enumerable.Range(0, deviceBase).Select(index => counts[unionFind.Find(index)] == 1).ToArray();
        }
        var loose = LooseEnds();
        var attachedFittings = Enumerable.Range(0, deviceBase).Select(index => unionFind.Find(index)).ToHashSet();
        for (var d = 0; d < devices.Count; d++)
        {
            // Tomadas d'água: a origem fica no reservatório, longe da ponta do tubo de sucção.
            if (devices[d].Kind == HydraulicDeviceKind.Fitting && IsTankOutlet(devices[d].Type) && !attachedFittings.Contains(unionFind.Find(deviceBase + d))
                && endIndex.TryNearest(devices[d].Origin, valveReach, index => loose[index], out var outletEnd, out var outletDistance))
            {
                Join(devices[d].Id, "tomada d'água ligada à ponta solta mais próxima", outletDistance);
                unionFind.Union(outletEnd, deviceBase + d);
                loose[outletEnd] = false;
            }
            if (devices[d].Kind != HydraulicDeviceKind.Valve) continue;
            // Válvula em linha: par de pontas livres de tubos diferentes cujo segmento passa a até 5 cm da origem da válvula.
            var origin = devices[d].Origin;
            var near = endIndex.FindWithin(origin, valveReach, index => loose[index]).ToArray();
            var pair = near.SelectMany(a => near.Where(b => a.Item / 2 != b.Item / 2 && a.Item < b.Item).Select(b => (A: a, B: b)))
                .Where(item => SegmentDistance(origin, EndPoint(item.A.Item), EndPoint(item.B.Item)) <= fittingTolerance)
                .OrderBy(item => item.A.Distance + item.B.Distance).Select(item => new[] { item.A.Item, item.B.Item }).FirstOrDefault();
            var ends = pair ?? near.OrderBy(match => match.Distance).Take(1).Select(match => match.Item).ToArray();
            foreach (var end in ends) { unionFind.Union(end, deviceBase + d); loose[end] = false; }
            Join(devices[d].Id, pair is not null ? "válvula entre duas pontas alinhadas" : ends.Length == 1 ? "válvula ligada a uma só ponta" : "válvula sem ligação",
                ends.Length == 0 ? double.NaN : ends.Max(end => (EndPoint(end) - origin).Length));
            if (pair is null) warnings.Add($"Válvula {devices[d].Type} ({devices[d].Id}) sem par de pontas alinhadas até 40 cm; {(ends.Length == 1 ? "ligada como ponta" : "sem ligação")}.");
        }
        var pumpPorts = new List<(int Device, int[] Ends)>();
        for (var d = 0; d < devices.Count; d++)
        {
            if (devices[d].Kind != HydraulicDeviceKind.Pump) continue;
            var found = endIndex.FindWithin(devices[d].Origin, pumpReach, index => loose[index]).OrderBy(match => match.Distance).Take(2).ToArray();
            var ends = found.Select(match => match.Item).ToArray();
            Join(devices[d].Id, ends.Length == 2 ? "bomba entre as duas pontas soltas mais próximas" : "bomba ignorada (sem duas pontas até 1 m)", found.Select(match => match.Distance).DefaultIfEmpty(double.NaN).Max());
            foreach (var end in ends) loose[end] = false;
            if (ends.Length == 2) pumpPorts.Add((d, ends));
            else warnings.Add($"Bomba {devices[d].Type} ({devices[d].Id}) com {ends.Length} extremidade(s) livre(s) até 1 m; ignorada.");
        }

        // Segunda passada: a origem da conexão fica no centro da peça e, em bitolas grandes, a ponta do tubo fica além de 5 cm.
        // Conexões com portas faltando recebem pontas ainda soltas até 2,5 × a maior bitola da peça, só de tubos com bitola presente na peça.
        var portsInCluster = new Dictionary<int, int>();
        for (var point = 0; point < deviceBase; point++) portsInCluster[unionFind.Find(point)] = portsInCluster.GetValueOrDefault(unionFind.Find(point)) + 1;
        var missing = devices.Select((device, d) => device.Kind != HydraulicDeviceKind.Fitting ? 0
            : (IsTee(device.Type) ? 3 : IsElbow(device.Type) || device.Type.Contains("redução", StringComparison.OrdinalIgnoreCase) ? 2 : 0)
              - portsInCluster.GetValueOrDefault(unionFind.Find(deviceBase + d))).ToArray();
        var extended = Enumerable.Range(0, devices.Count).Where(d => missing[d] > 0)
            .SelectMany(d => endIndex.FindWithin(devices[d].Origin, fittingReach[d],
                    end => loose[end] && (fittingSizes[d].Length == 0 || fittingSizes[d].Contains(NominalSize(pipes[end / 2].InnerDiameter_m))))
                .Select(match => (Fitting: d, End: match.Item, match.Distance)))
            .OrderBy(item => item.Distance).ToList();
        var extendedLinks = 0;
        foreach (var (d, end, distance) in extended)
        {
            if (!loose[end] || missing[d] <= 0 || Rejected(pipes[end / 2].Id, (end % 2).ToString(), devices[d].Id, "")) continue;
            Join(devices[d].Id, "conexão ligada a ponta além de 5 cm", distance);
            Join(pipes[end / 2].Id, "ponta ligada a conexão além de 5 cm", distance);
            unionFind.Union(end, deviceBase + d);
            loose[end] = false;
            missing[d]--;
            extendedLinks++;
        }
        // Conexões órfãs (sem tubo no nó): quando os tubos se encontram no vértice e a peça fica deslocada, a conexão é atribuída
        // ao nó de tubos compatível mais próximo que ainda não tenha conexão, só para contar a perda localizada.
        portsInCluster.Clear();
        for (var point = 0; point < deviceBase; point++) portsInCluster[unionFind.Find(point)] = portsInCluster.GetValueOrDefault(unionFind.Find(point)) + 1;
        var clusterFitted = new HashSet<int>(devices.Select((device, d) => (device, d))
            .Where(item => item.device.Kind == HydraulicDeviceKind.Fitting && portsInCluster.ContainsKey(unionFind.Find(deviceBase + item.d)))
            .Select(item => unionFind.Find(deviceBase + item.d)));
        var orphans = Enumerable.Range(0, devices.Count).Where(d => devices[d].Kind == HydraulicDeviceKind.Fitting && !IsTankOutlet(devices[d].Type)
            && !portsInCluster.ContainsKey(unionFind.Find(deviceBase + d)))
            .SelectMany(d => endIndex.FindWithin(devices[d].Origin, fittingReach[d],
                    end => fittingSizes[d].Length == 0 || fittingSizes[d].Contains(NominalSize(pipes[end / 2].InnerDiameter_m)))
                .Where(match => portsInCluster.GetValueOrDefault(unionFind.Find(match.Item)) >= 2)
                .Select(match => (Fitting: d, End: match.Item, match.Distance)))
            .OrderBy(item => item.Distance).ToList();
        var adopted = new HashSet<int>();
        // A peça só é atribuída a um nó com geometria compatível: tê com 3 tubos; cotovelo 90° com 2 tubos em ângulo reto
        // (45° com desvio de 45°); redução com 2 tubos alinhados de diâmetros diferentes.
        var endsByRoot = Enumerable.Range(0, deviceBase).GroupBy(end => unionFind.Find(end)).ToDictionary(group => group.Key, group => group.ToArray());
        XbimVector3D Away(int end) => (end % 2 == 0 ? pipes[end / 2].End - pipes[end / 2].Start : pipes[end / 2].Start - pipes[end / 2].End).Normalized();
        bool Compatible(HydraulicDevice device, int[] ends)
        {
            if (IsTee(device.Type)) return ends.Length == 3;
            if (ends.Length != 2) return false;
            var dot = Away(ends[0]).DotProduct(Away(ends[1]));
            if (IsElbow(device.Type)) return device.Type.Contains("45") ? dot is > -0.85 and < -0.55 : Math.Abs(dot) < 0.3;
            return dot < -0.9 && Math.Abs(pipes[ends[0] / 2].InnerDiameter_m - pipes[ends[1] / 2].InnerDiameter_m) > 1e-4;
        }
        foreach (var (d, end, distance) in orphans)
        {
            var root = unionFind.Find(end);
            if (adopted.Contains(d) || clusterFitted.Contains(root) || !Compatible(devices[d], endsByRoot[root])) continue;
            Join(devices[d].Id, "conexão deslocada atribuída ao nó de tubos mais próximo", distance);
            unionFind.Union(deviceBase + d, end);
            clusterFitted.Add(unionFind.Find(end));
            adopted.Add(d);
        }
        if (extendedLinks + adopted.Count > 0)
            warnings.Add($"Conexões além de 5 cm da ponta do tubo (até 2,5 × a bitola da peça): {extendedLinks} ponta(s) ligadas e {adopted.Count} conexão(ões) atribuídas a nós de tubos para a perda localizada.");

        for (var h = 0; h < heads.Count; h++)
        {
            if (!endIndex.TryNearest(heads[h].Origin, fittingTolerance, _ => true, out var end, out var headDistance))
                throw new InvalidDataException($"Bico {heads[h].Number} ({heads[h].Id}) sem extremidade de tubo até 5 cm.");
            if (Rejected(heads[h].Id, heads[h].Number.ToString(), pipes[end / 2].Id, (end % 2).ToString()))
                throw new InvalidDataException($"A associação do bico {heads[h].Number} ao ramal foi rejeitada na revisão; corrija o modelo ou confirme outra associação.");
            unionFind.Union(headBase + h, end);
            if (headDistance > directTolerance) Join(heads[h].Id, "bico ligado à ponta de tubo mais próxima", headDistance);
        }
        // Pontas de tubo sem nenhum outro elemento no nó (fim de linha, ou ligação que a geometria não encontrou).
        var pointsInNode = new Dictionary<int, int>();
        for (var point = 0; point < headBase + heads.Count; point++) pointsInNode[unionFind.Find(point)] = pointsInNode.GetValueOrDefault(unionFind.Find(point)) + 1;
        var deadEnds = Enumerable.Range(0, deviceBase).Where(end => pointsInNode[unionFind.Find(end)] == 1).Select(end => $"{pipes[end / 2].Id}:{end % 2}").ToArray();

        // Nós hidráulicos.
        var nodeOf = new Dictionary<int, int>();
        int Node(int point) { var root = unionFind.Find(point); if (!nodeOf.TryGetValue(root, out var node)) nodeOf[root] = node = nodeOf.Count; return node; }
        var pipeNodes = pipes.Select((_, i) => (From: Node(2 * i), To: Node(2 * i + 1))).ToArray();
        var headNodes = heads.Select((_, h) => Node(headBase + h)).ToArray();
        var deviceNodes = devices.Select((_, d) => Node(deviceBase + d)).ToArray();
        var nodeCount = nodeOf.Count;
        var elevationSum = new double[nodeCount];
        var elevationCount = new int[nodeCount];
        for (var i = 0; i < pipes.Count; i++)
        {
            elevationSum[pipeNodes[i].From] += pipes[i].Start.Z / unitsPerMeter; elevationCount[pipeNodes[i].From]++;
            elevationSum[pipeNodes[i].To] += pipes[i].End.Z / unitsPerMeter; elevationCount[pipeNodes[i].To]++;
        }
        var elevation = elevationSum.Select((sum, i) => elevationCount[i] > 0 ? sum / elevationCount[i] : 0).ToArray();
        var nodeLabel = new string[nodeCount];
        for (var i = 0; i < pipes.Count; i++)
        {
            nodeLabel[pipeNodes[i].From] ??= $"{pipes[i].Id}:0";
            nodeLabel[pipeNodes[i].To] ??= $"{pipes[i].Id}:1";
        }

        // Perdas localizadas: tabela do memorial (por item e bitola) quando houver; senão tabela usual de aço.
        var memorialTable = input.MemorialEquivalents.GroupBy(entry => (Type: NormalizeType(entry.Item), Sizes: Sizes(entry.Item)))
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Length_m).Distinct().Order().ToArray());
        // Sem bitola na indicação (válvulas trazem só códigos como "VA"), usa-se a bitola nominal do maior tubo ligado.
        double[]? MemorialValues(HydraulicDevice device, int[] ends, string? asType = null)
        {
            var sizes = Sizes(device.Indication);
            if (sizes.Length == 0) sizes = NominalSize(ends.Max(end => pipes[end / 2].InnerDiameter_m));
            return memorialTable.GetValueOrDefault((NormalizeType(asType ?? device.Type), sizes));
        }
        var equivalent = new double[pipes.Count];
        var fromMemorial = 0;
        var fromTable = 0;
        var endsByNode = Enumerable.Range(0, deviceBase).GroupBy(index => index % 2 == 0 ? pipeNodes[index / 2].From : pipeNodes[index / 2].To)
            .ToDictionary(group => group.Key, group => group.ToArray());
        for (var d = 0; d < devices.Count; d++)
        {
            var device = devices[d];
            if (device.Kind == HydraulicDeviceKind.Pump || !endsByNode.TryGetValue(deviceNodes[d], out var ends)) continue;
            // Válvula de segurança em linha: no projeto modelo é "com válvula de retenção integrada"; perda de retenção.
            var inlineRelief = device.Kind == HydraulicDeviceKind.Valve && device.Type.Contains("segurança", StringComparison.OrdinalIgnoreCase) && ends.Length == 2;
            var values = inlineRelief ? MemorialValues(device, ends, "Válvula de retençao vertical") ?? MemorialValues(device, ends, "Válvula de retenção vertical")
                : MemorialValues(device, ends);
            double Largest() => ends.Max(end => pipes[end / 2].InnerDiameter_m);
            if (device.Kind == HydraulicDeviceKind.Valve)
            {
                var length = values?.Max() ?? (IsCheck(device.Type) || inlineRelief ? Standard(Largest()).Check : Standard(Largest()).Gate);
                foreach (var end in ends) equivalent[end / 2] += length / ends.Length;
            }
            else if (IsElbow(device.Type))
                foreach (var end in ends) equivalent[end / 2] += (values?.Max() ?? Standard(pipes[end / 2].InnerDiameter_m).Elbow) / ends.Length;
            else if (IsTee(device.Type) && ends.Length == 3)
            {
                XbimVector3D Direction(int end)
                {
                    var pipe = pipes[end / 2];
                    return (end % 2 == 0 ? pipe.End - pipe.Start : pipe.Start - pipe.End).Normalized();
                }
                var directions = ends.Select(Direction).ToArray();
                // Os dois tubos da passagem direta são opostos (produto escalar ≈ -1); a saída lateral é a que não tem oposto.
                var branch = Enumerable.Range(0, 3).MaxBy(i => Enumerable.Range(0, 3).Where(j => j != i).Min(j => directions[i].DotProduct(directions[j])));
                var standard = Standard(pipes[ends[branch] / 2].InnerDiameter_m);
                var lateral = values?.Max() ?? standard.TeeBranch;
                // NBR 10897, 9.4.4.7 c) e e): perda do tê só na mudança de direção, atribuída à saída lateral; passagem direta sem perda.
                equivalent[ends[branch] / 2] += lateral;
            }
            else continue;
            if (values is null) fromTable++; else fromMemorial++;
        }

        // Grafo por tubos; a bomba entra como ligação própria.
        var adjacency = Enumerable.Range(0, nodeCount).Select(_ => new List<int>()).ToArray();
        for (var i = 0; i < pipes.Count; i++)
        {
            if (pipeNodes[i].From == pipeNodes[i].To) continue;
            adjacency[pipeNodes[i].From].Add(i);
            adjacency[pipeNodes[i].To].Add(i);
        }
        int Other(int pipe, int node) => pipeNodes[pipe].From == node ? pipeNodes[pipe].To : pipeNodes[pipe].From;
        bool[] Reach(int start, (int A, int B)? bridge)
        {
            var seen = new bool[nodeCount];
            var queue = new Queue<int>([start]);
            seen[start] = true;
            while (queue.TryDequeue(out var current))
            {
                var next = adjacency[current].Select(pipe => Other(pipe, current)).ToList();
                if (bridge is (var a, var b)) { if (current == a) next.Add(b); if (current == b) next.Add(a); }
                foreach (var node in next) if (!seen[node]) { seen[node] = true; queue.Enqueue(node); }
            }
            return seen;
        }
        var demandNodes = headNodes.ToHashSet();
        var discharge = Reach(headNodes[0], null);
        var disconnected = heads.Where((_, h) => !discharge[headNodes[h]]).Select(head => head.Number).ToArray();
        if (disconnected.Length > 0)
            throw new InvalidDataException($"Bicos {string.Join(", ", disconnected)} não estão na mesma rede geométrica do bico {heads[0].Number}; revise as ligações antes do cálculo.");

        // Bomba: uma porta do lado dos bicos (recalque), a outra fora (sucção). Entre várias, a de maior bocal de sucção.
        (int Device, int Suction, int Discharge)? pump = null;
        if (input.SourcePipeId is null)
        {
            // Sucção = bocal de maior diâmetro (3" x 2": sucção 3"); no empate, o que não alcança os bicos sem passar pela bomba.
            int PortNode(int end) => end % 2 == 0 ? pipeNodes[end / 2].From : pipeNodes[end / 2].To;
            double PortDiameter(int end) => pipes[end / 2].InnerDiameter_m;
            var candidates = pumpPorts
                .Select(port =>
                {
                    var (first, second) = (port.Ends[0], port.Ends[1]);
                    var firstIsSuction = Math.Abs(PortDiameter(first) - PortDiameter(second)) > 1e-4 ? PortDiameter(first) > PortDiameter(second) : !discharge[PortNode(first)];
                    var (suctionEnd, dischargeEnd) = firstIsSuction ? (first, second) : (second, first);
                    return (port.Device, Suction: PortNode(suctionEnd), Discharge: PortNode(dischargeEnd), SuctionEnd: suctionEnd, DischargeEnd: dischargeEnd);
                })
                .Where(port => discharge[port.Discharge] && port.Suction != port.Discharge)
                .OrderByDescending(port => PortDiameter(port.DischargeEnd)).ThenByDescending(port => PortDiameter(port.SuctionEnd)).ThenBy(port => devices[port.Device].Id, StringComparer.Ordinal).ToArray();
            if (candidates.Length > 0) pump = (candidates[0].Device, candidates[0].Suction, candidates[0].Discharge);
            if (candidates.Length > 1)
                warnings.Add($"{candidates.Length} bombas alimentam a área; usada {devices[candidates[0].Device].Type} ({devices[candidates[0].Device].Id}); as demais ficam desligadas.");
        }
        var inComponent = pump is (_, var ps, var pd) ? Reach(headNodes[0], (ps, pd)) : discharge;

        // Contornos: com bomba, nível do reservatório nas tomadas d'água da sucção; sem bomba, extremidade livre (v0.19).
        var fixedNodes = new List<int>();
        string sourcePipe;
        string mode;
        if (pump is (var pumpDevice, var suctionNode, _))
        {
            var suctionSide = Reach(suctionNode, null);
            fixedNodes.AddRange(devices.Select((device, d) => (device, d)).Where(item => IsTankOutlet(item.device.Type) && suctionSide[deviceNodes[item.d]] && adjacency[deviceNodes[item.d]].Count > 0)
                .Select(item => deviceNodes[item.d]).Distinct());
            if (fixedNodes.Count == 0)
            {
                var ends = Enumerable.Range(0, nodeCount).Where(node => suctionSide[node] && adjacency[node].Count == 1 && node != suctionNode).ToArray();
                var largest = ends.Length == 0 ? 0 : ends.Max(node => pipes[adjacency[node][0]].InnerDiameter_m);
                fixedNodes.AddRange(ends.Where(node => pipes[adjacency[node][0]].InnerDiameter_m >= largest - 1e-6));
                if (fixedNodes.Count == 0) fixedNodes.Add(suctionNode);
                warnings.Add($"Tomadas d'água não identificadas na sucção; reservatório adotado nas {fixedNodes.Count} extremidades livres de maior diâmetro.");
            }
            sourcePipe = devices[pumpDevice].Id;
            mode = input.PumpCurve is { Length: >= 2 } ? "Bomba com curva" : "Bomba: altura manométrica necessária";
        }
        else if (input.SourcePipeId is not null)
        {
            var index = pipes.Select((pipe, i) => (pipe, i)).FirstOrDefault(item => item.pipe.Id == input.SourcePipeId);
            if (index.pipe is null) throw new InvalidDataException($"Tubo de alimentação {input.SourcePipeId} não existe na rede Sprinklers.");
            var node = new[] { pipeNodes[index.i].From, pipeNodes[index.i].To }.OrderBy(node => adjacency[node].Count).ThenBy(node => elevation[node]).First();
            if (!inComponent[node]) throw new InvalidDataException("O tubo de alimentação informado não está ligado à área de operação.");
            fixedNodes.Add(node);
            sourcePipe = input.SourcePipeId;
            mode = "Pressão necessária no tubo de alimentação";
        }
        else
        {
            var dangling = Enumerable.Range(0, nodeCount).Where(node => inComponent[node] && adjacency[node].Count == 1 && !demandNodes.Contains(node)).ToArray();
            if (dangling.Length == 0) throw new InvalidDataException("Não foi encontrada extremidade livre para a alimentação; informe o tubo de alimentação.");
            fixedNodes.Add(dangling.OrderByDescending(node => pipes[adjacency[node][0]].InnerDiameter_m).ThenBy(node => elevation[node]).First());
            sourcePipe = pipes[adjacency[fixedNodes[0]][0]].Id;
            mode = "Pressão necessária na alimentação";
            warnings.Add("Nenhuma bomba ligada à área: alimentação escolhida automaticamente (extremidade livre de maior diâmetro e menor cota); confira ou informe o tubo de alimentação.");
        }
        var tankLevel = fixedNodes.Max(node => elevation[node]) + input.TankLevel_m;

        // Poda de ramais mortos e redução de trechos em série.
        var keep = fixedNodes.Concat(demandNodes).ToHashSet();
        if (pump is (_, var s2, var d2)) { keep.Add(s2); keep.Add(d2); }
        var degree = adjacency.Select(list => list.Count).ToArray();
        var removedPipe = new bool[pipes.Count];
        var removedNode = new bool[nodeCount];
        for (var node = 0; node < nodeCount; node++) if (!inComponent[node]) removedNode[node] = true;
        for (var i = 0; i < pipes.Count; i++) if (!inComponent[pipeNodes[i].From] || pipeNodes[i].From == pipeNodes[i].To) removedPipe[i] = true;
        var prune = new Stack<int>(Enumerable.Range(0, nodeCount).Where(node => !removedNode[node] && degree[node] <= 1 && !keep.Contains(node)));
        while (prune.TryPop(out var node))
        {
            if (removedNode[node]) continue;
            removedNode[node] = true;
            foreach (var pipe in adjacency[node].Where(pipe => !removedPipe[pipe]))
            {
                removedPipe[pipe] = true;
                var other = Other(pipe, node);
                if (--degree[other] <= 1 && !keep.Contains(other) && !removedNode[other]) prune.Push(other);
            }
        }
        var unconnectedInArea = Enumerable.Range(0, deviceBase).Count(end => !removedPipe[end / 2]
            && pointsInNode[unionFind.Find(end)] == 1);
        if (unconnectedInArea > 0)
            warnings.Add($"{unconnectedInArea} extremidade(s) de tubo sem conexão geométrica na rede hidráulica calculada; confira em Montagem da rede se são finais intencionais ou conexões ausentes.");
        double PipeResistance(int pipe) => Resistance(pipes[pipe].Length_m + equivalent[pipe], pipes[pipe].InnerDiameter_m, pipes[pipe].C);

        var junctions = Enumerable.Range(0, nodeCount).Where(node => !removedNode[node] && (degree[node] != 2 || keep.Contains(node))).ToList();
        var links = new List<Link>();
        var walked = new bool[pipes.Count];
        foreach (var start in junctions)
            foreach (var first in adjacency[start].Where(pipe => !removedPipe[pipe] && !walked[pipe]))
            {
                var link = new Link(LinkKind.Chain, start, -1, 0);
                var (current, pipe) = (start, first);
                while (true)
                {
                    walked[pipe] = true;
                    link.Pipes.Add((pipe, pipeNodes[pipe].From == current));
                    link.Resistance += PipeResistance(pipe);
                    current = Other(pipe, current);
                    if (degree[current] != 2 || keep.Contains(current)) break;
                    pipe = adjacency[current].First(next => !removedPipe[next] && !walked[next]);
                }
                link.To = current;
                if (link.To != link.From) links.Add(link);
            }
        var chainCount = links.Count;
        Link? pumpLink = null;
        if (pump is (_, var suction, var dischargeNode))
        {
            pumpLink = new Link(LinkKind.Pump, suction, dischargeNode, 0) { Flow = 0.01 };
            if (input.PumpCurve is { Length: >= 2 } curve) pumpLink.Curve = FitCurve(curve);
            links.Add(pumpLink);
        }

        // Nós desconhecidos = junções exceto os de carga fixa; bicos descarregam em nós virtuais na cota do bico.
        var fixedSet = fixedNodes.ToHashSet();
        var unknown = junctions.Where(node => !fixedSet.Contains(node)).ToArray();
        var position = unknown.Select((node, i) => (node, i)).ToDictionary(item => item.node, item => item.i);
        var headElevation = heads.Select(head => head.Origin.Z / unitsPerMeter).ToArray();
        var emitterStart = links.Count;
        var active = heads.Select(head => input.ActiveHeads is null || input.ActiveHeads.Contains(head.Id)).ToArray();
        for (var h = 0; h < heads.Count; h++)
        {
            var coefficient = heads[h].K * Math.Sqrt(BarPerMca) / 60000; // m³/s por m^0,5
            links.Add(new Link(LinkKind.Emitter, headNodes[h], -2 - h, active[h] ? 1 / (coefficient * coefficient) : 1e18) { Flow = active[h] ? 0.0005 : 0 });
        }
        int n = unknown.Length;

        var iterations = 0;
        double[] Run(double fixedHead)
        {
            double Head(double[] solution, int node) => node <= -2 ? headElevation[-2 - node] : position.TryGetValue(node, out var i) ? solution[i] : fixedHead;
            var solution = new double[n];
            for (var iteration = 0; iteration < 300; iteration++)
            {
                iterations++;
                // Matriz esparsa (diagonal + um termo fora da diagonal por trecho): memória e tempo crescem com o número de trechos,
                // não com o quadrado do número de nós, e não há limite de tamanho da rede.
                var diagonal = new double[n];
                var pairs = new List<(int A, int B, double W)>();
                var rhs = new double[n];
                foreach (var link in links)
                {
                    var (loss, derivative) = link.Evaluate();
                    var inverse = 1 / derivative;
                    // Newton: Q_novo = constante + inverso × (H_de − H_para)
                    var constant = link.Flow - loss * inverse;
                    int? from = position.TryGetValue(link.From, out var fp) ? fp : null;
                    int? to = link.To >= 0 && position.TryGetValue(link.To, out var tp) ? tp : null;
                    if (from is int i) { diagonal[i] += inverse; rhs[i] -= constant; }
                    if (to is int j) { diagonal[j] += inverse; rhs[j] += constant; }
                    if (from is int a && to is int b) pairs.Add((a, b, inverse));
                    if (from is int c && to is null) rhs[c] += inverse * Head(solution, link.To);
                    if (to is int e && from is null) rhs[e] += inverse * Head(solution, link.From);
                }
                solution = ConjugateGradient(diagonal, pairs, rhs, solution);
                double change = 0, total = 0;
                foreach (var link in links)
                {
                    var (loss, derivative) = link.Evaluate();
                    var updated = link.Flow + (Head(solution, link.From) - Head(solution, link.To) - loss) / derivative;
                    change += Math.Abs(updated - link.Flow);
                    total += Math.Abs(updated);
                    link.Flow = updated;
                }
                if (change <= 1e-9 * Math.Max(total, 1e-6)) return solution;
            }
            throw new InvalidDataException("O solucionador hidráulico não convergiu em 300 iterações.");
        }
        double NodeHead(double[] solution, int node, double fixedHead) => position.TryGetValue(node, out var index) ? solution[index] : fixedHead;
        double MinimumPressure(double[] solution, double fixedHead) =>
            Enumerable.Range(0, heads.Count).Where(h => active[h]).Min(h => NodeHead(solution, headNodes[h], fixedHead) - headElevation[h]);
        void SetActive(bool[] mask)
        {
            for (var h = 0; h < heads.Count; h++)
            {
                var coefficient = heads[h].K * Math.Sqrt(BarPerMca) / 60000;
                links[emitterStart + h].Resistance = mask[h] ? 1 / (coefficient * coefficient) : 1e18;
                links[emitterStart + h].Flow = mask[h] ? 0.0005 : 0;
                active[h] = mask[h];
            }
        }

        // Parâmetro ajustado pela secante: carga na alimentação, ou altura de uma bomba de curva plana; com curva, cálculo direto.
        var target = input.TargetMinimumPressure_mca;
        double? lastParameter = null;
        double[] solutionFinal;
        double fixedHeadFinal;
        if (pumpLink is not null && input.PumpCurve is { Length: >= 2 })
        {
            fixedHeadFinal = tankLevel;
            solutionFinal = Run(tankLevel);
            var minimum = MinimumPressure(solutionFinal, tankLevel);
            if (minimum < target - 0.005)
                warnings.Add($"Com a curva informada, a menor pressão nos bicos é {minimum:0.00} m.c.a., abaixo do mínimo de {target:0.00} m.c.a.: a bomba não atende.");
        }
        else (solutionFinal, fixedHeadFinal) = Design();

        // A secante parte do último resultado quando há um (áreas candidatas em sequência convergem em poucas iterações).
        (double[] Solution, double FixedHead) Design()
        {
            double[] solutionFinal;
            Func<double, double[]> evaluate = pumpLink is null
                ? head => Run(head)
                : height => { pumpLink.Curve = (height, 0, -50); return Run(tankLevel); };
            double FixedHead(double parameter) => pumpLink is null ? parameter : tankLevel;
            double x0 = lastParameter ?? (pumpLink is null ? headElevation.Max() + target + 10 : headElevation.Max() - tankLevel + target + 10), x1 = x0 + (lastParameter is null ? 30 : 3);
            var f0 = MinimumPressure(evaluate(x0), FixedHead(x0)) - target;
            solutionFinal = evaluate(x1);
            var f1 = MinimumPressure(solutionFinal, FixedHead(x1)) - target;
            for (var step = 0; step < 50 && Math.Abs(f1) > 1e-5; step++)
            {
                var x2 = x1 - f1 * (x1 - x0) / (f1 - f0);
                (x0, f0) = (x1, f1);
                x1 = x2;
                solutionFinal = evaluate(x1);
                f1 = MinimumPressure(solutionFinal, FixedHead(x1)) - target;
            }
            if (Math.Abs(f1) > 1e-3) throw new InvalidDataException("Não foi possível ajustar a alimentação à pressão mínima dos bicos.");
            lastParameter = x1;
            return (solutionFinal, FixedHead(x1));
        }

        var sprinklerResults = heads.Select((head, h) => (head, h)).Where(item => active[item.h]).Select(item => new HydraulicSprinklerResult(item.head.Number, item.head.Id, headElevation[item.h],
            NodeHead(solutionFinal, headNodes[item.h], fixedHeadFinal) - headElevation[item.h], links[emitterStart + item.h].Flow * 1000)).ToArray();
        var flowByPipe = new Dictionary<int, double>();
        for (var l = 0; l < chainCount; l++)
            foreach (var (pipe, forward) in links[l].Pipes) flowByPipe[pipe] = forward ? links[l].Flow : -links[l].Flow;
        var pipeResults = flowByPipe.OrderBy(item => item.Key).Select(item =>
        {
            var pipe = pipes[item.Key];
            var q = item.Value;
            var area = Math.PI * pipe.InnerDiameter_m * pipe.InnerDiameter_m / 4;
            var loss = PipeResistance(item.Key) * Math.Pow(Math.Abs(q), HazenExponent);
            var (from, to) = q >= 0 ? (pipeNodes[item.Key].From, pipeNodes[item.Key].To) : (pipeNodes[item.Key].To, pipeNodes[item.Key].From);
            return new HydraulicPipeResult(pipe.Id, pipe.Type, pipe.InnerDiameter_m * 1000, pipe.Length_m, equivalent[item.Key], pipe.C,
                Math.Abs(q) * 1000, Math.Abs(q) / area, loss, nodeLabel[from], nodeLabel[to]);
        }).ToArray();
        var maxVelocity = pipeResults.Max(pipe => pipe.Velocity_m_s);
        if (maxVelocity > 5) warnings.Add($"Velocidade máxima de {maxVelocity:0.00} m/s acima de 5 m/s; revise os diâmetros.");

        // Trechos (ligações entre nós de cálculo) para a planilha do memorial, numerados a partir da alimentação.
        double Pressure(int node) => NodeHead(solutionFinal, node, fixedHeadFinal) - elevation[node];
        var headNumber = headNodes.Select((node, h) => (node, h)).Where(item => active[item.h]).GroupBy(item => item.node).ToDictionary(group => group.Key, group => heads[group.First().h].Number);
        var nodeNames = new Dictionary<int, string>();
        foreach (var node in junctions.OrderByDescending(node => NodeHead(solutionFinal, node, fixedHeadFinal)))
            nodeNames[node] = headNumber.TryGetValue(node, out var number) ? $"B{number}"
                : fixedSet.Contains(node) ? "RES"
                : pump is (_, var ps0, var pd0) && node == ps0 ? "SUC"
                : pump is (_, _, var pd1) && node == pd1 ? "REC"
                : $"N{nodeNames.Count(item => item.Value.StartsWith('N')) + 1}";
        var segments = new List<HydraulicSegmentResult>();
        foreach (var link in links.Take(chainCount).Where(link => Math.Abs(link.Flow) > 1e-6)
                     .OrderByDescending(link => Math.Max(NodeHead(solutionFinal, link.From, fixedHeadFinal), NodeHead(solutionFinal, link.To, fixedHeadFinal))))
        {
            var (from, to) = link.Flow >= 0 ? (link.From, link.To) : (link.To, link.From);
            var members = link.Pipes.Select(item => item.PipeIndex).ToArray();
            var q = Math.Abs(link.Flow);
            segments.Add(new HydraulicSegmentResult(segments.Count + 1, nodeNames[from], nodeNames[to], q * 1000,
                string.Join("/", members.Select(i => Math.Round(pipes[i].InnerDiameter_m * 1000, 1)).Distinct()),
                members.Sum(i => pipes[i].Length_m), members.Sum(i => equivalent[i]), link.Resistance * Math.Pow(q, HazenExponent),
                members.Max(i => q / (Math.PI * pipes[i].InnerDiameter_m * pipes[i].InnerDiameter_m / 4)),
                elevation[from], elevation[to], Pressure(from), Pressure(to)));
        }

        HydraulicPumpResult? pumpResult = null;
        var sourceNode = fixedNodes[0];
        var sourcePressure = fixedHeadFinal - elevation[sourceNode];
        if (pumpLink is not null && pump is (var deviceIndex, var suctionPort, var dischargePort))
        {
            var suctionHead = NodeHead(solutionFinal, suctionPort, fixedHeadFinal);
            var dischargeHead = NodeHead(solutionFinal, dischargePort, fixedHeadFinal);
            pumpResult = new HydraulicPumpResult(devices[deviceIndex].Id, devices[deviceIndex].Type, mode, pumpLink.Flow * 1000, dischargeHead - suctionHead,
                elevation[suctionPort], suctionHead - elevation[suctionPort], elevation[dischargePort], dischargeHead - elevation[dischargePort], tankLevel, fixedNodes.Count);
            sourcePressure = dischargeHead - elevation[dischargePort];
            sourceNode = dischargePort;
        }
        // Bombas candidatas: ponto de operação de cada curva na mesma rede (partindo do resultado de projeto).
        var candidateResults = new List<PumpCandidateResult>();
        if (pumpLink is not null && pump is (_, var cs, var cd))
        {
            var warm = links.Select(link => link.Flow).ToArray();
            foreach (var candidate in input.Candidates)
            {
                for (var l = 0; l < links.Count; l++) links[l].Flow = warm[l];
                try
                {
                    pumpLink.Curve = FitCurve(candidate.Points);
                    var solution = Run(tankLevel);
                    candidateResults.Add(new PumpCandidateResult(candidate.Id, pumpLink.Flow * 1000,
                        NodeHead(solution, cd, tankLevel) - NodeHead(solution, cs, tankLevel), MinimumPressure(solution, tankLevel),
                        NodeHead(solution, cs, tankLevel) - elevation[cs], NodeHead(solution, cd, tankLevel) - elevation[cd], null));
                }
                catch (InvalidDataException exception)
                {
                    candidateResults.Add(new PumpCandidateResult(candidate.Id, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, exception.Message));
                }
            }
        }
        // Curva do sistema: altura manométrica exigida em função da vazão, obtida com bombas de altura constante de 50% a 200% da necessária.
        var systemCurve = new List<(double Flow_l_s, double Head_m)>();
        if (pumpLink is not null && pump is (_, var ss, var sd) && pumpResult is not null)
        {
            var warm = links.Select(link => link.Flow).ToArray();
            foreach (var factor in new[] { 0.5, 0.6, 0.7, 0.8, 0.9, 1.0, 1.1, 1.2, 1.35, 1.5, 1.7, 2.0 })
            {
                for (var l = 0; l < links.Count; l++) links[l].Flow = warm[l];
                try
                {
                    pumpLink.Curve = (pumpResult.Head_m * factor, 0, -50);
                    var solution = Run(tankLevel);
                    if (pumpLink.Flow > 0) systemCurve.Add((pumpLink.Flow * 1000, NodeHead(solution, sd, tankLevel) - NodeHead(solution, ss, tankLevel)));
                }
                catch (InvalidDataException) { }
            }
        }
        // Áreas candidatas: cada uma com seus bicos ativos, ajustada à pressão mínima (bomba de curva plana ou carga na alimentação).
        var areaResults = new List<AreaCandidateResult>();
        if (input.AreaCandidates is { Count: > 0 } && input.PumpCurve is not { Length: >= 2 })
        {
            var idIndex = heads.Select((head, h) => (head.Id, h)).ToDictionary(item => item.Id, item => item.h);
            for (var c = 0; c < input.AreaCandidates.Count; c++)
            {
                var mask = new bool[heads.Count];
                foreach (var id in input.AreaCandidates[c]) if (idIndex.TryGetValue(id, out var h)) mask[h] = true;
                if (!mask.Any(value => value)) { areaResults.Add(new AreaCandidateResult(c, double.NaN, double.NaN, "sem bicos")); continue; }
                SetActive(mask);
                if (input.AreaTargets is { } targets && c < targets.Count) target = targets[c];
                try
                {
                    var (solution, fixedHead) = Design();
                    var flow = Enumerable.Range(0, heads.Count).Where(h => mask[h]).Sum(h => links[emitterStart + h].Flow) * 1000;
                    var required = pumpLink is not null && pump is (_, var asu, var adi)
                        ? NodeHead(solution, adi, fixedHead) - NodeHead(solution, asu, fixedHead)
                        : fixedHead - elevation[fixedNodes[0]];
                    areaResults.Add(new AreaCandidateResult(c, required, flow, null));
                }
                catch (InvalidDataException exception) { areaResults.Add(new AreaCandidateResult(c, double.NaN, double.NaN, exception.Message)); }
            }
        }
        return new HydraulicResult(mode, nodeLabel[sourceNode] ?? sourcePipe, sourcePipe, elevation[sourceNode], sourcePressure,
            sprinklerResults.Sum(item => item.Flow_l_s), target, sprinklerResults.Min(item => item.Pressure_mca),
            inComponent.Count(value => value), n, chainCount, iterations, pumpResult,
            (memorialTable.Count > 0 ? "Memorial (itens e bitolas da tabela de conexões), com tabela usual de aço nos demais" : "Tabela usual de aço")
                + "; tês somente na mudança de direção (NBR 10897, 9.4.4.7)",
            fromMemorial, fromTable, sprinklerResults, pipeResults, segments.ToArray(), candidateResults.ToArray(), systemCurve.OrderBy(point => point.Flow_l_s).ToArray(), warnings.ToArray()) { Areas = areaResults.ToArray(), Joins = joins, DeadEnds = deadEnds };
    }

    // Ajuste H = A + B Q + C Q² por mínimos quadrados (linear com 2 pontos). Vazões em l/s, alturas em m.
    public static (double A, double B, double C) FitCurve((double Flow_l_s, double Head_m)[] points)
    {
        var data = points.Select(point => (Q: point.Flow_l_s / 1000, H: point.Head_m)).ToArray();
        if (data.Length == 2)
        {
            var slope = (data[1].H - data[0].H) / (data[1].Q - data[0].Q);
            return (data[0].H - slope * data[0].Q, slope, 0);
        }
        var m = new double[3, 3];
        var v = new double[3];
        foreach (var (q, h) in data)
        {
            double[] row = [1, q, q * q];
            for (var i = 0; i < 3; i++) { v[i] += row[i] * h; for (var j = 0; j < 3; j++) m[i, j] += row[i] * row[j]; }
        }
        var x = Cholesky(m, v);
        return (x[0], x[1], x[2]);
    }

    // Gradiente conjugado com pré-condicionador diagonal para a matriz simétrica positiva definida do método do gradiente global.
    // Parte da solução anterior (as iterações de Newton mudam pouco de uma para a outra).
    static double[] ConjugateGradient(double[] diagonal, List<(int A, int B, double W)> pairs, double[] rhs, double[] start, int? iterationLimit = null)
    {
        var n = rhs.Length;
        var maxIterations = iterationLimit ?? 20 * n + 100;
        if (maxIterations < 0) throw new ArgumentOutOfRangeException(nameof(iterationLimit));
        if (diagonal.Any(value => value <= 0)) throw new InvalidDataException("Sistema hidráulico singular: há trecho sem caminho até a alimentação.");
        void Multiply(double[] x, double[] y)
        {
            for (var i = 0; i < n; i++) y[i] = diagonal[i] * x[i];
            foreach (var (a, b, w) in pairs) { y[a] -= w * x[b]; y[b] -= w * x[a]; }
        }
        var x = start.Length == n ? (double[])start.Clone() : new double[n];
        var residual = new double[n];
        Multiply(x, residual);
        for (var i = 0; i < n; i++) residual[i] = rhs[i] - residual[i];
        var z = residual.Select((value, i) => value / diagonal[i]).ToArray();
        var direction = (double[])z.Clone();
        var product = new double[n];
        var rz = Dot(residual, z);
        var limit = 1e-26 * Math.Max(Dot(rhs, rhs), 1e-30);
        for (var iteration = 0; iteration < maxIterations && Dot(residual, residual) > limit; iteration++)
        {
            Multiply(direction, product);
            var curvature = Dot(direction, product);
            if (curvature <= 0) throw new InvalidDataException("Sistema hidráulico singular: há trecho sem caminho até a alimentação.");
            var step = rz / curvature;
            for (var i = 0; i < n; i++) { x[i] += step * direction[i]; residual[i] -= step * product[i]; z[i] = residual[i] / diagonal[i]; }
            var next = Dot(residual, z);
            var beta = next / rz;
            rz = next;
            for (var i = 0; i < n; i++) direction[i] = z[i] + beta * direction[i];
        }
        // Só devolve a solução se o resíduo verdadeiro (b − A·x, recalculado) for pequeno; senão o cálculo para em vez de aceitar
        // uma solução incompleta. O resíduo da recorrência se afasta do verdadeiro por arredondamento, daí a folga de 1e-9.
        Multiply(x, product);
        double trueResidual = 0;
        for (var i = 0; i < n; i++) trueResidual += (rhs[i] - product[i]) * (rhs[i] - product[i]);
        var relative = Math.Sqrt(trueResidual / Math.Max(Dot(rhs, rhs), 1e-30));
        if (!double.IsFinite(relative) || relative > 1e-9)
            throw new InvalidDataException($"O solucionador linear não convergiu (resíduo relativo {relative:0.0e+0} após {maxIterations} iterações no máximo); a rede pode estar mal condicionada.");
        return x;
    }

    // Autoteste do solucionador linear: grelha quadrada de trechos com resistências variadas (como a matriz do gradiente global),
    // ligada à alimentação pelas bordas. Compara com o Cholesky denso até 2.500 nós e mede o tempo em redes maiores.
    public static IEnumerable<string> SolverSelfTest(int[] sides)
    {
        var random = new Random(10897);
        var rejectedNonFinite = false;
        try { _ = ConjugateGradient([1.0], [], [double.NaN], [0.0]); }
        catch (InvalidDataException error) when (error.Message.StartsWith("O solucionador linear não convergiu", StringComparison.Ordinal)) { rejectedNonFinite = true; }
        if (!rejectedNonFinite) throw new InvalidDataException("Autoteste falhou: o solver aceitou resíduo não finito.");
        var rejectedUnconverged = false;
        try { _ = ConjugateGradient([1.0], [], [1.0], [0.0], 0); }
        catch (InvalidDataException error) when (error.Message.StartsWith("O solucionador linear não convergiu", StringComparison.Ordinal)) { rejectedUnconverged = true; }
        if (!rejectedUnconverged) throw new InvalidDataException("Autoteste falhou: o solver aceitou a solução antes de convergir.");
        yield return "Falhas por resíduo não finito e por limite de iterações são rejeitadas.";
        foreach (var side in sides)
        {
            var n = side * side;
            var diagonal = new double[n];
            var pairs = new List<(int A, int B, double W)>();
            void Link(int a, int b, double w) { diagonal[a] += w; diagonal[b] += w; pairs.Add((a, b, w)); }
            for (var row = 0; row < side; row++)
                for (var col = 0; col < side; col++)
                {
                    var node = row * side + col;
                    // inversos das derivadas variam ~4 ordens de grandeza (tubos de 25 a 150 mm, vazões diferentes)
                    if (col + 1 < side) Link(node, node + 1, Math.Pow(10, random.NextDouble() * 4 - 6));
                    if (row + 1 < side) Link(node, node + side, Math.Pow(10, random.NextDouble() * 4 - 6));
                    if (row == 0 || col == 0) diagonal[node] += 1e-5; // ligação à alimentação (carga fixa)
                }
            var rhs = Enumerable.Range(0, n).Select(_ => (random.NextDouble() - 0.5) * 1e-3).ToArray();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var x = ConjugateGradient(diagonal, pairs, rhs, new double[n]);
            var seconds = watch.Elapsed.TotalSeconds;
            var check = new double[n];
            for (var i = 0; i < n; i++) check[i] = diagonal[i] * x[i];
            foreach (var (a, b, w) in pairs) { check[a] -= w * x[b]; check[b] -= w * x[a]; }
            var residual = Math.Sqrt(Enumerable.Range(0, n).Sum(i => (check[i] - rhs[i]) * (check[i] - rhs[i])) / rhs.Sum(v => v * v));
            var line = $"Grelha {side} × {side} = {n:N0} nós, {pairs.Count:N0} trechos: {seconds:0.000} s, resíduo relativo {residual:0.0e+0}";
            if (n <= 2500)
            {
                var dense = new double[n, n];
                for (var i = 0; i < n; i++) dense[i, i] = diagonal[i];
                foreach (var (a, b, w) in pairs) { dense[a, b] -= w; dense[b, a] -= w; }
                var reference = Cholesky(dense, rhs);
                var scale = reference.Max(Math.Abs);
                line += $"; diferença para o Cholesky denso {Enumerable.Range(0, n).Max(i => Math.Abs(reference[i] - x[i])) / scale:0.0e+0} (relativa)";
            }
            yield return line;
        }
    }
    static double Dot(double[] a, double[] b) { double sum = 0; for (var i = 0; i < a.Length; i++) sum += a[i] * b[i]; return sum; }

    static double[] Cholesky(double[,] matrix, double[] rhs)
    {
        var n = rhs.Length;
        for (var j = 0; j < n; j++)
        {
            var sum = matrix[j, j];
            for (var k = 0; k < j; k++) sum -= matrix[j, k] * matrix[j, k];
            if (sum <= 0) throw new InvalidDataException("Sistema hidráulico singular: há trecho sem caminho até a alimentação.");
            matrix[j, j] = Math.Sqrt(sum);
            for (var i = j + 1; i < n; i++)
            {
                var value = matrix[i, j];
                for (var k = 0; k < j; k++) value -= matrix[i, k] * matrix[j, k];
                matrix[i, j] = value / matrix[j, j];
            }
        }
        var y = new double[n];
        for (var i = 0; i < n; i++)
        {
            var value = rhs[i];
            for (var k = 0; k < i; k++) value -= matrix[i, k] * y[k];
            y[i] = value / matrix[i, i];
        }
        var x = new double[n];
        for (var i = n - 1; i >= 0; i--)
        {
            var value = y[i];
            for (var k = i + 1; k < n; k++) value -= matrix[k, i] * x[k];
            x[i] = value / matrix[i, i];
        }
        return x;
    }
}
