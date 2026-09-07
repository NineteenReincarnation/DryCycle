from pathlib import Path

p = Path('scripts/task14_r4_b3_combat_apply.py')
s = p.read_text(encoding='utf-8')

# Preserve old semantics while moving the last combat-transient cleanup writes out of AI.
s = s.replace(
"""    internal void ClearAttackState()\n    {\n        hasSlot = false;\n        attachedChunk = null;\n        attachOffset = Vector2.zero;\n        retaliationDirection = Vector2.zero;\n        drainedWater = 0f;\n        interest = 0;\n        unseen = 0;\n    }\n""",
"""    internal void ClearAttackState()\n    {\n        hasSlot = false;\n        attachedChunk = null;\n        attachOffset = Vector2.zero;\n        retaliationDirection = Vector2.zero;\n        drainedWater = 0f;\n        interest = 0;\n        unseen = 0;\n    }\n\n    internal void ClearVisibilityTracking() => unseen = 0;\n""",
1)

needle = """# Ensure no moved execution/contact state leaked in the AI shell.\nai_text = read(path)\n"""
cleanup = """# Final cleanup of combat-local transient resets that live outside the moved execution methods.\nai_text = read(path)\nai_text = ai_text.replace('        memory = retreat = pursuit = unseen = 0;\\n', '        memory = retreat = pursuit = 0;\\n')\nai_text = ai_text.replace('        pursuit = 0;\\n        unseen = 0;\\n', '        pursuit = 0;\\n        combat.ClearVisibilityTracking();\\n')\nai_text = ai_text.replace(\n    '        hasRoost = false;\\n        hasSlot = false;\\n        attachedChunk = null;\\n        Target = null;\\n',\n    '        hasRoost = false;\\n        combat.ClearAttackState();\\n        Target = null;\\n')\nai_text = ai_text.replace(\n    '                brain.hasRoost = false;\\n                brain.hasSlot = false;\\n                brain.attachedChunk = null;\\n                brain.Target = null;\\n                brain.drainedWater = 0f;\\n                brain.interest = 0;\\n',\n    '                brain.hasRoost = false;\\n                brain.combat.ClearAttackState();\\n                brain.Target = null;\\n')\nwrite(path, ai_text)\n\n# Ensure no moved execution/contact state leaked in the AI shell.\nai_text = read(path)\n"""
if needle not in s:
    raise SystemExit('B3 v2 patch anchor missing')
s = s.replace(needle, cleanup, 1)

# Self-cleanup must also remove this wrapper.
s = s.replace(
"Path('scripts/task14_r4_b3_combat_apply.py').unlink()\n",
"Path('scripts/task14_r4_b3_combat_apply.py').unlink()\nPath('scripts/task14_r4_b3_combat_apply_v2.py').unlink()\n",
1)

p.write_text(s, encoding='utf-8')
exec(compile(s, str(p), 'exec'), {'__name__': '__main__'})
