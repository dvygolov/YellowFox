import importlib.util
import unittest
from pathlib import Path

PYTHON_DIR = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("yellowfox_broker", PYTHON_DIR / "camoufox-broker.py")
broker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(broker)


class FakePage:
    def on(self, *args):
        pass


class FakeContext:
    def __init__(self, pages=None):
        self.pages = list(pages or [])

    def set_default_timeout(self, *args):
        pass

    def set_default_navigation_timeout(self, *args):
        pass

    def on(self, *args):
        pass


class FakeBrowser:
    def __init__(self, connected=True):
        self.connected = connected

    def is_connected(self):
        return self.connected


class FakeServerProcess:
    pid = 4242
    returncode = None

    def poll(self):
        return None


class BrokerStatusTests(unittest.TestCase):
    def make_state(self, pages, connected=True):
        return broker.BrokerState(None, FakeBrowser(connected), FakeServerProcess(), FakeContext(pages))

    def test_status_reports_open_pages(self):
        status = self.make_state([FakePage(), FakePage()]).status()
        self.assertTrue(status["browserConnected"])
        self.assertEqual(2, status["openPages"])

    def test_status_reports_zero_pages_after_the_last_window_closed(self):
        status = self.make_state([]).status()
        self.assertTrue(status["browserConnected"])
        self.assertEqual(0, status["openPages"])


if __name__ == "__main__":
    unittest.main()
