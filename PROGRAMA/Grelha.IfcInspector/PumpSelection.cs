using System.Globalization;
using System.Text.Json;

// Catálogo de bombas de incêndio e verificação de desempenho conforme ABNT NBR 16704:2019.

internal sealed record CatalogCurvePoint(double Head_m, double Flow_m3_h);
internal sealed record CatalogPump(string Id, string Manufacturer, string Model, string Family, string Role, int CatalogPage, double Power_cv,
    double? Stages, bool SinglePhase, bool ThreePhase, double? Suction_in, double? Discharge_in, double? ShutoffHead_m, double? MaxSuctionLift_m,
    double? Impeller_mm, CatalogCurvePoint[] Curve)
{
    public double MinFlow_l_s => Curve.Min(point => point.Flow_m3_h) / 3.6;
    public double MaxFlow_l_s => Curve.Max(point => point.Flow_m3_h) / 3.6;
    public double Shutoff_m => ShutoffHead_m ?? Curve.Max(point => point.Head_m);

    // Pontos para o ajuste: tabela do catálogo mais a pressão sem vazão.
    public (double Flow_l_s, double Head_m)[] FitPoints => Curve.Select(point => (point.Flow_m3_h / 3.6, point.Head_m))
        .Concat(ShutoffHead_m is double shutoff ? new[] { (0.0, shutoff) } : [])
        .OrderBy(point => point.Item1).ToArray();

    public double HeadAt(double flow_l_s)
    {
        var (a, b, c) = Hydraulics.FitCurve(FitPoints);
        var q = flow_l_s / 1000;
        return a + b * q + c * q * q;
    }

    public string Description => $"{Manufacturer} {Model} – {Power_cv.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR"))} cv" +
        (Impeller_mm is double rotor ? $", rotor {rotor:0} mm" : "");
}

internal sealed record PumpCheck(string Rule, string Requirement, string Value, bool Pass);

internal sealed record PumpEvaluation(CatalogPump Pump, PumpCandidateResult Result, bool Meets, bool Extrapolated150, double Head150_m, PumpCheck[] Checks)
{
    public string Reasons => Meets ? (Extrapolated150 ? "Atende (150% extrapolado da curva)" : "Atende")
        : string.Join("; ", Checks.Where(check => !check.Pass).Select(check => check.Rule));
}

internal static class PumpSelection
{
    // Pressão nominal mínima de 2,7 bar (NBR 16704, 4.8.2) e sucção até −0,2 bar com reservatório afogado (4.14.3.1).
    const double MinimumRatedHead_m = 2.7 / 0.0980665;
    const double MinimumSuction_m = -0.2 / 0.0980665;

