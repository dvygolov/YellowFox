import importlib.util
import sys
import tempfile
import unittest
from pathlib import Path

PYTHON_DIR = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PYTHON_DIR))

try:
    import camoufox  # noqa: F401
except ImportError as exc:  # pragma: no cover - the venv installs it
    raise unittest.SkipTest(f"camoufox is not installed: {exc}")

spec = importlib.util.spec_from_file_location(
    "yellowfox_camoufox_server",
    PYTHON_DIR / "camoufox-server.py",
)
server = importlib.util.module_from_spec(spec)
spec.loader.exec_module(server)


class BrowserAutoconfigTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.browser_root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def test_autoconfig_points_at_yellowfox_cfg(self):
        server.ensure_minimal_browser_autoconfig(self.browser_root)

        autoconfig = (self.browser_root / "defaults" / "pref" / "autoconfig.js").read_text(encoding="utf-8")
        self.assertIn('pref("general.config.filename", "yellowfox.cfg");', autoconfig)
        self.assertIn('pref("general.config.obscure_value", 0);', autoconfig)

    def test_yellowfox_cfg_keeps_juggler_pipe_announcement_enabled(self):
        """Camoufox 158 turns dump() off, and Playwright waits for the Juggler line."""
        server.ensure_minimal_browser_autoconfig(self.browser_root)

        cfg = (self.browser_root / "yellowfox.cfg").read_text(encoding="utf-8")
        self.assertIn('defaultPref("browser.dom.window.dump.enabled", true);', cfg)

    def test_replaces_the_browser_own_autoconfig(self):
        pref_dir = self.browser_root / "defaults" / "pref"
        pref_dir.mkdir(parents=True)
        (pref_dir / "autoconfig.js").write_text(
            'pref("general.config.filename", "camoufox.cfg");\n', encoding="utf-8")
        (self.browser_root / "camoufox.cfg").write_text(
            'defaultPref("browser.dom.window.dump.enabled", true);\n', encoding="utf-8")

        server.ensure_minimal_browser_autoconfig(self.browser_root)

        autoconfig = (pref_dir / "autoconfig.js").read_text(encoding="utf-8")
        self.assertIn("yellowfox.cfg", autoconfig)
        cfg = (self.browser_root / "yellowfox.cfg").read_text(encoding="utf-8")
        self.assertIn("browser.dom.window.dump.enabled", cfg)


if __name__ == "__main__":
    unittest.main()
