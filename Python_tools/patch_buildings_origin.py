#!/usr/bin/env python3
"""
patch_buildings_origin.py
Pomiče Unity-space pozicije u buildings_*.json na novi world origin
i dodaje GPS koordinate (WGS84) za svaku zgradu.

Pokretanje NAKON fix_building_pivots.py.

Mijenja / dodaje:
  tileOriginUnity { x, z }          — pomak na novi origin
  buildings[*].localPosition { x, z } — pomak na novi origin
  buildings[*].gps { lat, lon }      — NOVO: stvarne GPS koordinate zgrade

Ne dotiče:
  tileId, tileSizeMeters, buildingCount
  localRotation, localScale
  sve .y vrijednosti (visina ovisi o terenu, ne o horizontalnom originu)
  FBX datoteke

Podržava europski decimalni format (zarez kao decimalni separator).
GPS polje se piše u standardnom formatu (točka kao decimalni separator).

Primjeri:
  # GPS koordinate željenog novog origina (preporučeno)
  python patch_buildings_origin.py --lat 45.8131 --lon 15.9786

  # Ili direktno EPSG:3765
  python patch_buildings_origin.py --origin-x 459200 --origin-y 5075900

  # Provjera bez pisanja
  python patch_buildings_origin.py --lat 45.8131 --lon 15.9786 --dry-run

  # Bez dodavanja GPS (samo pomak pozicija)
  python patch_buildings_origin.py --lat 45.8131 --lon 15.9786 --no-gps

  # Drukčiji direktorij
  python patch_buildings_origin.py --lat 45.8131 --lon 15.9786 --dir D:/moj_dataset/buildings
"""

import re
import math
import shutil
import argparse
from pathlib import Path


# ── EPSG:3765 ↔ WGS84 projekcija ─────────────────────────────────────────────
# Identično s C# ZGConnectCoordinates (Snyder 1987, sub-mm točnost za Hrvatsku)

def wgs84_to_epsg3765(lat_deg: float, lon_deg: float) -> tuple:
    a   = 6378137.0
    f   = 1.0 / 298.257222101
    e2  = 2*f - f*f
    e2_ = e2 / (1 - e2)
    k0  = 0.9999
    lon0 = 16.5 * math.pi / 180
    FE  = 500000.0

    phi = lat_deg * math.pi / 180
    lam = lon_deg * math.pi / 180
    sp, cp, tp = math.sin(phi), math.cos(phi), math.tan(phi)
    T = tp*tp;  C = e2_*cp*cp;  A = cp*(lam - lon0);  A2 = A*A
    N1 = a / math.sqrt(1 - e2*sp*sp)

    e4 = e2*e2;  e6 = e4*e2
    M = a * (
          (1      - e2/4    - 3*e4/64    - 5*e6/256  ) * phi
        - (3*e2/8 + 3*e4/32 + 45*e6/1024) * math.sin(2*phi)
        + (15*e4/256 + 45*e6/1024)         * math.sin(4*phi)
        - (35*e6/3072)                     * math.sin(6*phi)
    )
    E = FE + k0 * N1 * (
          A
        + (1 - T + C) * A2*A / 6
        + (5 - 18*T + T*T + 72*C - 58*e2_) * A2*A2*A / 120
    )
    N = k0 * (M + N1 * tp * (
          A2 / 2
        + (5 - T + 9*C + 4*C*C) * A2*A2 / 24
        + (61 - 58*T + T*T + 600*C - 330*e2_) * A2*A2*A2 / 720
    ))
    return E, N


