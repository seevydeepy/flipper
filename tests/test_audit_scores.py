import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


def load_audit():
    path = Path(__file__).resolve().parents[1] / "scripts" / "audit_scores.py"
    spec = importlib.util.spec_from_file_location("audit_scores", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


AUDIT = load_audit()


class AuditScoresTests(unittest.TestCase):
    def test_useful_keeps_headings_and_credits(self):
        self.assertTrue(AUDIT.useful("John Williams"))
        self.assertTrue(AUDIT.useful("(Main Theme)"))
        self.assertTrue(AUDIT.useful("Music by Dario Marianelli"))

    def test_useful_rejects_glyph_noise_and_junk(self):
        self.assertFalse(AUDIT.useful("789: ;<=> 9: ?9@AB"))
        self.assertFalse(AUDIT.useful("Copyright 2026"))
        self.assertFalse(AUDIT.useful("12a"))

    def test_tokens_fold_apostrophes(self):
        # Extraction-level agreement helper retained for line dedupe.
        self.assertTrue(AUDIT.useful("Schindler's List"))

    def test_folder_composer_supports_but_never_decides(self):
        self.assertEqual("Bach", AUDIT.folder_composer(Path("Corpus/Bach/Air.pdf")))
        self.assertEqual("", AUDIT.folder_composer(Path("Downloads/Air.pdf")))

    def test_extract_without_dotnet_never_invents_composer(self):
        facts = AUDIT.extract(
            Path("Air.pdf"), Path("Downloads/Air.pdf"), Path("nonexistent.dll"))
        self.assertEqual("Air", facts["title"])
        self.assertEqual("", facts["composer"])

    def test_main_failure_path_never_invents_composer(self):
        # A PDF whose text extraction throws must surface as an unreadable
        # file with an unknown composer — never the folder name.
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "Corpus" / "Bach").mkdir(parents=True)
            bad = root / "Corpus" / "Bach" / "Broken.pdf"
            bad.write_bytes(b"not a pdf at all")
            out = root / "catalog.json"
            real_page_text = AUDIT.page_text

            def boom(_pdf: Path):
                raise RuntimeError("unreadable")

            AUDIT.page_text = boom  # type: ignore[assignment]
            try:
                rc = AUDIT.run(root, out, Path("nonexistent.dll"), 0, "", "")
            finally:
                AUDIT.page_text = real_page_text  # type: ignore[assignment]
            self.assertEqual(0, rc)
            data = json.loads(out.read_text(encoding="utf-8"))
            self.assertEqual("", data["Corpus\\Bach\\Broken.pdf"]["composer"])
            self.assertEqual("Broken", data["Corpus\\Bach\\Broken.pdf"]["title"])

    def test_apply_to_catalog_preserves_existing_and_confirms(self):
        with tempfile.TemporaryDirectory() as folder:
            catalog_path = Path(folder) / ".flipper-catalog.json"
            catalog_path.write_text(
                json.dumps({"A.pdf": {"title": "Curated", "subtitle": "", "composer": "Bach"}}),
                encoding="utf-8")
            merge = AUDIT.apply_to_catalog(
                catalog_path,
                {"A.pdf": {"title": "Auto", "subtitle": "", "composer": "X"},
                 "B.pdf": {"title": "New", "subtitle": "", "composer": ""}},
                None)
            data = json.loads(catalog_path.read_text(encoding="utf-8"))
            self.assertEqual("Curated", data["A.pdf"]["title"])
            self.assertEqual("New", data["B.pdf"]["title"])
            self.assertEqual(["A.pdf"], [item["path"] for item in merge["skipped"]])
            self.assertEqual(1, merge["confirmed"])

    def test_write_catalog_replaces_via_temp_file(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / ".flipper-catalog.json"
            path.write_text("{}", encoding="utf-8")
            AUDIT.write_catalog(path, {"A.pdf": {"title": "Air", "subtitle": "", "composer": "Bach"}})
            data = json.loads(path.read_text(encoding="utf-8"))
            self.assertEqual("Air", data["A.pdf"]["title"])
            self.assertFalse(path.with_name(path.name + ".tmp").exists())


if __name__ == "__main__":
    unittest.main()
