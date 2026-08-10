# Official updater fixture

`TUFHelperLite-0.1.2.dll` is the unmodified root `TUFHelperLite.dll` from the
official GitHub release asset:

- Release: `https://github.com/KGH1113/TUFHelperLite/releases/tag/v0.1.2`
- Asset: `TUFHelperLite.zip`
- Asset SHA-256: `037a2738d02c1f4a5d2fc88c1ac567236ba3c6c5edf0415e06a5a496fa18a88d`
- DLL SHA-256: `ffbb08d28d5189528f4d64906d90e22e9f22eb23a433409e158f983c6b31cc55`

The update tests verify the DLL hash before invoking its real package stager
and pending-update installer against the generated 0.1.4 ZIP.
