using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace Grelha.Shared;

// Gráfico "curva de vazão & altura" (curva da bomba sobre a curva do sistema), usado na tela de seleção e no memorial de cálculo.
public static class PumpChart
{
    private static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");

    // Vazões em m³/h e alturas em m.c.a.
    public sealed record Data((double Flow, double Head)[] CatalogPoints, double? Shutoff, (double Flow, double Head)[] System,
        (double Flow, double Head)? Required, (double Flow, double Head)? Operating, (double Flow, double Head)? Point150);

    // Ajuste H = a + bQ + cQ² aos pontos do catálogo e à pressão sem vazão (o mesmo do cálculo da rede).
    private static (double A, double B, double C) Fit(Data data)
    {
        var points = data.CatalogPoints.Select(p => (Q: p.Flow, H: p.Head)).ToList();
        if (data.Shutoff is double shutoff) points.Add((0, shutoff));
        var m = new double[3, 4];
        foreach (var (q, h) in points)
        {
            double[] row = [1, q, q * q];
            for (var i = 0; i < 3; i++) { m[i, 3] += row[i] * h; for (var j = 0; j < 3; j++) m[i, j] += row[i] * row[j]; }
        }
        for (var i = 0; i < 3; i++)
        {
            var pivot = Enumerable.Range(i, 3 - i).MaxBy(r => Math.Abs(m[r, i]));
            for (var c = 0; c < 4; c++) (m[i, c], m[pivot, c]) = (m[pivot, c], m[i, c]);
            for (var r = 0; r < 3; r++)
            {
                if (r == i || m[i, i] == 0) continue;
                var f = m[r, i] / m[i, i];
                for (var c = 0; c < 4; c++) m[r, c] -= f * m[i, c];
            }
        }
        return (m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2]);
    }

    private static double Nice(double max)
    {
        var raw = max / 8;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        return new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * magnitude).First(step => step >= raw);
    }

    public static void Draw(Graphics g, int width, int height, Data? data, float scale = 1f)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.White);
        g.ScaleTransform(scale, scale);
        width = (int)(width / scale);
        height = (int)(height / scale);
        using var titleFont = new Font("Segoe UI", 12F);
        using var axisFont = new Font("Segoe UI", 8.5F);
        g.DrawString("curva de vazão & altura", titleFont, Brushes.Black, width / 2f - 90, 6);
        if (data is null || data.CatalogPoints.Length < 2) return;

        var (a, b, c) = Fit(data);
        double PumpHead(double q) => a + b * q + c * q * q;
        var qMaxCatalog = data.CatalogPoints.Max(p => p.Flow);
        var qMax = new[] { qMaxCatalog * 1.15, data.Operating is { } op0 ? op0.Flow * 1.6 : 0, data.System.Length > 0 ? data.System.Max(p => p.Flow) : 0 }.Max();
        var hMax = new[] { data.Shutoff ?? data.CatalogPoints.Max(p => p.Head), data.System.Where(p => p.Flow <= qMax).Select(p => p.Head).DefaultIfEmpty(0).Max() }.Max() * 1.1;
        var xStep = Nice(qMax);
        var yStep = Nice(hMax);
        qMax = Math.Ceiling(qMax / xStep) * xStep;
        hMax = Math.Ceiling(hMax / yStep) * yStep;

        var plot = new RectangleF(60, 36, width - 80, height - 80);
        PointF P(double q, double h) => new((float)(plot.Left + q / qMax * plot.Width), (float)(plot.Bottom - h / hMax * plot.Height));
        using var gridPen = new Pen(Color.Gainsboro);
        for (var x = 0.0; x <= qMax + 1e-9; x += xStep)
        {
            g.DrawLine(gridPen, P(x, 0), P(x, hMax));
            var label = x.ToString("0.##", Br);
            g.DrawString(label, axisFont, Brushes.Black, P(x, 0).X - g.MeasureString(label, axisFont).Width / 2, plot.Bottom + 4);
        }
        for (var y = 0.0; y <= hMax + 1e-9; y += yStep)
        {
            g.DrawLine(gridPen, P(0, y), P(qMax, y));
            var label = y.ToString("0.##", Br);
            g.DrawString(label, axisFont, Brushes.Black, plot.Left - g.MeasureString(label, axisFont).Width - 4, P(0, y).Y - 7);
        }
        g.DrawRectangle(Pens.Black, plot.X, plot.Y, plot.Width, plot.Height);
        g.DrawString("Vazão (m³/h)", axisFont, Brushes.Black, plot.Left + plot.Width / 2 - 35, plot.Bottom + 20);
        var state = g.Save();
        g.TranslateTransform(14, plot.Top + plot.Height / 2 + 35);
        g.RotateTransform(-90);
        g.DrawString("Altura (m.c.a.)", axisFont, Brushes.Black, 0, 0);
        g.Restore(state);

        g.SetClip(plot);
        // Curva da bomba: cheia na faixa do catálogo, tracejada fora dela.
        using var pumpPen = new Pen(Color.ForestGreen, 2f);
        using var pumpDash = new Pen(Color.ForestGreen, 1.5f) { DashStyle = DashStyle.Dash };
        var catalogRange = Enumerable.Range(0, 81).Select(i => qMaxCatalog * i / 80).Select(q => P(q, PumpHead(q))).ToArray();
        g.DrawLines(pumpPen, catalogRange);
        var beyond = Enumerable.Range(0, 41).Select(i => qMaxCatalog + (qMax - qMaxCatalog) * i / 40).Where(q => PumpHead(q) > 0).Select(q => P(q, PumpHead(q))).ToArray();
        if (beyond.Length > 1) g.DrawLines(pumpDash, beyond);
        foreach (var point in data.CatalogPoints) { var p = P(point.Flow, point.Head); g.FillEllipse(Brushes.ForestGreen, p.X - 2.5f, p.Y - 2.5f, 5, 5); }

        // Curva do sistema (rede do IFC).
        if (data.System.Length > 1)
        {
            using var systemPen = new Pen(Color.Red, 2f);
            g.DrawLines(systemPen, data.System.Select(p => P(p.Flow, p.Head)).ToArray());
        }
        if (data.Required is { } req)
        {
            var p = P(req.Flow, req.Head);
            using var blue = new Pen(Color.Blue, 2f);
            g.DrawRectangle(blue, p.X - 5, p.Y - 5, 10, 10);
        }
        if (data.Operating is { } e && !double.IsNaN(e.Flow))
        {
            var p = P(e.Flow, e.Head);
            using var dashed = new Pen(Color.Red, 1.2f) { DashStyle = DashStyle.Dash };
            g.DrawLine(dashed, P(0, e.Head), p);
            g.DrawLine(dashed, P(e.Flow, 0), P(e.Flow, hMax));
            using var red = new Pen(Color.Red, 2f);
            g.DrawRectangle(red, p.X - 5, p.Y - 5, 10, 10);
            // Verificação de 150% da vazão (NBR 16704, 6.2.1).
            if (data.Point150 is { } q150 && !double.IsNaN(q150.Head))
            {
            var p150 = P(q150.Flow, q150.Head);
            g.FillPolygon(Brushes.DarkOrange, [new PointF(p150.X, p150.Y - 6), new PointF(p150.X - 6, p150.Y + 5), new PointF(p150.X + 6, p150.Y + 5)]);
            }
        }
        g.ResetClip();

        // Legenda.
        var legend = new[] { ("Curva da bomba (catálogo)", Color.ForestGreen), ("Curva do sistema (rede do IFC)", Color.Red), ("Ponto mínimo da rede", Color.Blue), ("150% da vazão", Color.DarkOrange) };
        var y0 = plot.Top + 6;
        foreach (var (text, color) in legend)
        {
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, plot.Right - 200, y0 + 4, 12, 4);
            g.DrawString(text, axisFont, Brushes.Black, plot.Right - 184, y0 - 2);
            y0 += 16;
        }
    }

}
