using Xbim.Common.Geometry;

// Área de operação conforme ABNT NBR 10897:2020 para sistemas calculados hidraulicamente: retângulo com o lado paralelo aos ramais
// igual ou maior que 1,2 √A e número de chuveiros = A ÷ área por chuveiro (arredondado para cima).

internal sealed record AreaHead(string Id, string Storey, XbimPoint3D Point);
internal sealed record StoreyLayout(string Storey, double BranchAngle_rad, double SpacingAlong_m, double SpacingAcross_m, double Elevation_m)
{
    public double AreaPerHead_m2 => SpacingAlong_m * SpacingAcross_m;
}
internal sealed record AreaCandidate(string Storey, string SeedId, string[] HeadIds, int Lines, int HeadsPerLine);
internal sealed record AreaShape(int Required, int HeadsPerLine, double LengthAlong_m, int Selected, double SelectedLengthAlong_m, double SelectedWidthAcross_m, bool CountOk, bool LengthOk);

internal static class OperationAreas
{
    // Direção dos ramais: ângulo dominante (média ponderada pelo comprimento, em dobro do ângulo) dos tubos horizontais de até 1 1/2".
    public static double BranchAngle(IEnumerable<(XbimPoint3D A, XbimPoint3D B, double Diameter_m)> pipes, double unitsPerMeter)
    {
        double sx = 0, sy = 0;
        foreach (var (a, b, d) in pipes)
        {
            if (d > 0.043) continue;
            var dx = (b.X - a.X) / unitsPerMeter; var dy = (b.Y - a.Y) / unitsPerMeter; var dz = (b.Z - a.Z) / unitsPerMeter;
            var length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 0.5 || Math.Abs(dz) > 0.1 * length) continue;
            var angle = 2 * Math.Atan2(dy, dx);
            sx += length * Math.Cos(angle); sy += length * Math.Sin(angle);
        }
        return Math.Atan2(sy, sx) / 2;
    }

    static (double U, double V) Local(XbimPoint3D p, double angle, double unitsPerMeter) =>
        ((p.X * Math.Cos(angle) + p.Y * Math.Sin(angle)) / unitsPerMeter, (-p.X * Math.Sin(angle) + p.Y * Math.Cos(angle)) / unitsPerMeter);

    static double Median(List<double> values) { if (values.Count == 0) return double.NaN; values.Sort(); return values[values.Count / 2]; }

    public static StoreyLayout Layout(string storey, IReadOnlyList<AreaHead> heads, double angle, double unitsPerMeter)
    {
        var local = heads.Select(h => Local(h.Point, angle, unitsPerMeter)).ToArray();
        var along = new List<double>(); var across = new List<double>();
        for (var i = 0; i < local.Length; i++)
        {
            double bestAlong = double.MaxValue, bestAcross = double.MaxValue;
            for (var j = 0; j < local.Length; j++)
            {
                if (i == j) continue;
                var du = Math.Abs(local[j].U - local[i].U); var dv = Math.Abs(local[j].V - local[i].V);
                if (dv < 0.5 && du > 0.3 && du < bestAlong) bestAlong = du;
                if (du < 0.5 && dv > 0.3 && dv < bestAcross) bestAcross = dv;
            }
            if (bestAlong < 10) along.Add(bestAlong);
            if (bestAcross < 10) across.Add(bestAcross);
        }
        return new StoreyLayout(storey, angle, Median(along), Median(across), heads.Average(h => h.Point.Z) / unitsPerMeter);
    }

    public static (int Required, int HeadsPerLine, double LengthAlong) Requirement(double designArea_m2, double areaPerHead_m2, double spacingAlong_m)
    {
        var required = (int)Math.Ceiling(designArea_m2 / areaPerHead_m2 - 1e-9);
        var length = 1.2 * Math.Sqrt(designArea_m2);
        var perLine = Math.Max(1, (int)Math.Ceiling(length / spacingAlong_m - 1e-9));
        return (required, Math.Min(perLine, required), length);
    }

    // Retângulo a partir de um bico de canto, crescendo para o lado da alimentação: n bicos por ramal, ramais adjacentes até completar o total.
    public static AreaCandidate? Build(string storey, AreaHead seed, IReadOnlyList<AreaHead> heads, StoreyLayout layout, double designArea_m2,
        int signAlong, int signAcross, double unitsPerMeter, double? areaPerHead_m2 = null)
    {
        var (required, perLine, _) = Requirement(designArea_m2, areaPerHead_m2 ?? layout.AreaPerHead_m2, layout.SpacingAlong_m);
        var origin = Local(seed.Point, layout.BranchAngle_rad, unitsPerMeter);
        var tolerance = 0.4 * layout.SpacingAcross_m;
        var lines = heads.Select(h => (Head: h, P: Local(h.Point, layout.BranchAngle_rad, unitsPerMeter)))
            .Where(item => (item.P.V - origin.V) * signAcross > -tolerance && (item.P.U - origin.U) * signAlong > -0.3)
            .GroupBy(item => Math.Round((item.P.V - origin.V) * signAcross / layout.SpacingAcross_m))
            .OrderBy(group => group.Key).ToList();
        var chosen = new List<string>(); var used = 0;
        foreach (var line in lines)
        {
            var take = Math.Min(perLine, required - chosen.Count);
            var picked = line.OrderBy(item => (item.P.U - origin.U) * signAlong).Take(take).Select(item => item.Head.Id).ToList();
            if (picked.Count == 0) continue;
            chosen.AddRange(picked); used++;
            if (chosen.Count >= required) break;
        }
        if (chosen.Count < required) return null;
        // Área contígua: o retângulo que envolve os bicos (com meio espaçamento em cada lado) não pode passar de 1,3 × a área de aplicação.
        var members = heads.Where(h => chosen.Contains(h.Id)).Select(h => Local(h.Point, layout.BranchAngle_rad, unitsPerMeter)).ToArray();
        var envelope = (members.Max(q => q.U) - members.Min(q => q.U) + layout.SpacingAlong_m) * (members.Max(q => q.V) - members.Min(q => q.V) + layout.SpacingAcross_m);
        return envelope > 1.3 * Math.Max(designArea_m2, required * (areaPerHead_m2 ?? layout.AreaPerHead_m2)) ? null
            : new AreaCandidate(storey, seed.Id, chosen.ToArray(), used, perLine);
    }

    // Conferência de uma área escolhida: quantidade de bicos e comprimento paralelo aos ramais.
    public static AreaShape Check(IReadOnlyList<AreaHead> selected, StoreyLayout layout, double designArea_m2, double unitsPerMeter)
    {
        var (required, perLine, length) = Requirement(designArea_m2, layout.AreaPerHead_m2, layout.SpacingAlong_m);
        var local = selected.Select(h => Local(h.Point, layout.BranchAngle_rad, unitsPerMeter)).ToArray();
        var alongExtent = local.Length == 0 ? 0 : local.Max(p => p.U) - local.Min(p => p.U) + layout.SpacingAlong_m;
        var acrossExtent = local.Length == 0 ? 0 : local.Max(p => p.V) - local.Min(p => p.V) + layout.SpacingAcross_m;
        return new AreaShape(required, perLine, length, selected.Count, alongExtent, acrossExtent, selected.Count >= required, alongExtent >= length - 0.05 * layout.SpacingAlong_m);
    }
}
