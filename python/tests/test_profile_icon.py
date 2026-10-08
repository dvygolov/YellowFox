import importlib.util
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
spec = importlib.util.spec_from_file_location(
    "camoufox_native_broker",
    Path(__file__).resolve().parents[1] / "camoufox-native-broker.py",
)
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)


class ProfileIconTests(unittest.TestCase):
    def test_empty_name_falls_back_to_initials(self):
        self.assertEqual(["YF"], native.profile_icon_lines(""))
        self.assertEqual(["YF"], native.profile_icon_lines(None))

    def test_short_word_stays_on_one_line(self):
        self.assertEqual(["Work"], native.profile_icon_lines("Work"))

    def test_words_are_grouped_within_the_line_budget(self):
        self.assertEqual(["My Work", "Profile"], native.profile_icon_lines("My Work Profile"))

    def test_long_hyphenated_name_is_split_instead_of_clipped(self):
        self.assertEqual(
            ["VK628-D", "2-33551", "3571"],
            native.profile_icon_lines("VK628-D2-335513571"),
        )

    def test_over_long_name_is_truncated_with_ellipsis(self):
        lines = native.profile_icon_lines("abcdefghijklmnopqrstuvwxyz")
        self.assertEqual(3, len(lines))
        self.assertTrue(lines[-1].endswith("\u2026"))
        for line in lines:
            self.assertLessEqual(len(line), 7)


if __name__ == "__main__":
    unittest.main()
