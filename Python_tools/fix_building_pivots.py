#!/usr/bin/env python3
"""
fix_building_pivots.py

Za svaki par buildings_TILE.fbx + buildings_TILE.json:
  - Iz FBX verteksa izračuna stvarnu poziciju svake zgrade
    (centroid X/Z, minimum Y = dno zgrade)
  - Re-centrira FBX vertekse na novi pivot svake zgrade
  - Ažurira FBX Lcl Translation na ispravnu per-building poziciju
  - Ažurira JSON localPosition na ispravnu Unity world poziciju

Nakon obrade, Unity postavlja zgrade na identično realno mjesto,
ali svaka zgrada ima pivot u centru (X/Z) i na dnu (Y).

Koordinatni sustav (dokazano empirijski):
  Unity_X = fbx_local_x + shared_pivot_X   (FBX x os → Unity X)
  Unity_Y = fbx_local_z + shared_pivot_Y   (FBX z os → Unity Y, zbog -90° X rotacije)
  Unity_Z = fbx_local_y + shared_pivot_Z   (FBX y os → Unity Z)
"""

import re
import sys
from pathlib import Path


# ─── Parsiranje FBX floatova ──────────────────────────────────────────────────

def parse_floats(s: str) -> list:
    return [float(t) for t in re.split(r'[\s,]+', s.strip()) if t]


def fmt(v: float) -> str:
    """Formatira float kao FBX SDK (dovoljna preciznost, bez trailing nula)."""
    return f'{v:.10g}'


# ─── Čitanje dijeljenog pivota iz FBX Lcl Translation ────────────────────────

def read_shared_pivot(fbx_text: str) -> tuple:
    """
    Čita Lcl Translation prvog Mesh modela i konvertira u Unity world prostor.
    Svi modeli dijele isti pivot, pa je dovoljan prvi.
    Vraća (pivot_X, pivot_Y, pivot_Z) u Unity metrima.
    """
    m = re.search(
        r'Model:\s+\d+,\s+"Model::[^"]+",\s+"Mesh".*?'
        r'P:\s+"Lcl Translation"[^,]*,[^,]*,[^,]*,\s*[^,]*,\s*'
        r'(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)',
        fbx_text, re.DOTALL
    )
    if not m:
        raise ValueError("Nije pronađen Lcl Translation u FBX-u")
    tx, ty, tz = float(m.group(1)), float(m.group(2)), float(m.group(3))
    # FBX (cm, desni koordinatni sustav) → Unity (m, lijevi koordinatni sustav)
    # Unity_X = -Tx/100, Unity_Y = Ty/100, Unity_Z = Tz/100
    return (-tx / 100.0, ty / 100.0, tz / 100.0)


# ─── Mapiranje Geometry ID → ime zgrade ──────────────────────────────────────

def build_geo_name_map(fbx_text: str) -> dict:
    """Vraća {geometry_id: 'zagreb_Part_XXXX'} iz Connections i Model sekcija."""
    # Skupi sve geometry ID-ove
    geo_ids = {int(m.group(1))
               for m in re.finditer(r'^\tGeometry:\s+(\d+),', fbx_text, re.M)}

    # Model ID → ime zgrade
    mid_to_name = {int(m.group(1)): m.group(2)
                   for m in re.finditer(
                       r'^\tModel:\s+(\d+),\s+"Model::([^"]+)",\s+"Mesh"',
                       fbx_text, re.M)}

    # Connections: C: "OO",CHILD_ID,PARENT_ID
    # Geometry → Model veza: child je geometry, parent je model
    geo_to_name = {}
    for m in re.finditer(r'^\s*C:\s*"OO",(\d+),(\d+)', fbx_text, re.M):
        child_id = int(m.group(1))
        parent_id = int(m.group(2))
        if child_id in geo_ids and parent_id in mid_to_name:
            geo_to_name[child_id] = mid_to_name[parent_id]

    return geo_to_name


# ─── Računanje novog pivota i re-centriranje verteksa ────────────────────────

