name: DesertBatfly R6 Vengeance Owner Fix

on:
  push:
    branches:
      - task14-r6-cohesion-pass

permissions:
  contents: write

jobs:
  fix:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          ref: task14-r6-cohesion-pass
          fetch-depth: 0
      - name: Fix remaining Vengeance FlightMotor owner
        shell: bash
        run: |
          set -euo pipefail
          python3 - <<'PY'
          from pathlib import Path
          path = Path('src/Creatures/DesertBatfly/Behavior/DB_VengeanceRuntime.cs')
          text = path.read_text(encoding='utf-8')
          old = '            DB_BehaviorOwner.Mode,\n            goal,\n            speed,\n            response: 0.28f);'
          new = '            DB_BehaviorOwner.Vengeance,\n            goal,\n            speed,\n            response: 0.28f);'
          count = text.count(old)
          if count != 1:
              raise SystemExit(f'expected exactly one remaining FlightMotor owner match, found {count}')
          text = text.replace(old, new, 1)
          path.write_text(text, encoding='utf-8')
          PY
          ! grep -R -F -n 'DB_BehaviorOwner.Mode' src/Creatures/DesertBatfly
          grep -A5 -F 'DB_FlightMotor.TrySteer(' src/Creatures/DesertBatfly/Behavior/DB_VengeanceRuntime.cs | grep -F 'DB_BehaviorOwner.Vengeance'
          bash scripts/check-desertbatfly-r5-retention.sh
          bash scripts/check-desertbatfly-r6-b1.sh
      - name: Commit source fix
        shell: bash
        run: |
          set -euo pipefail
          git config user.name "github-actions[bot]"
          git config user.email "41898282+github-actions[bot]@users.noreply.github.com"
          git add src/Creatures/DesertBatfly/Behavior/DB_VengeanceRuntime.cs
          git diff --cached --check
          git commit -m "R6 fix Vengeance FlightMotor owner"
          git push origin HEAD:task14-r6-cohesion-pass
