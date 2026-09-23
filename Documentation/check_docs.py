"""Check documentation sources and the latest DocFX output (Python 3.9+, no packages)."""
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit
import json
import re
import sys
from generate_api_blocks import generate

DOCS = Path(__file__).resolve().parent
ROOT = DOCS.parent
errors = []


def fail(message):
    errors.append(message)


factory = (ROOT / "MOSAIC/MOSAIC/Components/Factory/BlockFactory.cs").read_text(encoding="utf-8-sig")
factory_keys = set()
for arm in re.finditer(r'((?:"[^"\n]+"\s*(?:or\s*)?)+)\s*=>\s*(?:\(typeof\([\w.]+\),\s*)?\(\)\s*=>\s*[\w.]+\.ConfigureInput', factory):
    factory_keys.update(re.findall(r'"([^"\n]+)"', arm[1]))
documented_keys = set()
pages = list((DOCS / "docs/blocks").glob("*.md"))
optional_api_guides = {"delsys"}
for page in pages:
    source = page.read_text(encoding="utf-8-sig")
    match = re.search(r'^\| \*\*Type\*\* \| (.+) \|$', source, re.M)
    if not match:
        fail(f"{page.name}: missing Type row")
        continue
    documented_keys.update(re.findall(r'`([^`]+)`', match[1]))
    for heading in ("## Input and output", "## Parameters", "## Requirements and use"):
        if heading not in source:
            fail(f"{page.name}: missing {heading}")
for key in sorted(factory_keys - documented_keys):
    fail(f"Factory alias missing from catalogue: {key}")
for key in sorted(documented_keys - factory_keys):
    fail(f"Catalogue alias missing from factory: {key}")

# Teaching blocks are intentionally not registered in the shipped application.
# Their runnable graph is covered by DocumentationWorkflowTests with the tutorial factory case.
teaching_keys = {"developer/gain-pipeline.json": {"gainblock"}}
for example in (DOCS / "examples").rglob("*.json"):
    try:
        graph = json.loads(example.read_text(encoding="utf-8-sig"))
        for name, block in graph.items():
            allowed_keys = factory_keys | teaching_keys.get(example.relative_to(DOCS / "examples").as_posix(), set())
            if block.get("Type", "").lower() not in allowed_keys:
                fail(f"{example.name}/{name}: unregistered Type")
            for upstream in block.get("Inputs", []):
                if upstream not in graph:
                    fail(f"{example.name}/{name}: missing input {upstream}")
    except (ValueError, TypeError, AttributeError) as exc:
        fail(f"Invalid example {example.name}: {exc}")


class Page(HTMLParser):
    def __init__(self, path):
        super().__init__()
        self.ids = set()
        self.links = []
        self.feed(path.read_text(encoding="utf-8-sig"))

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if "id" in attrs:
            self.ids.add(attrs["id"])
        if tag == "a" and "href" in attrs:
            self.links.append(attrs["href"])
        if tag in ("img", "script") and "src" in attrs:
            self.links.append(attrs["src"])
        if tag == "link" and "href" in attrs:
            self.links.append(attrs["href"])


site = DOCS / "_site"
try:
    mapping = json.loads((DOCS / "api-blocks.json").read_text(encoding="utf-8-sig"))
    if generate(DOCS) != mapping:
        fail("API block navigation is stale; run the documentation build script")
    api_toc = json.loads((site / "api/toc.json").read_text(encoding="utf-8-sig"))
    api_types = {t["topicUid"]: t for group in api_toc["items"] for t in group.get("items", [])}
    block_ids = set()
    for block in mapping["blocks"]:
        block_id = block["id"]
        if block_id in block_ids:
            fail(f"Duplicate API block mapping: {block_id}")
        block_ids.add(block_id)
        page = DOCS / block["guide"].replace(".html", ".md") if block.get("guide") else DOCS / "__no_guide__"
        if block.get("guide") and not page.exists():
            fail(f"{block_id}: missing block guide")
        if page.exists():
            source = page.read_text(encoding="utf-8-sig")
            model = re.search(r'\[API reference\]\(xref:([^)]+)', source)
            if not model or block["model"] != [model[1]]:
                fail(f"{block_id}: API model differs from block guide")
            if not source.startswith("# " + block["name"] + "\n"):
                fail(f"{block_id}: API block name differs from block guide")
            for uid in block["model"]:
                if uid not in api_types:
                    continue
                model_page = site / "api" / api_types[uid]["href"]
                if model_page.exists():
                    rendered = model_page.read_text(encoding="utf-8-sig")
                    config = re.search(r'<section class="model-configuration"[^>]*>(.*?)</section>', rendered, re.S)
                    if not config or 'id="json-configuration"' not in config[1] or 'class="lang-json"' not in config[1]:
                        fail(f"{block_id}: model page is missing its JSON configuration section")
                    elif rendered.index('id="json-configuration"') > rendered.find('class="inheritance"') > 0:
                        fail(f"{block_id}: JSON configuration should precede API inheritance details")
        listed = []
        for role in ("model", "viewModel", "view", "related"):
            for uid in block[role]:
                listed.append(uid)
                if uid not in api_types:
                    fail(f"{block_id}/{role}: unknown API type {uid}")
                elif not (site / "api" / api_types[uid]["href"]).exists():
                    fail(f"{block_id}/{role}: missing built API page for {uid}")
        if len(listed) != len(set(listed)):
            fail(f"{block_id}: an API type is repeated within the block")
    required_guide_ids = {p.stem for p in pages} - optional_api_guides
    if not required_guide_ids.issubset(block_ids):
        fail("API block navigation must include all documented blocks")
    if json.loads((site / "api-blocks.json").read_text(encoding="utf-8-sig")) != mapping:
        fail("Built API block mappings are stale; rebuild documentation")
except (OSError, ValueError, KeyError, TypeError) as exc:
    fail(f"Cannot validate API block navigation: {exc}")

built = list((site / "docs").rglob("*.html"))
if not built:
    fail("No built guides found. Build the documentation first.")
else:
    built.append(site / "index.html")
    built.append(site / "api/index.html")
    cache = {}
    for path in built:
        parsed = cache.setdefault(path, Page(path))
        for href in parsed.links:
            url = urlsplit(href)
            if url.scheme or url.netloc:
                continue
            target = ((site / unquote(url.path).lstrip("/")) if url.path.startswith("/") else
                      (path.parent / unquote(url.path)) if url.path else path).resolve()
            if target.is_dir():
                target /= "index.html"
            if not target.exists():
                fail(f"{path.relative_to(site)}: missing link target {href}")
            elif url.fragment and target.suffix == ".html":
                target_page = cache.setdefault(target, Page(target))
                if unquote(url.fragment) not in target_page.ids:
                    fail(f"{path.relative_to(site)}: missing anchor {href}")
    for source in (DOCS / "docs").rglob("*.md"):
        if not (site / source.relative_to(DOCS)).with_suffix(".html").exists():
            fail(f"Guide was not built: {source.relative_to(DOCS)}")

if errors:
    print("\n".join(errors))
    print(f"FAILED: {len(errors)} issue(s)")
    sys.exit(1)
print(f"PASS: {len(pages)} block pages cover {len(factory_keys)} factory aliases; examples and built guide links are valid.")
