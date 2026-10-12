"""Reduce fixed-candidate Unity captures to reproducible limb profiles.

Game-derived captures and review images stay in artifacts. Only numeric profiles,
source fingerprints and explicit review decisions are release/source inputs.
Requires numpy (available in the Codex workspace Python runtime).
"""
import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path
import numpy as np


def inside(poly, p=(0., 0.)):
    hit = False
    a = poly[-1]
    for b in poly:
        if (a[1] > p[1]) != (b[1] > p[1]):
            if p[0] < (b[0]-a[0])*(p[1]-a[1])/(b[1]-a[1])+a[0]:
                hit = not hit
        a = b
    return hit


def frame(axis):
    ref = np.array([0., 1., 0.]) if abs(axis[1]) < .8 else np.array([1., 0., 0.])
    u = np.cross(axis, ref)
    u /= np.linalg.norm(u)
    return np.stack([u, np.cross(axis, u)], axis=1)


def measure(report):
    rows = []
    for case in report['Cases']:
        if case['Status'] != 'Passed':
            continue
        for renderer in case['Renderers']:
            if not renderer.get('Capture'):
                continue
            with gzip.open(renderer['Capture'], 'rt') as f:
                capture = json.load(f)
            cut = capture['Cuts'][0]
            if not cut['IncludeAnatomicalDetails']:
                continue
            axis = np.array(cut['Axis']); scale = cut['WorldScale']
            center = np.array(cut['Center']); basis = frame(axis)
            start = np.array(cut['StartPosition'] if cut.get('StartPosition') is not None else capture['Bones'][cut['StartIndex']]['Position'])
            end = np.array(cut['EndPosition'] if cut.get('EndPosition') is not None else capture['Bones'][cut['EndIndex']]['Position'])
            length = float(np.linalg.norm(end-start)*scale)
            for side in capture['Boundaries']:
                choices = []
                for loop in side['Loops']:
                    poly = (np.array(side['Positions'])[loop[1:]]-center) @ basis
                    area = abs(np.sum(poly[:, 0]*np.roll(poly[:, 1], -1)-poly[:, 1]*np.roll(poly[:, 0], -1)))/2
                    contains = inside(poly)
                    score = (1000000 if contains else 0)+math.sqrt(area)*100-float(np.linalg.norm(poly.mean(axis=0)))*10
                    choices.append((score, poly, contains))
                if not choices:
                    continue
                _, poly, contains = max(choices, key=lambda c: c[0])
                poly *= scale
                d = poly[:, None]-poly[None, :]
                distances = np.sum(d*d, axis=2)
                i, j = np.unravel_index(np.argmax(distances), distances.shape)
                diameter = math.sqrt(float(distances[i, j]))
                if diameter <= 0 or length <= 0:
                    continue
                major = (poly[i]-poly[j])/diameter
                minor = np.array([-major[1], major[0]])
                width = float(np.ptp(poly @ minor))
                rows.append(dict(case=case['Id'], bundle=case['Bundle'], mesh=renderer['Mesh'],
                    source=capture['Source'].removeprefix('source-').removesuffix('.json.gz'),
                    capture=renderer['Capture'], leg=cut['IsLeg'], level=cut['Level'], face=side['Side'],
                    offset=round(cut['FractionOffset'], 2), side=case['Side'], diameter=diameter,
                    minor=width, angle=math.atan2(float(major[1]), float(major[0])),
                    boneLength=length, containsBone=contains, polygon=poly.tolist()))
    return rows


def key(row):
    return ('leg' if row['leg'] else 'arm')+'/'+row['level']+'/'+row['face']+'/'+format(row['offset'], '.2f')


def reduce(rows, decisions):
    profiles = []
    for group in sorted({key(r) for r in rows}):
        samples = [r for r in rows if key(r) == group and r['containsBone']]
        if not samples:
            raise ValueError('No bone-containing sample for '+group)
        # Mirrored sides and reused source meshes must not inflate the statistics.
        unique = {r['source']: r for r in samples}
        logs = np.log([r['diameter']/r['boneLength'] for r in unique.values()])
        median = float(np.median(logs)); mad = float(np.median(abs(logs-median)))
        for r in samples:
            r['smallOutlierFlag'] = math.log(r['diameter']/r['boneLength']) < median-3*1.4826*max(mad, .01)
            r['excluded'] = any(e['source'] == r['source'] and
                (not e.get('groups') or group in e['groups']) for e in decisions.get('excluded', []))
        accepted = [r for r in samples if not r['excluded']]
        if not accepted:
            raise ValueError('All samples excluded for '+group)
        reference = float(np.median([r['boneLength'] for r in accepted]))
        # The user selected the bare upper/lower mannequin as anatomical truth.
        # Other garments remain validation samples; small outfits cannot pull the
        # runtime anatomy down. Pool only the two mirrored mannequin measurements.
        mannequin = 'Mannequin_legs_LOD0' if group.startswith('leg/') else 'Mannequin_body_LOD0'
        baseline = list({r['side']:r for r in accepted if r['mesh'] == mannequin}.values())
        if len(baseline) != 2:
            raise ValueError('Missing mirrored mannequin baseline for '+group)
        specimen = baseline[0]
        reference = float(np.mean([r['boneLength'] for r in baseline]))
        diameter = float(np.mean([r['diameter']/r['boneLength']*reference for r in baseline]))
        minor = float(np.mean([r['minor']/r['boneLength']*reference for r in baseline]))
        profiles.append(dict(group=group, diameter=diameter,
            minor=minor, boneLength=reference, specimen=[r['case'] for r in baseline],
            source=specimen['source'], bundle=specimen['bundle'], mesh=specimen['mesh'],
            threshold=diameter*1.10, count=len(accepted),
            flagged=sum(r['smallOutlierFlag'] for r in samples)))
    return profiles


