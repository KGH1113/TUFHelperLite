"""Activate the checksum-pinned bootstrap during a cold local installation."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import tempfile

_VERSION = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?")


def _hash(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(64 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _read_state(path):
    state = json.loads(path.read_text())
    if not isinstance(state, dict) or state.get("SchemaVersion") != 1:
        raise ValueError("Unsupported dependency bootstrap state.")
    for name in ("Current", "Previous", "Trial"):
        value = state.get(name)
        if (name == "Current" or value is not None) and (not isinstance(value, str) or not _VERSION.fullmatch(value)):
            raise ValueError("Invalid dependency bootstrap version in " + name + ".")
    return state


def _atomic_write(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=path.name + ".tmp-", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(content)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        if temporary is not None and temporary.exists():
            temporary.unlink()


def _preserve(path, history):
    if not path.exists():
        return
    destination = history / (path.name + "-" + _hash(path))
    if not destination.exists():
        history.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, destination)


def activate(mod_root, source, version, expected_sha):
    root, source = Path(mod_root).resolve(), Path(source).resolve()
    if root == Path(root.anchor) or not _VERSION.fullmatch(version):
        raise ValueError("A mod folder and canonical bootstrap version are required.")
    if not re.fullmatch(r"[0-9a-f]{64}", expected_sha) or _hash(source) != expected_sha:
        raise ValueError("Packaged bootstrap does not match its locked SHA256.")

    directory = root / "DependencyBootstrap"
    state_path = directory / "state.json"
    backup_path = directory / "state.json.bak"
    history = directory / "install-history"
    previous = None
    if state_path.exists() or backup_path.exists():
        try:
            previous = _read_state(state_path)
        except (ValueError, FileNotFoundError):
            previous = _read_state(backup_path)

    candidate = directory / "versions" / version / "AdofaiIpc.Bootstrap.dll"
    if not candidate.exists() or _hash(candidate) != expected_sha:
        _preserve(candidate, history)
        _atomic_write(candidate, source.read_bytes())
        if _hash(candidate) != expected_sha:
            raise ValueError("Installed bootstrap failed checksum verification.")

    state = dict(previous or {"SchemaVersion": 1, "Previous": None})
    old_current = state.get("Current")
    if old_current and old_current != version:
        state["Previous"] = old_current
    state.update(SchemaVersion=1, Current=version, Trial=None)
    if not state_path.exists() or _readable_equals(state_path, state) is False:
        _preserve(state_path, history)
        _preserve(backup_path, history)
        if previous is not None:
            _atomic_write(backup_path, (json.dumps(previous, indent=2) + "\n").encode())
        _atomic_write(state_path, (json.dumps(state, indent=2) + "\n").encode())
    return candidate


def _readable_equals(path, state):
    try:
        return _read_state(path) == state
    except (ValueError, FileNotFoundError):
        return False


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mod_root")
    parser.add_argument("source")
    parser.add_argument("version")
    parser.add_argument("sha256")
    args = parser.parse_args()
    activated = activate(args.mod_root, args.source, args.version, args.sha256)
    print("Activated pinned dependency bootstrap:", activated)
