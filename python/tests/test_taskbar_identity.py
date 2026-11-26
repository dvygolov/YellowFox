import importlib.util
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
spec = importlib.util.spec_from_file_location(
    "camoufox_native_broker",
    Path(__file__).resolve().parents[1] / "camoufox-native-broker.py",
)
native = importlib.util.module_from_spec(spec)
spec.loader.exec_module(native)


def descendants(parents, created, root):
    with mock.patch.object(native.sys, "platform", "win32"), \
            mock.patch.object(native, "_process_api", return_value=None), \
            mock.patch.object(native, "_process_parents", return_value=parents), \
            mock.patch.object(native, "_process_creation_time", side_effect=lambda _, pid: created.get(pid)):
        return native.descendant_process_ids(root)


class DescendantProcessTests(unittest.TestCase):
    def test_children_and_grandchildren_are_included(self):
        parents = {100: 1, 200: 100, 300: 200}
        created = {100: 1000, 200: 1100, 300: 1200}
        self.assertEqual({100, 200, 300}, descendants(parents, created, 100))

    def test_window_process_of_another_browser_stays_out(self):
        # Windows keeps the original parent id after that parent exits, so a
        # recycled id can make an unrelated browser look like a child of ours.
        # The unrelated browser is older than the process that took over the id.
        parents = {100: 1, 200: 100, 500: 200}
        created = {100: 1000, 200: 9000, 500: 3000}
        self.assertEqual({100, 200}, descendants(parents, created, 100))

    def test_gone_processes_are_not_assumed_alive(self):
        parents = {100: 1, 200: 100, 300: 200}
        created = {100: 1000, 300: 1300}
        self.assertEqual({100}, descendants(parents, created, 100))


class AppWindowFilterTests(unittest.TestCase):
    """Firefox menus are owned WS_POPUP windows of the browser process; the
    identity refresh must not restyle them or they jump while open."""

    def test_popup_widgets_are_excluded(self):
        popup = 0x96000000  # WS_POPUP | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN
        self.assertFalse(native.is_taskbar_app_window(popup, 0x88, 12345))
        self.assertFalse(native.is_taskbar_app_window(popup, 0x88, 0))

    def test_owned_dialogs_without_taskbar_button_are_excluded(self):
        self.assertFalse(native.is_taskbar_app_window(0x16CF0000, 0x100, 12345))

    def test_real_browser_windows_are_included(self):
        self.assertTrue(native.is_taskbar_app_window(0x16CF0000, 0x100, 0))
        self.assertTrue(native.is_taskbar_app_window(0x16CF0000, 0x40100, 12345))


if __name__ == "__main__":
    unittest.main()
