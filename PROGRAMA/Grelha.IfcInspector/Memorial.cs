using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Grelha.Shared;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

// Memorial de cálculo do sistema de chuveiros automáticos (conteúdo mínimo da ABNT NBR 10897:2020, 9.3) e da bomba (ABNT NBR 16704:2019).

internal sealed record MemorialContext(string Version, string IfcFile, MemorialData Criteria, double TargetPressure_mca, double DensityPressure_mca,
    string RiskClass, int ReserveDuration_min, HydraulicResult Required, HydraulicResult Operating, PumpEvaluation Pump, CatalogPump? Jockey,
    (int Number, double X_cm, double Y_cm)[] HeadPositions, string CatalogSource);

internal static class MemorialWriter
{
    static readonly CultureInfo Br = CultureInfo.GetCultureInfo("pt-BR");
    static string N(double value, string format = "0.00") => double.IsNaN(value) ? "—" : value.ToString(format, Br);

    // Insere o gráfico como imagem PNG de 16 × 11 cm.
    static void Picture(MainDocumentPart main, Body body, PumpChart.Data data)
    {
        const int width = 1600, height = 1100;
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) PumpChart.Draw(graphics, width, height, data, 2f);
        using var stream = new MemoryStream();
        bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;
        var part = main.AddImagePart(ImagePartType.Png);
        part.FeedData(stream);
        var id = main.GetIdOfPart(part);
        const long cx = 16L * 360000, cy = 11L * 360000;
        var inline = new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
            new DW.DocProperties { Id = 1U, Name = "Curva da bomba e do sistema" },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(new A.GraphicData(
                new PIC.Picture(
                    new PIC.NonVisualPictureProperties(new PIC.NonVisualDrawingProperties { Id = 0U, Name = "curva.png" }, new PIC.NonVisualPictureDrawingProperties()),
                    new PIC.BlipFill(new A.Blip { Embed = id }, new A.Stretch(new A.FillRectangle())),
                    new PIC.ShapeProperties(new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                        new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
            { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
        { DistanceFromTop = 0U, DistanceFromBottom = 0U, DistanceFromLeft = 0U, DistanceFromRight = 0U };
        body.Append(new Paragraph(new ParagraphProperties(new Justification { Val = JustificationValues.Center }), new Run(new Drawing(inline))));
    }

    public static void Write(string path, MemorialContext m)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;

        Paragraph Text(string text, bool bold = false, int size = 20, string? color = null, JustificationValues? align = null, int after = 80)
        {
            var props = new RunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = size.ToString() });
            if (bold) props.Append(new Bold());
            if (color is not null) props.Append(new Color { Val = color });
            var paragraph = new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = after.ToString() }), new Run(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
            if (align is not null) paragraph.ParagraphProperties!.Append(new Justification { Val = align });
            body.Append(paragraph);
            return paragraph;
        }
        void Heading(string text) => Text(text, true, 26, "17365D", after: 120);
        void Table(string[] header, IEnumerable<string[]> rows, int size = 16)
        {
            var table = new Table(new TableProperties(
                new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 },
                    new LeftBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
                new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" }));
            TableCell Cell(string text, bool head) => new(
                new TableCellProperties(head ? new Shading { Fill = "17365D", Val = ShadingPatternValues.Clear } : new Shading { Fill = "FFFFFF", Val = ShadingPatternValues.Clear }),
                new Paragraph(new ParagraphProperties(new SpacingBetweenLines { After = "0" }),
                    new Run(new RunProperties(new RunFonts { Ascii = "Calibri", HighAnsi = "Calibri" }, new FontSize { Val = size.ToString() }, head ? new Bold() : new Bold { Val = false }, new Color { Val = head ? "FFFFFF" : "1E2933" }),
                        new Text(text) { Space = SpaceProcessingModeValues.Preserve })));
            var headerRow = new TableRow(new TableRowProperties(new TableHeader()));
            headerRow.Append(header.Select(text => Cell(text, true)));
            table.Append(headerRow);
            foreach (var row in rows) table.Append(new TableRow(row.Select(text => Cell(text, false))));
            body.Append(table);
            Text("", after: 60);
        }

        var c = m.Criteria;
        var op = m.Operating;
        var req = m.Required;
        var pump = m.Pump.Pump;
        var pr = m.Pump.Result;

        Text("MEMORIAL DE CÁLCULO", true, 32, "17365D", JustificationValues.Center, 40);
        Text("Sistema de proteção por chuveiros automáticos – cálculo hidráulico e seleção da bomba de incêndio", true, 22, "0F6B78", JustificationValues.Center, 200);
        Text($"Modelo IFC: {Path.GetFileName(m.IfcFile)}");
        Text($"Emitido por Dimensionador Tipo Grelha {m.Version} em {DateTime.Now:dd/MM/yyyy HH:mm}");
        Text("Normas: ABNT NBR 10897:2020 (chuveiros automáticos) e ABNT NBR 16704:2019 (conjuntos de bombas de incêndio).", after: 200);

        Heading("1. Critérios de projeto");
        Table(["Parâmetro", "Valor"],
        [
            ["Classificação de risco", m.RiskClass],
            ["Área de operação", c.OperationArea],
            ["Densidade de projeto", $"{N(c.Density_mm_min, "0.0")} mm/min"],
            ["Área de aplicação", $"{N(c.DesignArea_m2, "0.00")} m²"],
            ["Quantidade de chuveiros na área", c.SprinklerCount.ToString()],
            ["Área por chuveiro", $"{N(c.DesignArea_m2 / c.SprinklerCount, "0.00")} m²"],
            ["Fator K", $"{N(c.KFactor_l_min_bar_sqrt, "0")} L/min/bar½"],
            ["Pressão mínima (memorial de referência)", $"{N(c.MinimumSprinklerPressure_mca)} m.c.a."],
            ["Pressão mínima pela densidade", $"{N(m.DensityPressure_mca)} m.c.a."],
            ["Pressão mínima adotada no chuveiro mais desfavorável", $"{N(m.TargetPressure_mca)} m.c.a. (NBR 10897: mínimo 48 kPa)"],
            ["Método de cálculo", "Hazen-Williams (NBR 10897, 9.4.2), rede em grelha resolvida pelo método do gradiente global"],
            ["Coeficiente C", "Aço carbono (sistema molhado) 120; cobre 150 (NBR 10897, Tabela 30)"],
            ["Perdas localizadas", op.EquivalentLengthSource],
        ]);

        Heading("2. Chuveiros da área de operação");
        Text("Pressões e vazões com a bomba selecionada em operação; vazão de cada chuveiro Q = K√P (NBR 10897, 9.4.4.6).");
        Table(["Nº", "X (cm)", "Y (cm)", "Cota (m)", "K", "Pressão (m.c.a.)", "Vazão (L/min)", "Vazão (L/s)"],
            op.Sprinklers.OrderBy(head => head.Number).Select(head =>
            {
                var position = m.HeadPositions.FirstOrDefault(item => item.Number == head.Number);
                return new[] { head.Number.ToString(), N(position.X_cm), N(position.Y_cm), N(head.Elevation_m), N(c.KFactor_l_min_bar_sqrt, "0"),
                    N(head.Pressure_mca), N(head.Flow_l_s * 60, "0.0"), N(head.Flow_l_s, "0.000") };
            }));
        Text($"Vazão total na área: {N(op.TotalFlow_l_s, "0.00")} L/s ({N(op.TotalFlow_l_s * 60, "0.0")} L/min). Mínimo pela densidade: {N(c.DensityMinimumFlow_l_s, "0.00")} L/s.", true);

        Heading("3. Planilha de cálculo por trecho");
        Text("Nós: RES = reservatório (tomadas d'água), SUC/REC = sucção e recalque da bomba, B = chuveiro, N = nó da rede. Trechos sem vazão foram omitidos; L eq. = comprimento equivalente de conexões e válvulas.");
        Table(["Trecho", "De", "Para", "Q (L/s)", "Ø int. (mm)", "v (m/s)", "L (m)", "L eq. (m)", "J (m/m)", "Perda (m)", "Cota de (m)", "Cota para (m)", "P de (m.c.a.)", "P para (m.c.a.)"],
            op.Segments.Select(s => new[] { s.Number.ToString(), s.FromNode, s.ToNode, N(s.Flow_l_s, "0.000"), s.Diameters_mm.Replace('.', ','), N(s.Velocity_m_s),
                N(s.Length_m), N(s.EquivalentLength_m), N(s.HeadLoss_m / Math.Max(s.Length_m + s.EquivalentLength_m, 1e-9), "0.0000"), N(s.HeadLoss_m, "0.000"),
                N(s.FromElevation_m), N(s.ToElevation_m), N(s.FromPressure_mca), N(s.ToPressure_mca) }), 13);

        Heading("4. Bomba de incêndio");
        Table(["Item", "Valor"],
        [
            ["Bomba selecionada", pump.Description],
            ["Linha / família", pump.Family],
            ["Bocais (sucção × recalque)", $"{N(pump.Suction_in ?? double.NaN, "0.##")}\" × {N(pump.Discharge_in ?? double.NaN, "0.##")}\""],
            ["Fonte dos dados", $"{m.CatalogSource}, página {pump.CatalogPage}"],
            ["Ponto requerido pelo sistema", $"{N(req.TotalFlow_l_s * 3.6)} m³/h a {N(req.Pump?.Head_m ?? double.NaN)} m.c.a. (pressão mínima exata no chuveiro mais desfavorável)"],
            ["Ponto de operação", $"{N(pr.Flow_l_s * 3.6)} m³/h ({N(pr.Flow_l_s)} L/s) a {N(pr.Head_m)} m.c.a."],
            ["Pressão no recalque", $"{N(pr.DischargePressure_mca)} m.c.a. na cota {N(op.Pump?.DischargeElevation_m ?? double.NaN)} m"],
            ["Pressão na sucção", $"{N(pr.SuctionPressure_mca)} m.c.a. na cota {N(op.Pump?.SuctionElevation_m ?? double.NaN)} m"],
            ["Nível de água adotado", $"{N(op.Pump?.TankLevelElevation_m ?? double.NaN)} m ({op.Pump?.TankOutlets ?? 0} tomada(s) d'água)"],
            ["Menor pressão nos chuveiros em operação", $"{N(pr.MinimumSprinklerPressure_mca)} m.c.a."],
        ]);
        // Curva da bomba sobre a curva do sistema (NBR 10897, 9.3.4).
        var chartData = new PumpChart.Data(pump.Curve.Select(point => (point.Flow_m3_h, point.Head_m)).ToArray(), pump.ShutoffHead_m,
            req.SystemCurve.Select(point => (point.Flow_l_s * 3.6, point.Head_m)).ToArray(),
            req.Pump is { } minimum ? (minimum.Flow_l_s * 3.6, minimum.Head_m) : null,
            (pr.Flow_l_s * 3.6, pr.Head_m), (1.5 * pr.Flow_l_s * 3.6, m.Pump.Head150_m));
        Picture(main, body, chartData);
        Text("Curva verde: bomba (pontos do catálogo; tracejada fora da faixa tabelada). Curva vermelha: sistema da rede do IFC. Quadrado vermelho: ponto de operação; quadrado azul: ponto mínimo da rede; triângulo laranja: 150% da vazão.", size: 16, color: "526170");
        Text("Verificação de desempenho (ABNT NBR 16704:2019), com o ponto de operação adotado como ponto nominal:", true);
        Table(["Verificação", "Exigência", "Valor", "Situação"], m.Pump.Checks.Select(check => new[] { check.Rule, check.Requirement, check.Value, check.Pass ? "Atende" : "Não atende" }));
        if (m.Pump.Extrapolated150)
            Text("Observação: a vazão de 150% fica além da última vazão tabelada no catálogo; a altura correspondente foi estimada pelo ajuste quadrático da curva e deve ser confirmada na curva de ensaio do fabricante (NBR 16704, 4.5).", color: "8A4B00");

        Heading("5. Bomba de manutenção de pressão (jóquei)");
        Text(m.Jockey is null
            ? "Nenhuma bomba jóquei do catálogo atende à pressão sem vazão da bomba principal; especificar com o fabricante (NBR 16704, 4.22)."
            : $"{m.Jockey.Description} ({m.Jockey.Family}, página {m.Jockey.CatalogPage}). Critério: pressão igual ou superior à pressão sem vazão da principal ({N(pump.Shutoff_m)} m.c.a.) com vazão de reposição de 1,0 m³/h (NBR 16704, 4.22.3).");

        Heading("6. Reserva técnica de incêndio");
        var volume = op.TotalFlow_l_s * 60 * m.ReserveDuration_min / 1000;
        Text($"Volume = vazão de operação × duração = {N(op.TotalFlow_l_s * 60, "0.0")} L/min × {m.ReserveDuration_min} min = {N(volume, "0.00")} m³ (NBR 10897, Tabela 27). Reserva do memorial de referência: {N(c.FireReserveTank_m3, "0.00")} m³.");

        Heading("7. Observações");
        foreach (var note in new[]
                 {
                     "Topologia da rede obtida automaticamente da geometria do modelo IFC; conferir no modelo as ligações de válvulas e bombas.",
                     "Curva da bomba ajustada por mínimos quadrados aos pontos tabelados do catálogo (vazões válidas para sucção de 0 m.c.a.) e à pressão sem vazão.",
                     "A seleção deve ser confirmada com a curva certificada do fabricante e com o responsável técnico.",
                 }.Concat(op.Warnings))
            Text("• " + note);
        main.Document.Save();
    }
}
