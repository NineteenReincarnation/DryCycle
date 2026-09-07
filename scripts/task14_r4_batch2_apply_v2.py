from pathlib import Path

path = Path('scripts/task14_r4_batch2_apply.py')
source = path.read_text(encoding='utf-8')

replacements = {
    "r'bat\\\\.AI\\\\.localGoal\\\\s*(?:\\\\+=|=)'": "r'bat\\.AI\\.localGoal\\s*(?:\\+=|=)'",
    "r'bat\\\\.AI\\\\.localGoal\\\\s*\\\\+=\\\\s*(.*?);'": "r'bat\\.AI\\.localGoal\\s*\\+=\\s*(.*?);'",
    "r'bat\\\\.AI\\\\.localGoal\\\\s*=\\\\s*(.*?);'": "r'bat\\.AI\\.localGoal\\s*=\\s*(.*?);'",
    "bat.AI.localGoal + (\\\\1)": "bat.AI.localGoal + (\\1)",
    "DB_BehaviorOwner.Combat, \\\\1);": "DB_BehaviorOwner.Combat, \\1);",
    "r'bat\\\\.AI\\\\.localGoal\\\\s*(?:\\\\+=|=)'": "r'bat\\.AI\\.localGoal\\s*(?:\\+=|=)'",
}

changed = 0
for old, new in replacements.items():
    if old in source:
        source = source.replace(old, new)
        changed += 1

if changed < 4:
    raise SystemExit(f'expected to repair at least 4 regex fragments, repaired {changed}')

code = compile(source, str(path), 'exec')
namespace = {'__name__': '__main__', '__file__': str(path)}
exec(code, namespace)

self_path = Path('scripts/task14_r4_batch2_apply_v2.py')
if self_path.exists():
    self_path.unlink()
