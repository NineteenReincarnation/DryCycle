from pathlib import Path

p = Path('scripts/task14_r4_b4_combat_selection_apply.py')
s = p.read_text(encoding='utf-8')

old = "remove_method(path, '    private bool GriefAllowsHarass()')"
new = """text = read(path)
grief = '''    private bool GriefAllowsHarass() => !fly.Injury.BlocksCombat &&\n        (fly.DesertState.GriefStrength <= 0f || fly.DesertState.Thirst * fly.DesertState.GriefAttackScale >= DesertBatflyTuning.ObserveThirst) &&\n        (fly.Injury.AggressionScale >= 0.99f || fly.DesertState.Thirst * fly.Injury.AggressionScale >= DesertBatflyTuning.ObserveThirst);\n\n'''
text = replace_once(text, grief, '', 'remove expression-bodied GriefAllowsHarass')
write(path, text)"""
if old not in s:
    raise SystemExit('B4 v2 Grief removal anchor missing')
s = s.replace(old, new, 1)

s = s.replace(
    "Path('scripts/task14_r4_b4_combat_selection_apply.py').unlink()\n",
    "Path('scripts/task14_r4_b4_combat_selection_apply.py').unlink()\nPath('scripts/task14_r4_b4_combat_selection_apply_v2.py').unlink()\n",
    1)

p.write_text(s, encoding='utf-8')
exec(compile(s, str(p), 'exec'), {'__name__': '__main__'})
