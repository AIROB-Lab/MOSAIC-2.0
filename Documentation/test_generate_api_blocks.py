"""Regression tests for automatic navigation; no .NET build or existing site needed."""
import json
from pathlib import Path
import tempfile
import unittest

from generate_api_blocks import BASE, generate

MODEL = "MOSAIC.Models.SignalProcessing.NewBlock"
VM = "MOSAIC.ViewModels.SignalProcessing.NewViewModel"
VIEW = "MOSAIC.Views.Cards.SignalProcessing.NewCardView"


class DiscoveryTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.docs = self.root / "Documentation"
        self.api = self.docs / "_api"
        self.api.mkdir(parents=True)
        self.toc = self.api / "toc.yml"
        self.toc.write_text("items:\n", encoding="utf-8")
        self.app = self.root / "MOSAIC/MOSAIC"
        self.write("Components/Factory/BlockCatalog.cs", '''
using Alias = MOSAIC.Models.SignalProcessing.NewBlock;
namespace MOSAIC.Components.Factory;
// new("ignored", typeof(Alias), "Wrong", BlockCategory.Tests)
new("new", typeof(Alias), "New signal block", BlockCategory.SignalProcessing, "Example", []);
''')
        self.write("Components/Factory/BlockFactory.cs", '''
using MOSAIC.Models.SignalProcessing;
"new" or "new_alias" => () => NewBlock.ConfigureInput(sp, model),
''')
        self.write("Selector/BlockTemplateSelector.cs", '''
using Alias = MOSAIC.Models.SignalProcessing.NewBlock;
using MOSAIC.ViewModels.SignalProcessing;
using MOSAIC.Views.Cards.SignalProcessing;
Alias block => new NewCardView {
    DataContext = new NewViewModel(block)
},
_ => new TextBlock()
''')
        self.type(MODEL, block=True)
        self.type(VM)
        self.type(VIEW)

    def write(self, path, text):
        path = self.app / path
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8")

    def type(self, uid, block=False, source=None, extra="", current=True):
        text = f"### YamlMime:ManagedReference\nitems:\n- uid: {uid}\n  type: Class\n"
        if block:
            text += f"  inheritance:\n  - System.Object\n  - {BASE}\n"
        if source:
            text += f"  source:\n    path: {source}\n"
        (self.api / (uid + ".yml")).write_text(text + extra, encoding="utf-8")
        if current:
            with self.toc.open("a", encoding="utf-8") as toc:
                toc.write(f"- uid: {uid}\n")

    def override(self, data):
        (self.docs / "api-block-overrides.json").write_text(json.dumps(data), encoding="utf-8")

    def test_new_block_without_guide_or_site_and_multiline_selector(self):
        block, = generate(self.docs)["blocks"]
        self.assertEqual(block["name"], "New signal block")
        self.assertEqual(block["category"], "Signal Processing")
        self.assertEqual(block["aliases"], ["new", "new_alias"])
        self.assertEqual(block["model"], [MODEL])
        self.assertEqual(block["viewModel"], [VM])
        self.assertEqual(block["view"], [VIEW])
        self.assertNotIn("guide", block)
        self.assertFalse((self.docs / "_site").exists())
        self.assertEqual(generate(self.docs), generate(self.docs))

    def test_guide_is_optional_and_preserves_existing_slug(self):
        guide = self.docs / "docs/blocks/my-old-slug.md"
        guide.parent.mkdir(parents=True)
        guide.write_text(f"# Friendly block\n[API reference](xref:{MODEL})", encoding="utf-8")
        block, = generate(self.docs)["blocks"]
        self.assertEqual(block["id"], "my-old-slug")
        self.assertEqual(block["name"], "Friendly block")
        self.assertEqual(block["guide"], "docs/blocks/my-old-slug.html")

    def test_factory_registration_with_type_metadata(self):
        self.write("Components/Factory/BlockFactory.cs", '''
using MOSAIC.Models.SignalProcessing;
"new" or "new_alias" => (typeof(NewBlock), () => NewBlock.ConfigureInput(sp, model)),
''')
        block, = generate(self.docs)["blocks"]
        self.assertEqual(block["aliases"], ["new", "new_alias"])

    def test_same_file_helpers_and_namespace_override(self):
        helper = "MOSAIC.Components.DeviceHelpers.Protocol"
        colocated = "MOSAIC.Models.SignalProcessing.NewOptions"
        source = "../MOSAIC/MOSAIC/Models/NewBlock.cs"
        self.type(MODEL, block=True, source=source)
        self.type(colocated, source=source)
        self.type(helper)
        self.override({MODEL: {"relatedNamespaces": ["MOSAIC.Components.DeviceHelpers"]}})
        block, = generate(self.docs)["blocks"]
        self.assertEqual(block["related"], sorted([helper, colocated]))

    def test_ambiguous_convention_requires_override(self):
        alternate = "MOSAIC.ViewModels.Other.NewViewModel"
        self.type(alternate)
        self.write("Selector/BlockTemplateSelector.cs", "")
        with self.assertRaisesRegex(ValueError, "ambiguous"):
            generate(self.docs)
        self.override({MODEL: {"viewModel": [VM]}})
        self.assertEqual(generate(self.docs)["blocks"][0]["viewModel"], [VM])

    def test_selector_resolves_same_name_with_explicit_alias(self):
        self.type("MOSAIC.ViewModels.Other.NewViewModel")
        block, = generate(self.docs)["blocks"]
        self.assertEqual(block["viewModel"], [VM])

    def test_removed_types_and_abstract_bases_are_not_blocks(self):
        self.type("MOSAIC.Models.Gone", block=True, current=False)
        self.type("MOSAIC.Models.Abstract", block=True,
                  extra="  syntax:\n    content: public abstract class Abstract : BaseBlock\n")
        self.type("MOSAIC.Models.NonBlock", extra=f"  derivedClasses:\n  - {BASE}\n")
        self.assertEqual(len(generate(self.docs)["blocks"]), 1)

    def test_invalid_override_fails_instead_of_creating_dead_link(self):
        self.override({MODEL: {"related": ["MOSAIC.Missing"]}})
        with self.assertRaisesRegex(ValueError, "unknown related types"):
            generate(self.docs)
        self.override({"MOSAIC.RemovedBlock": {}})
        with self.assertRaisesRegex(ValueError, "absent blocks"):
            generate(self.docs)


if __name__ == "__main__":
    unittest.main()
