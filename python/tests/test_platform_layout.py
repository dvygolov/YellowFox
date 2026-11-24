import importlib.util
import tempfile
import unittest
from pathlib import Path

PYTHON_DIR = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("yellowfox_platform", PYTHON_DIR / "yellowfox_platform.py")
platform_layout = importlib.util.module_from_spec(spec)
spec.loader.exec_module(platform_layout)


class PlatformLayoutTests(unittest.TestCase):
    def test_asset_names_follow_official_releases(self):
        folder = "152.0.4-beta.30"
        self.assertEqual(platform_layout.browser_asset_name(folder, "win", "x86_64"),
                         f"camoufox-{folder}-win.x86_64.zip")
        self.assertEqual(platform_layout.browser_asset_name(folder, "lin", "x86_64"),
                         f"camoufox-{folder}-lin.x86_64.zip")
        self.assertEqual(platform_layout.browser_asset_name(folder, "mac", "arm64"),
                         f"camoufox-{folder}-mac.arm64.zip")

    def test_unsupported_architecture_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "no arm64 build for win"):
            platform_layout.browser_asset_name("152.0.4-beta.30", "win", "arm64")

    def test_installed_layout_per_platform(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.assertFalse(platform_layout.browser_is_installed(root, "lin"))

            (root / "camoufox-bin").write_bytes(b"binary")
            (root / "application.ini").write_text("version")
            self.assertFalse(platform_layout.browser_is_installed(root, "lin"))
            (root / "omni.ja").write_bytes(b"resources")
            self.assertTrue(platform_layout.browser_is_installed(root, "lin"))

    def test_macos_browser_root_is_the_resources_directory(self):
        executable = Path("/tmp/Camoufox.app/Contents/MacOS/camoufox")
        self.assertEqual(platform_layout.browser_root_for(executable, "mac"),
                         Path("/tmp/Camoufox.app/Contents/Resources"))
        self.assertEqual(platform_layout.browser_root_for(Path("/tmp/linux/camoufox-bin"), "lin"),
                         Path("/tmp/linux"))
        self.assertEqual(platform_layout.browser_root_for(Path("/opt/browser/camoufox.exe"), "win"),
                         Path("/opt/browser"))

    def test_process_names_per_platform(self):
        self.assertEqual(platform_layout.browser_process_names("lin"), ("camoufox-bin",))
        self.assertEqual(platform_layout.browser_process_names("win"), ("camoufox",))
        self.assertEqual(platform_layout.browser_process_names("mac"), ("camoufox",))


if __name__ == "__main__":
    unittest.main()
