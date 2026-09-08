"""Clean-break guard shared by every standalone capability-host Python worker."""

import os

_LEGACY_PREFIX = "HULL_"
_REPLACEMENT_PREFIX = "CAPABILITY_HOST_"


def assert_no_legacy_operational_variables(environment=None):
    """Refuse stale capability-host configuration before any worker reads its env."""
    environment = os.environ if environment is None else environment
    legacy_name = next(
        (name for name in sorted(environment) if name.startswith(_LEGACY_PREFIX)),
        None,
    )
    if legacy_name is None:
        return
    replacement = _REPLACEMENT_PREFIX + legacy_name[len(_LEGACY_PREFIX):]
    raise RuntimeError(
        f"Legacy capability-host environment variable {legacy_name} is not supported; "
        f"use {replacement}."
    )
