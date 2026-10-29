"""Extrai as tabelas de seleção do catálogo Schneider para bombas de uso em incêndio."""
import json, re, sys
import pymupdf

SRC = sys.argv[1]
OUT = sys.argv[2]

# Páginas e enquadramento declarados no próprio catálogo.
PAGES = {
    **{p: ("BPI - linha para prevenção e combate a incêndio", "principal") for p in range(63, 66)},
    66: ("BPI - linha para prevenção e combate a incêndio (jockey)", "jockey"),
    67: ("BPI - linha para prevenção e combate a incêndio (multiestágio)", "principal"),
    68: ("BPI - linha para prevenção e combate a incêndio (multiestágio)", "principal"),
    **{p: ("Uso declarado em sistemas de prevenção e combate a incêndio", "principal") for p in (28, 29, 30)},
    **{p: ("Série FIT - versão BPI para incêndio (consultar fabricante)", "principal") for p in range(33, 45)},
    **{p: ("Série FIT PRO - versão BPI para incêndio (consultar fabricante)", "principal") for p in range(46, 50)},
    **{p: ("Motobomba jockey para prevenção e combate a incêndio", "jockey") for p in (54, 59, 60, 61)},
    62: ("Uso declarado em sistemas de prevenção e combate a incêndio", "principal"),
}


def num(text):
    text = (text or "").strip().replace(" ", "")
    if not text or text == "*":
        return None
    frac = re.fullmatch(r"(\d+)?(\d)/(\d)", text)
    if frac:
        return (float(frac.group(1)) if frac.group(1) else 0.0) + float(frac.group(2)) / float(frac.group(3))
    text = text.replace(".", "").replace(",", ".") if "," in text else text
    try:
        return float(text)
    except ValueError:
        return None


def inches(text):
    text = (text or "").replace("\n", " ").strip()
    m = re.fullmatch(r"(\d+)?\s*(?:(\d+)/(\d+))?", text)
    if not text or not m:
        return text or None
    whole = float(m.group(1)) if m.group(1) else 0.0
    if m.group(2):
        whole += float(m.group(2)) / float(m.group(3))
    return whole


doc = pymupdf.open(SRC)
pumps, problems = [], []
for page_no, (family_note, role) in PAGES.items():
    page = doc[page_no - 1]
    words = page.get_text("words")
    for table in page.find_tables().tables:
        rows = table.extract()
        header_index = next((i for i, r in enumerate(rows) if r and r[0] and r[0].strip().upper() == "MODELO"), None)
        if header_index is None:
            continue
        header = [(c or "").replace("\n", " ").strip() for c in rows[header_index]]
        hyd = next((i for i, c in enumerate(header) if "HIDRÁULICAS" in c.upper()), None)
        if hyd is None:
            continue
        axis_index = next((i for i in range(header_index + 1, min(header_index + 5, len(rows)))
                           if rows[i][hyd] and re.match(r"^[\d\s,]+$", rows[i][hyd])), None)
        if axis_index is None:
            problems.append(f"p{page_no}: eixo não encontrado")
            continue
        label = " ".join(filter(None, sum(rows[header_index:axis_index + 2], [])))
        heads_are_columns = "Altura Manométrica" in label and "Vazão (m" not in label.split("Altura Manométrica")[0][-40:]
        # Colunas nomeadas depois do bloco hidráulico (ex.: pressão sem vazão na série FIT) não são vazões.
        trailing = [i for i in range(hyd + 1, len(header)) if header[i]]
        end_col = trailing[0] if trailing else len(header)

        def x_of(col, side=0):
            values = [row.cells[col][side] for row in table.rows[header_index:] if col < len(row.cells) and row.cells[col]]
            return min(values) if values else None

        x_start = x_of(hyd) or 0
        x_end = (x_of(end_col) or 1e9) if trailing else 1e9

        def row_words(i):
            # A célula do modelo ocupa várias linhas; a altura da linha vem da célula de potência.
            cells = table.rows[i].cells
            box = cells[1] if len(cells) > 1 and cells[1] else table.rows[i].bbox
            _, y0, _, y1 = box
            return [w for w in words if y0 - 0.5 <= (w[1] + w[3]) / 2 <= y1 + 0.5 and x_start - 1 <= (w[0] + w[2]) / 2 < x_end - 1]

        axis = [((w[0] + w[2]) / 2, num(w[4])) for w in row_words(axis_index) if num(w[4]) is not None]
        model = None
        for index in range(axis_index + 1, len(rows)):
            r = rows[index]
            if not r or all(not c for c in r):
                continue
            if r[0] and r[0].strip() and not r[0].strip().upper().startswith("MODELO"):
                model = r[0].replace("\n", " ").strip()
            if r[hyd] and not re.match(r"^[\d\s,*]+$", r[hyd]):
                continue
            fields = {header[i]: (r[i] or "").replace("\n", " ").strip() for i in list(range(hyd)) + trailing if i < len(r)}
            power = num(fields.get("Potência (cv)"))
            if model is None or power is None:
                continue
            points, used = [], set()
            for w in row_words(index):
                value = num(w[4])
                if value is None:
                    continue
                x = (w[0] + w[2]) / 2
                k = min(range(len(axis)), key=lambda j: abs(axis[j][0] - x))
                if abs(axis[k][0] - x) > 12 or k in used:
                    problems.append(f"p{page_no} {model} {power:g}cv: valor {w[4]} fora de coluna")
                    continue
                used.add(k)
                a = axis[k][1]
                head, flow = (a, value) if heads_are_columns else (value, a)
                points.append({"Head_m": head, "Flow_m3_h": flow})
            points.sort(key=lambda p: (p["Flow_m3_h"], -p["Head_m"]))

            def field(*names):
                for key, value in fields.items():
                    if any(n.lower() in key.lower() for n in names):
                        return value
                return None

            pumps.append({
                "Manufacturer": "Schneider",
                "Model": model,
                "Family": family_note,
                "Role": role,
                "CatalogPage": page_no,
                "Power_cv": power,
                "Stages": num(field("Estágios")),
                "SinglePhase": (field("Monofásico") or "").strip().lower() in ("x", "◆"),
                "ThreePhase": (field("Trifásico") or "").strip().lower() in ("x", "◆"),
                "Suction_in": inches(field("Sucção")),
                "Discharge_in": inches(field("Recalque")),
                "ShutoffHead_m": num(field("Pressão máxima")),
                "MaxSuctionLift_m": num(field("Altura máxima")),
                "Impeller_mm": num(field("Rotor")),
                "Curve": points,
            })

for i, pump in enumerate(pumps):
    pump["Id"] = f"SCH-{pump['CatalogPage']:02d}-{i + 1:03d}"
json.dump({"Source": SRC.split("\\")[-1], "Units": {"Flow": "m3/h", "Head": "m.c.a."},
           "Note": "Vazões válidas para sucção de 0 m.c.a.; faixas com asterisco (*) foram descartadas.",
           "Pumps": pumps}, open(OUT, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
print(len(pumps), "bombas")
for p in problems:
    print("AVISO", p)
by = {}
for p in pumps:
    by.setdefault((p["CatalogPage"], p["Model"]), []).append(p)
for (page, model), items in sorted(by.items()):
    print(page, model, [f"{x['Power_cv']:g}cv/{len(x['Curve'])}pts" for x in items])