def recentre(raw: list, pivot: tuple) -> tuple:
    """
    raw:   flat lista FBX lokalnih verteks floatova [x0,y0,z0, x1,y1,z1, ...]
    pivot: (Unity_X, Unity_Y, Unity_Z) dijeljeni pivot u Unity metrima

    Vraća:
        new_pivot: (Unity_X, Unity_Y, Unity_Z) stvarni centar zgrade u Unity metrima
        new_raw:   flat lista re-centriranih FBX verteks floatova
    """
    px, py, pz = pivot
    n = len(raw) // 3

    # FBX lokalne koordinate po osi
    fbx_xs = [raw[i * 3]     for i in range(n)]  # FBX x → Unity X
    fbx_ys = [raw[i * 3 + 1] for i in range(n)]  # FBX y → Unity Z
    fbx_zs = [raw[i * 3 + 2] for i in range(n)]  # FBX z → Unity Y

    # Konverzija u Unity world prostor
    unity_xs = [x + px for x in fbx_xs]
    unity_ys = [z + py for z in fbx_zs]  # FBX z → Unity Y
    unity_zs = [y + pz for y in fbx_ys]  # FBX y → Unity Z

    # Novi pivot: centar X/Z, dno Y (najniža točka zgrade)
    cx = sum(unity_xs) / n
    cy = min(unity_ys)
    cz = sum(unity_zs) / n

    # Re-centrirani FBX verteksi: Unity offset → FBX lokalne koordinate
    new_raw = []
    for i in range(n):
        new_raw.append(unity_xs[i] - cx)   # Unity X offset → FBX x
        new_raw.append(unity_zs[i] - cz)   # Unity Z offset → FBX y
        new_raw.append(unity_ys[i] - cy)   # Unity Y offset → FBX z
    return (cx, cy, cz), new_raw


def unity_to_lcl(ux: float, uy: float, uz: float) -> tuple:
    """
    Konvertira Unity world pivot (m) u FBX Lcl Translation (cm, desni koordinatni sustav).
    Inverz od: Unity_X = -Tx/100, Unity_Y = Ty/100, Unity_Z = Tz/100
    """
    return (-ux * 100.0, uy * 100.0, uz * 100.0)


# ─── Zamjena dijelova teksta ──────────────────────────────────────────────────

def apply_replacements(text: str, replacements: list) -> str:
    """
    Primjenjuje listu (start, end, new_str) zamjena od kraja prema početku
    kako bi offseti ostali ispravni.
    """
    replacements.sort(key=lambda r: r[0], reverse=True)
    for start, end, new_str in replacements:
        text = text[:start] + new_str + text[end:]
    return text


# ─── FBX obrada ───────────────────────────────────────────────────────────────

# Regex za Geometry blok: hvata ID i cijelo tijelo do zatvaranja na 1-tab razini
GEO_BLOCK_RE = re.compile(
    r'^\tGeometry:\s+(\d+),[^\n]*\n'  # otvarajući red (grupa 1 = ID)
    r'(.*?)'                           # tijelo (lazy)
    r'^\t\}',                          # zatvarajuća zagrada na 1-tab razini
    re.M | re.DOTALL
)

# Regex za Vertices blok unutar Geometry tijela
# Podatkovne linije nakon 'a:' mogu biti bez indentacije (FBX wrap)
VERTS_RE = re.compile(
    r'(\t+Vertices:\s+\*\d+\s*\{\n\t+a:\s*)'  # header + 'a: ' prefiks (grupa 1)
    r'(.*?)'                                    # podaci (grupa 2, lazy)
    r'(\n\t+\})',                               # zatvarajuća zagrada (grupa 3)
    re.DOTALL
)

# Regex za Lcl Translation unutar Model tijela
LCL_TRANS_RE = re.compile(
    r'(P:\s+"Lcl Translation"(?:[^,]*,){3}\s*[^,]*,\s*)'  # prefiks do vrijednosti
    r'(-?[\d.]+),(-?[\d.]+),(-?[\d.]+)'                    # Tx, Ty, Tz (grupe 2,3,4)
)

# Regex za Model Mesh blok: hvata ime i tijelo
MODEL_BLOCK_RE = re.compile(
    r'^\tModel:\s+\d+,\s+"Model::([^"]+)",\s+"Mesh"\s*\{'  # otvarajući red (grupa 1 = ime)
    r'(.*?)'                                                  # tijelo (grupa 2, lazy)
    r'^\t\}',                                                 # zatvarajuća zagrada
    re.M | re.DOTALL
)


