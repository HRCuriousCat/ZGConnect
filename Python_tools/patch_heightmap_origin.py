#!/usr/bin/env python3
"""
patch_heightmap_origin.py
Ažurira unity_origin i unity_position koordinate u heightmap metadata.json
na novi world origin.

Za razliku od patch_buildings_origin.py koji radi delta-shift, ovdje se
unity_position svake pločice izračunava direktno iz apsolutnih left/bottom
koordinata i novog origina:
    unity_position.x = left   − novi_origin_x
    unity_position.z = bottom − novi_origin_y

Mijenja:
    settings.unity_origin_x
    settings.unity_origin_y
    tiles[*].unity_position.x
    tiles[*].unity_position.z

Ne mijenja:
    tiles[*].unity_position.y  (uvijek 0 — visina ovisi o heightmapu)
    left, bottom, right, top   (apsolutne EPSG:3765 koordinate, nepromjenjive)
    raw_file, terrain_size, sve ostale vrijednosti

Primjeri:
    # GPS koordinate željenog novog origina (preporučeno)
    python patch_heightmap_origin.py --lat 45.8131 --lon 15.9786

    # Ili direktno EPSG:3765
    python patch_heightmap_origin.py --origin-x 459200 --origin-y 5075900

    # Provjera bez pisanja
    python patch_heightmap_origin.py --lat 45.8131 --lon 15.9786 --dry-run

    # Drugi metadata.json
    python patch_heightmap_origin.py --lat 45.8131 --lon 15.9786 \\
        --file D:/moj_dataset/heightmaps/metadata.json
"""

import json
import math
import shutil
import argparse
from pathlib import Path


# ── EPSG:3765 ↔ WGS84 projekcija ─────────────────────────────────────────────
# Identično s C# ZGConnectCoordinates i patch_buildings_origin.py
# (Snyder 1987, sub-mm točnost za Hrvatsku)

def wgs84_to_epsg3765(lat_deg: float, lon_deg: float) -> tuple:
    a    = 6378137.0
    f    = 1.0 / 298.257222101
    e2   = 2*f - f*f
    e2_  = e2 / (1 - e2)
    k0   = 0.9999
    lon0 = 16.5 * math.pi / 180
    FE   = 500000.0

    phi = lat_deg * math.pi / 180
    lam = lon_deg * math.pi / 180
    sp, cp, tp = math.sin(phi), math.cos(phi), math.tan(phi)
    T = tp*tp;  C = e2_*cp*cp;  A = cp*(lam - lon0);  A2 = A*A
    N1 = a / math.sqrt(1 - e2*sp*sp)

    e4 = e2*e2;  e6 = e4*e2
    M = a * (
          (1       - e2/4    - 3*e4/64    - 5*e6/256  ) * phi
        - (3*e2/8  + 3*e4/32 + 45*e6/1024) * math.sin(2*phi)
        + (15*e4/256 + 45*e6/1024)          * math.sin(4*phi)
        - (35*e6/3072)                      * math.sin(6*phi)
    )
    E = FE + k0 * N1 * (
          A
        + (1 - T + C)                              * A2*A  / 6
        + (5 - 18*T + T*T + 72*C - 58*e2_)         * A2*A2*A / 120
    )
    N = k0 * (M + N1 * tp * (
          A2 / 2
        + (5  - T  + 9*C  + 4*C*C)                 * A2*A2    / 24
        + (61 - 58*T + T*T + 600*C - 330*e2_)       * A2*A2*A2 / 720
    ))
    return E, N


# ── Obrada metadata.json ──────────────────────────────────────────────────────

