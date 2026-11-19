#!/usr/bin/env python3
"""Static source hotfix validation only; NOT a C# build or game test."""
from pathlib import Path
root=Path(__file__).resolve().parents[1]
stub=(root/'src/MovementTests/TestDoubles.cs').read_text(encoding='utf-8-sig')
bridge=(root/'src/Client/CharacterMovementBridge.cs').read_text(encoding='utf-8-sig')
project=(root/'src/MovementTests/MovementTests.csproj').read_text(encoding='utf-8-sig')
checks={
    'TEST_STUB_HARMONY_PRIORITY_FIELD':'public int priority = Priority.Normal;' in stub,
    'TEST_STUB_HARMONY_LAST':'public const int Last = 0;' in stub,
    'TEST_STUB_HARMONY_NORMAL':'public const int Normal = 400;' in stub,
    'PRODUCTION_PRIORITY_LAST_PRESERVED':'lateVisual.priority=Priority.Last;' in bridge and 'lateMotion.priority=Priority.Last;' in bridge,
    'MOVEMENT_TEST_LINKS_PRODUCTION_BRIDGE':'../Client/CharacterMovementBridge.cs' in project,
    'NO_GAMEPLAY_STUB_IN_PRODUCTION_PROJECT':'../MovementTests/TestDoubles.cs' not in (root/'src/Client/GigaPMC.Client.csproj').read_text(encoding='utf-8-sig'),
}
for key,ok in checks.items():
    if not ok:raise SystemExit('FAIL|'+key)
    print('PASS|'+key)
print('P205_SOURCE_CHECK_PASS|STATIC_ONLY|NOT_COMPILE_PASS')
