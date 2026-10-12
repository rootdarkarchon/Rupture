"""Run with the numpy-enabled Python runtime after the local calibration sweep."""
import importlib.util
import json
from pathlib import Path
import tempfile

root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('calibration', root/'scripts/Calibrate-LimbClothing.py')
calibration = importlib.util.module_from_spec(spec)
spec.loader.exec_module(calibration)
result = json.loads((root/'artifacts/cloth-calibration.json').read_text())
review = json.loads((root/'docs/limb-clothing-review.json').read_text())
profiles = calibration.reduce(result['samples'], review)
assert len(profiles) == 24
assert profiles == result['profiles'], 'Stored calibration is not reproducible'
for p in profiles:
    assert len(p['specimen']) == 2
    assert p['mesh'] == ('Mannequin_legs_LOD0' if p['group'].startswith('leg/') else 'Mannequin_body_LOD0')
    assert abs(p['threshold'] / p['diameter'] - 1.10) < 1e-12
    assert 0 < p['minor'] <= p['diameter'] and p['boneLength'] > 0

# A bizarre tiny garment must not change anatomical dimensions or thresholds.
samples = [dict(row) for row in result['samples']]
tiny = dict(samples[0], source='synthetic-tiny-outlier', mesh='tiny-clothing', diameter=.001, minor=.0005)
samples.append(tiny)
with_outlier = calibration.reduce(samples, review)
for a, b in zip(profiles, with_outlier):
    for field in ['group', 'diameter', 'minor', 'threshold', 'boneLength', 'source']:
        assert a[field] == b[field], 'A clothing outlier contaminated mannequin anatomy'

# Missing reference geometry is a calibration failure, never a fallback to the
# smallest garment, a mirrored guess, or a hard-coded human dimension.
missing = [r for r in result['samples'] if not (r['mesh'] == 'Mannequin_body_LOD0' and r['side'] == 'RightArm')]
try:
    calibration.reduce(missing, review)
    raise AssertionError('Missing mirrored mannequin accepted')
except ValueError as error:
    assert 'Missing mirrored mannequin' in str(error)
with tempfile.TemporaryDirectory(prefix='rpt-cloth-profile-') as directory:
    output = Path(directory)/'profiles.cs'
    calibration.emit_cs(profiles, review, output, result['reportSha256'])
    expected = root/'unity/RuptureWound/Assets/Rupture/Limbs/LimbClothingProfiles.Generated.cs'
    assert output.read_text() == expected.read_text(), 'Compiled profile table differs from measured/reviewed data'
print('PASS: 24 mirrored mannequin profiles, exact 110% thresholds, outlier isolation, missing-reference failure and reproducible runtime table')
