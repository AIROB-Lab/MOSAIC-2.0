"""Generate block navigation from DocFX metadata and C# registrations (Python 3.9+).

Reads the emitted ManagedReference format, not arbitrary YAML. Source matching supports
the catalogue/factory/selector declaration conventions used in this repository; it is
not a C# compiler. Compiled metadata determines which types can be linked.
"""
import argparse
import json
from pathlib import Path
import re

ROLES = ("model", "viewModel", "view", "related")
BASE = "MOSAIC.Components.Basics.BaseBlock"


def read(path):
    return path.read_text(encoding="utf-8-sig")


def source_text(path):
    # Remove comments while preserving strings, including URLs inside literals.
    return re.sub(r'"(?:\\.|[^"\\])*"|//[^\n]*|/\*.*?\*/',
                  lambda m: m[0] if m[0].startswith('"') else ' ', read(path), flags=re.S)


def metadata(directory):
    types = {}
    # Ignore obsolete YAML files left behind when a type is removed or renamed.
    current = set(re.findall(r"^\s*- uid: (.+)$", read(directory / "toc.yml"), re.M))
    for path in sorted(directory.glob("*.yml")):
        text = read(path)
        if not text.startswith("### YamlMime:ManagedReference"):
            continue
        items = text.split("\nreferences:", 1)[0]
        header = re.split(r"\n- uid:", items, maxsplit=2)
        if len(header) < 2:
            continue
        first = header[1]
        uid = first.splitlines()[0].strip()
        if uid not in current:
            continue
        kind = re.search(r"^  type: (\w+)$", first, re.M)
        if not kind or kind[1] == "Namespace":
            continue
        inheritance = re.search(r"^  inheritance:\n((?:  - [^\n]+\n)*)", first, re.M)
        types[uid] = {
            "block": kind[1] == "Class" and inheritance is not None
                     and BASE in inheritance[1].split()
                     and not re.search(r"content: .*\babstract\b", first),
            "sources": set(re.findall(r"^\s+path: (\.\./MOSAIC/[^\n]+\.cs)\s*$", items, re.M)),
        }
    if not types:
        raise ValueError("No API metadata found. Run DocFX metadata before generating navigation.")
    return types


def resolve(name, text, types):
    aliases = dict(re.findall(r"\busing\s+(\w+)\s*=\s*([\w.]+)\s*;", text))
    first, dot, rest = name.partition(".")
    name = aliases.get(first, first) + (dot + rest if dot else "")
    namespaces = re.findall(r"\busing\s+([\w.]+)\s*;", text)
    scope = re.search(r"\bnamespace\s+([\w.]+)", text)
    if scope:
        parts = scope[1].split(".")
        namespaces += [".".join(parts[:i]) for i in range(len(parts), 0, -1)]
    if name in types:
        return name
    found = {ns + "." + name for ns in namespaces} & types.keys()
    if len(found) > 1:
        raise ValueError(f"Ambiguous API type {name}: {sorted(found)}. Qualify it in source.")
    return next(iter(found), None)