def epsg3765_to_wgs84(E: float, N: float) -> tuple:
    """EPSG:3765 (easting, northing) → WGS84 (lat, lon) u decimalnim stupnjevima."""
    a   = 6378137.0
    f   = 1.0 / 298.257222101
    e2  = 2*f - f*f
    e2_ = e2 / (1 - e2)
    k0  = 0.9999
    lon0 = 16.5 * math.pi / 180
    FE  = 500000.0

    x = E - FE
    M = N / k0

    mu = M / (a * (1 - e2/4 - 3*e2*e2/64 - 5*e2*e2*e2/256))
    e1 = (1 - math.sqrt(1 - e2)) / (1 + math.sqrt(1 - e2))
    e1_2 = e1*e1;  e1_3 = e1_2*e1;  e1_4 = e1_3*e1

    phi1 = (mu
        + ( 3.0*e1/2    - 27.0*e1_3/32 ) * math.sin(2*mu)
        + (21.0*e1_2/16 - 55.0*e1_4/32 ) * math.sin(4*mu)
        + (151.0*e1_3/96               ) * math.sin(6*mu)
        + (1097.0*e1_4/512             ) * math.sin(8*mu))

    sp, cp, tp = math.sin(phi1), math.cos(phi1), math.tan(phi1)
    sp2 = sp*sp
    T1 = tp*tp
    C1 = e2_ * cp*cp
    N1 = a / math.sqrt(1 - e2*sp2)
    R1 = a * (1 - e2) / (1 - e2*sp2)**1.5
    D  = x / (N1 * k0)
    D2 = D*D

    lat = phi1 - (N1*tp/R1) * (
          D2/2
        - (5 + 3*T1 + 10*C1 - 4*C1*C1 - 9*e2_)          * D2*D2/24
        + (61 + 90*T1 + 298*C1 + 45*T1*T1 - 252*e2_ - 3*C1*C1) * D2*D2*D2/720
    )
    lon = lon0 + (
          D
        - (1 + 2*T1 + C1)                                  * D2*D/6
        + (5 - 2*C1 + 28*T1 - 3*C1*C1 + 8*e2_ + 24*T1*T1) * D2*D2*D/120
    ) / cp

    return lat * (180 / math.pi), lon * (180 / math.pi)


# ── Decimalni format ──────────────────────────────────────────────────────────

def eu_to_std(text: str) -> str:
    """Europski decimalni zarez → standardna točka, ali SAMO između znamenki.
    '1995,9163' → '1995.9163'
    ', "y":' ostaje nepromijenjeno (zarez iza kojeg nije znamenka).
    """
    return re.sub(r'(-?\d+),(\d+)', r'\1.\2', text)

def std_to_eu(text: str) -> str:
    """Standardna decimalna točka → europski zarez, ali SAMO između znamenki.
    '1995.9163' → '1995,9163'
    """
    return re.sub(r'(\d)\.(\d)', r'\1,\2', text)


# ── Pomak x/z koordinata unutar jednog JSON bloka ────────────────────────────

_X_RE = re.compile(r'("x"\s*:\s*)(-?[\d.]+)')
_Z_RE = re.compile(r'("z"\s*:\s*)(-?[\d.]+)')

def shift_block(block_std: str, dx: float, dz: float) -> tuple:
    """
    Ulaz:  tekst JSON bloka u STANDARDNOM formatu (eu_to_std već primijenjen na cijeli fajl)
    Izlaz: (novi_blok_std, (nova_x_vrijednost, nova_z_vrijednost))
    """
    new_x_val = [None]
    new_z_val = [None]

    def sub_x(m):
        v = float(m.group(2)) - dx
        new_x_val[0] = v
        return f'{m.group(1)}{v:.4f}'

    def sub_z(m):
        v = float(m.group(2)) - dz
        new_z_val[0] = v
        return f'{m.group(1)}{v:.4f}'

    result = _X_RE.sub(sub_x, block_std)
    result = _Z_RE.sub(sub_z, result)
    return result, (new_x_val[0], new_z_val[0])


# ── Regex za blokove koje mijenjamo ──────────────────────────────────────────

_ORIGIN_RE = re.compile(r'("tileOriginUnity"\s*:\s*\{)([^}]*)(\})')
_POS_RE    = re.compile(r'("localPosition"\s*:\s*\{)([^}]*)(\})')

# Postojeće GPS polje — briše se prije ponovnog pisanja da nema duplikata
_GPS_RE    = re.compile(r',\s*"gps"\s*:\s*\{[^}]*\}')


# ── Obrada jednog fajla ───────────────────────────────────────────────────────

