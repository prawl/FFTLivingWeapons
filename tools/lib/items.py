"""data/items.json access shared by the pipeline and the manual scripts."""
import json
from pathlib import Path

from .paths import ITEMS


def load_items(path=None):
    """Parse data/items.json (or an explicit override path) into the full doc dict."""
    p = Path(path) if path is not None else ITEMS
    return json.loads(p.read_text(encoding="utf-8"))


def display_name(it):
    """The name an item renders with: the overhaul name unless unset/TBD, else vanilla."""
    name = it.get("name")
    return name if name not in (None, "TBD") else it["vanillaName"]