def emit_cs(profiles, decisions, path, provenance):
    lines = ['// Generated by scripts/Calibrate-LimbClothing.py. Do not hand-edit dimensions.',
        '// Calibration SHA256: '+provenance,
        'namespace Rupture.Dismemberment.Classes.Limbs', '{',
        'internal static partial class LimbClothingProfiles', '{',
        '    internal static readonly Profile[] Values = new[]', '    {']
    for p in profiles:
        kind, level, face, offset = p['group'].split('/')
        lines.append('        new Profile('+str(kind=='leg').lower()+', LimbSeverLevel.'+level+', '+
            str(face=='gib').lower()+', '+offset+'f, '+', '.join(format(p[k], '.9f')+'f' for k in ['diameter','minor','boneLength'])+'),')
    lines += ['    };', '    internal static readonly BareRegion[] Bare = new[]', '    {']
    bare = decisions.get('bare', [])
    for item in bare:
        lines.append('        new BareRegion('+json.dumps(item['mesh'])+', '+json.dumps(item['source'])+', '+
            str(item['leg']).lower()+', LimbSeverLevel.'+item['level']+', '+str(item['gib']).lower()+'),')
    if not bare:
        lines[-2:] = ['    internal static readonly BareRegion[] Bare = new BareRegion[]', '    {']
    lines += ['    };', '}', '}', '']
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text('\n'.join(lines), encoding='utf-8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('report', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--review', type=Path)
    parser.add_argument('--emit-cs', type=Path)
    parser.add_argument('--supplement', type=Path)
    args = parser.parse_args()
    report = json.loads(args.report.read_text())
    supplement_hash = None
    if args.supplement:
        supplement = json.loads(args.supplement.read_text())
        if supplement['State'] != 'Complete' or supplement['CatalogHash'] != report['CatalogHash']:
            raise ValueError('Incomplete or mismatched supplemental capture sweep')
        if {s['File']:s['CompiledHash'] for s in report['Sources']} != {s['File']:s['CompiledHash'] for s in supplement['Sources']}:
            raise ValueError('Supplement used different cutter algorithms')
        replacements = {c['Id']:c for c in supplement['Cases']}
        for i, case in enumerate(report['Cases']):
            if case['Id'] in replacements:
                if case['Stage'] != 'diagnostic-export':
                    raise ValueError('Only missing diagnostic exports may be replaced')
                report['Cases'][i] = replacements.pop(case['Id'])
        if replacements:
            raise ValueError('Unknown cases in supplement')
        supplement_hash = hashlib.sha256(args.supplement.read_bytes()).hexdigest()
    if report['State'] != 'Complete':
        raise ValueError('Calibration requires a completed sweep')
    if args.emit_cs and (not report.get('Calibration') or report['ExpectedCases'] != 2928):
        raise ValueError('Runtime profiles require the full fixed-offset calibration sweep')
    decisions = json.loads(args.review.read_text()) if args.review else {}
    for item in decisions.get('excluded', [])+decisions.get('bare', []):
        if not item.get('reason') or not item.get('evidence'):
            raise ValueError('Every review decision needs a reason and evidence')
    rows = measure(report)
    profiles = reduce(rows, decisions)
    provenance = hashlib.sha256(args.report.read_bytes()).hexdigest()
    result = dict(schema=1, reportSha256=provenance, catalogHash=report['CatalogHash'],
        supplementSha256=supplement_hash,
        rule='mean mirrored bare mannequin diameter normalized by bone length; loose above 110%; garments validate only',
        profiles=profiles, review=decisions, samples=rows)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, indent=2)+'\n')
    if args.emit_cs:
        if len(profiles) != 24:
            raise ValueError('Expected all 24 limb/face/offset profiles')
        emit_cs(profiles, decisions, args.emit_cs, provenance)
    for p in profiles:
        print(p['group'], 'mannequin/threshold mm', round(p['diameter']*1000,2), round(p['threshold']*1000,2), p['mesh'], 'flagged', p['flagged'])


if __name__ == '__main__':
    main()
