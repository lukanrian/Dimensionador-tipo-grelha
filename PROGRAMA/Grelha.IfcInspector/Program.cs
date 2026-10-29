using System.Reflection;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Xbim.Common.Geometry;
using Xbim.Ifc;
using Xbim.Ifc.Extensions;
using Xbim.Ifc4.Interfaces;

static string? TypeName(IIfcObject item) => item.IsTypedBy.FirstOrDefault()?.RelatingType.Name.ToString();
static string Id(IIfcRoot item) => item.GlobalId.Value.ToString() ?? throw new InvalidDataException("Elemento IFC sem GlobalId.");
static object? Position(IIfcObject item)
{
    if (item is not IIfcProduct product || product.ObjectPlacement is null) return null;
    var point = Origin(product);
    return new { point.X, point.Y, point.Z };
}

static string? Network(IIfcObject item) => item.IsDefinedBy
    .OfType<IIfcRelDefinesByProperties>()
    .SelectMany(relation => relation.RelatingPropertyDefinition is IIfcPropertySet set && set.Name.ToString() == "Identificação_Elemento"
        ? set.HasProperties.OfType<IIfcPropertySingleValue>()
        : Array.Empty<IIfcPropertySingleValue>())
    .FirstOrDefault(property => property.Name.ToString() == "Rede")?.NominalValue?.ToString();

static object Describe(IIfcObject item) => new
{
    GlobalId = item.GlobalId.Value,
    Name = item.Name.ToString(),
    Type = TypeName(item),
    Position = Position(item),
    PropertySets = item.IsDefinedBy
        .OfType<IIfcRelDefinesByProperties>()
        .SelectMany(relation => relation.RelatingPropertyDefinition is IIfcPropertySet set
            ? new[] { set }
            : Array.Empty<IIfcPropertySet>())
        .Select(set => new
        {
            Name = set.Name.ToString(),
            Properties = set.HasProperties.OfType<IIfcPropertySingleValue>()
                .Select(property => new
                {
                    Name = property.Name.ToString(),
                    Value = property.NominalValue?.ToString()
                })
                .ToArray()
        })
        .ToArray()
};

static XbimPoint3D Origin(IIfcProduct item) => item.ObjectPlacement!.ToMatrix3D().Transform(new XbimPoint3D(0, 0, 0));

static string Csv(string? value) => $"\"{(value ?? "").Replace("\"", "\"\"")}\"";

static int? ExpectedFittingPorts(string? type)
{
    if (type?.StartsWith("Tê", StringComparison.OrdinalIgnoreCase) == true || type?.StartsWith("Te ", StringComparison.OrdinalIgnoreCase) == true) return 3;
    if (type?.Contains("cotovelo", StringComparison.OrdinalIgnoreCase) == true || type?.Contains("curva", StringComparison.OrdinalIgnoreCase) == true
        || type?.Contains("redução", StringComparison.OrdinalIgnoreCase) == true || type?.Contains("visor", StringComparison.OrdinalIgnoreCase) == true) return 2;
    return null;
}

static string EdgeKey(string from, string fromEnd, string to, string toEnd) => string.Join('\u001f', from, fromEnd, to, toEnd);

static Dictionary<string, (string Decision, string Notes, Dictionary<string, string> Fields)> ReadPriorReviews(string path)
{
    var reviews = new Dictionary<string, (string Decision, string Notes, Dictionary<string, string> Fields)>(StringComparer.Ordinal);
    if (!File.Exists(path)) return reviews;
    using var parser = new TextFieldParser(path, System.Text.Encoding.UTF8)
    {
        TextFieldType = FieldType.Delimited,
        HasFieldsEnclosedInQuotes = true,
        TrimWhiteSpace = false
    };
    parser.SetDelimiters(",");
    var header = parser.ReadFields() ?? throw new InvalidDataException("Cabeçalho ausente no relatório de revisão anterior.");
    var indices = header.Select((name, index) => (name, index)).ToDictionary(value => value.name, value => value.index, StringComparer.OrdinalIgnoreCase);
    string Field(string[] values, string name) => indices.TryGetValue(name, out var index) && index < values.Length ? values[index] : "";
    while (!parser.EndOfData)
    {
        var values = parser.ReadFields() ?? [];
        var from = Field(values, "FromGlobalId");
        if (from.Length == 0) from = Field(values, "SprinklerGlobalId");
        if (from.Length == 0) continue;
        var fromEnd = Field(values, "FromEnd");
        if (fromEnd.Length == 0) fromEnd = Field(values, "SprinklerNumber");
        var to = Field(values, "ToGlobalId");
        if (to.Length == 0) to = Field(values, "PipeGlobalId");
        var toEnd = Field(values, "ToEnd");
        if (toEnd.Length == 0) toEnd = Field(values, "PipeEnd");
        var key = EdgeKey(from, fromEnd, to, toEnd);
        var decision = Field(values, "Decision").Trim();
        decision = decision.Length == 0 || decision.Equals("Pendente", StringComparison.OrdinalIgnoreCase) ? "Pendente"
            : decision.Equals("Confirmada", StringComparison.OrdinalIgnoreCase) ? "Confirmada"
            : decision.Equals("Rejeitada", StringComparison.OrdinalIgnoreCase) ? "Rejeitada"
            : throw new InvalidDataException($"Decisão '{decision}' inválida no relatório de revisão. Use Pendente, Confirmada ou Rejeitada.");
        var fields = header.Select((name, index) => (name, value: index < values.Length ? values[index] : ""))
            .ToDictionary(value => value.name, value => value.value, StringComparer.OrdinalIgnoreCase);
        if (!reviews.TryAdd(key, (decision, Field(values, "ReviewNotes"), fields)))
            throw new InvalidDataException("Relatório de revisão contém a mesma ligação mais de uma vez.");
    }
    return reviews;
}

// Diâmetro interno do tubo (propriedade do IFC, na unidade de comprimento do modelo); 0 quando ausente.
static double InnerDiameter(IIfcObject item)
{
    var value = item.IsDefinedBy.OfType<IIfcRelDefinesByProperties>()
        .SelectMany(relation => relation.RelatingPropertyDefinition is IIfcPropertySet set ? set.HasProperties.OfType<IIfcPropertySingleValue>() : Array.Empty<IIfcPropertySingleValue>())
        .FirstOrDefault(property => property.Name.ToString() is "Diâmetro interno" or "InnerDiameter")?.NominalValue?.ToString();
    return value is { Length: > 0 } && double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}

