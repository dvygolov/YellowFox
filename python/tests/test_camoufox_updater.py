import hashlib
import importlib.util
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
spec = importlib.util.spec_from_file_location('updater', Path(__file__).resolve().parents[1] / 'install-camoufox-browser.py')
u = importlib.util.module_from_spec(spec)
spec.loader.exec_module(u)


class UpdateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.patches = [patch.object(u, 'CONFIG_FILE', self.root / 'config.json'),
                        patch.object(u, 'BROWSERS_DIR', self.root / 'browsers'),
                        patch.object(u, 'COMPAT_FLAG', self.root / '.flag')]
        for item in self.patches:
            item.start()
        self.old = {'active_version': 'browsers/coryking/142.0.1-fork.26', 'pinned': '142.0.1-fork.26', 'channel': 'coryking/stable'}
        u.atomic_json(u.CONFIG_FILE, self.old)
        old_dir = u.BROWSERS_DIR / 'coryking/142.0.1-fork.26'
        old_dir.mkdir(parents=True)
        (old_dir / 'camoufox.exe').write_bytes(b'old')
        u.atomic_json(old_dir / 'version.json', {'version': '142.0.1', 'build': 'fork.26'})

    def tearDown(self):
        for item in reversed(self.patches):
            item.stop()
        self.temp.cleanup()

    def archive(self, unsafe=False, incomplete=False):
        path = self.root / 'browser.zip'
        with zipfile.ZipFile(path, 'w') as z:
            z.writestr('camoufox.exe', b'new')
            if not incomplete:
                z.writestr('application.ini', b'version')
                z.writestr('omni.ja', b'resources')
                z.writestr('browser/omni.ja', b'browser resources')
            if unsafe:
                z.writestr('../escape.txt', b'unsafe')
        asset = {'version': '152.0.4', 'build': 'beta.30', 'asset_size': path.stat().st_size,
                 'digest': 'sha256:' + hashlib.sha256(path.read_bytes()).hexdigest()}
        return path, asset

    def test_legacy_current_repo_is_detected(self):
        state = u.current_install_state()
        self.assertTrue(state['installed'])
        self.assertEqual('coryking', state['repo'])
        self.assertEqual('142.0.1', state['version'])

    def test_full_install_keeps_old_and_atomically_switches(self):
        path, asset = self.archive()
        result = u.install_zip(path, asset)
        self.assertTrue(result['installed'])
        self.assertEqual('official', result['repo'])
        self.assertEqual(self.old['active_version'], u.read_config()['previous_version'])
        self.assertEqual(b'old', (u.BROWSERS_DIR / 'coryking/142.0.1-fork.26/camoufox.exe').read_bytes())
        self.assertTrue((u.BROWSERS_DIR / 'official/152.0.4-beta.30/browser/omni.ja').exists())
        self.assertFalse(u.is_update_available(result, asset))

    def test_bad_digest_does_not_change_active_version(self):
        path, asset = self.archive()
        asset['digest'] = 'sha256:' + '0' * 64
        with self.assertRaisesRegex(RuntimeError, 'checksum'):
            u.install_zip(path, asset)
        self.assertEqual(self.old, u.read_config())
        self.assertFalse(path.exists())

    def test_incomplete_archive_does_not_activate(self):
        path, asset = self.archive(incomplete=True)
        with self.assertRaisesRegex(RuntimeError, 'Incomplete'):
            u.install_zip(path, asset)
        self.assertEqual(self.old, u.read_config())

    def test_zip_traversal_does_not_escape_or_activate(self):
        path, asset = self.archive(unsafe=True)
        with self.assertRaisesRegex(RuntimeError, 'Unsafe'):
            u.install_zip(path, asset)
        self.assertEqual(self.old, u.read_config())
        self.assertFalse(list(self.root.rglob('escape.txt')))

    def test_version_order_uses_numbers_not_dates_or_lexical_order(self):
        self.assertGreater(u.version_key('152.0.4-beta.30'), u.version_key('152.0.4-beta.9'))
        self.assertGreater(u.version_key('153.0.1-beta.1'), u.version_key('152.0.4-beta.99'))
        current = {'installed': True, 'folder': '153.0.1-beta.1'}
        self.assertFalse(u.is_update_available(current, {'version': '152.0.4', 'build': 'beta.30'}))

    def test_path_in_version_is_rejected(self):
        with self.assertRaises(ValueError):
            u.version_key('../../152.0.4-beta.30')

    def test_prerelease_and_missing_windows_asset_rejected(self):
        with self.assertRaisesRegex(RuntimeError, 'stable'):
            u.asset_from_release({'prerelease': True})
        with self.assertRaisesRegex(RuntimeError, 'Windows'):
            u.asset_from_release({'tag_name': 'v152.0.4-beta.30', 'assets': []})

    def test_activation_failure_preserves_config_and_allows_retry(self):
        path, asset = self.archive()
        atomic = u.atomic_json
        def fail_config(target, value):
            if target == u.CONFIG_FILE:
                raise OSError('simulated disk error')
            return atomic(target, value)
        with patch.object(u, 'atomic_json', side_effect=fail_config):
            with self.assertRaises(OSError):
                u.install_zip(path, asset)
        self.assertEqual(self.old, u.read_config())
        self.assertTrue(u.install_zip(path, asset)['installed'])

if __name__ == '__main__':
    unittest.main()