def patch_file(path: Path, dx: float, dz: float,
               new_ox: int, new_oy: int,
               add_gps: bool, dry_run: bool, backup: bool) -> int:
    """
    Pomiče pozicije i opcionalno dodaje GPS u jednom buildings_*.json fajlu.
    Vraća broj ažuriranih zgrada.
    """
    text = path.read_text(encoding='utf-8')

    # Konvertiraj cijeli fajl na standardni JSON format (točka umjesto zareza)
    # Ovo ispravlja sve fieldove (localRotation, localScale, ...) odjednom,
    # a ne samo one koje eksplicitno mijenjamo.
    text = eu_to_std(text)

    # Ukloni eventualna prethodna GPS polja (idempotentnost kod ponovnog pokretanja)
    if add_gps:
        text = _GPS_RE.sub('', text)

    building_count = [0]

    def sub_origin(m):
        new_block, _ = shift_block(m.group(2), dx, dz)
        return m.group(1) + new_block + m.group(3)

    def sub_pos(m):
        new_block, (nx, nz) = shift_block(m.group(2), dx, dz)
        replacement = m.group(1) + new_block + m.group(3)

        if add_gps and nx is not None and nz is not None:
            # GPS: Unity pozicija → EPSG:3765 (apsolutne koordinate, neovisne o originu)
            E = nx + new_ox
            N = nz + new_oy
            lat, lon = epsg3765_to_wgs84(E, N)
            replacement += f', "gps": {{"lat": {lat:.6f}, "lon": {lon:.6f}}}'

        building_count[0] += 1
        return replacement

    result = _ORIGIN_RE.sub(sub_origin, text, count=1)
    result = _POS_RE.sub(sub_pos, result)

    if result == text:
        return 0

    if not dry_run:
        if backup:
            shutil.copy2(path, path.with_suffix('.json.bak'))
        path.write_text(result, encoding='utf-8')

    return building_count[0]


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(
        description="Pomiče Unity pozicije u buildings_*.json na novi world origin.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__.split('\n\n', 1)[1]
    )

    ap.add_argument('--dir', default=None,
        help="Direktorij s buildings_*.json fajlovima "
             "(default: direktorij u kojemu se nalazi ova skripta)")

    origin_grp = ap.add_mutually_exclusive_group(required=True)
    origin_grp.add_argument('--lat', type=float,
        metavar='LAT',
        help="WGS84 latituda željenog novog origina (°N)")
    origin_grp.add_argument('--origin-x', type=int,
        metavar='E',
        help="Novi unity_origin_x kao EPSG:3765 easting (m)")

    ap.add_argument('--lon', type=float, metavar='LON',
        help="WGS84 longituda (°E) — obavezno uz --lat")
    ap.add_argument('--origin-y', type=int, metavar='N',
        help="Novi unity_origin_y kao EPSG:3765 northing (m) — obavezno uz --origin-x")

    ap.add_argument('--old-x', type=int, default=442000,
        help="Stari unity_origin_x (default: 442000)")
    ap.add_argument('--old-y', type=int, default=5051000,
        help="Stari unity_origin_y (default: 5051000)")

    ap.add_argument('--no-gps', action='store_true',
        help="Ne dodavaj GPS koordinate (samo pomak pozicija)")
    ap.add_argument('--dry-run', action='store_true',
        help="Prikaži što bi se promijenilo bez pisanja fajlova")
    ap.add_argument('--no-backup', action='store_true',
        help="Preskoči pravljenje .bak kopija (ne preporučuje se za prvi run)")

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

    dx = new_x - args.old_x
    dz = new_y - args.old_y

    print()
    add_gps = not args.no_gps

    print(f"Stari origin   ({args.old_x}, {args.old_y})")
    print(f"Novi origin    ({new_x}, {new_y})")
    print(f"Delta          ({dx:+d}, {dz:+d})")
    print(f"GPS polje      {'da' if add_gps else 'ne (--no-gps)'}")
    if args.dry_run:
        print("\n[DRY RUN — fajlovi se neće mijenjati]")
    print()

    # ── Pronalaženje fajlova ──────────────────────────────────────────────────
    directory = Path(args.dir) if args.dir else Path(__file__).parent
    files = sorted(directory.glob('buildings_*.json'))

    if not files:
        print(f"Nisu pronađeni buildings_*.json fajlovi u:\n  {directory}")
        return

    print(f"Pronađeno {len(files)} buildings_*.json fajlova u:\n  {directory}\n")

    # ── Obrada ───────────────────────────────────────────────────────────────
    backup = not args.no_backup
    total_files    = 0
    total_buildings = 0

    for f in files:
        count = patch_file(f, dx, dz, new_x, new_y, add_gps, args.dry_run, backup)
        if count > 0:
            total_files    += 1
            total_buildings += count

    # ── Sažetak ───────────────────────────────────────────────────────────────
    print("─" * 50)
    verb = "bi ažuriralo" if args.dry_run else "ažurirano"
    print(f"Fajlova obrađeno   : {total_files} / {len(files)}")
    print(f"Zgrada {verb}    : {total_buildings}")
    if add_gps and not args.dry_run:
        print(f"GPS polja dodana   : {total_buildings}")

    if args.dry_run:
        print("\nPokreni bez --dry-run da primijeniš promjene.")
    elif total_buildings > 0:
        print("\nGotovo.")
        print("Sljedeći koraci:")
        print("  1. Promijeni UNITY_ORIGIN_X / UNITY_ORIGIN_Y u geotiff_tiles_2_raw.py")
        print("     i pokreni je ponovo (generira novi tiles_unity_metadata.json).")
        print("  2. Pokreni ZGConnect importer u Unity za re-import dataseta.")


if __name__ == '__main__':
    main()