def process_fbx(fbx_text: str) -> tuple:
    """
    Obrađuje FBX tekst:
    - Re-centrira verteks podatke za svaki Geometry blok
    - Ažurira Lcl Translation za svaki Model blok

    Vraća:
        new_text: modificirani FBX sadržaj
        pivots:   {ime_zgrade: (Unity_X, Unity_Y, Unity_Z)}
    """
    pivot = read_shared_pivot(fbx_text)
    geo_to_name = build_geo_name_map(fbx_text)

    replacements = []
    pivots = {}

    # ── Geometry blokovi: re-centriranje verteksa ─────────────────────────────
    for gm in GEO_BLOCK_RE.finditer(fbx_text):
        gid = int(gm.group(1))
        body = gm.group(2)
        body_offset = gm.start(2)

        vm = VERTS_RE.search(body)
        if not vm:
            continue

        raw = parse_floats(vm.group(2))
        if not raw or len(raw) % 3 != 0:
            continue

        new_pivot, new_raw = recentre(raw, pivot)

        name = geo_to_name.get(gid)
        if name:
            pivots[name] = new_pivot

        # Zamijeniti samo podatkovni dio (grupa 2) unutar Vertices bloka
        abs_start = body_offset + vm.start(2)
        abs_end   = body_offset + vm.end(2)
        replacements.append((abs_start, abs_end, ','.join(fmt(v) for v in new_raw)))

    # ── Model blokovi: ažuriranje Lcl Translation ─────────────────────────────
    for mm in MODEL_BLOCK_RE.finditer(fbx_text):
        name = mm.group(1)
        if name not in pivots:
            continue

        body = mm.group(2)
        body_offset = mm.start(2)

        lm = LCL_TRANS_RE.search(body)
        if not lm:
            continue

        ux, uy, uz = pivots[name]
        tx, ty, tz = unity_to_lcl(ux, uy, uz)

        abs_start = body_offset + lm.start(2)
        abs_end   = body_offset + lm.end(4)
        replacements.append((abs_start, abs_end, f'{fmt(tx)},{fmt(ty)},{fmt(tz)}'))

    return apply_replacements(fbx_text, replacements), pivots


# ─── JSON obrada ──────────────────────────────────────────────────────────────

def fmt_eu(v: float, decimals: int = 4) -> str:
    """Formatira float s europskim zarezom kao decimalni separator."""
    return f'{v:.{decimals}f}'.replace('.', ',')


def process_json(json_text: str, pivots: dict) -> str:
    """Zamjenjuje localPosition za svaku zgradu u JSON tekstu."""

    def replacer(m: re.Match) -> str:
        name = m.group(1)
        if name not in pivots:
            return m.group(0)
        ux, uy, uz = pivots[name]
        return (
            f'"name": "{name}", '
            f'"localPosition": {{"x": {fmt_eu(ux)}, "y": {fmt_eu(uy)}, "z": {fmt_eu(uz)}}}'
        )

    return re.sub(
        r'"name":\s*"([^"]+)",\s*"localPosition":\s*\{[^}]+\}',
        replacer,
        json_text
    )


# ─── Obrada jednog para datoteka ──────────────────────────────────────────────

def process_pair(fbx_path: Path, json_path: Path, dry_run: bool = False) -> int:
    """Vraća broj ažuriranih zgrada (0 ako je preskočeno)."""
    with open(fbx_path, 'r', encoding='utf-8') as f:
        fbx_text = f.read()
    with open(json_path, 'r', encoding='utf-8') as f:
        json_text = f.read()

    try:
        new_fbx, pivots = process_fbx(fbx_text)
    except ValueError as e:
        print(f'  UPOZORENJE: {fbx_path.name}: {e}')
        return 0

    if not pivots:
        print(f'  {fbx_path.name}: nema zgrada, preskočeno')
        return 0

    new_json = process_json(json_text, pivots)

    if not dry_run:
        with open(fbx_path, 'w', encoding='utf-8') as f:
            f.write(new_fbx)
        with open(json_path, 'w', encoding='utf-8') as f:
            f.write(new_json)

    return len(pivots)


# ─── Main ─────────────────────────────────────────────────────────────────────

def main():
    args = sys.argv[1:]
    dry_run = '--dry-run' in args

    directory = Path(__file__).parent
    pairs = sorted(
        (p, p.with_suffix('.json'))
        for p in directory.glob('buildings_*.fbx')
        if p.with_suffix('.json').exists()
    )

    if not pairs:
        print('Nisu pronađene buildings_*.fbx datoteke.')
        return

    mode = ' (dry-run, bez pisanja)' if dry_run else ''
    print(f'Obrađujem {len(pairs)} parova datoteka{mode}...')

    total_buildings = 0
    for fbx_path, json_path in pairs:
        count = process_pair(fbx_path, json_path, dry_run=dry_run)
        if count:
            label = 'bi ažuriralo' if dry_run else 'ažurirano'
            print(f'  {fbx_path.stem}: {count} zgrada {label}')
        total_buildings += count

    print(f'\nUkupno: {total_buildings} zgrada obrađeno.')


if __name__ == '__main__':
    main()
