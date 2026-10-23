import copy
import tempfile
import unittest
from pathlib import Path
import core
import devices

class ConfigTests(unittest.TestCase):
    def test_round_trip(self):
        data = core.defaults()
        data['assignments']['USB/example'] = 'seat-1'
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'nested' / 'config.json'
            core.save(path, data)
            self.assertEqual(core.load(path), data)
            self.assertEqual(list(path.parent.glob('*.tmp')), [])

    def test_reject_bad_reference_without_overwriting(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'config.json'
            data = core.defaults()
            core.save(path, data)
            data['assignments']['USB/example'] = 'missing-seat'
            with self.assertRaises(ValueError):
                core.save(path, data)
            self.assertEqual(core.load(path), core.defaults())

    def test_reassignment_has_one_owner(self):
        data = core.defaults()
        data['assignments']['keyboard'] = 'seat-1'
        data['assignments']['keyboard'] = 'seat-2'
        self.assertEqual(len(core.validate(data)['assignments']), 1)

    def test_offline_and_missing_devices(self):
        data = core.defaults()
        data['assignments']['missing-keyboard'] = 'seat-1'
        issues = core.inspect(data, [])
        self.assertTrue(any('离线' in x for x in issues))
        self.assertTrue(any('键盘' in x for x in issues))

    def test_complete_demo_still_cannot_run(self):
        data = core.defaults()
        rows = devices.demo()
        for row in rows:
            data['assignments'][row['id']] = 'seat-' + row['id'].split('/')[1]
        self.assertEqual(len(core.inspect(data, rows)), 1)
        self.assertIn('未实现', core.inspect(data, rows)[0])

    def test_invalid_inputs(self):
        candidates = [None, {}, {'version': 2}, dict(core.defaults(), seats=[]), dict(core.defaults(), assignments=[])]
        duplicate = core.defaults()
        duplicate['seats'][1]['id'] = 'seat-1'
        candidates.append(duplicate)
        too_many = core.defaults()
        too_many['seats'] = [{'id': str(n), 'name': '工位'} for n in range(13)]
        candidates.append(too_many)
        for data in candidates:
            with self.subTest(data=data), self.assertRaises(ValueError):
                core.validate(data)

if __name__ == '__main__':
    unittest.main()
