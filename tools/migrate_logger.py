import os

files = [
    r'C:\Users\adimn\WorkBuddy\20260424122117\MapOddsTracker\Scripts\MonsterImageMapper.cs',
    r'C:\Users\adimn\WorkBuddy\20260424122117\MapOddsTracker\Scripts\MapTracker.cs',
    r'C:\Users\adimn\WorkBuddy\20260424122117\MapOddsTracker\Scripts\Entry.cs',
    r'C:\Users\adimn\WorkBuddy\20260424122117\MapOddsTracker\Scripts\UI\MonsterTooltip.cs',
]

replacements = [
    ('GD.Print($"[MapOddsTracker] ',  'ModLogger.Log($"'),
    ('GD.PrintErr($"[MapOddsTracker] ', 'ModLogger.LogErr($"'),
    ('GD.Print("[MapOddsTracker] ',   'ModLogger.Log("'),
    ('GD.PrintErr("[MapOddsTracker] ',  'ModLogger.LogErr("'),
]

for f in files:
    with open(f, 'r', encoding='utf-8') as fh:
        content = fh.read()
    for old, new in replacements:
        content = content.replace(old, new)
    with open(f, 'w', encoding='utf-8', newline='\n') as fh:
        fh.write(content)
    remaining = content.count('GD.Print(') + content.count('GD.PrintErr(')
    print(f'{os.path.basename(f)}: {remaining} GD.Print/Err remaining')
