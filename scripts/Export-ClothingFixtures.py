"""Read installed EFT bundles; emit private, lossless cutter fixtures (LOD0 or all LODs).

Requires UnityPy==1.25.3. Never writes to the game directory. Coordinates and
indices remain in Unity mesh space (unlike OBJ/FBX interchange exporters).
"""
import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path

import UnityPy
from UnityPy.helpers.MeshHelper import MeshHandler
from UnityPy.classes import PPtr


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def vector(value, size=3):
    return [value[k] for k in "xyzw"[:size]]


def channel(values, descriptor):
    """MeshHandler exposes integer storage for normalized vertex formats."""
    if not values:
        return []
    fmt = descriptor["format"]
    if fmt in (2, 4):  # UNorm8, UNorm16
        divisor = 255.0 if fmt == 2 else 65535.0
        return [[v / divisor for v in row] for row in values]
    if fmt in (3, 5):  # SNorm8, SNorm16
        divisor = 127.0 if fmt == 3 else 32767.0
        return [[max(-1.0, v / divisor) for v in row] for row in values]
    return [list(row) for row in values]


def extract(path, bundle_root, manifest, bundle_name, all_lods=False):
    env = UnityPy.load(str(path))
    prefab_objects = list(env.objects)
    objects = {o.path_id: o for o in prefab_objects}
    prefab_file = prefab_objects[0].assets_file
    loaded = {bundle_name}

    def dependencies(name):
        for dependency in manifest.get(name, {}).get("Dependencies", []):
            # Geometry fixtures don't need the large global shader/cubemap banks.
            if dependency in loaded or dependency in ("shaders", "cubemaps"):
                continue
            loaded.add(dependency)
            dependencies(dependency)
            env.load_file(str(bundle_root / dependency))

    dependencies(bundle_name)

    def resolve(pointer, context=None):
        return PPtr(**pointer, assetsfile=context or prefab_file).deref()

    def read(pointer, context=None):
        return resolve(pointer, context).read_typetree()

    palettes, transforms, renderer_lods, skin_lods = {}, {}, {}, []
    for obj in prefab_objects:
        if obj.type.name == "MonoBehaviour":
            data = obj.read_typetree()
            if "_bonePaths" in data:
                palettes[data["_skinnedMeshRenderer"]["m_PathID"]] = data["_bonePaths"]
            if "_lods" in data and data["_lods"]:
                skin_lods.extend(enumerate(data["_lods"] if all_lods else data["_lods"][:1]))
        elif obj.type.name == "Transform":
            data = obj.read_typetree()
            transforms[data["m_GameObject"]["m_PathID"]] = data
        elif obj.type.name == "LODGroup":
            data = obj.read_typetree()
            if data["m_LODs"]:
                for level, lod in enumerate(data["m_LODs"] if all_lods else data["m_LODs"][:1]):
                    for r in lod["renderers"]:
                        renderer_lods.setdefault(r["renderer"]["m_PathID"], level)
    if not renderer_lods:
        for level, ptr in skin_lods:
            skin = read(ptr)
            if "_skinnedMeshRenderer" in skin:
                renderer_lods.setdefault(skin["_skinnedMeshRenderer"]["m_PathID"], level)
    if not renderer_lods:
        raise ValueError("No explicit LOD0 renderer references (LODGroup or skin _lods)")
    output = []
    for rid in sorted(renderer_lods, key=lambda rid: (renderer_lods[rid], rid)):
        obj = objects[rid]
        if obj.type.name != "SkinnedMeshRenderer":
            raise ValueError("LOD contains unsupported renderer " + obj.type.name)
        renderer = obj.read_typetree()
        mesh_ptr = renderer["m_Mesh"]
        mesh_object = resolve(mesh_ptr, obj.assets_file)
        raw = mesh_object.read_typetree()
        mesh = mesh_object.read()
        helper = MeshHandler(mesh)
        helper.process()
        descriptors = raw["m_VertexData"]["m_Channels"]
        n = len(helper.m_Vertices)
        bones = palettes.get(rid, [])
        bindposes = [[p[f"e{r}{c}"] for c in range(4) for r in range(4)] for p in raw["m_BindPose"]]
        weights = channel(helper.m_BoneWeights, descriptors[12])
        indices = [list(row) for row in helper.m_BoneIndices or []]
        if not bones:
            raise ValueError(f"{mesh.m_Name}: missing bone palette")
        # Keep mismatches intact: the editor must spawn the garment and report
        # the source-capture rejection, never silently repair/reindex its rig.
        if len(weights) != n or len(indices) != n:
            raise ValueError(f"{mesh.m_Name}: missing per-vertex skinning data")
        if any(abs(sum(row) - 1) > 0.002 for row in weights):
            raise ValueError(f"{mesh.m_Name}: non-normalized weights after storage conversion")
        if any(w > 0 and not 0 <= i < len(bones) for ids, ws in zip(indices, weights) for i, w in zip(ids, ws)):
            raise ValueError(f"{mesh.m_Name}: weighted bone index outside palette")
        submeshes = [[i for triangle in sub for i in triangle] for sub in helper.get_triangles()]
        if any(i < 0 or i >= n for sub in submeshes for i in sub):
            raise ValueError(f"{mesh.m_Name}: triangle index outside vertex buffer")
        shapes = []
        raw_shapes = raw["m_Shapes"]
        for ch in raw_shapes["channels"]:
            for frame_index in range(ch["frameIndex"], ch["frameIndex"] + ch["frameCount"]):
                shape = raw_shapes["shapes"][frame_index]
                deltas = [[[0.0, 0.0, 0.0] for _ in range(n)] for _ in range(3)]
                for v in raw_shapes["vertices"][shape["firstVertex"]:shape["firstVertex"] + shape["vertexCount"]]:
                    for arr, key in zip(deltas, ("vertex", "normal", "tangent")):
                        arr[v["index"]] = vector(v[key])
                shapes.append(dict(Name=ch["name"], Weight=raw_shapes["fullWeights"][frame_index],
                                   Positions=deltas[0], Normals=deltas[1], Tangents=deltas[2]))
        chain = []
        transform = transforms[renderer["m_GameObject"]["m_PathID"]]
        transform_file = obj.assets_file
        while transform:
            chain.insert(0, dict(Position=vector(transform["m_LocalPosition"]),
                                 Rotation=vector(transform["m_LocalRotation"], 4),
                                 Scale=vector(transform["m_LocalScale"])))
            parent = transform["m_Father"]
            if parent["m_PathID"]:
                parent_object = resolve(parent, transform_file)
                transform, transform_file = parent_object.read_typetree(), parent_object.assets_file
            else:
                transform = None
        source = dict(Name=mesh.m_Name, Positions=channel(helper.m_Vertices, descriptors[0]),
                      Normals=channel(helper.m_Normals, descriptors[1]),
                      Tangents=channel(helper.m_Tangents, descriptors[2]),
                      Colors=channel(helper.m_Colors, descriptors[3]),
                      Uv0=channel(helper.m_UV0, descriptors[4]), Uv1=channel(helper.m_UV1, descriptors[5]),
                      Uv2=channel(helper.m_UV2, descriptors[6]), Uv3=channel(helper.m_UV3, descriptors[7]),
                      Weights=weights, BoneIndices=indices, Bindposes=bindposes, Submeshes=submeshes,
                      BlendShapes=shapes)
        if any(not math.isfinite(x) for key in ("Positions", "Normals", "Tangents", "Bindposes", "Weights") for row in source[key] for x in row):
            raise ValueError(f"{mesh.m_Name}: non-finite vertex/skinning data")
        output.append(dict(Renderer=read(renderer["m_GameObject"])["m_Name"], PathId=str(rid), LodLevel=renderer_lods[rid],
                           UnityVersion=obj.assets_file.unity_version, Readable=raw["m_IsReadable"],
                           BonePaths=bones, TransformChain=chain, Mesh=source))
    return output


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--filter", default="", help="Optional name/path substring for a small probe")
    parser.add_argument("--all-lods", action="store_true", help="Include all explicitly referenced renderer LODs")
    args = parser.parse_args()
    if UnityPy.__version__ != "1.25.3":
        raise RuntimeError("Use UnityPy==1.25.3; the fixture decoder is validated against that version")
    game, out = args.game.resolve(), args.output.resolve()
    if out == game or game in out.parents:
        raise ValueError("Fixtures must be outside the installed game")
    out.mkdir(parents=True, exist_ok=True)
    catalog_path = game / "SPT/SPT_Data/database/templates/customization.json"
    catalog = json.loads(catalog_path.read_text(encoding="utf-8-sig"))
    bundle_root = game / "EscapeFromTarkov_Data/StreamingAssets/Windows"
    dependencies_manifest = json.loads((bundle_root / "Windows.json").read_text(encoding="utf-8-sig"))
    grouped = {}
    for key, item in catalog.items():
        props = item.get("_props", {})
        part = props.get("BodyPart")
        if part not in ("Body", "Feet"):
            continue
        prefab = props.get("Prefab", {})
        bundle = prefab.get("path") if isinstance(prefab, dict) else None
        if not bundle:
            continue
        record = grouped.setdefault((part, bundle), dict(Part=part, Bundle=bundle, Names=[], TemplateIds=[]))
        record["Names"].append(item["_name"])
        record["TemplateIds"].append(key)
    manifest = dict(Schema=1, UnityPy=UnityPy.__version__, Game=str(game), CatalogHash=digest(catalog_path), Garments=[])
    for (part, bundle), entry in sorted(grouped.items()):
        if not any(term in (bundle + " " + " ".join(entry["Names"])).lower() for term in args.filter.lower().split('|')):
            continue
        path = game / "EscapeFromTarkov_Data/StreamingAssets/Windows" / bundle
        identity = hashlib.sha256((part + ":" + bundle).encode()).hexdigest()[:16]
        entry.update(Id=identity, Fixture=identity + ".json.gz")
        try:
            entry["BundleHash"] = digest(path)
            renderers = extract(path, bundle_root, dependencies_manifest, bundle, args.all_lods)
            with gzip.open(out / entry["Fixture"], "wt", encoding="utf-8") as stream:
                json.dump(dict(Schema=1, **entry, Renderers=renderers), stream, separators=(",", ":"), allow_nan=False)
            entry["RendererCount"] = len(renderers)
            entry["FixtureHash"] = digest(out / entry["Fixture"])
            entry["Error"] = ""
        except Exception as exc:
            entry["Error"] = f"{type(exc).__name__}: {exc}"
        manifest["Garments"].append(entry)
        print(f"{len(manifest['Garments'])}/{len(grouped)} {bundle.rsplit('/', 1)[-1]}: {entry['Error'] or 'OK'}", flush=True)
    (out / "catalog.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    failures = sum(bool(e["Error"]) for e in manifest["Garments"])
    print(f"Catalog: {len(manifest['Garments'])} garments, {failures} extraction failures (all retained in catalog).")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
