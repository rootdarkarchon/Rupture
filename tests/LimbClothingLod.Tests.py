"""Validate reference-core continuity in a completed all-LOD harness report."""
import json
import sys
from collections import defaultdict
from pathlib import Path

report = json.loads(Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
assert report['State'] == 'Complete', 'LOD sweep did not complete'
assert report['CompletedCases'] == report['ExpectedCases']
checked = 0
for case in report['Cases']:
    if case['Status'] != 'Passed':
        continue
    tiers = defaultdict(list)
    for renderer in case['Renderers']:
        if renderer['Status'] == 'Passed':
            tiers[renderer.get('LodLevel', 0)].extend(
                d for d in renderer.get('Clothing') or [] if d['Face'] == 'body')
    references = [d for d in tiers[0] if d['Status'] == 'folded']
    if not references:
        continue
    assert len(references) == 1, f"Multiple reference cores: {case['Id']}"
    reference = references[0]
    for level, diagnostics in tiers.items():
        if level == 0 or not diagnostics:
            continue
        cores = [d for d in diagnostics if d['Status'] == 'shared-core']
        assert len(cores) == 1, f"LOD{level} lost its reference core: {case['Id']}: {diagnostics}"
        for dimension in ('MajorWorld', 'MinorWorld'):
            assert abs(cores[0][dimension] - reference[dimension]) < 0.00001, (
                f"LOD{level} changed {dimension}: {case['Id']}")
        checked += 1
assert checked > 0, 'No folded LOD transitions were exercised'
print(f'PASS: {checked} folded LOD transitions retain one core with matching anatomical dimensions')
