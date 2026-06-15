#!/usr/bin/env python3
"""
fix_buildings_json_decimals.py
Converts European decimal format (zarez) u standardni JSON format (točka)
u svim buildings_*.json fajlovima.

Problem: fix_building_pivots.py piše  "x": 1995,9163
         Unity Newtonsoft.Json očekuje "x": 1995.9163

Pokretanje:
  python fix_buildings_json_decimals.py
  python fix_buildings_json_decimals.py --dry-run
  python fix_buildings_json_decimals.py --dir D:/neki/drugi/path
"""

import re
import shutil
import argparse
from pathlib import Path

# Zamjena zareza s točkom, ali SAMO između znamenki (ne dira strukturne zareze)
# "x": 1995,9163  →  "x": 1995.9163
# }, "y":          →  nepromijenjeno
_EU_DECIMAL_RE = re.compile(r'(-?\d+),(\d+)')


def fix_file(path: Path, backup: bool) -> bool:
    text = path.read_text(encoding='utf-8')
    fixed = _EU_DECIMAL_RE.sub(r'\1.\2', text)

    if fixed == text:
        return False   # fajl je već u ispravnom formatu

    if backup:
        shutil.copy2(path, path.with_suffix('.json.bak'))

    path.write_text(fixed, encoding='utf-8')
    return True


def main():
    ap = argparse.ArgumentParser(
        description="Pretvara europski decimalni format u standardni JSON u buildings_*.json fajlovima.")
    ap.add_argument('--dir', default=None,
        help="Direktorij s buildings_*.json fajlovima (default: isti direktorij kao skripta)")
    ap.add_argument('--no-backup', action='store_true',
        help="Preskoči .bak kopije")
    ap.add_argument('--dry-run', action='store_true',
        help="Prikaži što bi se promijenilo bez pisanja")
    args = ap.parse_args()

    directory = Path(args.dir) if args.dir else Path(__file__).parent
    files = sorted(directory.glob('buildings_*.json'))

    if not files:
        print(f"Nisu pronađeni buildings_*.json fajlovi u: {directory}")
        return

    if args.dry_run:
        print(f"[DRY RUN] Pregled {len(files)} fajlova...\n")

    fixed_count = 0
    ok_count = 0

    for f in files:
        text = f.read_text(encoding='utf-8')
        fixed_text = _EU_DECIMAL_RE.sub(r'\1.\2', text)

        if fixed_text == text:
            ok_count += 1
            continue

        if not args.dry_run:
            if not args.no_backup:
                shutil.copy2(f, f.with_suffix('.json.bak'))
            f.write_text(fixed_text, encoding='utf-8')

        fixed_count += 1

    print(f"Fajlova ispravljeno : {fixed_count}")
    print(f"Već ispravnih       : {ok_count}")
    print(f"Ukupno              : {len(files)}")

    if args.dry_run:
        print("\n[DRY RUN] Pokreni bez --dry-run da primijeniš promjene.")
    elif fixed_count > 0:
        print("\nGotovo. Pokreni Buildings import u Unity.")


if __name__ == '__main__':
    main()
