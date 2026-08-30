import importlib.util
import json
import sys
import tempfile
import unittest
from pathlib import Path


def load_gazetteer():
    path = Path(__file__).resolve().parents[1] / "scripts" / "score_gazetteer.py"
    spec = importlib.util.spec_from_file_location("score_gazetteer", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules["score_gazetteer"] = module
    spec.loader.exec_module(module)
    return module


G = load_gazetteer()


def index_with(*records):
    return G.Index.from_records(list(records))


class ScoreGazetteerTests(unittest.TestCase):
    def test_unique_title_snaps_from_dirty_filename(self):
        index = index_with(
            {"t": "Schindler's List", "c": "John Williams", "a": ["Schindlers List"]},
            {"t": "Star Wars", "c": "John Williams", "a": []},
        )
        found = index.match("Schindlers List - Main Theme Piano Version.pdf", [])
        self.assertIsNotNone(found)
        self.assertEqual("Schindler's List", found.title)
        self.assertEqual("John Williams", found.composer)
        self.assertEqual("unique-title", found.reason)

    def test_generic_title_does_not_snap_without_composer(self):
        index = index_with(
            {"t": "Prelude", "c": "Johann Sebastian Bach", "a": []},
            {"t": "Prelude", "c": "Frédéric Chopin", "a": []},
        )
        self.assertIsNone(index.match("Prelude.pdf", []))

    def test_generic_title_snaps_with_filename_composer(self):
        index = index_with(
            {"t": "Prelude", "c": "Johann Sebastian Bach", "a": []},
            {"t": "Prelude", "c": "Frédéric Chopin", "a": []},
        )
        found = index.match("Prelude by Bach.pdf", [])
        self.assertIsNotNone(found)
        self.assertEqual("Johann Sebastian Bach", found.composer)
        self.assertEqual("title+composer", found.reason)

    def test_generic_title_snaps_with_catalog_composer(self):
        index = index_with(
            {"t": "Air", "c": "Johann Sebastian Bach", "a": []},
            {"t": "Air", "c": "George Gershwin", "a": []},
        )
        found = index.match("Air.pdf", ["Johann Sebastian Bach"])
        self.assertIsNotNone(found)
        self.assertEqual("Johann Sebastian Bach", found.composer)

    def test_generic_title_snaps_with_folder_composer(self):
        index = index_with(
            {"t": "Suite", "c": "Johann Sebastian Bach", "a": []},
            {"t": "Suite", "c": "Claude Debussy", "a": []},
        )
        found = index.match(r"Corpus\Bach\Suite.pdf", [G.folder_composer(r"Corpus\Bach\Suite.pdf")])
        self.assertIsNotNone(found)
        self.assertEqual("Johann Sebastian Bach", found.composer)

    def test_catalogue_number_unique_snap(self):
        index = index_with(
            {"t": "Prelude in C major, BWV 846", "c": "Johann Sebastian Bach", "a": []},
            {"t": "Prelude", "c": "Frédéric Chopin", "a": []},
        )
        found = index.match("Ave Maria Prelude in C Major BWV 846 - Bach Gounod.pdf", [])
        self.assertIsNotNone(found)
        self.assertEqual("Prelude in C major, BWV 846", found.title)

    def test_short_unique_title_needs_composer(self):
        index = index_with({"t": "Stay", "c": "Rihanna", "a": []})
        self.assertIsNone(index.match("Stay.pdf", []))
        found = index.match("Stay.pdf", ["Rihanna"])
        self.assertIsNotNone(found)
        self.assertEqual("Rihanna", found.composer)

    def test_duplicate_works_collapse_to_unique(self):
        index = index_with(
            {"t": "Greensleeves", "c": "Traditional", "a": []},
            {"t": "Greensleeves", "c": "Traditional", "a": ["What Child Is This"]},
        )
        found = index.match("Traditional Folk Song - Greensleeves.pdf", [])
        self.assertIsNotNone(found)
        self.assertEqual("Greensleeves", found.title)

    def test_camel_case_filename_splits(self):
        index = index_with({"t": "Greensleeves", "c": "Traditional", "a": []})
        found = index.match("GreensleevesPianoSolo.pdf", [])
        self.assertIsNotNone(found)
        self.assertEqual("Greensleeves", found.title)

    def test_stopword_span_does_not_unique_snap(self):
        index = index_with({"t": "Anarchy in the UK", "c": "Sex Pistols", "a": ["in the"]})
        self.assertIsNone(index.match("In the Bleak Midwinter.pdf", []))

    def test_anna_magdalena_does_not_snap_to_unrelated_anna(self):
        index = index_with({"t": "Anna", "c": "Cseh Tamás", "a": ["Anna Magdalena"]})
        self.assertIsNone(
            index.match(r"Corpus\Bach\Anna - Magdalena - 03.pdf", ["J. S. Bach"])
        )

    def test_folder_composer_blocks_conflicting_unique_title(self):
        index = index_with(
            {"t": "English Suite", "c": "Paul Lewis", "a": []},
            {"t": "English Suite no. 1 in A major, BWV 806", "c": "Johann Sebastian Bach", "a": []},
        )
        self.assertIsNone(index.match(r"Corpus\Bach\Bach - English - Suite - 1 - Bourree - 1.pdf", []))

    def test_catalogue_suffix_does_not_split_into_tiny_span(self):
        index = index_with({"t": "A-3", "c": "Wagner Tiso", "a": []})
        self.assertIsNone(index.match(r"Corpus\Bach\Bwv - 1006a 3.pdf", ["Johann Sebastian Bach"]))

    def test_generic_fugue_plus_bach_does_not_collapse(self):
        index = index_with(
            {"t": "Fugue", "c": "Johann Sebastian Bach", "a": []},
            {"t": "Fugue", "c": "Johann Sebastian Bach", "a": []},
            {"t": "Toccata und Fuge d-Moll, BWV 565: II. Fuge", "c": "Johann Sebastian Bach", "a": []},
        )
        self.assertIsNone(index.match(r"Corpus\Bach\Fugue - C - Major.pdf", ["Johann Sebastian Bach"]))

    def test_does_not_replace_unrelated_current_title(self):
        index = index_with({"t": "Pride and Prejudice", "c": "Dario Marianelli", "a": []})
        catalog = {
            r"K's Collection\Dawn Pride and Prejudice Music by Dario Marianelli.pdf": {
                "title": "Dawn",
                "subtitle": 'from "Pride and Prejudice"',
                "composer": "Dario Marianelli",
            }
        }
        result = G.dry_run(catalog, index)
        self.assertEqual(0, result["updates"])

    def test_does_not_shorten_more_specific_current_title(self):
        index = index_with({"t": "Alice", "c": "Joseph Ascher", "a": []})
        catalog = {
            r"Corpus\Ascher\Alice.pdf": {
                "title": "Alice, Where Art Thou?",
                "subtitle": "",
                "composer": "Joseph Ascher",
            }
        }
        result = G.dry_run(catalog, index)
        self.assertEqual(0, result["updates"])

    def test_same_catalogue_keeps_current_wording(self):
        index = index_with(
            {
                "t": "The Well-Tempered Clavier, Book I: Prelude and Fugue no. 1 in C major, BWV 846: Prelude",
                "c": "Johann Sebastian Bach",
                "a": [],
            }
        )
        catalog = {
            r"Christmas\Ave Maria Prelude in C Major BWV 846 - Bach Gounod.pdf": {
                "title": "Prelude in C major BWV 846",
                "subtitle": "",
                "composer": "Johann Sebastian Bach",
            }
        }
        result = G.dry_run(catalog, index)
        self.assertEqual(0, result["updates"])

    def test_refuse_catalog_write(self):
        with self.assertRaises(SystemExit):
            G.refuse_catalog_write(Path(".flipper-catalog.json"))
        with self.assertRaises(SystemExit):
            G.refuse_catalog_write(Path(r"//Alexandria/Charles/Scores/.flipper-catalog.json"))

    def test_apply_updates_writes_matching_rows_and_skips_stale(self):
        catalog = {
            r"Christmas\Carol.pdf": {"title": "Carol of the Bells", "subtitle": "cresc.", "composer": "William J. Ross"},
            r"Christmas\Away.pdf": {"title": "Away in a Manger", "subtitle": "", "composer": ""},
        }
        rows = [
            {
                "path": r"Christmas\Carol.pdf",
                "current_title": "Carol of the Bells",
                "current_composer": "William J. Ross",
                "proposed_title": "Carol of the Bells",
                "proposed_composer": "Mykola Leontovych",
            },
            {
                "path": r"Christmas\Missing.pdf",
                "current_title": "Gone",
                "current_composer": "",
                "proposed_title": "Gone",
                "proposed_composer": "Anon",
            },
            {
                "path": r"Christmas\Away.pdf",
                "current_title": "Old Away",
                "current_composer": "",
                "proposed_title": "Away in a Manger",
                "proposed_composer": "Traditional",
            },
        ]
        applied, skipped = G.apply_updates(catalog, rows)
        self.assertEqual([r"Christmas\Carol.pdf"], applied)
        self.assertEqual(["missing", "stale"], [item["reason"] for item in skipped])
        self.assertEqual("Mykola Leontovych", catalog[r"Christmas\Carol.pdf"]["composer"])
        self.assertEqual("cresc.", catalog[r"Christmas\Carol.pdf"]["subtitle"])
        self.assertEqual("Away in a Manger", catalog[r"Christmas\Away.pdf"]["title"])

    def test_dry_run_reports_only_changes_and_does_not_write_catalog(self):
        index = index_with({"t": "Carol of the Bells", "c": "Mykola Leontovych", "a": []})
        catalog = {
            "Christmas\\Carol of the Bells.pdf": {
                "title": "Carol of the Bells",
                "subtitle": "cresc.",
                "composer": "William J. Ross",
            },
            "Christmas\\Away in a Manger.pdf": {
                "title": "Away in a Manger",
                "subtitle": "",
                "composer": "",
            },
        }
        with tempfile.TemporaryDirectory() as folder:
            catalog_path = Path(folder) / ".flipper-catalog.json"
            catalog_path.write_text(json.dumps(catalog), encoding="utf-8")
            result = G.dry_run(catalog, index)
            self.assertEqual(1, result["updates"])
            self.assertEqual("Mykola Leontovych", result["rows"][0]["proposed_composer"])
            self.assertEqual(json.dumps(catalog), catalog_path.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
