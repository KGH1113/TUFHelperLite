import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import sys
import unittest

sys.dont_write_bytecode = True

module_path = Path(__file__).resolve().parents[2] / "lib" / "bootstrap_install.py"
spec = importlib.util.spec_from_file_location("bootstrap_install", module_path)
bootstrap = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bootstrap)


class BootstrapInstallTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="helper bootstrap install ")
        self.root = Path(self.scratch.name) / "Mods" / "TUFHelperLite"
        self.source = Path(self.scratch.name) / "AdofaiIpc.Bootstrap.dll"
        self.source.write_bytes(b"checksum-pinned bootstrap 1.0.0")
        self.sha = hashlib.sha256(self.source.read_bytes()).hexdigest()
        self.directory = self.root / "DependencyBootstrap"
        self.state_path = self.directory / "state.json"

    def tearDown(self):
        self.scratch.cleanup()

    def install(self):
        return bootstrap.activate(self.root, self.source, "1.0.0", self.sha)

    def state(self, current="0.3.0", previous="0.2.0", trial="0.4.1"):
        return {"SchemaVersion": 1, "Current": current, "Previous": previous, "Trial": trial}

    def write_state(self, state):
        self.directory.mkdir(parents=True, exist_ok=True)
        self.state_path.write_text(json.dumps(state))

    def test_existing_install_promotes_pinned_candidate_and_preserves_history(self):
        state = self.state()
        self.write_state(state)
        old = self.directory / "versions" / "0.3.0" / "AdofaiIpc.Bootstrap.dll"
        old.parent.mkdir(parents=True)
        old.write_bytes(b"historical bootstrap")
        backup = self.directory / "state.json.bak"
        backup.write_text(json.dumps(self.state("0.2.0", None, None)))
        old_backup = backup.read_bytes()
        candidate = self.install()
        self.assertEqual(candidate.read_bytes(), self.source.read_bytes())
        active = json.loads(self.state_path.read_text())
        self.assertEqual(active, self.state("1.0.0", "0.3.0", None))
        self.assertEqual(old.read_bytes(), b"historical bootstrap")
        self.assertEqual(json.loads(backup.read_text()), state)
        history = list((self.directory / "install-history").iterdir())
        self.assertTrue(any(path.read_bytes() == old_backup for path in history))
        self.install()
        self.assertEqual(history, list((self.directory / "install-history").iterdir()))
        self.assertEqual(active, json.loads(self.state_path.read_text()))

    def test_fresh_install_seeds_selected_candidate(self):
        self.install()
        self.assertEqual(json.loads(self.state_path.read_text()), self.state("1.0.0", None, None))

    def test_checksum_mismatch_rejects_before_any_state_write(self):
        self.write_state(self.state())
        original = self.state_path.read_bytes()
        self.source.write_bytes(b"wrong artifact")
        with self.assertRaises(ValueError):
            self.install()
        self.assertEqual(self.state_path.read_bytes(), original)
        self.assertFalse((self.directory / "versions").exists())

    def test_stale_candidate_is_preserved_and_repaired(self):
        self.write_state(self.state("1.0.0", "0.3.0", None))
        candidate = self.directory / "versions" / "1.0.0" / "AdofaiIpc.Bootstrap.dll"
        candidate.parent.mkdir(parents=True)
        candidate.write_bytes(b"stale candidate with the same version")
        self.install()
        self.assertEqual(candidate.read_bytes(), self.source.read_bytes())
        self.assertTrue(any(path.read_bytes() == b"stale candidate with the same version"
                            for path in (self.directory / "install-history").iterdir()))
        self.assertEqual(json.loads(self.state_path.read_text())["Previous"], "0.3.0")

    def test_corrupt_state_recovers_valid_backup_and_keeps_original(self):
        self.write_state(self.state())
        (self.directory / "state.json.bak").write_bytes(self.state_path.read_bytes())
        self.state_path.write_bytes(b"corrupt state")
        self.install()
        self.assertEqual(json.loads(self.state_path.read_text())["Current"], "1.0.0")
        self.assertTrue(any(path.read_bytes() == b"corrupt state"
                            for path in (self.directory / "install-history").iterdir()))

    def test_invalid_state_does_not_activate_or_overwrite(self):
        self.write_state({"SchemaVersion": 2, "Current": "../../untrusted"})
        original = self.state_path.read_bytes()
        with self.assertRaises((ValueError, FileNotFoundError)):
            self.install()
        self.assertEqual(self.state_path.read_bytes(), original)
        self.assertFalse((self.directory / "versions").exists())


if __name__ == "__main__":
    unittest.main()
