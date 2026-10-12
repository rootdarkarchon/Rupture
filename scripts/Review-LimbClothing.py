"""Render local extracted mesh silhouettes and measured loops for calibration review.

This is a geometry evidence sheet, not an EFT material preview. numpy + Pillow.
"""
import argparse
import gzip
import json
import math
from pathlib import Path
import numpy as np
from PIL import Image, ImageDraw


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('measurements', type=Path)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--all', action='store_true')
    args = parser.parse_args()
    data = json.loads(args.measurements.read_text())
    selected = {}
    for row in data['samples']:
        if args.all or row.get('smallOutlierFlag') or any(p['source'] == row['source'] for p in data['profiles']):
            selected[row['source']] = row
    width = 420; height = 580; columns = 4
    image = Image.new('RGB', (width*columns, height*math.ceil(len(selected)/columns)), '#17202a')
    draw = ImageDraw.Draw(image)
    for index, row in enumerate(sorted(selected.values(), key=lambda r: r['mesh'])):
        ox = (index % columns)*width; oy = (index // columns)*height
        path = Path(row['capture']).parent/('source-'+row['source']+'.json.gz')
        with gzip.open(path, 'rt') as f:
            source = json.load(f)
        points = np.array(source['Positions'])
        triangles = np.array(source['Triangles'])[:, :3]
        # Imported EFT garment meshes are Z-up in renderer bind space.
        projected = points[:, [0, 2]]
        low = projected.min(axis=0); high = projected.max(axis=0)
        factor = min(380/max(high[0]-low[0], .1), 350/max(high[1]-low[1], .1))
        p2 = (projected-(low+high)/2)*factor
        p2[:, 0] += ox+width/2; p2[:, 1] = oy+235-p2[:, 1]
        normals = np.cross(points[triangles[:, 1]]-points[triangles[:, 0]], points[triangles[:, 2]]-points[triangles[:, 0]])
        normals /= np.maximum(np.linalg.norm(normals, axis=1)[:, None], 1e-12)
        shades = .30+.70*abs(normals @ np.array([.25, .85, .46]))
        for i in np.argsort(points[triangles, 1].mean(axis=1)):
            shade = int(np.clip(shades[i]*210, 30, 245))
            draw.polygon([tuple(v) for v in p2[triangles[i]]], fill=(shade, shade, shade))
        draw.text((ox+8, oy+8), row['mesh'][:52], fill='white')
        draw.text((ox+8, oy+26), row['source'][:16], fill='#8fa8c1')
        draw.text((ox+8, oy+405), 'Bind-space extent (m): '+str(np.round(high-low,3)), fill='white')
        samples = list({(r['level'],r['face']):r for r in data['samples'] if r['source'] == row['source'] and r['offset'] == 0 and r['side'].startswith('Left')}.values())
        for i, sample in enumerate(samples):
            poly = np.array(sample['polygon'])*220
            cx = ox+55+i*100; cy = oy+485
            shape = [(cx+p[0], cy-p[1]) for p in poly]
            draw.line(shape+[shape[0]], fill='#faac6d', width=2)
            draw.ellipse((cx-2, cy-2, cx+2, cy+2), fill='#7bcaff')
            draw.text((cx-40, oy+532), sample['level'][:4]+'/'+sample['face'], fill='white')
            draw.text((cx-40, oy+546), str(round(sample['diameter']*1000,1))+' mm', fill='#faac6d')
    args.output.parent.mkdir(parents=True, exist_ok=True)
    if args.all:
        for top in range(0, image.height, height*4):
            image.crop((0, top, image.width, min(top+height*4,image.height))).save(args.output.with_name(args.output.stem+'-'+str(top//(height*4))+args.output.suffix))
    else:
        image.save(args.output)
    print(args.output, len(selected), 'meshes')


if __name__ == '__main__':
    main()
