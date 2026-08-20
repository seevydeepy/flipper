import importlib.util
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
    def test_schindler_page_prefers_work_title(self):
        lines = ["John Williams", "(Main Theme)", "Schindler's List", "1993"]
        title = AUDIT.pick_page_title(lines, "Schindlers List - Main Theme Piano Version")
        self.assertEqual("Schindler's List", title)

    def test_schindler_headings_split_subtitle(self):
        lines = ["John Williams", "(Main Theme)", "Schindler's List", "1993"]
        title, subtitle = AUDIT.pick_headings(lines, "Schindlers List - Main Theme Piano Version")
        self.assertEqual("Schindler's List", title)
        self.assertEqual("Main Theme", subtitle)

    def test_tokens_fold_apostrophes(self):
        self.assertTrue(AUDIT.agrees("Schindler's List", "Schindlers List"))

    def test_possessive_work_title_is_not_a_name(self):
        self.assertFalse(AUDIT.looks_like_name("Schindler's List"))
        self.assertTrue(AUDIT.looks_like_name("John Williams"))

    def test_all_caps_two_words_are_not_a_name(self):
        self.assertFalse(AUDIT.looks_like_name("LA MER"))

    def test_parenthetical_work_title_unwraps_when_alone(self):
        lines = ["(Night on Bald Mountain)"]
        title, subtitle = AUDIT.pick_headings(lines, "Nobm")
        self.assertEqual("Night on Bald Mountain", title)
        self.assertEqual("", subtitle)

    def test_dawn_keeps_source_as_subtitle(self):
        lines = ["Music by", "Dario Marianelli", "Dawn", '(from "Pride and Prejudice")']
        title, subtitle = AUDIT.pick_headings(
            lines, "Dawn Pride and Prejudice Music by Dario Marianelli")
        self.assertEqual("Dawn", title)
        self.assertEqual('from "Pride and Prejudice"', subtitle)

    def test_music_by_is_not_a_name(self):
        self.assertFalse(AUDIT.looks_like_name("Music by"))
        self.assertFalse(AUDIT.looks_like_name("Love is Blue"))
        self.assertTrue(AUDIT.looks_like_name("Dario Marianelli"))

    def test_elllington_keeps_parenthetical_as_subtitle(self):
        lines = ["Duke Ellington", "It Don't Mean a Thing", "(If It Ain't Got That Swing)"]
        title, subtitle = AUDIT.pick_headings(
            lines, "It Dont Mean A Thing If It Aint Got That Swing Duke Ellington")
        self.assertEqual("It Don't Mean a Thing", title)
        self.assertEqual("If It Ain't Got That Swing", subtitle)

    def test_fullwidth_from_credit_is_subtitle(self):
        lines = ['（From The Universal Motion Picture "SCHINDLER\'S LIST")', "John Williams"]
        title, subtitle = AUDIT.pick_headings(
            lines, "John Williams Theme from Schindler's List")
        self.assertEqual("John Williams", title)
        self.assertEqual('From The Universal Motion Picture "SCHINDLER\'S LIST"', subtitle)


if __name__ == "__main__":
    unittest.main()
