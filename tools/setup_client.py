"""
Prepare client/ as a Unity project based on the installed editor's "Universal 3D" (URP) template.

Why: the client folder already contains our scripts, so Unity Hub can't create a new project there.
This copies the template's Packages/manifest.json, ProjectSettings and URP settings assets from the
installed editor (so package versions always match that editor), then adds our local shared package.

Usage:  python -I tools/setup_client.py [--editor "C:/Program Files/Unity/Hub/Editor/6000.x.y"]
Then:   Unity Hub > Add > Add project from disk > select rpg-game-a/client
"""
import argparse
import json
import re
import shutil
import sys
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CLIENT = ROOT / "client"
HUB_EDITORS = Path("C:/Program Files/Unity/Hub/Editor")
EXTRA_DEPS = {
    "com.rpg.shared": "file:../../shared",
}
REQUIRED = ["com.unity.render-pipelines.universal", "com.unity.inputsystem"]


def find_editor(explicit):
    if explicit:
        return Path(explicit)
    if not HUB_EDITORS.exists():
        sys.exit("Unity Hub 에디터 폴더가 없어: " + str(HUB_EDITORS))
    cands = [p for p in HUB_EDITORS.iterdir() if p.name.startswith("6000.")]
    if not cands:
        sys.exit("Unity 6 (6000.x) 에디터를 찾지 못했어.")

    def key(p):
        return [int(x) if x.isdigit() else 0 for x in re.split(r"[.a-z]+", p.name)]

    return sorted(cands, key=key)[-1]


def find_template(editor):
    tdir = editor / "Editor" / "Data" / "Resources" / "PackageManager" / "ProjectTemplates"
    # Name changed across Unity 6 releases: urp-blank (6000.0) -> 3d-cross-platform (6000.3, URP based).
    tgzs = (
        sorted(tdir.glob("com.unity.template.urp-blank*.tgz"))
        or sorted(tdir.glob("com.unity.template.universal*.tgz"))
        or sorted(tdir.glob("com.unity.template.3d-cross-platform*.tgz"))
    )
    if not tgzs:
        sys.exit("URP 템플릿(.tgz)을 찾지 못했어: " + str(tdir))
    return tgzs[-1]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--editor")
    args = ap.parse_args()

    editor = find_editor(args.editor)
    tgz = find_template(editor)
    print("editor  :", editor)
    print("template:", tgz.name)

    with tarfile.open(tgz, "r:gz") as tf:
        members = tf.getmembers()
        prefix = "package/ProjectData~/"

        def extract(sub, dest):
            n = 0
            for m in members:
                if not m.name.startswith(prefix + sub) or not m.isfile():
                    continue
                rel = m.name[len(prefix):]
                out = CLIENT / rel
                # Never overwrite our own scripts.
                if out.exists() and sub.startswith("Assets"):
                    continue
                out.parent.mkdir(parents=True, exist_ok=True)
                with tf.extractfile(m) as src, open(out, "wb") as dst:
                    shutil.copyfileobj(src, dst)
                n += 1
            return n

        n1 = extract("ProjectSettings/", CLIENT)
        n2 = extract("Assets/Settings", CLIENT)
        n3 = extract("Packages/manifest.json", CLIENT)
        print(f"copied: ProjectSettings {n1}, Assets/Settings {n2}, manifest {n3}")

    man_path = CLIENT / "Packages" / "manifest.json"
    man = json.loads(man_path.read_text(encoding="utf-8"))
    deps = man.setdefault("dependencies", {})
    deps.update(EXTRA_DEPS)
    missing = [r for r in REQUIRED if r not in deps]
    if missing:
        print("경고: 템플릿에 없는 패키지:", missing, "→ Unity Package Manager에서 추가 필요")
    man_path.write_text(json.dumps(man, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("manifest deps:", ", ".join(f"{k}@{v}" for k, v in sorted(deps.items()) if not k.startswith("com.unity.modules")))
    print("\n다음: Unity Hub > Add > Add project from disk > " + str(CLIENT))


if __name__ == "__main__":
    main()