def patch_metadata(path: Path, new_x: int, new_y: int,
                   dry_run: bool, backup: bool) -> dict:
    """
    Učitava metadata.json, ažurira origin i sve unity_position vrijednosti,
    i zapisuje nazad (osim u dry-run modu).

    Vraća dict s info o promjenama: old/new origin, broj ažuriranih pločica.
    """
    with open(path, encoding='utf-8') as f:
        data = json.load(f)

    old_x = data['settings']['unity_origin_x']
    old_y = data['settings']['unity_origin_y']

    # ── Ažuriraj settings ─────────────────────────────────────────────────────
    data['settings']['unity_origin_x'] = new_x
    data['settings']['unity_origin_y'] = new_y

    # ── Recalculate unity_position za svaku pločicu ───────────────────────────
    # Izravno iz apsolutnih koordinata — nema propagacije greški kao kod delta-shifta.
    updated = 0
    x_range = [float('inf'), float('-inf')]
    z_range = [float('inf'), float('-inf')]

    for tile in data.get('tiles', []):
        new_px = tile['left']   - new_x
        new_pz = tile['bottom'] - new_y

        tile['unity_position']['x'] = new_px
        tile['unity_position']['z'] = new_pz
        # unity_position.y ostaje 0 — visinska pozicija terena ovisi o heightmapu

        x_range[0] = min(x_range[0], new_px)
        x_range[1] = max(x_range[1], new_px)
        z_range[0] = min(z_range[0], new_pz)
        z_range[1] = max(z_range[1], new_pz)
        updated += 1

    # ── Zapis ─────────────────────────────────────────────────────────────────
    if not dry_run:
        if backup:
            shutil.copy2(path, path.with_suffix('.json.bak'))
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(data, f, indent=2, ensure_ascii=False)
            f.write('\n')   # trailing newline

    return {
        'old_x': old_x, 'old_y': old_y,
        'new_x': new_x, 'new_y': new_y,
        'dx': new_x - old_x, 'dy': new_y - old_y,
        'tiles': updated,
        'x_range': x_range,
        'z_range': z_range,
    }


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(
        description="Ažurira unity_origin i unity_position u heightmap metadata.json.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__.split('\n\n', 1)[1]
    )

    ap.add_argument('--file', default=None,
        metavar='PATH',
        help="Putanja do metadata.json "
             "(default: metadata.json u direktoriju ove skripte)")

    origin_grp = ap.add_mutually_exclusive_group(required=True)
    origin_grp.add_argument('--lat', type=float, metavar='LAT',
        help="WGS84 latituda željenog novog origina (°N)")
    origin_grp.add_argument('--origin-x', type=int, metavar='E',
        help="Novi unity_origin_x kao EPSG:3765 easting (m)")

    ap.add_argument('--lon', type=float, metavar='LON',
        help="WGS84 longituda (°E) — obavezno uz --lat")
    ap.add_argument('--origin-y', type=int, metavar='N',
        help="Novi unity_origin_y kao EPSG:3765 northing (m) — obavezno uz --origin-x")

    ap.add_argument('--dry-run', action='store_true',
        help="Prikaži što bi se promijenilo bez pisanja fajla")
    ap.add_argument('--no-backup', action='store_true',
        help="Preskoči pravljenje .json.bak kopije")

    args = ap.parse_args()

    # ── Izračun novog origina ─────────────────────────────────────────────────
    if args.lat is not None:
        if args.lon is None:
            ap.error("--lon je obavezno uz --lat")
        e, n = wgs84_to_epsg3765(args.lat, args.lon)
        new_x, new_y = round(e), round(n)
        print(f"GPS ({args.lat:.6f}°N, {args.lon:.6f}°E)")
        print(f"  → EPSG:3765  E = {e:.3f} m,  N = {n:.3f} m")
        print(f"  → zaokruženo ({new_x}, {new_y})")
    else:
        if args.origin_y is None:
            ap.error("--origin-y je obavezno uz --origin-x")
        new_x, new_y = args.origin_x, args.origin_y

    # ── Pronalazak fajla ──────────────────────────────────────────────────────
    if args.file:
        meta_path = Path(args.file)
    else:
        meta_path = Path(__file__).parent / 'metadata.json'

    if not meta_path.exists():
        ap.error(f"Fajl nije pronađen: {meta_path}")

    # ── Ispis plana ───────────────────────────────────────────────────────────
    print()
    print(f"Fajl:          {meta_path}")
    if args.dry_run:
        print("\n[DRY RUN — fajl se neće mijenjati]")
    print()

    # ── Obrada ───────────────────────────────────────────────────────────────
    result = patch_metadata(
        meta_path, new_x, new_y,
        dry_run=args.dry_run,
        backup=not args.no_backup
    )

    # ── Sažetak ───────────────────────────────────────────────────────────────
    print("─" * 52)
    print(f"Stari origin   ({result['old_x']}, {result['old_y']})")
    print(f"Novi origin    ({result['new_x']}, {result['new_y']})")
    print(f"Delta          ({result['dx']:+d}, {result['dy']:+d})")
    print()
    print(f"Pločica ažurirano : {result['tiles']}")

    if result['tiles'] > 0:
        print(f"Unity X raspon    : {result['x_range'][0]} .. {result['x_range'][1]}")
        print(f"Unity Z raspon    : {result['z_range'][0]} .. {result['z_range'][1]}")

    print("─" * 52)

    if args.dry_run:
        print("\nPokreni bez --dry-run da primijeniš promjene.")
    elif result['tiles'] > 0:
        print("\nGotovo.")
        if not args.no_backup:
            print(f"Backup:  {meta_path.with_suffix('.json.bak')}")
        print()
        print("Sljedeći korak:")
        print("  Pokreni ZGConnect importer u Unity za re-import dataseta s novim originom.")


if __name__ == '__main__':
    main()
