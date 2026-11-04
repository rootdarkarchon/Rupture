"""Read installed prefab collider transforms into private test fixtures. Requires UnityPy.

No meshes, textures, or game files are modified or redistributed. Generated prefab
poses are evidence for fitting; they are not a live assembled-weapon capture.
"""
import argparse
import hashlib
import json
from pathlib import Path
import UnityPy


def extract(path):
    objects = list(UnityPy.load(str(path)).objects)
    gos = {o.path_id: o.read_typetree() for o in objects if o.type.name == "GameObject"}
    transforms = {o.path_id: o.read_typetree() for o in objects if o.type.name == "Transform"}
    by_go = {t["m_GameObject"]["m_PathID"]: i for i, t in transforms.items()}

    def root_id(i):
        parent = transforms[i]["m_Father"]["m_PathID"]
        return root_id(parent) if parent in transforms else i

    def hierarchy(i):
        parent = transforms[i]["m_Father"]["m_PathID"]
        prefix = hierarchy(parent) + "/" if parent in transforms else ""
        return prefix + gos[transforms[i]["m_GameObject"]["m_PathID"]]["m_Name"]

    result = []
    for root in sorted({root_id(i) for i in transforms}):
        nodes = []
        for i, t in transforms.items():
            if root_id(i) != root:
                continue
            go = gos[t["m_GameObject"]["m_PathID"]]
            nodes.append(dict(id=str(i), parent=str(t["m_Father"]["m_PathID"]), name=go["m_Name"],
                              position=t["m_LocalPosition"], rotation=t["m_LocalRotation"],
                              scale=t["m_LocalScale"], layer=go["m_Layer"]))
        boxes = []
        for o in objects:
            if o.type.name != "BoxCollider":
                continue
            c = o.read_typetree()
            transform = by_go[c["m_GameObject"]["m_PathID"]]
            if root_id(transform) == root:
                boxes.append(dict(node=str(transform), center=c["m_Center"], size=c["m_Size"],
                                  trigger=c["m_IsTrigger"], path=hierarchy(transform)))
        if boxes:
            result.append(dict(name=hierarchy(root), root=str(root), nodes=nodes, boxes=boxes,
                               bundle=str(path), sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    weapons = Path(args.game) / "EscapeFromTarkov_Data/StreamingAssets/Windows/assets/content/weapons"
    names = ["toz106/weapon_toz_toz-106_20g_container.bundle", "uzi/weapon_iwi_uzi_9x19_container.bundle",
             "aks74u/weapon_izhmash_aks74ub_545x39_container.bundle", "ak74/weapon_izhmash_ak74n_545x39_container.bundle"]
    fixtures = [f for name in names for f in extract(weapons / name)]
    Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    Path(args.output).write_text(json.dumps(dict(fixtures=fixtures), indent=2), encoding="utf-8")
    print(f"Exported {len(fixtures)} authored prefab fixtures to {args.output}")