static string Number(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

static double UnitsPerMeter(IIfcSIUnit unit) => unit.Prefix switch
{
    IfcSIPrefix.MILLI => 1000,
    IfcSIPrefix.CENTI => 100,
    IfcSIPrefix.DECI => 10,
    IfcSIPrefix.DECA => 0.1,
    IfcSIPrefix.HECTO => 0.01,
    IfcSIPrefix.KILO => 0.001,
    null => 1,
    _ => throw new InvalidDataException($"Prefixo de unidade não suportado: {unit.Prefix}")
};

static double ParseDecimal(string value)
{
    var normalized = value.Trim();
    var decimalSeparator = Math.Max(normalized.LastIndexOf('.'), normalized.LastIndexOf(','));
    if (decimalSeparator >= 0)
        normalized = string.Concat(normalized[..decimalSeparator].Replace(".", "").Replace(",", ""), ".", normalized[(decimalSeparator + 1)..]);
    return double.Parse(normalized, CultureInfo.InvariantCulture);
}

static string RequiredGroup(string text, string pattern, string field) =>
    Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Groups[1].Value is { Length: > 0 } value
        ? value.Trim()
        : throw new InvalidDataException($"Campo '{field}' não encontrado no memorial.");

static (string Path, MemorialData Data)? ReadMemorial(string directory)
{
    var files = Directory.GetFiles(directory, "*.pdf")
        .Where(path => Path.GetFileName(path).Contains("MEM", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(path).Contains("SPK", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    if (files.Length == 0) return null;
    if (files.Length > 1) throw new InvalidDataException("Há mais de um PDF de memorial com MEM e SPK no nome; deixe apenas um ou torne o nome inequívoco.");

    using var document = PdfDocument.Open(files[0]);
    var firstPage = ContentOrderTextExtractor.GetText(document.GetPage(1));
    var allPages = string.Join(Environment.NewLine, document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
    var density = ParseDecimal(RequiredGroup(firstPage, @"Densidade aplicada\s*\(mm/min\):\s*([\d.,]+)", "densidade"));
    var designArea = ParseDecimal(RequiredGroup(firstPage, @"Área de aplicação\s*\(m2\):\s*([\d.,]+)", "área de aplicação"));
    var sprinklerCount = int.Parse(RequiredGroup(firstPage, @"Quantidade de bicos:\s*(\d+)", "quantidade de bicos"), CultureInfo.InvariantCulture);
    var kFactor = ParseDecimal(RequiredGroup(firstPage, "Fator\\s+[“\\\"']?k[”\\\"']?\\s+adotado:\\s*([\\d.,]+)", "fator K"));
    var minimumPressure = ParseDecimal(RequiredGroup(firstPage, @"Pressão mínima no chuveiro mais desfavorável:\s*([\d.,]+)", "pressão mínima"));
    var operationArea = RequiredGroup(firstPage, @"Área de operação\s+(.+?)\s+Sprinklers analisados", "área de operação");
    var method = RequiredGroup(firstPage, @"Processo de cálculo:\s*(.+)", "processo de cálculo");
    var risk = Regex.Match(firstPage, @"Ocupação de (risco[^\r\n]+)", RegexOptions.IgnoreCase) is { Success: true } riskMatch ? riskMatch.Groups[1].Value.Trim() : "";
    var rows = Regex.Matches(firstPage, @"(?m)^\s*(\d{1,2})\s+([\d.,]+)\s+([\d.,]+)\s+(\d+)\s+([\d.,]+)\s+([\d.,]+)\s+([\d.,]+)\s*$")
        .Select(match => new
        {
            Number = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            X = ParseDecimal(match.Groups[2].Value),
            Y = ParseDecimal(match.Groups[3].Value),
            Thread = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
            K = ParseDecimal(match.Groups[5].Value),
            Flow = ParseDecimal(match.Groups[6].Value),
            Pressure = ParseDecimal(match.Groups[7].Value)
        }).ToArray();
    if (rows.Length != sprinklerCount || rows.Where((row, index) => row.Number != index + 1 || row.K != kFactor).Any())
        throw new InvalidDataException($"Tabela do memorial inconsistente: esperados {sprinklerCount} bicos sequenciais com K={kFactor}; encontrados {rows.Length}.");
    var pumpFlow = ParseDecimal(RequiredGroup(allPages, @"Vazão na área de aplicação l/min\s*:?\s*([\d.,]+)", "vazão da bomba"));
    var pumpPower = ParseDecimal(RequiredGroup(allPages, @"Potência\s*\(cv\):\s*([\d.,]+)", "potência da bomba"));
    var pumpHead = ParseDecimal(RequiredGroup(allPages, @"Altura manométrica:\s*([\d.,]+)\s*m\.c\.a\.", "altura manométrica da bomba"));
    var tankVolume = ParseDecimal(RequiredGroup(allPages, @"Quantidade de Reserva Técnica de Incêndio\s*\(m³\):\s*([\d.,]+)", "reserva técnica"));
    // Tabela "Conexões / L equivalente": cada item termina em "quantidade unitária total"; nomes podem quebrar linha.
    var equivalents = new List<EquivalentLengthEntry>();
    var section = allPages.IndexOf("L equivalente", StringComparison.OrdinalIgnoreCase);
    if (section >= 0)
    {
        var pending = "";
        foreach (var rawLine in allPages[section..].Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("=====") || line.StartsWith("Departamento") || line.StartsWith("Tel.") || line.StartsWith("“") || line.StartsWith("Material Grupo")
                || line.StartsWith("Trecho de") || line.Contains("L equivalente") || line.Length == 0) { pending = ""; continue; }
            var material = Regex.Match(line, @"^(ACa|FºGº|FoGo|Cu|PVC|BH|CPVC)\s+");
            if (material.Success) { pending = ""; line = line[material.Length..]; }
            var tail = Regex.Match(line, @"^(.*?)\s*(\d+)\s+(\d+[.,]\d+)\s+(\d+[.,]\d+)$");
            if (!tail.Success) { pending = $"{pending} {line}".Trim(); continue; }
            var item = $"{pending} {tail.Groups[1].Value}".Trim();
            pending = "";
            if (item.Length > 0) equivalents.Add(new EquivalentLengthEntry(item, ParseDecimal(tail.Groups[3].Value)));
        }
    }
    var heads = rows.Select(row => new MemorialSprinkler(row.Number, row.X, row.Y, row.Thread, row.K, row.Flow, row.Pressure)).ToArray();
    if (Math.Abs(heads.Min(head => head.Pressure_mca) - minimumPressure) > 0.01 || Math.Abs(heads.Sum(head => head.Flow_l_s) - pumpFlow / 60) > 0.02)
        throw new InvalidDataException("Pressão mínima ou vazão da tabela de bicos não confere com o resumo hidráulico do memorial.");
    if (heads.Any(head => Math.Abs(head.Flow_l_s - kFactor * Math.Sqrt(head.Pressure_mca * 0.0980665) / 60) > 0.02))
        throw new InvalidDataException("Uma vazão da tabela não confere com K√P usando a pressão em m.c.a.");
    var data = new MemorialData(operationArea, method, density, designArea, sprinklerCount, kFactor, minimumPressure, pumpFlow,
        pumpPower, pumpHead, tankVolume, heads, equivalents.ToArray(), risk);
    return (files[0], data);
}

static void AddEdge(Dictionary<string, HashSet<string>> graph, string from, string to)
{
    graph[from].Add(to);
    graph[to].Add(from);
}

static (Dictionary<string, int> Ids, Dictionary<int, int> Sizes) FindComponents(Dictionary<string, HashSet<string>> graph)
{
    var groups = new List<List<string>>();
    var visited = new HashSet<string>();
    foreach (var start in graph.Keys.Order(StringComparer.Ordinal))
    {
        if (!visited.Add(start)) continue;
        var group = new List<string>();
        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.TryPop(out var current))
        {
            group.Add(current);
            foreach (var neighbor in graph[current])
                if (visited.Add(neighbor)) pending.Push(neighbor);
        }
        groups.Add(group);
    }

    groups.Sort((left, right) => right.Count != left.Count
        ? right.Count.CompareTo(left.Count)
        : StringComparer.Ordinal.Compare(left.Min(StringComparer.Ordinal), right.Min(StringComparer.Ordinal)));
    var ids = new Dictionary<string, int>(StringComparer.Ordinal);
    var sizes = new Dictionary<int, int>();
    for (var i = 0; i < groups.Count; i++)
    {
        sizes[i + 1] = groups[i].Count;
        foreach (var pipeId in groups[i]) ids[pipeId] = i + 1;
    }
    return (ids, sizes);
}

var logDirectory = Environment.GetEnvironmentVariable("GRELHA_LOG_DIR") ?? Path.Combine(Environment.CurrentDirectory, "logs");
Directory.CreateDirectory(logDirectory);
var logPath = Environment.GetEnvironmentVariable("GRELHA_LOG_PATH") ?? Path.Combine(logDirectory, $"Grelha_{DateTime.Now:yyyyMMdd_HHmmss_fff}.log");
async Task LogAsync(string message) => await File.AppendAllTextAsync(logPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");

var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "desconhecida";
Console.WriteLine($"Log: {logPath}");
await LogAsync($"Leitor IFC iniciado. Versão {version}.");

try
{
    string? sourcePipeOption = null;
    var sourceOptionIndex = Array.IndexOf(args, "--alimentacao");
    if (sourceOptionIndex >= 0 && sourceOptionIndex + 1 < args.Length)
    {
        sourcePipeOption = args[sourceOptionIndex + 1];
        args = args.Where((_, index) => index != sourceOptionIndex && index != sourceOptionIndex + 1).ToArray();
    }
    string? memorialPumpOption = null;
    var memorialOptionIndex = Array.IndexOf(args, "--memorial");
    if (memorialOptionIndex >= 0 && memorialOptionIndex + 1 < args.Length)
    {
        memorialPumpOption = args[memorialOptionIndex + 1];
        args = args.Where((_, index) => index != memorialOptionIndex && index != memorialOptionIndex + 1).ToArray();
    }
    string? areaAutoOption = null;
    var areaAutoIndex = Array.IndexOf(args, "--area-auto");
    if (areaAutoIndex >= 0 && areaAutoIndex + 1 < args.Length)
    {
        areaAutoOption = args[areaAutoIndex + 1];
        args = args.Where((_, index) => index != areaAutoIndex && index != areaAutoIndex + 1).ToArray();
    }
    string? areaOption = null;
    var areaOptionIndex = Array.IndexOf(args, "--area");
    if (areaOptionIndex >= 0 && areaOptionIndex + 1 < args.Length)
    {
        areaOption = args[areaOptionIndex + 1];
        args = args.Where((_, index) => index != areaOptionIndex && index != areaOptionIndex + 1).ToArray();
    }
    double tankLevelOption = 0;
    var tankOptionIndex = Array.IndexOf(args, "--nivel-reservatorio");
    if (tankOptionIndex >= 0 && tankOptionIndex + 1 < args.Length)
    {
        tankLevelOption = ParseDecimal(args[tankOptionIndex + 1]);
        args = args.Where((_, index) => index != tankOptionIndex && index != tankOptionIndex + 1).ToArray();
    }
    if (args.Length != 1 || !File.Exists(args[0]))
    {
        const string message = "Uso: Grelha.IfcInspector <arquivo.ifc> [--alimentacao <GlobalId do tubo>] [--nivel-reservatorio <m acima da tomada>] [--memorial <Id da bomba>] [--area <área de operação .json>] [--area-auto <critérios .json>]. Arquivo IFC ausente ou não encontrado.";
        Console.Error.WriteLine(message);
        await LogAsync($"Falha: {message} Argumentos: {string.Join(" ", args)}");
        return 2;
    }

    var input = Path.GetFullPath(args[0]);
    await LogAsync($"Abrindo IFC: {input}");
    using var model = IfcStore.Open(input, ifcDatabaseSizeThreshHold: 64);

    var pipes = model.Instances.OfType<IIfcPipeSegment>().Where(item => Network(item) == "Sprinklers").ToList();
    var fittings = model.Instances.OfType<IIfcPipeFitting>().Where(item => Network(item) == "Sprinklers").ToList();
    var terminals = model.Instances.OfType<IIfcFireSuppressionTerminal>().Where(item => Network(item) == "Sprinklers").ToList();
    var sprinklers = terminals.Where(item => TypeName(item)?.StartsWith("Sprinkler", StringComparison.OrdinalIgnoreCase) == true).ToList();
    var project = model.Instances.OfType<IIfcProject>().FirstOrDefault() ?? throw new InvalidDataException("IFC sem projeto.");
    var lengthUnit = project.UnitsInContext.Units.OfType<IIfcSIUnit>().FirstOrDefault(unit => unit.UnitType == IfcUnitEnum.LENGTHUNIT)
        ?? throw new InvalidDataException("Unidade IFC de comprimento SI não encontrada.");
    var unitsPerMeter = UnitsPerMeter(lengthUnit);
    var sprinklerAnchors = sprinklers.Where(item => item.ObjectPlacement is not null)
        .Select(item => (Item: item, Point: Origin(item))).ToList();
    var memorialSource = ReadMemorial(Path.GetDirectoryName(input)!);
    var memorial = memorialSource?.Data;
    // Área de operação escolhida na janela (modo 2D): substitui a tabela de bicos do memorial; o memorial PDF, se houver, só fornece os comprimentos equivalentes.
    var areaMode = areaOption is not null;
    if (areaMode)
    {
        using var areaDocument = JsonDocument.Parse(await File.ReadAllTextAsync(areaOption!));
        var area = areaDocument.RootElement;
        double AreaNumber(string name, double fallback) => area.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;
        var ids = area.GetProperty("SprinklerIds").EnumerateArray().Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var chosen = sprinklerAnchors.Where(anchor => ids.Contains(Id(anchor.Item))).OrderByDescending(anchor => Math.Round(anchor.Point.Y)).ThenBy(anchor => anchor.Point.X).ToArray();
        if (chosen.Length == 0) throw new InvalidDataException("Nenhum bico da área escolhida existe na rede Sprinklers do IFC.");
        var k = AreaNumber("K", 80);
        var heads = chosen.Select((anchor, index) => new MemorialSprinkler(index + 1, anchor.Point.X, anchor.Point.Y, 15, k, double.NaN, double.NaN)).ToArray();
        memorial = new MemorialData(area.TryGetProperty("Name", out var name) ? name.GetString() ?? "Área escolhida" : "Área escolhida", "Hazen-Williams",
            AreaNumber("Density_mm_min", 6.1), AreaNumber("DesignArea_m2", 140), heads.Length, k, AreaNumber("MinimumPressure_mca", 0),
            double.NaN, double.NaN, double.NaN, double.NaN, heads, memorial?.EquivalentLengths ?? [],
            area.TryGetProperty("RiskClass", out var risk) ? risk.GetString() ?? "" : memorial?.RiskClass ?? "");
        await LogAsync($"Área de operação escolhida na janela: {heads.Length} bicos ({areaOption}).");
    }
    var operationArea = memorial?.Sprinklers.Select(point => (X: point.X_cm, Y: point.Y_cm, Flow: point.Flow_l_s, Pressure: point.Pressure_mca, Number: point.Number, Thread: point.ThreadDiameter_mm, K: point.K)).ToArray() ?? [];
    var bestAlignment = (Matches: 0, Rms: double.PositiveInfinity, Rotation: (A: 1.0, B: 0.0), Translation: (X: 0.0, Y: 0.0));
    if (areaMode) bestAlignment = (operationArea.Length, 0, (1.0, 0.0), (0.0, 0.0));
    if (!areaMode && operationArea.Length > 0 && sprinklerAnchors.Count < 2)
        throw new InvalidDataException("O IFC não tem sprinklers suficientes para comparar com o memorial.");
    if (!areaMode && operationArea.Length > 0)
    {
        var (seedA, seedB) = (operationArea[0], operationArea[1]);
        var targetDistance = Math.Sqrt(Math.Pow(seedA.X - seedB.X, 2) + Math.Pow(seedA.Y - seedB.Y, 2));
        for (var i = 0; i < sprinklerAnchors.Count; i++)
        for (var j = i + 1; j < sprinklerAnchors.Count; j++)
        {
            var a = sprinklerAnchors[i].Point;
            var b = sprinklerAnchors[j].Point;
            var distance = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            if (Math.Abs(distance - targetDistance) > 0.1 * unitsPerMeter) continue;
            foreach (var (from, to) in new[] { (a, b), (b, a) })
            {
                var sx = to.X - from.X;
                var sy = to.Y - from.Y;
                var cosine = ((seedB.X - seedA.X) * sx + (seedB.Y - seedA.Y) * sy) / (targetDistance * targetDistance);
                var sine = ((seedB.Y - seedA.Y) * sx - (seedB.X - seedA.X) * sy) / (targetDistance * targetDistance);
                var offsetX = seedA.X - (cosine * from.X - sine * from.Y);
                var offsetY = seedA.Y - (sine * from.X + cosine * from.Y);
                var errors = operationArea.Select(point => sprinklerAnchors.Min(anchor =>
                {
                    var x = cosine * anchor.Point.X - sine * anchor.Point.Y + offsetX;
                    var y = sine * anchor.Point.X + cosine * anchor.Point.Y + offsetY;
                    return Math.Sqrt(Math.Pow(x - point.X, 2) + Math.Pow(y - point.Y, 2));
                })).ToArray();
                var count = errors.Count(error => error <= 0.1 * unitsPerMeter);
                var rms = Math.Sqrt(errors.Average(error => error * error));
                if (count > bestAlignment.Matches || count == bestAlignment.Matches && rms < bestAlignment.Rms)
                    bestAlignment = (count, rms, (cosine, sine), (offsetX, offsetY));
            }
        }
    }
    if (operationArea.Length > 0 && (bestAlignment.Matches != operationArea.Length || bestAlignment.Rms > 0.1 * unitsPerMeter))
        throw new InvalidDataException("Não foi possível alinhar com confiança os sprinklers do memorial ao IFC.");
    var operationSprinklers = operationArea.Select((point, index) =>
    {
        var match = sprinklerAnchors
            .Select(anchor =>
            {
                var x = bestAlignment.Rotation.A * anchor.Point.X - bestAlignment.Rotation.B * anchor.Point.Y + bestAlignment.Translation.X;
                var y = bestAlignment.Rotation.B * anchor.Point.X + bestAlignment.Rotation.A * anchor.Point.Y + bestAlignment.Translation.Y;
                return (anchor.Item, Distance: Math.Sqrt(Math.Pow(x - point.X, 2) + Math.Pow(y - point.Y, 2)));
            })
            .MinBy(candidate => candidate.Distance);
        if (match.Item is null || match.Distance > 0.01 * unitsPerMeter)
            throw new InvalidDataException($"Sprinkler {index + 1} do memorial não corresponde ao IFC dentro de 1 cm após alinhamento.");
        return new
        {
            Number = point.Number,
            MemorialX_cm = point.X,
            MemorialY_cm = point.Y,
            MemorialFlow_l_s = point.Flow,
            MemorialPressure_mca = point.Pressure,
            MemorialThreadDiameter_mm = point.Thread,
            MemorialKFactor = point.K,
            IfcGlobalId = Id(match.Item),
            IfcX = Origin(match.Item).X,
            IfcY = Origin(match.Item).Y,
            MatchDistance_ifc_units = match.Distance
        };
    }).ToArray();
    if (operationSprinklers.Select(sprinkler => sprinkler.IfcGlobalId).Distinct(StringComparer.Ordinal).Count() != operationSprinklers.Length)
        throw new InvalidDataException("Dois bicos do memorial foram associados ao mesmo sprinkler IFC.");
    // shortcut: 20 cm is an audit window only; use fitting-specific ports before treating proximity as a connection.
    var searchRadius = 0.20 * unitsPerMeter;
    // shortcut: 1 mm marks direct geometric contact for this audit; confirm the tolerance against exported coordinates before calculation.
    var directTouchTolerance = 0.001 * unitsPerMeter;

    var pipeEnds = new List<(IIfcPipeSegment Pipe, int End, XbimPoint3D Point)>();
    foreach (var pipe in pipes)
    {
        var solid = pipe.Representation?.Representations
            .SelectMany(representation => representation.Items)
            .OfType<IIfcExtrudedAreaSolid>()
            .FirstOrDefault();
        if (solid is null || pipe.ObjectPlacement is null) continue;

        var ratios = solid.ExtrudedDirection.DirectionRatios.ToArray();
        if (ratios.Length < 3) continue;
        var depth = double.Parse(solid.Depth.ToString(), CultureInfo.InvariantCulture);
        var transform = solid.Position.ToMatrix3D() * pipe.ObjectPlacement.ToMatrix3D();
        var start = transform.Transform(new XbimPoint3D(0, 0, 0));
        var end = transform.Transform(new XbimPoint3D(ratios[0] * depth, ratios[1] * depth, ratios[2] * depth));
        pipeEnds.Add((pipe, 0, start));
        pipeEnds.Add((pipe, 1, end));
    }

    string? operationAreaPath = memorial is null ? null : Path.ChangeExtension(input, ".operation-area-candidates.csv");
    var operationAreaPipeNumbers = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
    if (operationAreaPath is not null)
    await using (var writer = new StreamWriter(operationAreaPath, false, System.Text.Encoding.UTF8))
    {
        await writer.WriteLineAsync("SprinklerNumber,SprinklerGlobalId,MemorialThreadDiameter_mm,MemorialKFactor,MemorialFlow_l_s,MemorialPressure_mca,PipeGlobalId,PipeType,PipeDiameter,PipeLength_ifc_unit,PipeEnd,Distance_ifc_unit,Status");
        foreach (var head in operationSprinklers)
        {
            var anchor = sprinklerAnchors.First(item => Id(item.Item) == head.IfcGlobalId);
            foreach (var end in pipeEnds)
            {
                var dx = anchor.Point.X - end.Point.X;
                var dy = anchor.Point.Y - end.Point.Y;
                var dz = anchor.Point.Z - end.Point.Z;
                var distance = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (distance > 0.05 * unitsPerMeter) continue;
                var pipeId = Id(end.Pipe);
                if (!operationAreaPipeNumbers.TryGetValue(pipeId, out var sprinklerNumbers))
                    operationAreaPipeNumbers[pipeId] = sprinklerNumbers = [];
                sprinklerNumbers.Add(head.Number);
                var properties = end.Pipe.IsDefinedBy.OfType<IIfcRelDefinesByProperties>()
                    .SelectMany(relation => relation.RelatingPropertyDefinition is IIfcPropertySet set ? set.HasProperties.OfType<IIfcPropertySingleValue>() : Array.Empty<IIfcPropertySingleValue>())
                    .GroupBy(property => property.Name.ToString(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First().NominalValue?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
                properties.TryGetValue("Diâmetro", out var diameter);
                properties.TryGetValue("Comprimento", out var length);
                await writer.WriteLineAsync(string.Join(",", new[]
                {
                    head.Number.ToString(CultureInfo.InvariantCulture), Csv(head.IfcGlobalId), head.MemorialThreadDiameter_mm.ToString(CultureInfo.InvariantCulture), Number(head.MemorialKFactor), Number(head.MemorialFlow_l_s), Number(head.MemorialPressure_mca),
                    Csv(pipeId), Csv(TypeName(end.Pipe) ?? ""), Csv(diameter), Csv(length), end.End.ToString(CultureInfo.InvariantCulture),
                    Number(distance), Csv("Candidato geométrico até 5 cm; confirmar no modelo antes do cálculo")
                }));
            }
        }
    }

    string? branchReviewPath = operationAreaPath is null ? null : Path.ChangeExtension(input, ".operation-area-branch-review.csv");
    var branchReviewRows = 0;
    var branchReviewDecisionCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["Pendente"] = 0, ["Confirmada"] = 0, ["Rejeitada"] = 0 };
    var branchReviewHistoryRows = 0;
    var currentBranchReviewRows = new List<Dictionary<string, string>>();
    var confirmedBranchRows = new List<Dictionary<string, string>>();
    if (branchReviewPath is not null)
    {
        var prior = ReadPriorReviews(branchReviewPath);
        var current = new HashSet<string>(StringComparer.Ordinal);
        using var source = new StreamReader(operationAreaPath!, System.Text.Encoding.UTF8);
        using var parser = new TextFieldParser(source)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        await using var review = new StreamWriter(branchReviewPath, false, System.Text.Encoding.UTF8);
        var header = parser.ReadFields() ?? throw new InvalidDataException("Cabeçalho de candidatos sprinkler-ramal ausente.");
        var indices = header.Select((name, index) => (name, index)).ToDictionary(value => value.name, value => value.index, StringComparer.OrdinalIgnoreCase);
        string Field(string[] values, string name) => indices.TryGetValue(name, out var index) && index < values.Length ? values[index] : "";
        await review.WriteLineAsync(string.Join(",", header.Concat(new[] { "Decision", "ReviewNotes", "CurrentCandidate" })));
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? [];
            var key = EdgeKey(Field(fields, "SprinklerGlobalId"), Field(fields, "SprinklerNumber"), Field(fields, "PipeGlobalId"), Field(fields, "PipeEnd"));
            if (!current.Add(key)) throw new InvalidDataException("Candidatos sprinkler-ramal contém a mesma associação mais de uma vez.");
            var old = prior.GetValueOrDefault(key);
            var decision = old.Decision ?? "Pendente";
            branchReviewDecisionCounts[decision]++;
            var row = header.Select((name, index) => (name, value: index < fields.Length ? fields[index] : ""))
                .ToDictionary(value => value.name, value => value.value, StringComparer.OrdinalIgnoreCase);
            currentBranchReviewRows.Add(row);
            if (decision == "Confirmada") confirmedBranchRows.Add(row);
            await review.WriteLineAsync(string.Join(",", fields.Concat(new[] { decision, old.Notes ?? "", "true" }).Select(Csv)));
            branchReviewRows++;
        }
        foreach (var stale in prior.Where(item => !current.Contains(item.Key)))
        {
            string OldField(string name) => stale.Value.Fields.GetValueOrDefault(name, "");
            var row = header.Select(name => name == "Status" ? "Candidato não está presente no IFC atual" : OldField(name))
                .Concat(new[] { stale.Value.Decision, stale.Value.Notes, "false" });
            await review.WriteLineAsync(string.Join(",", row.Select(Csv)));
            branchReviewHistoryRows++;
        }
    }

    var fittingAnchors = fittings.Where(item => item.ObjectPlacement is not null)
        .Select(item => (Item: item, Point: Origin(item))).ToList();

    var pipeIndex = new SpatialIndex<(IIfcPipeSegment Pipe, int End)>(pipeEnds.Select(end => ((end.Pipe, end.End), end.Point)), searchRadius);
    var fittingIndex = new SpatialIndex<IIfcPipeFitting>(fittingAnchors.Select(anchor => (anchor.Item, anchor.Point)), searchRadius);
    var sprinklerIndex = new SpatialIndex<IIfcFireSuppressionTerminal>(sprinklerAnchors.Select(anchor => (anchor.Item, anchor.Point)), searchRadius);
    var directGraph = pipes.ToDictionary(Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
    var candidateGraph = pipes.ToDictionary(Id, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
    var fittingPipeLinks = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
    var fittingPipeEndsAt5cm = new Dictionary<string, HashSet<(string PipeId, int End)>>(StringComparer.Ordinal);
    var pipeEndpointCandidates = 0;
    var directPipeTouches = 0;
    var fittingCandidates = 0;
    var sprinklerCandidates = 0;
    var auditPath = Path.ChangeExtension(input, ".connectivity-audit.csv");
    var networkCandidatesPath = Path.ChangeExtension(input, ".network-candidates.csv");
    var candidatePipeContacts = 0;
    var candidatePipeProximities = 0;
    var candidateFittingLinks = 0;
    var candidateSprinklerLinks = 0;

    await using (var writer = new StreamWriter(networkCandidatesPath, false, System.Text.Encoding.UTF8))
    {
        await writer.WriteLineAsync("FromGlobalId,FromType,FromEnd,ToGlobalId,ToType,ToEnd,Distance_ifc_unit,Evidence,Status");
        foreach (var end in pipeEnds)
        {
            foreach (var match in pipeIndex.FindWithin(end.Point, searchRadius,
                         candidate => candidate.Pipe.GlobalId != end.Pipe.GlobalId))
            {
                var fromKey = $"{Id(end.Pipe)}:{end.End}";
                var toKey = $"{Id(match.Item.Pipe)}:{match.Item.End}";
                if (string.CompareOrdinal(fromKey, toKey) >= 0) continue;
                var direct = match.Distance <= directTouchTolerance;
                if (direct)
                {
                    candidatePipeContacts++;
                    AddEdge(directGraph, Id(end.Pipe), Id(match.Item.Pipe));
                    AddEdge(candidateGraph, Id(end.Pipe), Id(match.Item.Pipe));
                }
                else
                {
                    candidatePipeProximities++;
                    AddEdge(candidateGraph, Id(end.Pipe), Id(match.Item.Pipe));
                }
                await writer.WriteLineAsync(string.Join(",", new[]
                {
                    Csv(Id(end.Pipe)), "PipeEnd", end.End.ToString(CultureInfo.InvariantCulture),
                    Csv(Id(match.Item.Pipe)), "PipeEnd", match.Item.End.ToString(CultureInfo.InvariantCulture),
                    Number(match.Distance), Csv(direct ? "Extremidades em contato geométrico" : "Extremidades próximas"),
                    direct ? "Contato direto candidato; revisar IFC" : "Proximidade candidata; revisar IFC"
                }));
            }

            foreach (var match in fittingIndex.FindWithin(end.Point, searchRadius, _ => true))
            {
                candidateFittingLinks++;
                var fittingId = Id(match.Item);
                if (match.Distance <= 0.05 * unitsPerMeter)
                {
                    if (!fittingPipeEndsAt5cm.TryGetValue(fittingId, out var nearbyEnds))
                        fittingPipeEndsAt5cm[fittingId] = nearbyEnds = [];
                    nearbyEnds.Add((Id(end.Pipe), end.End));
                }
                if (!fittingPipeLinks.TryGetValue(fittingId, out var linkedPipes))
                    fittingPipeLinks[fittingId] = linkedPipes = new HashSet<string>(StringComparer.Ordinal);
                linkedPipes.Add(Id(end.Pipe));
                await writer.WriteLineAsync(string.Join(",", new[]
                {
                    Csv(Id(end.Pipe)), "PipeEnd", end.End.ToString(CultureInfo.InvariantCulture),
                    Csv(Id(match.Item)), Csv(TypeName(match.Item) ?? "PipeFitting"), "",
                    Number(match.Distance), Csv("Extremidade próxima à origem da conexão"), "Proximidade candidata; revisar portas"
                }));
            }

            foreach (var match in sprinklerIndex.FindWithin(end.Point, searchRadius, _ => true))
            {
                candidateSprinklerLinks++;
                await writer.WriteLineAsync(string.Join(",", new[]
                {
                    Csv(Id(end.Pipe)), "PipeEnd", end.End.ToString(CultureInfo.InvariantCulture),
                    Csv(Id(match.Item)), Csv(TypeName(match.Item) ?? "FireSuppressionTerminal"), "",
                    Number(match.Distance), Csv("Extremidade próxima à origem do sprinkler"), "Proximidade candidata; revisar conexão"
                }));
            }
        }
    }

    string? topologyReviewPath = operationAreaPath is null ? null : Path.ChangeExtension(input, ".operation-area-topology-review.csv");
    string? confirmedTopologyPath = operationAreaPath is null ? null : Path.ChangeExtension(input, ".operation-area-confirmed-topology.csv");
    string? topologyAuditPath = operationAreaPath is null ? null : Path.ChangeExtension(input, ".operation-area-topology-audit.csv");
    var operationAreaReviewRows = 0;
    var reviewDecisionCounts = new Dictionary<string, int>(StringComparer.Ordinal) { ["Pendente"] = 0, ["Confirmada"] = 0, ["Rejeitada"] = 0 };
    var operationAreaReviewHistoryRows = 0;
    var topologyAuditElements = 0;
    var topologyAuditComponents = 0;
    var topologyAuditIsolatedElements = 0;
    if (topologyReviewPath is not null)
    {
        var priorReviews = ReadPriorReviews(topologyReviewPath);
        var currentKeys = new HashSet<string>(StringComparer.Ordinal);
        using var source = new StreamReader(networkCandidatesPath, System.Text.Encoding.UTF8);
        using var parser = new TextFieldParser(source)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        await using var review = new StreamWriter(topologyReviewPath, false, System.Text.Encoding.UTF8);
        var header = parser.ReadFields() ?? throw new InvalidDataException("Cabeçalho de candidatos de conectividade ausente.");
        var candidateIndices = header.Select((name, index) => (name, index)).ToDictionary(value => value.name, value => value.index, StringComparer.OrdinalIgnoreCase);
        string CandidateField(string[] values, string name) => candidateIndices.TryGetValue(name, out var index) && index < values.Length ? values[index] : "";
        var outputHeader = new[] { "AreaSprinklerNumbers" }.Concat(header).Concat(new[] { "FittingExpectedPipeEnds", "FittingObservedPipeEndsWithin5cm", "FittingGeometryCheck", "Decision", "ReviewNotes", "CurrentCandidate" }).ToArray();
        await review.WriteLineAsync(string.Join(",", outputHeader));
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields() ?? [];
            var from = CandidateField(fields, "FromGlobalId");
            var to = CandidateField(fields, "ToGlobalId");
            var key = EdgeKey(from, CandidateField(fields, "FromEnd"), to, CandidateField(fields, "ToEnd"));
            var numbers = new HashSet<int>();
            if (operationAreaPipeNumbers.TryGetValue(from, out var fromHeads)) numbers.UnionWith(fromHeads);
            if (operationAreaPipeNumbers.TryGetValue(to, out var toHeads)) numbers.UnionWith(toHeads);
            if (numbers.Count == 0) continue;
            if (!currentKeys.Add(key)) throw new InvalidDataException("Arquivo de candidatos contém a mesma ligação mais de uma vez.");
            var fittingItem = fittings.FirstOrDefault(fitting => Id(fitting) == to);
            var expected = ExpectedFittingPorts(fittingItem is null ? null : TypeName(fittingItem));
            var observed = fittingPipeEndsAt5cm.GetValueOrDefault(to)?.Count ?? 0;
            var geometryCheck = expected is null ? "Tipo não classificado" : expected == observed
                ? "Contagem compatível; ligação ainda não confirmada"
                : "Contagem divergente; revisar manualmente";
            var prior = priorReviews.GetValueOrDefault(key);
            var decision = prior.Decision ?? "Pendente";
            var notes = prior.Notes ?? "";
            reviewDecisionCounts[decision]++;
            var outputFields = new[] { string.Join(";", numbers.Order()) }
                .Concat(fields)
                .Concat(new[] { expected?.ToString(CultureInfo.InvariantCulture) ?? "", expected is null ? "" : observed.ToString(CultureInfo.InvariantCulture), expected is null ? "" : geometryCheck, decision, notes, "true" });
            await review.WriteLineAsync(string.Join(",", outputFields.Select(Csv)));
            operationAreaReviewRows++;
        }
        foreach (var stale in priorReviews.Where(item => !currentKeys.Contains(item.Key)))
        {
            var old = stale.Value.Fields;
            string OldField(string name) => old.GetValueOrDefault(name, "");
            var historical = outputHeader.Select(name => name switch
            {
                "Status" => "Candidato não está presente no IFC atual",
                "Decision" => stale.Value.Decision,
                "ReviewNotes" => stale.Value.Notes,
                "CurrentCandidate" => "false",
                _ => name == "AreaSprinklerNumbers" ? OldField(name) : OldField(name)
            });
            await review.WriteLineAsync(string.Join(",", historical.Select(Csv)));
            operationAreaReviewHistoryRows++;
        }
        var confirmedRows = priorReviews.Where(item => currentKeys.Contains(item.Key) && item.Value.Decision == "Confirmada")
            .Select(item => item.Value.Fields)
            .ToArray();
        await using var confirmedTopology = new StreamWriter(confirmedTopologyPath!, false, System.Text.Encoding.UTF8);
        var confirmedHeader = new[] { "FromGlobalId", "FromType", "FromEnd", "ToGlobalId", "ToType", "ToEnd", "Distance_ifc_unit", "Evidence", "Status", "ReviewNotes" };
        await confirmedTopology.WriteLineAsync(string.Join(",", confirmedHeader));
        foreach (var edge in confirmedRows)
            await confirmedTopology.WriteLineAsync(string.Join(",", confirmedHeader.Select(name => Csv(edge.GetValueOrDefault(name, "")))));

        var topologyGraph = new Dictionary<string, (string Type, bool IsBranchPipe, bool IsOperationAreaSprinkler, bool HasConfirmedBranchAssociation, HashSet<string> Neighbors)>(StringComparer.Ordinal);
        var topologyElementTypes = pipes.Cast<IIfcObject>().Concat(fittings).Concat(sprinklers)
            .ToDictionary(Id, item => TypeName(item) ?? item.GetType().Name, StringComparer.Ordinal);
        void AddTopologyNode(string id, string type, bool isBranchPipe = false, bool isOperationAreaSprinkler = false, bool hasConfirmedBranchAssociation = false)
        {
            if (id.Length == 0) throw new InvalidDataException("GlobalId vazio em aresta confirmada.");
            if (!topologyGraph.TryGetValue(id, out var node))
                topologyGraph[id] = (type, isBranchPipe, isOperationAreaSprinkler, hasConfirmedBranchAssociation, new HashSet<string>(StringComparer.Ordinal));
            else
                topologyGraph[id] = (node.Type, node.IsBranchPipe || isBranchPipe, node.IsOperationAreaSprinkler || isOperationAreaSprinkler,
                    node.HasConfirmedBranchAssociation || hasConfirmedBranchAssociation, node.Neighbors);
        }
        foreach (var branch in currentBranchReviewRows)
        {
            var sprinklerId = branch.GetValueOrDefault("SprinklerGlobalId", "");
            var branchPipeId = branch.GetValueOrDefault("PipeGlobalId", "");
            var branchPipe = pipes.FirstOrDefault(pipe => Id(pipe) == branchPipeId)
                ?? throw new InvalidDataException($"Tubo de ramal candidato não existe no IFC atual: {branchPipeId}");
            var sprinkler = sprinklers.FirstOrDefault(item => Id(item) == sprinklerId)
                ?? throw new InvalidDataException($"Sprinkler candidato não existe no IFC atual: {sprinklerId}");
            var confirmedBranch = confirmedBranchRows.Any(item => item.GetValueOrDefault("SprinklerGlobalId") == sprinklerId && item.GetValueOrDefault("PipeGlobalId") == branchPipeId && item.GetValueOrDefault("PipeEnd") == branch.GetValueOrDefault("PipeEnd"));
            AddTopologyNode(branchPipeId, TypeName(branchPipe) ?? "PipeSegment", true, hasConfirmedBranchAssociation: confirmedBranch);
            AddTopologyNode(sprinklerId, TypeName(sprinkler) ?? "FireSuppressionTerminal", isOperationAreaSprinkler: true, hasConfirmedBranchAssociation: confirmedBranch);
            if (confirmedBranch)
            {
                topologyGraph[sprinklerId].Neighbors.Add(branchPipeId);
                topologyGraph[branchPipeId].Neighbors.Add(sprinklerId);
            }
        }
        foreach (var edge in confirmedRows)
        {
            var from = edge.GetValueOrDefault("FromGlobalId", "");
            var to = edge.GetValueOrDefault("ToGlobalId", "");
            AddTopologyNode(from, topologyElementTypes.GetValueOrDefault(from, edge.GetValueOrDefault("FromType", "")));
            AddTopologyNode(to, topologyElementTypes.GetValueOrDefault(to, edge.GetValueOrDefault("ToType", "")));
            topologyGraph[from].Neighbors.Add(to);
            topologyGraph[to].Neighbors.Add(from);
        }
        var topologyComponentIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var topologyComponentSizes = new Dictionary<int, int>();
        foreach (var start in topologyGraph.Keys.Order(StringComparer.Ordinal))
        {
            if (topologyComponentIds.ContainsKey(start)) continue;
            var componentId = topologyComponentSizes.Count + 1;
            var pending = new Stack<string>();
            pending.Push(start);
            topologyComponentIds[start] = componentId;
            var size = 0;
            while (pending.TryPop(out var current))
            {
                size++;
                foreach (var neighbor in topologyGraph[current].Neighbors)
                    if (topologyComponentIds.TryAdd(neighbor, componentId)) pending.Push(neighbor);
            }
            topologyComponentSizes[componentId] = size;
        }
        await using var topologyAudit = new StreamWriter(topologyAuditPath!, false, System.Text.Encoding.UTF8);
        await topologyAudit.WriteLineAsync("GlobalId,ElementType,IsOperationAreaBranchPipe,IsOperationAreaSprinkler,HasConfirmedSprinklerBranchAssociation,ConfirmedDegree,ConfirmedComponent,ComponentElementCount,IsolatedFromConfirmedTopology");
        foreach (var (id, node) in topologyGraph.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var componentId = topologyComponentIds[id];
            topologyAuditElements++;
            if (node.Neighbors.Count == 0) topologyAuditIsolatedElements++;
            await topologyAudit.WriteLineAsync(string.Join(",", new[]
            {
                Csv(id), Csv(node.Type), node.IsBranchPipe ? "true" : "false", node.IsOperationAreaSprinkler ? "true" : "false",
                node.HasConfirmedBranchAssociation ? "true" : "false", node.Neighbors.Count.ToString(CultureInfo.InvariantCulture),
                $"T{componentId:D3}", topologyComponentSizes[componentId].ToString(CultureInfo.InvariantCulture), node.Neighbors.Count == 0 ? "true" : "false"
            }));
        }
        topologyAuditComponents = topologyComponentSizes.Count;
    }

    foreach (var linkedPipes in fittingPipeLinks.Values)
    {
        var ids = linkedPipes.ToArray();
        for (var i = 1; i < ids.Length; i++) AddEdge(candidateGraph, ids[0], ids[i]);
    }
    var directComponents = FindComponents(directGraph);
    var candidateComponents = FindComponents(candidateGraph);
    var fittingGeometryGroups = fittings.Select(fitting =>
    {
        var expected = ExpectedFittingPorts(TypeName(fitting));
        var observed = fittingPipeEndsAt5cm.GetValueOrDefault(Id(fitting))?.Count ?? 0;
        return (Expected: expected, Observed: observed);
    }).Where(group => group.Expected is not null).ToArray();
    var componentsPath = Path.ChangeExtension(input, ".network-components.csv");
    await using (var writer = new StreamWriter(componentsPath, false, System.Text.Encoding.UTF8))
    {
        await writer.WriteLineAsync("PipeGlobalId,PipeType,DirectComponent,DirectComponentPipeCount,CandidateComponent,CandidateComponentPipeCount,Interpretation");
        foreach (var pipe in pipes.OrderBy(Id, StringComparer.Ordinal))
        {
            var pipeId = Id(pipe);
            var directId = directComponents.Ids[pipeId];
            var candidateId = candidateComponents.Ids[pipeId];
            await writer.WriteLineAsync(string.Join(",", new[]
            {
                Csv(pipeId), Csv(TypeName(pipe) ?? "PipeSegment"),
                $"D{directId:D3}", directComponents.Sizes[directId].ToString(CultureInfo.InvariantCulture),
                $"C{candidateId:D3}", candidateComponents.Sizes[candidateId].ToString(CultureInfo.InvariantCulture),
                Csv("Grupos geométricos candidatos; não confirmam continuidade hidráulica")
            }));
        }
    }

    await using (var writer = new StreamWriter(auditPath, false, System.Text.Encoding.UTF8))
    {
        await writer.WriteLineAsync("PipeGlobalId,End,X_ifc_unit,Y_ifc_unit,Z_ifc_unit,NearestPipeGlobalId,NearestPipeEnd,NearestPipeDistance_ifc_unit,NearestFittingGlobalId,NearestFittingType,NearestFittingDistance_ifc_unit,NearestSprinklerGlobalId,NearestSprinklerDistance_ifc_unit");
        foreach (var end in pipeEnds)
        {
            var hasPipe = pipeIndex.TryNearest(end.Point, searchRadius,
                candidate => candidate.Pipe.GlobalId != end.Pipe.GlobalId, out var nearestPipe, out var pipeDistance);
            var hasFitting = fittingIndex.TryNearest(end.Point, searchRadius, _ => true, out var nearestFitting, out var fittingDistance);
            var hasSprinkler = sprinklerIndex.TryNearest(end.Point, searchRadius, _ => true, out var nearestSprinkler, out var sprinklerDistance);
            if (hasPipe)
            {
                pipeEndpointCandidates++;
                if (pipeDistance <= directTouchTolerance) directPipeTouches++;
            }
            if (hasFitting) fittingCandidates++;
            if (hasSprinkler) sprinklerCandidates++;

            await writer.WriteLineAsync(string.Join(",", new[]
            {
                Csv(end.Pipe.GlobalId.Value.ToString()), end.End.ToString(CultureInfo.InvariantCulture),
                Number(end.Point.X), Number(end.Point.Y), Number(end.Point.Z),
                hasPipe ? Csv(nearestPipe.Pipe.GlobalId.Value.ToString()) : "", hasPipe ? nearestPipe.End.ToString(CultureInfo.InvariantCulture) : "", hasPipe ? Number(pipeDistance) : "",
                hasFitting ? Csv(nearestFitting.GlobalId.Value.ToString()) : "", hasFitting ? Csv(TypeName(nearestFitting) ?? "") : "", hasFitting ? Number(fittingDistance) : "",
                hasSprinkler ? Csv(nearestSprinkler.GlobalId.Value.ToString()) : "", hasSprinkler ? Number(sprinklerDistance) : ""
            }));
        }
    }

    HydraulicResult? hydraulic = null;
    string? hydraulicError = null;
    string? hydraulicSprinklersPath = null;
    string? hydraulicPipesPath = null;
    double? densityPressure_mca = null;
    string? pumpCurvePath = null;
    string? catalogPath = null;
    string? pumpSelectionPath = null;
    string? memorialDocumentPath = null;
    PumpEvaluation[] selection = [];
    // Pavimento de cada bico (IfcBuildingStorey) e arranjo dos ramais por pavimento: direção, espaçamentos e área por bico.
    string StoreyOf(IIfcProduct item) => (item as IIfcElement)?.ContainedInStructure.FirstOrDefault()?.RelatingStructure?.Name.ToString() ?? "Sem pavimento";
    var areaHeads = sprinklerAnchors.Select(anchor => new AreaHead(Id(anchor.Item), StoreyOf(anchor.Item), anchor.Point)).ToList();
    var layouts = new Dictionary<string, StoreyLayout>(StringComparer.Ordinal);
    foreach (var group in areaHeads.GroupBy(head => head.Storey).Where(group => group.Count() >= 4))
    {
        var z = group.Average(head => head.Point.Z);
        var storeyPipes = pipeEnds.Where((end, index) => index % 2 == 0 && index + 1 < pipeEnds.Count).Select((end, index) => (A: end.Point, B: pipeEnds[2 * index + 1].Point, Pipe: end.Pipe))
            .Where(item => Math.Abs((item.A.Z + item.B.Z) / 2 - z) < 2.0 * unitsPerMeter)
            .Select(item => (item.A, item.B, Diameter_m: InnerDiameter(item.Pipe) / unitsPerMeter));
        var angle = OperationAreas.BranchAngle(storeyPipes, unitsPerMeter);
        layouts[group.Key] = OperationAreas.Layout(group.Key, group.ToList(), angle, unitsPerMeter);
    }
    string? areaCandidatesPath = null;
    if (areaAutoOption is not null && memorial is null)
        memorial = new MemorialData("Área mais desfavorável", "Hazen-Williams", 6.1, 140, 0, 80, 0, double.NaN, double.NaN, double.NaN, double.NaN, [], [], "");
    if (memorial is not null)
    {
        try
        {
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in new[] { topologyReviewPath, branchReviewPath })
                if (path is not null)
                    foreach (var (key, review) in ReadPriorReviews(path))
                        if (review.Decision == "Rejeitada") rejected.Add(key);
            static Dictionary<string, string> Properties(IIfcObject item) => item.IsDefinedBy.OfType<IIfcRelDefinesByProperties>()
                .SelectMany(relation => relation.RelatingPropertyDefinition is IIfcPropertySet set ? set.HasProperties.OfType<IIfcPropertySingleValue>() : Array.Empty<IIfcPropertySingleValue>())
                .GroupBy(property => property.Name.ToString(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().NominalValue?.ToString() ?? "", StringComparer.OrdinalIgnoreCase);
            var hydraulicPipes = new List<HydraulicPipe>();
            for (var i = 0; i < pipeEnds.Count; i += 2)
            {
                var pipe = pipeEnds[i].Pipe;
                var properties = Properties(pipe);
                var innerText = properties.GetValueOrDefault("Diâmetro interno") is { Length: > 0 } inner ? inner : properties.GetValueOrDefault("InnerDiameter", "");
                if (innerText.Length == 0) throw new InvalidDataException($"Tubo {Id(pipe)} sem diâmetro interno no IFC.");
                var type = TypeName(pipe) ?? "PipeSegment";
                // C de Hazen-Williams: aço carbono 120; cobre 150.
                var c = type.Contains("cobre", StringComparison.OrdinalIgnoreCase) ? 150 : 120;
                var (start, end) = (pipeEnds[i].Point, pipeEnds[i + 1].Point);
                hydraulicPipes.Add(new HydraulicPipe(Id(pipe), type, ParseDecimal(innerText) / unitsPerMeter, (end - start).Length / unitsPerMeter, c, start, end));
            }
            string Indication(IIfcObject item) => Properties(item).GetValueOrDefault("Indicação", "");
            var valves = model.Instances.OfType<IIfcValve>().Where(item => Network(item) is null or "Sprinklers" && item.ObjectPlacement is not null).ToList();
            var pumps = model.Instances.OfType<IIfcPump>().Where(item => Network(item) is null or "Sprinklers" && item.ObjectPlacement is not null).ToList();
            var hydraulicDevices = fittingAnchors.Select(anchor => new HydraulicDevice(Id(anchor.Item), HydraulicDeviceKind.Fitting, TypeName(anchor.Item) ?? "PipeFitting", Indication(anchor.Item), anchor.Point))
                .Concat(valves.Select(item => new HydraulicDevice(Id(item), HydraulicDeviceKind.Valve, TypeName(item) ?? "Valve", Indication(item), Origin(item))))
                .Concat(pumps.Select(item => new HydraulicDevice(Id(item), HydraulicDeviceKind.Pump, TypeName(item) ?? "Pump", Indication(item), Origin(item))))
                .ToArray();
            await LogAsync($"Dispositivos para o cálculo: {fittingAnchors.Count} conexões; {valves.Count} válvulas ({string.Join("; ", valves.GroupBy(item => TypeName(item)).Select(group => $"{group.Key} x{group.Count()}"))}); {pumps.Count} bombas ({string.Join("; ", pumps.Select(item => $"{TypeName(item)} [{Network(item)}]"))}); {memorial.EquivalentLengths.Length} itens de comprimento equivalente no memorial.");
            pumpCurvePath = Path.ChangeExtension(input, ".pump-curve.csv");
            if (!File.Exists(pumpCurvePath))
                await File.WriteAllTextAsync(pumpCurvePath, "Flow_l_s,Head_m" + Environment.NewLine, System.Text.Encoding.UTF8);
            var curve = File.ReadAllLines(pumpCurvePath).Skip(1).Select(line => line.Split(',', ';'))
                .Where(parts => parts.Length >= 2 && parts[0].Trim().Length > 0)
                .Select(parts => (Flow_l_s: ParseDecimal(parts[0]), Head_m: ParseDecimal(parts[1]))).OrderBy(point => point.Flow_l_s).ToArray();
            if (curve.Length == 1) throw new InvalidDataException("A curva da bomba precisa de pelo menos 2 pontos (vazão l/s, altura m).");
            var hydraulicHeads = operationSprinklers.Select(head => new HydraulicSprinkler(head.IfcGlobalId, head.Number, head.MemorialKFactor,
                sprinklerAnchors.First(anchor => Id(anchor.Item) == head.IfcGlobalId).Point)).ToArray();
            if (areaAutoOption is not null)
            {
                // Área mais desfavorável: retângulos da NBR 10897 a partir dos bicos mais distantes das prumadas, em cada pavimento,
                // todos calculados na mesma rede; vale a que exige maior altura manométrica.
                using var criteriaDocument = JsonDocument.Parse(await File.ReadAllTextAsync(areaAutoOption));
                var criteria = criteriaDocument.RootElement;
                double Criterion(string name, double fallback) => criteria.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : fallback;
                var density = Criterion("Density_mm_min", memorial.Density_mm_min); var designArea = Criterion("DesignArea_m2", memorial.DesignArea_m2);
                var kAuto = Criterion("K", memorial.KFactor_l_min_bar_sqrt); var minimumAuto = Criterion("MinimumPressure_mca", 0);
                var onlyStorey = criteria.TryGetProperty("Storey", out var storeyValue) ? storeyValue.GetString() : null;
                double? perHead = criteria.TryGetProperty("AreaPerHead_m2", out var perHeadValue) && perHeadValue.ValueKind == JsonValueKind.Number ? perHeadValue.GetDouble() : null;
                var risers = hydraulicPipes.Where(pipe => pipe.InnerDiameter_m >= 0.06 && Math.Abs(pipe.End.Z - pipe.Start.Z) > 1.0 * unitsPerMeter)
                    .Select(pipe => pipe.Start).ToList();
                double RiserDistance(XbimPoint3D point) => risers.Count == 0 ? 0 : risers.Min(r => Math.Sqrt(Math.Pow(r.X - point.X, 2) + Math.Pow(r.Y - point.Y, 2)));
                XbimPoint3D NearestRiser(XbimPoint3D point) => risers.MinBy(r => Math.Pow(r.X - point.X, 2) + Math.Pow(r.Y - point.Y, 2));
                var candidates = new List<AreaCandidate>();
                foreach (var (storey, layout) in layouts.Where(item => onlyStorey is null || item.Key == onlyStorey))
                {
                    if (double.IsNaN(layout.SpacingAlong_m) || double.IsNaN(layout.SpacingAcross_m)) continue;
                    var storeyHeads = areaHeads.Where(head => head.Storey == storey).ToList();
                    foreach (var seed in storeyHeads.OrderByDescending(head => RiserDistance(head.Point)).Take(16))
                    {
                        var riser = risers.Count == 0 ? seed.Point : NearestRiser(seed.Point);
                        var (cos, sin) = (Math.Cos(layout.BranchAngle_rad), Math.Sin(layout.BranchAngle_rad));
                        var du = (riser.X - seed.Point.X) * cos + (riser.Y - seed.Point.Y) * sin;
                        var dv = -(riser.X - seed.Point.X) * sin + (riser.Y - seed.Point.Y) * cos;
                        foreach (var (signAlong, signAcross) in new[] { (1, 1), (1, -1), (-1, 1), (-1, -1) })
                            if (OperationAreas.Build(storey, seed, storeyHeads, layout, designArea, signAlong, signAcross, unitsPerMeter, perHead) is { } candidate
                                && !candidates.Any(other => other.HeadIds.ToHashSet().SetEquals(candidate.HeadIds)))
                                candidates.Add(candidate);
                    }
                }
                // A área do memorial (se houver) também entra na comparação.
                if (operationSprinklers.Length > 0 && !candidates.Any(c => c.HeadIds.ToHashSet().SetEquals(operationSprinklers.Select(h => h.IfcGlobalId))))
                    candidates.Add(new AreaCandidate(areaHeads.First(h => h.Id == operationSprinklers[0].IfcGlobalId).Storey + " (memorial)", operationSprinklers[0].IfcGlobalId,
                        operationSprinklers.Select(h => h.IfcGlobalId).ToArray(), 0, 0));
                if (candidates.Count == 0) throw new InvalidDataException("Não foi possível montar áreas de operação candidatas (pavimentos sem bicos suficientes ou sem ramais identificados).");
                var pool = candidates.SelectMany(candidate => candidate.HeadIds).Distinct(StringComparer.Ordinal)
                    .Select((id, index) => new HydraulicSprinkler(id, index + 1, kAuto, sprinklerAnchors.First(anchor => Id(anchor.Item) == id).Point)).ToArray();
                // Pressão mínima de cada área: a maior entre a informada, a da densidade (Q = densidade × área ÷ bicos da área) e 48 kPa.
                double TargetFor(AreaCandidate candidate) => Math.Max(Math.Max(minimumAuto, Math.Pow(density * designArea / candidate.HeadIds.Length / kAuto, 2) / 0.0980665), 48 / 9.80665);
                var autoTarget = TargetFor(candidates[0]);
                await LogAsync($"Área automática: {candidates.Count} áreas candidatas em {candidates.Select(c => c.Storey).Distinct().Count()} pavimento(s).");
                var evaluation = Hydraulics.Solve(new HydraulicInput(hydraulicPipes, hydraulicDevices, pool, unitsPerMeter, autoTarget, rejected, sourcePipeOption, null, tankLevelOption,
                    memorial.EquivalentLengths, [], candidates[0].HeadIds.ToHashSet(StringComparer.Ordinal), candidates.Select(c => c.HeadIds).ToArray(), candidates.Select(TargetFor).ToArray()));
                var ranked = evaluation.Areas.Where(a => a.Error is null).OrderByDescending(a => a.RequiredHead_m).ToArray();
                if (ranked.Length == 0) throw new InvalidDataException("Nenhuma área candidata pôde ser calculada.");
                var worst = candidates[ranked[0].Index];
                areaCandidatesPath = Path.ChangeExtension(input, ".area-candidates.json");
                await File.WriteAllTextAsync(areaCandidatesPath, JsonSerializer.Serialize(new
                {
                    Criteria = new { Density_mm_min = density, DesignArea_m2 = designArea, K = kAuto, MinimumPressure_mca = minimumAuto, AreaPerHead_m2 = perHead },
                    Candidates = evaluation.Areas.Select(a => new { candidates[a.Index].Storey, candidates[a.Index].SeedId, candidates[a.Index].HeadIds, candidates[a.Index].Lines,
                        candidates[a.Index].HeadsPerLine, TargetPressure_mca = TargetFor(candidates[a.Index]), a.RequiredHead_m, a.Flow_l_s, a.Error, Worst = a.Index == ranked[0].Index })
                }, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
                var chosen = worst.HeadIds.Select(id => sprinklerAnchors.First(anchor => Id(anchor.Item) == id))
                    .OrderByDescending(anchor => Math.Round(anchor.Point.Y)).ThenBy(anchor => anchor.Point.X).ToArray();
                var autoHeads = chosen.Select((anchor, index) => new MemorialSprinkler(index + 1, anchor.Point.X, anchor.Point.Y, 15, kAuto, double.NaN, double.NaN)).ToArray();
                var risk = criteria.TryGetProperty("RiskClass", out var riskValue) ? riskValue.GetString() ?? "" : memorial.RiskClass;
                memorial = new MemorialData($"Área mais desfavorável – {worst.Storey} ({autoHeads.Length} bicos)", "Hazen-Williams", density, designArea, autoHeads.Length, kAuto,
                    minimumAuto, double.NaN, double.NaN, double.NaN, double.NaN, autoHeads, memorial.EquivalentLengths, risk);
                hydraulicHeads = chosen.Select((anchor, index) => new HydraulicSprinkler(Id(anchor.Item), index + 1, kAuto, anchor.Point)).ToArray();
                // A área escolhida passa a ser a área do projeto (mesmo arquivo da escolha manual no 2D).
                await File.WriteAllTextAsync(Path.ChangeExtension(input, ".operation-area.json"), JsonSerializer.Serialize(new
                {
                    Name = memorial.OperationArea, SprinklerIds = worst.HeadIds, Density_mm_min = density, DesignArea_m2 = designArea, K = kAuto, MinimumPressure_mca = minimumAuto, RiskClass = risk
                }));
                await LogAsync($"Área mais desfavorável: {worst.Storey}, {worst.HeadIds.Length} bicos, {ranked[0].RequiredHead_m:0.00} m (de {ranked.Length} áreas calculadas).");
            }
            // Pressão mínima: maior entre a do memorial e a que garante a densidade (Q = densidade × área / bicos).
            var densityFlow_l_min = memorial.Density_mm_min * memorial.DesignArea_m2 / memorial.SprinklerCount;
            densityPressure_mca = Math.Pow(densityFlow_l_min / memorial.KFactor_l_min_bar_sqrt, 2) / 0.0980665;
            // NBR 10897, 9.4.4.10: nenhum chuveiro abaixo de 48 kPa.
            var target = Math.Max(Math.Max(memorial.MinimumSprinklerPressure_mca, densityPressure_mca.Value), 48 / 9.80665);
            catalogPath = PumpSelection.Find(Path.GetDirectoryName(input)!);
            var catalog = catalogPath is null ? [] : PumpSelection.Load(catalogPath);
            if (catalogPath is null) await LogAsync("Catálogo de bombas (bombas_incendio.json) não encontrado; seleção de bombas não executada.");
            var principal = catalog.Where(pump => pump.Role == "principal").ToArray();
            HydraulicInput HydraulicCase((double Flow_l_s, double Head_m)[]? pumpCurve, IReadOnlyList<PumpCandidate> candidates) =>
                new(hydraulicPipes, hydraulicDevices, hydraulicHeads, unitsPerMeter, target, rejected, sourcePipeOption, pumpCurve, tankLevelOption,
                    memorial.EquivalentLengths, candidates);
            hydraulic = Hydraulics.Solve(HydraulicCase(curve.Length >= 2 ? curve : null,
                curve.Length >= 2 ? [] : principal.Select(pump => new PumpCandidate(pump.Id, pump.FitPoints)).ToArray()));
            var byId = catalog.ToDictionary(pump => pump.Id);
            selection = PumpSelection.Rank(hydraulic.Candidates.Select(result => PumpSelection.Evaluate(byId[result.Id], result, target)));
            if (selection.Length > 0)
            {
                pumpSelectionPath = Path.ChangeExtension(input, ".pump-selection.csv");
                await PumpSelection.WriteCsvAsync(pumpSelectionPath, selection);
            }
            if (hydraulic.Pump is { } required && hydraulic.SystemCurve.Length > 0)
            {
                // Curva do sistema e ponto mínimo da rede (vazão e altura com a pressão mínima exata no chuveiro mais desfavorável), em m³/h.
                await File.WriteAllLinesAsync(Path.ChangeExtension(input, ".system-curve.csv"),
                    new[] { "Kind,Flow_m3_h,Head_m", $"Required,{Number(Math.Round(required.Flow_l_s * 3.6, 3))},{Number(Math.Round(required.Head_m, 3))}" }
                        .Concat(hydraulic.SystemCurve.Select(point => $"Curve,{Number(Math.Round(point.Flow_l_s * 3.6, 3))},{Number(Math.Round(point.Head_m, 3))}")));
            }
            // Rede em 3D para o visualizador: coordenadas em metros, relativas ao canto mínimo da rede (Z para cima), com os resultados do cálculo.
            {
                var minX = hydraulicPipes.Min(pipe => Math.Min(pipe.Start.X, pipe.End.X));
                var minY = hydraulicPipes.Min(pipe => Math.Min(pipe.Start.Y, pipe.End.Y));
                var minZ = hydraulicPipes.Min(pipe => Math.Min(pipe.Start.Z, pipe.End.Z));
                double[] Xyz(Xbim.Common.Geometry.XbimPoint3D point) =>
                    [Math.Round((point.X - minX) / unitsPerMeter, 3), Math.Round((point.Y - minY) / unitsPerMeter, 3), Math.Round((point.Z - minZ) / unitsPerMeter, 3)];
                var pipeResults = hydraulic.Pipes.ToDictionary(pipe => pipe.Id);
                var headResults = hydraulic.Sprinklers.ToDictionary(head => head.Id);
                var network = new
                {
                    Version = version,
                    Ifc = Path.GetFileName(input),
                    Origin = new { X = minX / unitsPerMeter, Y = minY / unitsPerMeter, Z = minZ / unitsPerMeter },
                    Summary = new
                    {
                        hydraulic.TotalFlow_l_s,
                        hydraulic.TargetMinimumPressure_mca,
                        hydraulic.MinimumSprinklerPressure_mca,
                        RequiredFlow_m3_h = hydraulic.Pump is { } rp ? rp.Flow_l_s * 3.6 : (double?)null,
                        RequiredHead_m = hydraulic.Pump?.Head_m,
                        PumpId = hydraulic.Pump?.Id,
                        MaxVelocity_m_s = hydraulic.Pipes.Max(pipe => pipe.Velocity_m_s),
                        PumpsEvaluated = selection.Length,
                        PumpsMeeting = selection.Count(item => item.Meets),
                        BestPump = selection.FirstOrDefault(item => item.Meets)?.Pump.Description,
                        memorial.OperationArea,
                        memorial.Density_mm_min,
                        memorial.DesignArea_m2,
                        memorial.SprinklerCount,
                        Warnings = hydraulic.Warnings
                    },
                    Pipes = hydraulicPipes.Select(pipe =>
                    {
                        var result = pipeResults.GetValueOrDefault(pipe.Id);
                        return new
                        {
                            pipe.Id, pipe.Type, D_mm = Math.Round(pipe.InnerDiameter_m * 1000, 1), L_m = Math.Round(pipe.Length_m, 3), A = Xyz(pipe.Start), B = Xyz(pipe.End),
                            Q_l_s = result is null ? (double?)null : Math.Round(result.Flow_l_s, 4), V_m_s = result is null ? (double?)null : Math.Round(result.Velocity_m_s, 3),
                            Loss_m = result is null ? (double?)null : Math.Round(result.HeadLoss_m, 4), Leq_m = result is null ? (double?)null : Math.Round(result.EquivalentLength_m, 2)
                        };
                    }),
                    Sprinklers = sprinklerAnchors.Select(anchor =>
                    {
                        var id = Id(anchor.Item);
                        var result = headResults.GetValueOrDefault(id);
                        return new { Id = id, Type = TypeName(anchor.Item), Storey = StoreyOf(anchor.Item), P = Xyz(anchor.Point), Number = result?.Number, Pressure_mca = result?.Pressure_mca, Q_l_s = result?.Flow_l_s };
                    }),
                    Layouts = layouts.Values.Select(layout => new { layout.Storey, layout.BranchAngle_rad, layout.SpacingAlong_m, layout.SpacingAcross_m, Elevation_m = layout.Elevation_m - minZ / unitsPerMeter }),
                    Devices = hydraulicDevices.Select(device => new { device.Id, Kind = device.Kind.ToString(), device.Type, device.Indication, P = Xyz(device.Origin), Active = device.Id == hydraulic.Pump?.Id })
                };
                await File.WriteAllTextAsync(Path.ChangeExtension(input, ".network-3d.json"), JsonSerializer.Serialize(network, new JsonSerializerOptions { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));

                // IFC só com a rede de sprinklers (geometria original dos elementos) para o visualizador; refeito quando o IFC
                // ou o próprio leitor mudam (uma versão nova do programa pode extrair a rede de outro jeito).
                var networkIfc = Path.ChangeExtension(input, ".network.ifc");
                var reader = Path.Combine(AppContext.BaseDirectory, "Grelha.IfcInspector.dll");
                var stale = !File.Exists(networkIfc) || File.GetLastWriteTimeUtc(networkIfc) < File.GetLastWriteTimeUtc(input)
                    || (File.Exists(reader) && File.GetLastWriteTimeUtc(networkIfc) < File.GetLastWriteTimeUtc(reader));
                if (stale)
                {
                    using var subset = IfcStore.Create(model.SchemaVersion, Xbim.IO.XbimStoreType.InMemoryModel);
                    using (var transaction = subset.BeginTransaction("Rede de sprinklers"))
                    {
                        var map = new Xbim.Common.XbimInstanceHandleMap(model, subset);
                        subset.InsertCopy(project, map, null, false, true);
                        foreach (var element in pipes.Cast<IIfcProduct>().Concat(fittings).Concat(terminals).Concat(valves).Concat(pumps))
                            subset.InsertCopy(element, map, null, false, true);
                        transaction.Commit();
                    }
                    subset.SaveAs(networkIfc, Xbim.IO.StorageType.Ifc);
                    await LogAsync($"IFC da rede para o visualizador: {networkIfc} ({new FileInfo(networkIfc).Length / 1048576.0:0.0} MB).");
                }
            }
            if (memorialPumpOption is not null)
            {
                var chosen = byId.GetValueOrDefault(memorialPumpOption) ?? throw new InvalidDataException($"Bomba {memorialPumpOption} não existe no catálogo.");
                var operating = Hydraulics.Solve(HydraulicCase(chosen.FitPoints, []));
                var pumpOperation = operating.Pump ?? throw new InvalidDataException("A bomba do modelo IFC não foi identificada; o memorial com bomba não pode ser gerado.");
                var evaluation = PumpSelection.Evaluate(chosen, new PumpCandidateResult(chosen.Id, pumpOperation.Flow_l_s, pumpOperation.Head_m,
                    operating.MinimumSprinklerPressure_mca, pumpOperation.SuctionPressure_mca, pumpOperation.DischargePressure_mca, null), target);
                var riskText = memorial.RiskClass.Length > 0 ? memorial.RiskClass : "não informada no memorial de referência";
                // NBR 10897, Tabela 27: leve 30 min; ordinário 60 min; extraordinário 90 min.
                var duration = riskText.Contains("leve", StringComparison.OrdinalIgnoreCase) ? 30 : riskText.Contains("extra", StringComparison.OrdinalIgnoreCase) ? 90 : 60;
                memorialDocumentPath = Path.Combine(Path.GetDirectoryName(input)!, $"Memorial_Calculo_{Path.GetFileNameWithoutExtension(input)}_{DateTime.Now:yyyyMMdd_HHmm}.docx");
                MemorialWriter.Write(memorialDocumentPath, new MemorialContext(version, input, memorial, target, densityPressure_mca.Value, riskText, duration,
                    hydraulic, operating, evaluation, PumpSelection.Jockey(catalog, chosen),
                    memorial.Sprinklers.Select(head => (head.Number, head.X_cm, head.Y_cm)).ToArray(), Path.GetFileName(catalogPath!)));
                if (!evaluation.Meets) await LogAsync($"Memorial gerado com bomba que não atende: {evaluation.Reasons}");
            }

            hydraulicSprinklersPath = Path.ChangeExtension(input, ".hydraulic-sprinklers.csv");
            await using (var writer = new StreamWriter(hydraulicSprinklersPath, false, System.Text.Encoding.UTF8))
            {
                await writer.WriteLineAsync("SprinklerNumber,SprinklerGlobalId,Elevation_m,Pressure_mca,Flow_l_s,MemorialPressure_mca,MemorialFlow_l_s,PressureDifference_mca,FlowDifference_l_s");
                foreach (var head in hydraulic.Sprinklers)
                {
                    var reference = memorial.Sprinklers.First(item => item.Number == head.Number);
                    await writer.WriteLineAsync(string.Join(",", head.Number.ToString(CultureInfo.InvariantCulture), Csv(head.Id), Number(Math.Round(head.Elevation_m, 3)),
                        Number(Math.Round(head.Pressure_mca, 2)), Number(Math.Round(head.Flow_l_s, 3)), Number(reference.Pressure_mca), Number(reference.Flow_l_s),
                        Number(Math.Round(head.Pressure_mca - reference.Pressure_mca, 2)), Number(Math.Round(head.Flow_l_s - reference.Flow_l_s, 3))));
                }
            }
            await File.WriteAllLinesAsync(Path.ChangeExtension(input, ".hydraulic-segments.csv"),
                new[] { "Segment,FromNode,ToNode,Flow_l_s,Diameters_mm,Length_m,EquivalentLength_m,HeadLoss_m,Velocity_m_s,FromElevation_m,ToElevation_m,FromPressure_mca,ToPressure_mca" }
                    .Concat(hydraulic.Segments.Select(s => string.Join(",", s.Number, s.FromNode, s.ToNode, Number(Math.Round(s.Flow_l_s, 3)), Csv(s.Diameters_mm),
                        Number(Math.Round(s.Length_m, 2)), Number(Math.Round(s.EquivalentLength_m, 2)), Number(Math.Round(s.HeadLoss_m, 3)), Number(Math.Round(s.Velocity_m_s, 2)),
                        Number(Math.Round(s.FromElevation_m, 2)), Number(Math.Round(s.ToElevation_m, 2)), Number(Math.Round(s.FromPressure_mca, 2)), Number(Math.Round(s.ToPressure_mca, 2))))));
            hydraulicPipesPath = Path.ChangeExtension(input, ".hydraulic-pipes.csv");
            await using (var writer = new StreamWriter(hydraulicPipesPath, false, System.Text.Encoding.UTF8))
            {
                await writer.WriteLineAsync("PipeGlobalId,PipeType,InnerDiameter_mm,Length_m,EquivalentLength_m,HazenWilliamsC,Flow_l_s,Velocity_m_s,HeadLoss_mca,FromNode,ToNode");
                foreach (var pipe in hydraulic.Pipes.OrderByDescending(pipe => pipe.Flow_l_s))
                    await writer.WriteLineAsync(string.Join(",", Csv(pipe.Id), Csv(pipe.Type), Number(Math.Round(pipe.InnerDiameter_mm, 1)), Number(Math.Round(pipe.Length_m, 3)),
                        Number(Math.Round(pipe.EquivalentLength_m, 2)), Number(pipe.C), Number(Math.Round(pipe.Flow_l_s, 3)), Number(Math.Round(pipe.Velocity_m_s, 2)),
                        Number(Math.Round(pipe.HeadLoss_m, 3)), Csv(pipe.FromNode), Csv(pipe.ToNode)));
            }
        }
        catch (InvalidDataException exception)
        {
            hydraulicError = exception.Message;
        }
    }

    var output = new
    {
        Version = version,
        File = input,
        System = "Sprinklers (Identificação_Elemento.Rede)",
        OperationAreaBranchReview = branchReviewPath is null ? null : new
        {
            File = branchReviewPath,
            CandidateAssociations = branchReviewRows,
            DecisionCounts = branchReviewDecisionCounts,
            HistoricalAssociations = branchReviewHistoryRows,
            Status = "Associações geométricas pendentes de revisão; decisões não alimentam o solver hidráulico"
        },
        OperationAreaTopologyReview = topologyReviewPath is null ? null : new
        {
            File = topologyReviewPath,
            CandidateEdges = operationAreaReviewRows,
            DecisionCounts = reviewDecisionCounts,
            HistoricalEdges = operationAreaReviewHistoryRows,
            SprinklerBranchPipes = operationAreaPipeNumbers.Count,
            Status = "Decisões de revisão preservadas por ligação; apenas candidatos atuais; não usar como conexões confirmadas"
        },
        ConfirmedOperationAreaTopology = confirmedTopologyPath is null ? null : new
        {
            File = confirmedTopologyPath,
            Edges = reviewDecisionCounts["Confirmada"],
            Status = "Exportação de decisões humanas; não validada hidraulicamente e não conectada ao grafo de cálculo"
        },
        OperationAreaTopologyAudit = topologyAuditPath is null ? null : new
        {
            File = topologyAuditPath,
            Elements = topologyAuditElements,
            Components = topologyAuditComponents,
            ConfirmedEdges = reviewDecisionCounts["Confirmada"],
            ConfirmedBranchAssociations = branchReviewDecisionCounts["Confirmada"],
            IsolatedElements = topologyAuditIsolatedElements,
            Status = "Nós candidatos identificados; arestas incluem apenas associações sprinkler-ramal e conexões confirmadas; não representa cálculo hidráulico"
        },
        FittingGeometryAudit = new
        {
            SearchDistance_ifc_units = 0.05 * unitsPerMeter,
            FittingsWithKnownPortCount = fittingGeometryGroups.Length,
            GeometricallyConsistentFittings = fittingGeometryGroups.Count(group => group.Expected == group.Observed),
            AmbiguousFittingsWithNearbyPipeEnds = fittingGeometryGroups.Count(group => group.Observed > 0 && group.Expected != group.Observed),
            KnownFittingsWithoutNearbyPipeEnds = fittingGeometryGroups.Count(group => group.Observed == 0),
            Rule = "Tê=3 extremidades; cotovelo, curva, redução e visor=2; proximidade geométrica não confirma ligação"
        },
        HydraulicCalculation = hydraulic is null
            ? (object)new
            {
                Status = "NotRun",
                Reason = hydraulicError ?? "Sem memorial MEM/SPK ao lado do IFC: a área de operação não está definida.",
            }
            : new
            {
                Status = "Calculated",
                hydraulic.Mode,
                Method = "Hazen-Williams; gradiente global (Todini); bicos como emissores Q = K√P",
                Topology = "Automática pela geometria (contato ≤ 1 mm; conexões e bicos ≤ 5 cm); ligações 'Rejeitada' excluídas",
                LocalLosses = hydraulic.EquivalentLengthSource,
                hydraulic.FittingsWithMemorialEquivalent,
                hydraulic.FittingsWithTableEquivalent,
                hydraulic.Pump,
                PumpCurveFile = pumpCurvePath,
                PumpCatalog = catalogPath,
                PumpSelectionFile = pumpSelectionPath,
                PumpsEvaluated = selection.Length,
                PumpsMeeting = selection.Count(item => item.Meets),
                BestPumps = selection.Where(item => item.Meets).Take(10).Select(item => new { item.Pump.Id, item.Pump.Description, Flow_m3_h = item.Result.Flow_l_s * 3.6, item.Result.Head_m, item.Reasons }),
                MemorialDocument = memorialDocumentPath,
                MemorialPump = new { Flow_l_s = memorial!.DesignFlow_l_s, memorial.PumpHead_mca, memorial.PumpPower_cv },
                hydraulic.MinimumSprinklerPressure_mca,
                HazenWilliamsC = "Aço carbono 120; cobre 150",
                hydraulic.TargetMinimumPressure_mca,
                MemorialMinimumPressure_mca = memorial.MinimumSprinklerPressure_mca,
                DensityMinimumPressure_mca = densityPressure_mca,
                hydraulic.SourcePipeId,
                hydraulic.SourceNode,
                hydraulic.SourceElevation_m,
                RequiredSourcePressure_mca = hydraulic.SourcePressure_mca,
                hydraulic.TotalFlow_l_s,
                TotalFlow_l_min = hydraulic.TotalFlow_l_s * 60,
                MemorialTotalFlow_l_s = memorial.SprinklerFlowSum_l_s,
                DensityMinimumFlow_l_s = memorial.DensityMinimumFlow_l_s,
                MaxVelocity_m_s = hydraulic.Pipes.Max(pipe => pipe.Velocity_m_s),
                hydraulic.NetworkNodes,
                hydraulic.SolverNodes,
                hydraulic.SolverLinks,
                hydraulic.Iterations,
                hydraulic.Warnings,
                SprinklersFile = hydraulicSprinklersPath,
                PipesFile = hydraulicPipesPath,
                hydraulic.Sprinklers
            },
        Schema = model.SchemaVersion.ToString(),
        MemorialReference = memorial is null ? null : new
        {
            Source = memorialSource?.Path,
            memorial.OperationArea,
            memorial.Method,
            memorial.Density_mm_min,
            memorial.DesignArea_m2,
            memorial.SprinklerCount,
            memorial.KFactor_l_min_bar_sqrt,
            memorial.MinimumSprinklerPressure_mca,
            memorial.PumpFlow_l_min,
            memorial.DesignFlow_l_s,
            memorial.SprinklerFlowSum_l_s,
            memorial.FlowRoundingDifference_l_s,
            memorial.DensityMinimumFlow_l_s,
            FlowAboveDensityMinimum_l_s = memorial.SprinklerFlowSum_l_s - memorial.DensityMinimumFlow_l_s,
            memorial.PumpPower_cv,
            memorial.PumpHead_mca,
            memorial.FireReserveTank_m3,
            CoordinateTransform = new { RotationCos = bestAlignment.Rotation.A, RotationSin = bestAlignment.Rotation.B, OffsetX = bestAlignment.Translation.X, OffsetY = bestAlignment.Translation.Y },
            Sprinklers = operationSprinklers
        },
        Units = model.Instances.OfType<IIfcSIUnit>()
            .Where(unit => unit.UnitType == IfcUnitEnum.LENGTHUNIT)
            .Select(unit => $"{unit.Prefix} {unit.Name}".Trim())
            .Distinct()
            .ToArray(),
        Counts = new
        {
            PipeSegments = pipes.Count,
            PipeFittings = fittings.Count,
            FireSuppressionTerminals = terminals.Count,
            Sprinklers = sprinklers.Count,
        PipesWithExtractedEndpoints = pipeEnds.Count / 2,
        PipeEndpointsWithNearbyPipe = pipeEndpointCandidates,
        PipeEndpointsWithDirectPipeTouch = directPipeTouches,
        PipeEndpointsNearFittingOrigin = fittingCandidates,
        PipeEndpointsNearSprinklerOrigin = sprinklerCandidates,
            ConnectedElementsRelations = model.Instances.OfType<IIfcRelConnectsElements>().Count()
        },
        PipeSegments = pipes.Select(Describe).ToArray(),
        PipeFittings = fittings.Select(Describe).ToArray(),
        FireSuppressionTerminals = terminals.Select(Describe).ToArray()
    };

    var report = Path.ChangeExtension(input, ".inventory.json");
    await File.WriteAllTextAsync(report, JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    Console.WriteLine($"Inventário salvo: {report}");
    Console.WriteLine($"Rede Sprinklers: {pipes.Count} tubos; {fittings.Count} conexões; {sprinklers.Count} sprinklers");
    Console.WriteLine($"Tubos com extremidades extraídas: {pipeEnds.Count / 2} de {pipes.Count}");
    Console.WriteLine($"Extremidades com tubo próximo: {pipeEndpointCandidates}; contato direto (<= 1 mm): {directPipeTouches}; conexão próxima: {fittingCandidates}; sprinkler próximo: {sprinklerCandidates}");
    Console.WriteLine($"Relações IFC de conexão entre elementos: {output.Counts.ConnectedElementsRelations}");
    if (hydraulic is null)
        Console.WriteLine($"Cálculo hidráulico: NÃO EXECUTADO. {hydraulicError ?? "Sem memorial MEM/SPK para definir a área de operação."}");
    else
    {
        Console.WriteLine($"Cálculo hidráulico (Hazen-Williams, topologia automática): vazão total {hydraulic.TotalFlow_l_s:0.00} l/s ({hydraulic.TotalFlow_l_s * 60:0} l/min); pressão mínima nos bicos {hydraulic.MinimumSprinklerPressure_mca:0.00} m.c.a. (exigida {hydraulic.TargetMinimumPressure_mca:0.00}); pressão na alimentação {hydraulic.SourcePressure_mca:0.00} m.c.a. na cota {hydraulic.SourceElevation_m:0.00} m (tubo {hydraulic.SourcePipeId}).");
        if (hydraulic.Pump is { } pumpResult)
            Console.WriteLine($"Bomba ({pumpResult.Mode}): {pumpResult.Flow_l_s:0.00} l/s; altura manométrica {pumpResult.Head_m:0.00} m; recalque {pumpResult.DischargePressure_mca:0.00} m.c.a. na cota {pumpResult.DischargeElevation_m:0.00} m; sucção {pumpResult.SuctionPressure_mca:0.00} m.c.a. Memorial: {memorial!.DesignFlow_l_s:0.00} l/s; {memorial.PumpHead_mca:0.00} m.");
        if (selection.Length > 0)
        {
            Console.WriteLine($"Seleção de bombas (NBR 16704): {selection.Count(item => item.Meets)} de {selection.Length} bombas do catálogo atendem. Lista: {pumpSelectionPath}");
            foreach (var item in selection.Where(item => item.Meets).Take(5))
                Console.WriteLine($"  {item.Pump.Id} {item.Pump.Description}: {item.Result.Flow_l_s * 3.6:0.0} m³/h a {item.Result.Head_m:0.0} m; menor pressão nos bicos {item.Result.MinimumSprinklerPressure_mca:0.00} m.c.a.");
        }
        if (memorialDocumentPath is not null) Console.WriteLine($"Memorial de cálculo gerado: {memorialDocumentPath}");
        Console.WriteLine($"Memorial: vazão {memorial!.SprinklerFlowSum_l_s:0.00} l/s. Resultados por bico: {hydraulicSprinklersPath}; por tubo: {hydraulicPipesPath}");
        foreach (var warning in hydraulic.Warnings) Console.WriteLine($"Aviso: {warning}");
    }
    Console.WriteLine($"Ligações candidatas: {candidatePipeContacts} contatos tubo-tubo; {candidatePipeProximities} proximidades tubo-tubo; {candidateFittingLinks} tubo-conexão; {candidateSprinklerLinks} tubo-sprinkler.");
    Console.WriteLine($"Componentes por contato direto: {directComponents.Sizes.Count} (isolados: {directGraph.Count(pair => pair.Value.Count == 0)}); com candidatos ampliados: {candidateComponents.Sizes.Count} (isolados: {candidateGraph.Count(pair => pair.Value.Count == 0)}).");
    Console.WriteLine($"Auditoria geométrica salva: {auditPath} (unidade IFC: {lengthUnit.Prefix} {lengthUnit.Name}; busca até 20 cm)");
    Console.WriteLine($"Ligações candidatas salvas: {networkCandidatesPath} (distâncias em unidade IFC; relações geométricas não confirmam conexão hidráulica).");
    Console.WriteLine($"Componentes candidatos salvos: {componentsPath} (IDs D = contato direto; IDs C = proximidade incluindo conexões).");
    if (operationAreaPath is not null) Console.WriteLine($"Candidatos próximos à área do memorial salvos: {operationAreaPath} (até 5 cm; confirmar conexões).");
    if (branchReviewPath is not null) Console.WriteLine($"Revisão de associações sprinkler-ramal salva: {branchReviewPath} ({branchReviewRows} atuais: pendentes {branchReviewDecisionCounts["Pendente"]}, confirmadas {branchReviewDecisionCounts["Confirmada"]}, rejeitadas {branchReviewDecisionCounts["Rejeitada"]}).");
    if (topologyReviewPath is not null) Console.WriteLine($"Revisão de topologia da área salva: {topologyReviewPath} ({operationAreaReviewRows} atuais: pendentes {reviewDecisionCounts["Pendente"]}, confirmadas {reviewDecisionCounts["Confirmada"]}, rejeitadas {reviewDecisionCounts["Rejeitada"]}).");
    if (confirmedTopologyPath is not null) Console.WriteLine($"Arestas revisadas como confirmadas exportadas (sem uso no cálculo): {confirmedTopologyPath} ({reviewDecisionCounts["Confirmada"]}).");
    if (topologyAuditPath is not null) Console.WriteLine($"Auditoria da topologia revisada salva: {topologyAuditPath} ({topologyAuditElements} elementos, {topologyAuditComponents} componentes, {branchReviewDecisionCounts["Confirmada"]} associações sprinkler-ramal confirmadas, {reviewDecisionCounts["Confirmada"]} conexões confirmadas, {topologyAuditIsolatedElements} isolados).");
    await LogAsync($"Processamento concluído. Tubos: {pipes.Count}; conexões: {fittings.Count}; sprinklers: {sprinklers.Count}; extremidades extraídas: {pipeEnds.Count}; extremidades com tubo próximo: {pipeEndpointCandidates}; contato direto: {directPipeTouches}; extremidades próximas a conexões: {fittingCandidates}; próximas a sprinklers: {sprinklerCandidates}; relações IFC de conexão: {output.Counts.ConnectedElementsRelations}; candidatos tubo-tubo em contato: {candidatePipeContacts}; candidatos tubo-tubo próximos: {candidatePipeProximities}; candidatos tubo-conexão: {candidateFittingLinks}; candidatos tubo-sprinkler: {candidateSprinklerLinks}; componentes por contato direto: {directComponents.Sizes.Count}; componentes candidatos ampliados: {candidateComponents.Sizes.Count}. Inventário: {report}. Auditoria: {auditPath}. Candidatos: {networkCandidatesPath}. Componentes: {componentsPath}");
    await LogAsync(hydraulic is null ? $"Cálculo hidráulico não executado: {hydraulicError}" : $"Cálculo hidráulico: vazão {hydraulic.TotalFlow_l_s:0.000} l/s; pressão na alimentação {hydraulic.SourcePressure_mca:0.00} m.c.a.; tubo {hydraulic.SourcePipeId}; nós {hydraulic.SolverNodes}; iterações {hydraulic.Iterations}.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Erro ao processar o IFC. Consulte o log: {logPath}");
    await LogAsync($"Erro: {exception}");
    return 1;
}

sealed record MemorialSprinkler(int Number, double X_cm, double Y_cm, int ThreadDiameter_mm, double K, double Flow_l_s, double Pressure_mca);
sealed record MemorialData(string OperationArea, string Method, double Density_mm_min, double DesignArea_m2, int SprinklerCount,
    double KFactor_l_min_bar_sqrt, double MinimumSprinklerPressure_mca, double PumpFlow_l_min, double PumpPower_cv,
    double PumpHead_mca, double FireReserveTank_m3, MemorialSprinkler[] Sprinklers, EquivalentLengthEntry[] EquivalentLengths, string RiskClass)
{
    public double DesignFlow_l_s => PumpFlow_l_min / 60;
    public double SprinklerFlowSum_l_s => Sprinklers.Sum(sprinkler => sprinkler.Flow_l_s);
    public double FlowRoundingDifference_l_s => SprinklerFlowSum_l_s - DesignFlow_l_s;
    public double DensityMinimumFlow_l_s => Density_mm_min * DesignArea_m2 / 60;
}