def generate(docs):
    types = metadata(docs / "_api")
    app = docs.parent / "MOSAIC/MOSAIC"
    catalog = source_text(app / "Components/Factory/BlockCatalog.cs")
    factory = source_text(app / "Components/Factory/BlockFactory.cs")
    selector = source_text(app / "Selector/BlockTemplateSelector.cs")
    overrides_path = docs / "api-block-overrides.json"
    overrides = json.loads(read(overrides_path)) if overrides_path.exists() else {}
    blocks = {uid: {"aliases": set()} for uid, info in types.items() if info["block"]}
    for match in re.finditer(r'new(?:\s+BlockDescriptor)?\s*\(\s*"([^"]+)"\s*,\s*typeof\s*\(\s*([\w.]+)\s*\)\s*,\s*"([^"]+)"\s*,\s*BlockCategory\.(\w+)', catalog):
        uid = resolve(match[2], catalog, types)
        if uid in blocks:
            blocks[uid].update(name=match[3], category=split_name(match[4]))
            blocks[uid]["aliases"].add(match[1])
    for match in re.finditer(r'((?:"[^"\n]+"\s*(?:or\s*)?)+)\s*=>\s*(?:\(typeof\([\w.]+\),\s*)?\(\)\s*=>\s*([\w.]+)\.ConfigureInput', factory):
        uid = resolve(match[2], factory, types)
        if uid in blocks:
            blocks[uid]["aliases"].update(re.findall(r'"([^"\n]+)"', match[1]))
    for page in sorted((docs / "docs/blocks").glob("*.md")):
        text = read(page)
        match = re.search(r'\[API reference\]\(xref:([^)]+)', text)
        if match and match[1] in blocks:
            blocks[match[1]].update(id=page.stem, name=text.splitlines()[0][2:],
                                    guide=f"docs/blocks/{page.stem}.html")

    arms = list(re.finditer(r'\b([\w.]+)(?:\s+\w+)?\s*=>\s*new\s+([\w.]+)', selector))
    result = []
    for uid, info in sorted(blocks.items()):
        override = overrides.get(uid, {})
        short = uid.rsplit(".", 1)[-1]
        stem = short[:-5] if short.endswith("Block") else short
        roles = {role: set() for role in ROLES}
        roles["model"].add(uid)
        for i, arm in enumerate(arms):
            if resolve(arm[1], selector, types) != uid:
                continue
            view = resolve(arm[2], selector, types)
            if view:
                roles["view"].add(view)
            body = selector[arm.end():arms[i + 1].start() if i + 1 < len(arms) else len(selector)]
            for vm in re.findall(r'new\s+([\w.]+ViewModel)\s*\(', body):
                resolved = resolve(vm, selector, types)
                if resolved:
                    roles["viewModel"].add(resolved)
        for role, prefix, names in (
            ("viewModel", "MOSAIC.ViewModels.", {short + "ViewModel", stem + "ViewModel"}),
            ("view", "MOSAIC.Views.", {stem + suffix for suffix in ("CardView", "View", "ConfigView")}),
        ):
            if role in override:
                roles[role] = set(override[role])
                continue
            for name in sorted(names):
                found = {u for u in types if u.startswith(prefix) and u.rsplit(".", 1)[-1] == name}
                if len(found) > 1:
                    # The selector provides an authoritative answer for colliding names.
                    if roles[role] & found:
                        continue
                    raise ValueError(f"{uid}: ambiguous {name}; set {role} in api-block-overrides.json")
                roles[role].update(found)
        owned = set().union(*roles.values())
        unknown = owned - types.keys()
        if unknown:
            raise ValueError(f"{uid}: unknown override types {sorted(unknown)}")
        sources = set().union(*(types[u]["sources"] for u in owned))
        roles["related"] = {u for u in types if types[u]["sources"] & sources
                            or any(u.startswith(parent + ".") for parent in owned)}
        roles["related"].update(override.get("related", []))
        for ns in override.get("relatedNamespaces", []):
            matches = {u for u in types if u.startswith(ns + ".")}
            if not matches:
                raise ValueError(f"{uid}: override namespace has no API types: {ns}")
            roles["related"].update(matches)
        roles["related"] -= owned
        unknown = roles["related"] - types.keys()
        if unknown:
            raise ValueError(f"{uid}: unknown related types {sorted(unknown)}")
        result.append({"id": info.get("id", uid.lower().replace(".", "-")),
                       "name": info.get("name", split_name(stem)),
                       "category": info.get("category", split_name(uid.split(".")[2]) if len(uid.split(".")) > 3 else "Other Blocks"),
                       "aliases": sorted(info["aliases"]),
                       **({"guide": info["guide"]} if "guide" in info else {}),
                       **{role: sorted(roles[role]) for role in ROLES}})
    unknown = overrides.keys() - blocks.keys()
    if unknown:
        raise ValueError(f"Overrides reference absent blocks: {sorted(unknown)}")
    return {"blocks": sorted(result, key=lambda b: (b["category"], b["name"], b["id"]))}


def split_name(name):
    return re.sub(r"(?<=[a-z])(?=[A-Z])", " ", name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="Fail if generated navigation is stale")
    args = parser.parse_args()
    docs = Path(__file__).resolve().parent
    output = docs / "api-blocks.json"
    try:
        data = generate(docs)
        text = json.dumps(data, indent=2) + "\n"
        if args.check:
            if not output.exists() or read(output) != text:
                raise ValueError("API block navigation is stale. Run generate_api_blocks.py.")
        else:
            output.write_text(text, encoding="utf-8")
        print(f"API navigation: {len(data['blocks'])} blocks discovered.")
    except (OSError, ValueError) as exc:
        parser.exit(1, f"API navigation: {exc}\n")


if __name__ == "__main__":
    main()
