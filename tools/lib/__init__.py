"""Shared library for the tools layer: paths, items.json access, category sets, the
description-prose bake, and the FF16Tools nxd round-trip helpers.

Scripts run as `python tools\\foo.py` (not -m), so each script bootstraps sys.path with the
tools dir and imports `from lib... import ...`. Nothing in this package imports a deploy
script -- analyze.py is the CI gate and must depend only on library code, never on the
manual nxd deploy scripts (the import graph used to point the other way).
"""
