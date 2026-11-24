import importlib.util
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

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


    def test_data_directory_honours_the_override(self):
        with patch.dict(os.environ, {"YELLOWFOX_DATA_DIR": "/tmp/yellowfox-data"}):
            self.assertEqual(platform_layout.data_directory("lin"), Path("/tmp/yellowfox-data"))
            self.assertEqual(platform_layout.data_directory("mac"), Path("/tmp/yellowfox-data"))
            self.assertEqual(platform_layout.data_directory("win"), Path("/tmp/yellowfox-data"))

    def test_linux_data_directory_uses_xdg_data_home(self):
        environment = {"YELLOWFOX_DATA_DIR": "", "XDG_DATA_HOME": "/tmp/xdg-data"}
        with patch.dict(os.environ, environment):
            self.assertEqual(platform_layout.data_directory("lin"), Path("/tmp/xdg-data/YellowFox"))

    def test_macos_data_directory_is_in_application_support(self):
        with patch.dict(os.environ, {"YELLOWFOX_DATA_DIR": ""}):
            self.assertEqual(
                platform_layout.data_directory("mac"),
                Path.home() / "Library" / "Application Support" / "YellowFox")

    def test_camoufox_home_is_per_user_on_posix(self):
        with patch.dict(os.environ, {"YELLOWFOX_DATA_DIR": "/tmp/yellowfox-data"}):
            self.assertEqual(platform_layout.camoufox_home("lin"),
                             Path("/tmp/yellowfox-data/camoufox"))
            self.assertEqual(platform_layout.camoufox_home("mac"),
                             Path("/tmp/yellowfox-data/camoufox"))

    def test_camoufox_home_stays_portable_on_windows(self):
        with patch.dict(os.environ, {"YELLOWFOX_DATA_DIR": "/tmp/yellowfox-data"}):
            self.assertEqual(platform_layout.camoufox_home("win"), PYTHON_DIR / ".camoufox")


if __name__ == "__main__":
    unittest.main()