    public static CatalogPump[] Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return document.RootElement.GetProperty("Pumps").Deserialize<CatalogPump[]>(options)
            ?.Where(pump => pump.Curve.Length >= 2).ToArray()
            ?? throw new InvalidDataException($"Catálogo de bombas vazio: {path}");
    }

    public static string? Find(string ifcDirectory)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, ifcDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                foreach (var candidate in new[] { Path.Combine(directory.FullName, "Catalogo", "bombas_incendio.json"), Path.Combine(directory.FullName, "CATALOGO", "bombas_incendio.json") })
                    if (File.Exists(candidate)) return candidate;
        return null;
    }

    public static PumpEvaluation Evaluate(CatalogPump pump, PumpCandidateResult result, double targetPressure_mca)
    {
        if (result.Error is not null || double.IsNaN(result.Flow_l_s))
            return new PumpEvaluation(pump, result, false, false, double.NaN, [new PumpCheck("Cálculo não convergiu", "—", result.Error ?? "—", false)]);
        var q = result.Flow_l_s;
        var h = result.Head_m;
        var head150 = pump.HeadAt(1.5 * q);
        var extrapolated = 1.5 * q > pump.MaxFlow_l_s + 1e-6;
        string F(double value, string unit) => $"{value.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"))} {unit}";
        PumpCheck[] checks =
        [
            new("Pressão mínima nos bicos", $"≥ {F(targetPressure_mca, "m.c.a.")}", F(result.MinimumSprinklerPressure_mca, "m.c.a."), result.MinimumSprinklerPressure_mca >= targetPressure_mca - 0.01),
            new("Ponto na faixa do catálogo", $"{F(pump.MinFlow_l_s * 3.6, "m³/h")} a {F(pump.MaxFlow_l_s * 3.6, "m³/h")}", F(q * 3.6, "m³/h"), q >= pump.MinFlow_l_s - 1e-6 && q <= pump.MaxFlow_l_s + 1e-6),
            new("Pressão nominal ≥ 2,7 bar (4.8.2)", $"≥ {F(MinimumRatedHead_m, "m")}", F(h, "m"), h >= MinimumRatedHead_m),
            new("150% da vazão com ≥ 65% da pressão (6.2.1)", $"≥ {F(0.65 * h, "m")} a {F(1.5 * q * 3.6, "m³/h")}", F(head150, "m") + (extrapolated ? " (extrapolado)" : ""), head150 >= 0.65 * h),
            new("Pressão sem vazão ≤ 140% (6.2.2)", $"≤ {F(1.4 * h, "m")}", F(pump.Shutoff_m, "m"), pump.Shutoff_m <= 1.4 * h),
            new("Sucção sem pressão negativa excessiva (4.14.3.1)", $"≥ {F(MinimumSuction_m, "m.c.a.")}", F(result.SuctionPressure_mca, "m.c.a."), result.SuctionPressure_mca >= MinimumSuction_m),
        ];
        return new PumpEvaluation(pump, result, checks.All(check => check.Pass), extrapolated, head150, checks);
    }

    // Ordem: atende, linha BPI (específica para incêndio), menor potência, menor folga de pressão.
    public static PumpEvaluation[] Rank(IEnumerable<PumpEvaluation> evaluations) => evaluations
        .OrderByDescending(item => item.Meets)
        .ThenByDescending(item => item.Pump.Model.StartsWith("BPI", StringComparison.OrdinalIgnoreCase))
        .ThenBy(item => item.Pump.Power_cv)
        .ThenBy(item => double.IsNaN(item.Result.MinimumSprinklerPressure_mca) ? double.MaxValue : item.Result.MinimumSprinklerPressure_mca)
        .ToArray();

    // Jóquei (4.22.3): pressão ≥ pressão sem vazão da principal; vazão de reposição adotada de 1 m³/h.
    public static CatalogPump? Jockey(IEnumerable<CatalogPump> catalog, CatalogPump main, double flow_m3_h = 1.0) => catalog
        .Where(pump => pump.Role == "jockey" && pump.Shutoff_m >= main.Shutoff_m && pump.MaxFlow_l_s * 3.6 >= flow_m3_h
            && pump.HeadAt(flow_m3_h / 3.6) >= main.Shutoff_m)
        .OrderByDescending(pump => pump.Model.StartsWith("BPI", StringComparison.OrdinalIgnoreCase))
        .ThenBy(pump => pump.Power_cv).FirstOrDefault();

    public static async Task WriteCsvAsync(string path, IEnumerable<PumpEvaluation> ranked)
    {
        var culture = CultureInfo.GetCultureInfo("pt-BR");
        string N(double value, string format = "0.00") => double.IsNaN(value) ? "" : value.ToString(format, culture);
        string Q(string? text) => $"\"{(text ?? "").Replace("\"", "\"\"")}\"";
        await using var writer = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        await writer.WriteLineAsync("Id;Atende;Modelo;Familia;Pagina;Potencia_cv;Rotor_mm;Succao_pol;Recalque_pol;Vazao_operacao_m3h;Altura_operacao_m;Pressao_min_bicos_mca;Succao_mca;Recalque_mca;Pressao_sem_vazao_m;Limite_140_m;Altura_150_m;Limite_65_m;Observacoes");
        foreach (var item in ranked)
        {
            var r = item.Result;
            await writer.WriteLineAsync(string.Join(";", Q(item.Pump.Id), item.Meets ? "Sim" : "Não", Q(item.Pump.Model), Q(item.Pump.Family), item.Pump.CatalogPage,
                N(item.Pump.Power_cv, "0.##"), item.Pump.Impeller_mm is double rotor ? N(rotor, "0") : "", item.Pump.Suction_in is double s ? N(s, "0.##") : "",
                item.Pump.Discharge_in is double d ? N(d, "0.##") : "", N(r.Flow_l_s * 3.6), N(r.Head_m), N(r.MinimumSprinklerPressure_mca), N(r.SuctionPressure_mca),
                N(r.DischargePressure_mca), N(item.Pump.Shutoff_m), N(1.4 * r.Head_m), N(item.Head150_m), N(0.65 * r.Head_m), Q(item.Reasons)));
        }
    }
}
