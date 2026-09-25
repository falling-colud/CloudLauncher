#!/usr/bin/env python3
"""
Slate Icons: a TrueType font that redraws the Segoe MDL2 Assets codepoints CloudLauncher uses as
Slate's 16x16 pixel icons.

Why a font: the launcher draws every icon as an MDL2 private-use codepoint in a TextBlock whose
FontFamily is the IconFont resource. With IconFont = "Slate Icons, Segoe MDL2 Assets", WPF takes
each character from the first family that has it, so the codepoints mapped here come out as Slate
glyphs and any codepoint this font lacks still comes from MDL2. No XAML has to change. (That
per-character fallback only happens because this font does not look like a symbol font to
DirectWrite - see _panose - and validate_font guards it.)

Where the glyphs come from:
  * Slate's own glyphs are read from Slate's tools/icons.py (imported, not copied), so an edit to a
    Slate icon flows into the font on the next run.
  * Glyphs Slate does not have are drawn below (NEW_GLYPHS) in the same ASCII format and to the
    same rules ('#' opaque, '.' transparent, 2 px main strokes, 1 px detail, content in rows and
    columns 2..13, round shapes may touch 1..14, optically centred).
  * The four window-caption glyphs are drawn on a 10 x 10 grid instead (CAPTION_GLYPHS). The title
    bar draws them at FontSize 10, where one cell of a 10-grid is exactly one pixel, so their lines
    come out crisp and 1 px - MDL2's own caption glyphs are built the same way.
  * '+' (50% alpha) pixels are dropped: a font has no alpha. The only mapped glyph that has any
    is BLOCK, which still reads as a cube without its shaded faces.

Metrics match Segoe MDL2 Assets exactly, so swapping the font moves nothing: unitsPerEm 2048,
ascent 2048, descent 0, line gap 0, every advance 2048. On the 16-grid, pixel (column c, row r
from the top) is the square x 128c..128c+128, y 2048-128(r+1)..2048-128r. Each glyph is the union
of its pixels, traced into as few contours as possible (outer clockwise, holes counter-clockwise).

Usage:
  python build_slate_icons.py               build the font and the preview sheet
  python build_slate_icons.py --check       validate the glyphs and the map only, write nothing
  python build_slate_icons.py --review DIR  also write zoomed per-glyph sheets (32/16/13/12/10 px)
Options: --slate PATH (Slate's icons.py; env SLATE_ICONS_PY), --font PATH, --preview PATH.
Needs: fontTools, Pillow.
"""
from __future__ import annotations

import argparse
import importlib.util
import os
import re
import sys

from fontTools.fontBuilder import FontBuilder
from fontTools.misc.timeTools import timestampFromString
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.ttLib import TTFont, newTable
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
CLIENT_DIR = os.path.join(REPO, "CloudLauncher")
DEFAULT_SLATE = os.environ.get("SLATE_ICONS_PY", "icons.py")
DEFAULT_FONT = os.path.join(CLIENT_DIR, "Assets", "Fonts", "SlateIcons.ttf")
DEFAULT_PREVIEW = os.path.join(HERE, "preview.png")
MDL2_PATH = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", "segmdl2.ttf")

FAMILY = "Slate Icons"
PS_NAME = "SlateIcons-Regular"
VERSION = "1.000"
COPYRIGHT = "Slate icons by falling_colud, MIT"
UPM = 2048

# ----------------------------------------------------------------------------------------------
# The map. Every MDL2 codepoint the client uses (XAML &#xE...;, C# "\uE...", 0xE... glyph
# constants and literal private-use characters), what it means there, and the glyph it gets.
# Several codepoints may share a glyph. "-" instead of a glyph name leaves that codepoint to MDL2.
# The [bracket] is MDL2's own name for the codepoint (or what it looks like, where it has none).
# The build warns about any private-use codepoint in the client that is missing from this table.
# ----------------------------------------------------------------------------------------------
CODEPOINTS = """
E700  MENU              # sidebar "Your Minecraft" header                                  [GlobalNavigationButton]
E706  SHADER            # shader packs: nav, storage category, turn a shader on           [Brightness]
E70B  TEXT              # a mod's note (cards, graph, plan board); Slate's note glyph      [QuickNote]
E70D  CHEVRON_DOWN      # expand, dropdown arrow, natural sort order                       [ChevronDown]
E70E  CHEVRON_UP        # collapse, reversed sort order                                    [ChevronUp]
E70F  EDIT              # edit, open in the editor, change, manage files                   [Edit]
E710  PLUS              # add, create, new                                                 [Add]
E711  CLOSE             # cancel, clear, close a card, stop a running job                  [Cancel]
E712  DOTS              # more actions                                                     [More]
E713  SETTINGS          # settings nav, config & scripts category, console settings        [Setting]
E715  INVITE            # activity: invite sent / accepted                                 [Mail]
E716  GROUP             # teams, collaborators, team members                               [People]
E718  PIN               # pin                                                              [Pin]
E71A  STOP              # stop the server / a transfer                                     [Stop]
E71B  LINK              # links, dependencies, copy a share link, the "Shared" source      [Link]
E71C  FILTER            # filters                                                          [Filter]
E71D  LIST              # "All" chips, resource-pack load order                            [AllApps]
E71F  ZOOM_OUT          # zoom out (mod graph, plan board)                                 [ZoomOut]
E721  SEARCH            # search, find in files, filtered-empty states                     [Search]
E72B  ARROW_LEFT        # back                                                             [Back]
E72C  REFRESH           # refresh, reload, re-read                                         [Refresh]
E72D  SHARE             # sharing nav, "shared with me", shared / unshared activity        [Share]
E72E  LOCK              # update locked                                                    [Lock]
E734  STAR              # "show only your defaults"                                        [FavoriteStar]
E73E  CHECK             # check mark, enable, identical files, set as default              [CheckMark]
E748  USER_CHECK        # Microsoft account (offline accounts are E77B, plain USER)        [SwitchUser]
E74A  ARROW_UP          # move up / to the top, up one folder, previous match              [Up]
E74B  ARROW_DOWN        # move down / to the bottom, next match                            [Down]
E74C  MODS              # mods storage category, "no mods yet"                             [OEM]
E74D  TRASH             # delete, remove, recently deleted                                 [Delete]
E74E  SAVE              # save, save as                                                    [Save]
E753  CLOUD             # CurseForge / Modrinth sources, cloud, the launcher's own mark    [Cloud]
E756  TERMINAL          # server console, the Java runtime category                        [CommandPrompt]
E762  GRID              # select all; Slate's select-all glyph                             [MultiSelect]
E768  PLAY              # play, launch, start, turn on                                     [Play]
E769  PAUSE             # pause a transfer                                                 [Pause]
E76B  CHEVRON_LEFT      # previous, show the instance page                                 [ChevronLeft]
E76C  CHEVRON_RIGHT     # next, folded group, hide the instance page                       [ChevronRight]
E772  LAN               # a mod's side: client, server or both                             [Devices]
E774  WORLD             # the web: open website / page / link, public, store; (Servers nav) [Globe]
E777  SYNC              # update available (download the new version)                      [UpdateRestore]
E77A  PIN_OFF           # unpin                                                            [Unpin]
E77B  USER              # account, personal, offline account                               [Contact]
E77F  IMPORT            # paste; Slate's paste glyph                                       [Paste]
E783  ERROR             # comparison failed                                                [Error]
E7B3  EYE               # visibility                                                       [RedEye]
E7BA  WARNING           # warnings, crash reports, delete confirmation, dev tools nav      [Warning]
E7C1  TEAM              # a mod's priority (Slate's TEAM glyph is a flag)                  [Flag]
E7C3  PAGE              # instances nav, an instance, open a page, logs, changelog         [Page]
E7C9  PACK              # resource packs nav + empty states; Slate's resource-pack glyph   [TouchPointer]
E7EE  USER              # profile storage category (settings, caches)                      [OtherUser]
E7F4  MONITOR           # an instance in the storage list                                  [TVMonitor]
E7F8  BLOCK             # runtime storage: libraries, assets, version jars                 [DeviceLaptopNoPic]
E81C  HISTORY           # update to version..., activity history                           [History]
E838  FOLDER            # show in Explorer (storage page), the storage page                [FolderOpen]
E895  SYNC              # apply, update, restart the server, ping now                      [Sync]
E896  DOWNLOAD          # download, install, browse the stores                             [Download]
E897  HELP              # help ("what does low mode do?")                                  [Help]
E898  UPLOAD            # upload, import, publish a version                                [Upload]
E89B  EXIT              # leave team                                                       [LeaveChat]
E8A3  ZOOM_IN           # zoom in (mod graph, plan board)                                  [ZoomIn]
E8A5  DOCUMENT          # a file: new file, pick a file, recent files, file kinds          [Document]
E8A7  EXTERNAL          # open save folder (opens a window outside the launcher)           [OpenInNewWindow]
E8AB  SWAP              # swap the two sides, update channel                               [Switch]
E8AC  RENAME            # rename                                                           [Rename]
E8B7  FOLDER            # a folder, reveal in Explorer                                     [Folder]
E8B9  EXTERNAL          # open with Windows, open a screenshot, open the share tab         [Photo2]
E8BB  CAPTION_CLOSE     # window close button, small close buttons (panel, tab, find bar)  [ChromeClose]
E8C6  CUT               # cut                                                              [Cut]
E8C8  COPY              # copy, copy to other instances, copy path / ID                    [Copy]
E8CB  SORT              # sort                                                             [Sort]
E8D7  CROWN             # transfer ownership, hand the team over                           [Permissions]
E8DA  FOLDER            # show in Explorer, open folder; Slate's open-folder glyph         [OpenLocal]
E8DE  IMPORT            # install / copy into another instance                             [MoveToFolder]
E8E1  SLIDERS           # sync rules                                                       [Like]
E8E5  DOCUMENT          # a file on disk, import a description from a file                 [OpenFile]
E8EC  TAG               # category, put it in..., compare across instances                 [Tag]
E8F1  LIBRARY           # folders (saved views), the shared library, library mods          [Library]
E8F2  SHARE             # "shared with me"                                                 [ChatBubbles]
E8F4  FOLDER_ADD        # new folder                                                       [NewFolder]
E8F7  BACKUP            # back up now                                                      [SyncFolder]
E8FD  LIST              # manage modpack, file management                                  [BulletedList]
E909  WORLD             # worlds nav, worlds category, "no worlds"                         [World]
E91B  PACK              # resource packs storage category                                  [Photo]
E921  CAPTION_MINIMIZE  # window minimize                                                  [ChromeMinimize]
E922  CAPTION_MAXIMIZE  # window maximize                                                  [ChromeMaximize]
E923  CAPTION_RESTORE   # window restore                                                   [ChromeRestore]
E943  CODE              # config & scripts nav, KubeJS scripts                             [Code]
E946  INFO              # info                                                             [Info]
E968  SERVER            # server running, server files, servers page                       [Network]
E9D9  SIGNAL            # content-size meter, mod graph, diagnostics                       [Diagnostic]
EB9F  IMAGE             # screenshots, set as instance image                               [picture]
EC4A  MODS              # mods nav                                                         [gauge]
EC7A  FLASK             # a mod marked "testing"                                           [DeveloperTools]
ED1A  EYE_OFF           # show hidden server entries                                       [Hide]
EDA2  STORAGE           # storage nav                                                      [HardDrive]
EDE1  EXPORT            # zip selection, export as .zip                                    [Export]
EDE3  LIST              # "All" chip (resource packs), import from an empty state          [list in a circle]
EDE4  LIBRARY           # resource-pack folder chips                                       [cloud + magnifier]
F12B  IMPORT            # extract here (unzip)                                             [folder]
"""


def parse_codepoints(text: str) -> list[tuple[int, str, str, str]]:
    """(codepoint, glyph name or '-', meaning, MDL2 name) per table row."""
    out = []
    seen = set()
    for n, raw in enumerate(text.strip("\n").splitlines(), 1):
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        m = re.match(r"^([0-9A-Fa-f]{4,5})\s+(\S+)\s*(?:#\s*(.*?))?\s*(?:\[(.*)\])?\s*$", line)
        if not m:
            raise SystemExit(f"CODEPOINTS line {n}: cannot parse {raw!r}")
        cp = int(m.group(1), 16)
        if cp in seen:
            raise SystemExit(f"CODEPOINTS: {cp:04X} listed twice")
        seen.add(cp)
        out.append((cp, m.group(2), (m.group(3) or "").strip(), (m.group(4) or "").strip()))
    return out


# ----------------------------------------------------------------------------------------------
# Glyphs Slate does not have. Same format and rules as Slate's icons.py.
# ----------------------------------------------------------------------------------------------
NEW_GLYPHS: dict[str, list[str]] = {}
CAPTION_GLYPHS: dict[str, list[str]] = {}


def _grid(name: str, art: str, size: int) -> list[str]:
    rows = art.strip("\n").splitlines()
    if len(rows) != size:
        raise SystemExit(f"{name}: expected {size} rows, got {len(rows)}")
    for i, row in enumerate(rows):
        if len(row) != size:
            raise SystemExit(f"{name}: row {i} has {len(row)} chars, expected {size}: {row!r}")
        bad = set(row) - set(".#+")
        if bad:
            raise SystemExit(f"{name}: row {i} has invalid chars {sorted(bad)}")
    if not name.isidentifier() or name != name.upper():
        raise SystemExit(f"{name}: must be an UPPER_CASE identifier")
    if name in NEW_GLYPHS or name in CAPTION_GLYPHS:
        raise SystemExit(f"{name}: duplicate glyph name")
    return rows


def icon(name: str, art: str) -> None:
    """A new 16x16 glyph in Slate's style."""
    NEW_GLYPHS[name] = _grid(name, art, 16)


def caption(name: str, art: str) -> None:
    """A window-caption glyph on a 10x10 grid: 1 cell = exactly 1 px at FontSize 10."""
    CAPTION_GLYPHS[name] = _grid(name, art, 10)


# A blank page with its top-right corner folded over.
icon("PAGE", """
................
................
...######.......
...#######......
...##...###.....
...##...####....
...##...#####...
...##......##...
...##......##...
...##......##...
...##......##...
...##......##...
...##########...
...##########...
................
................
""")

# The same page with two lines of text on it.
icon("DOCUMENT", """
................
................
...######.......
...#######......
...##...###.....
...##...####....
...##...#####...
...##......##...
...##.####.##...
...##......##...
...##.####.##...
...##......##...
...##########...
...##########...
................
................
""")

# Two books standing up and one leaning on them.
icon("LIBRARY", """
................
................
.###............
.###.###........
.###.###..###...
.###.###..###...
.###.###...###..
.###.###...###..
.###.###...###..
.###.###...###..
.###.###....###.
.###.###....###.
.###.###....###.
.###.###....###.
................
................
""")

# FOLDER with a plus cut out of it.
icon("FOLDER_ADD", """
................
................
................
..#####.........
..######........
..############..
..#####..#####..
..#####..#####..
..###......###..
..###......###..
..#####..#####..
..#####..#####..
..############..
................
................
................
""")

# Scissors: an X like CLOSE whose lower arms end in finger rings.
icon("CUT", """
................
................
..##........##..
...##......##...
....##....##....
.....##..##.....
......####......
.......##.......
......####......
.....##..##.....
..####....####..
.##..##..##..##.
.##..##..##..##.
..####....####..
................
................
""")

# USER with a check cut into the shoulder: a signed-in (Microsoft) account, next to plain USER for
# an offline one. Reads at 12 px, where a swap-arrows badge (MDL2's SwitchUser) turns to noise.
icon("USER_CHECK", """
................
................
....####........
...######.......
...######.......
...######.......
....####........
.............##.
...######...##..
..########.##...
.#######.####...
.########.##....
.#########......
.##########.....
................
................
""")

# SORT turned on its side: two arrows passing each other.
icon("SWAP", """
................
................
..........#.....
..........##....
...##########...
...##########...
..........##....
..........#.....
.....#..........
....##..........
...##########...
...##########...
....##..........
.....#..........
................
................
""")

# A text field with a word in it and the text cursor after it.
icon("RENAME", """
................
................
.........##..##.
...........##...
...........##...
.#########.##...
.#.........##...
.#.######..##...
.#.######..##...
.#.........##...
.#########.##...
...........##...
...........##...
.........##..##.
................
................
""")

# SEARCH with a plus / a minus in the lens.
icon("ZOOM_IN", """
................
................
.....####.......
...########.....
..###....###....
..##..##..##....
..##.####.##....
..##.####.##....
..##..##..##....
..###....###....
...########.....
.....####.##....
...........##...
............##..
................
................
""")

icon("ZOOM_OUT", """
................
................
.....####.......
...########.....
..###....###....
..##......##....
..##.####.##....
..##.####.##....
..##......##....
..###....###....
...########.....
.....####.##....
...........##...
............##..
................
................
""")

# Three nodes, one handing on to two (Lucide share-2).
icon("SHARE", """
................
................
...........##...
..........####..
........######..
......####.##...
...##.##........
..####..........
..####..........
...##.##........
......####.##...
........######..
..........####..
...........##...
................
................
""")

# PIN with the slash every Slate *_OFF glyph uses.
icon("PIN_OFF", """
................
................
..##...##.......
...##.####......
....##.###......
.....##.##......
......##.#......
....##.##.##....
...####.##.##...
...#####.##.#...
.......##.##....
.......##..##...
.......##...##..
.......#........
................
................
""")

# REFRESH with a second arrow: the top half of REFRESH over the bottom half turned round.
icon("SYNC", """
................
................
......####......
....########....
...###...#####..
...##.....###...
..##.......#....
..##............
............##..
....#.......##..
...###.....##...
..#####...###...
....########....
......####......
................
................
""")

# A conical flask with liquid in it (Lucide flask-conical).
icon("FLASK", """
................
................
.....######.....
.....######.....
......#..#......
......#..#......
.....##..##.....
.....#....#.....
....##....##....
....#......#....
...##......##...
...##########...
..############..
..############..
................
................
""")

# Storage: a database cylinder, open lid over two bands. (A hard drive read as a printer or a
# basket at 12 px; three plain bands would look like MENU, which sits in the same sidebar.)
icon("STORAGE", """
................
................
....########....
..############..
..##........##..
...##########...
..#..........#..
..############..
..############..
...##########...
..#..........#..
..############..
..############..
....########....
................
................
""")

# A bare question mark for help buttons. Slate's QUESTION (a disc with the mark cut out of it) turns
# into a plain dot at the 12 px of the launcher's help button, whose tooltip says "click the ?".
icon("HELP", """
................
................
......####......
....########....
...###....###...
...##......##...
...........##...
.........####...
........###.....
.......##.......
.......##.......
................
.......##.......
.......##.......
................
................
""")

# Window caption glyphs, 10x10 like MDL2's: they fill the em and are 1 px strokes at FontSize 10.
caption("CAPTION_MINIMIZE", """
..........
..........
..........
..........
##########
..........
..........
..........
..........
..........
""")

caption("CAPTION_MAXIMIZE", """
##########
#........#
#........#
#........#
#........#
#........#
#........#
#........#
#........#
##########
""")

caption("CAPTION_RESTORE", """
..########
..#......#
########.#
#......#.#
#......#.#
#......#.#
#......#.#
#......###
#......#..
########..
""")

# Two-cell steps like Slate's CLOSE: a one-cell staircase is crisp at 10 px but breaks into beads at
# the 14 px "Kill instance" button (21 px at 150%), which uses this codepoint too.
caption("CAPTION_CLOSE", """
##......##
.##....##.
..##..##..
...####...
....##....
....##....
...####...
..##..##..
.##....##.
##......##
""")


# ----------------------------------------------------------------------------------------------
# Slate's glyphs
# ----------------------------------------------------------------------------------------------
def load_slate(path: str) -> dict[str, list[str]]:
    """Slate's glyph list, by importing its icons.py (it only writes files when run as a script)."""
    if not os.path.isfile(path):
        raise SystemExit(f"Slate icons.py not found at {path} (pass --slate or set SLATE_ICONS_PY)")
    spec = importlib.util.spec_from_file_location("slate_icons_source", path)
    module = importlib.util.module_from_spec(spec)
    was = sys.dont_write_bytecode
    sys.dont_write_bytecode = True  # leave no __pycache__ behind in Slate's tree
    try:
        spec.loader.exec_module(module)
    finally:
        sys.dont_write_bytecode = was
    return {name: list(rows) for name, rows in module.GLYPHS}


# ----------------------------------------------------------------------------------------------
# Outlines
# ----------------------------------------------------------------------------------------------
def opaque_pixels(rows: list[str]) -> set[tuple[int, int]]:
    return {(c, r) for r, row in enumerate(rows) for c, ch in enumerate(row) if ch == "#"}


def trace(pixels: set[tuple[int, int]]) -> list[list[tuple[int, int]]]:
    """Outline of a union of grid pixels as closed polygons over grid-line coordinates (x = column
    line, y = row line counted from the top). In font space (y up) outer contours run clockwise and
    holes counter-clockwise, TrueType's convention, and no polygon touches itself."""
    edges: set[tuple[tuple[int, int], tuple[int, int]]] = set()
    for c, r in pixels:
        bl, tl, tr, br = (c, r + 1), (c, r), (c + 1, r), (c + 1, r + 1)
        for a, b in ((bl, tl), (tl, tr), (tr, br), (br, bl)):
            if (b, a) in edges:
                edges.remove((b, a))  # shared with a neighbour: inside the shape
            else:
                edges.add((a, b))
    outgoing: dict[tuple[int, int], list[tuple[int, int]]] = {}
    for a, b in edges:
        outgoing.setdefault(a, []).append(b)

    def turn(prev, cur, nxt) -> int:
        # cross product in font space (y up): negative = right (clockwise) turn
        dx1, dy1 = cur[0] - prev[0], -(cur[1] - prev[1])
        dx2, dy2 = nxt[0] - cur[0], -(nxt[1] - cur[1])
        return dx1 * dy2 - dy1 * dx2

    contours = []
    while outgoing:
        # start where only one edge leaves (every contour has such corners), so the turn rule below
        # decides every corner where two pixels touch diagonally
        start = min(v for v, options in outgoing.items() if len(options) == 1)
        path = [start]
        prev, cur = None, start
        while True:
            options = outgoing[cur]
            if len(options) == 1 or prev is None:
                nxt = options[0]
            else:
                # two pixels meeting only at this corner: take the right turn, which keeps each
                # pixel run in its own contour instead of a figure eight
                nxt = min(options, key=lambda o: turn(prev, cur, o))
            options.remove(nxt)
            if not options:
                del outgoing[cur]
            prev, cur = cur, nxt
            if cur == start:
                break
            path.append(cur)
        for loop in _split_pinches(path):
            # drop the points in the middle of straight runs
            n = len(loop)
            contours.append([loop[i] for i in range(n) if turn(loop[i - 1], loop[i], loop[(i + 1) % n]) != 0])
    return contours


def _split_pinches(path: list[tuple[int, int]]) -> list[list[tuple[int, int]]]:
    """Split a closed path that passes through the same corner twice (a hole that meets the outline,
    or another hole, only diagonally) into simple loops. Each loop keeps its direction, so the outer
    one stays clockwise and the hole counter-clockwise."""
    loops, stack, seen = [], [], {}
    for v in path:
        if v in seen:
            start = seen[v]
            loops.append(stack[start:])
            for u in stack[start + 1:]:
                del seen[u]
            del stack[start + 1:]
        else:
            seen[v] = len(stack)
            stack.append(v)
    loops.append(stack)
    return loops


def glyph_contours(rows: list[str]) -> list[list[tuple[int, int]]]:
    """Contours in font units for a 16- or 10-grid glyph."""
    size = len(rows)
    cell = UPM / size

    def fx(x: int) -> int:
        return round(x * cell)

    def fy(y: int) -> int:
        return UPM - round(y * cell)

    return [[(fx(x), fy(y)) for x, y in poly] for poly in trace(opaque_pixels(rows))]


def notdef_contours() -> list[list[tuple[int, int]]]:
    """The usual hollow box, on the 16-grid, clockwise outside and counter-clockwise inside."""
    o0, o1, i0, i1 = 3 * 128, 13 * 128, 4 * 128, 12 * 128
    top, bottom = UPM - 2 * 128, UPM - 14 * 128
    itop, ibottom = UPM - 3 * 128, UPM - 13 * 128
    return [[(o0, bottom), (o0, top), (o1, top), (o1, bottom)],
            [(i0, ibottom), (i1, ibottom), (i1, itop), (i0, itop)]]


# ----------------------------------------------------------------------------------------------
# Font
# ----------------------------------------------------------------------------------------------
def build_font(outlines: dict[str, list[list[tuple[int, int]]]], cmap: dict[int, str],
               glyph_order: list[str]) -> FontBuilder:
    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(glyph_order)
    fb.setupCharacterMap(cmap)
    glyphs = {}
    for name in glyph_order:
        pen = TTGlyphPen(None)
        for poly in outlines[name]:
            pen.moveTo(poly[0])
            for p in poly[1:]:
                pen.lineTo(p)
            pen.closePath()
        glyphs[name] = pen.glyph()
    fb.setupGlyf(glyphs)
    glyf = fb.font["glyf"]
    fb.setupHorizontalMetrics({name: (UPM, getattr(glyf[name], "xMin", 0)) for name in glyph_order})
    fb.setupHorizontalHeader(ascent=UPM, descent=0, lineGap=0)
    # Fixed timestamps: the same input builds a byte-identical file, so a rebuild only shows up in a
    # diff when a glyph or the map really changed.
    stamp = timestampFromString("Thu Sep 24 00:00:00 2026")
    fb.updateHead(fontRevision=float(VERSION), lowestRecPPEM=8, flags=0b1001,  # y=0 baseline, integer ppem
                  created=stamp, modified=stamp)
    fb.font.recalcTimestamp = False
    fb.setupNameTable({
        "copyright": COPYRIGHT,
        "familyName": FAMILY,
        "styleName": "Regular",
        "uniqueFontIdentifier": f"{VERSION};{PS_NAME}",
        "fullName": FAMILY,
        "version": f"Version {VERSION}",
        "psName": PS_NAME,
        "description": "Slate's pixel icons at the Segoe MDL2 Assets codepoints CloudLauncher uses.",
        "licenseDescription": "MIT License",
    })
    fb.setupOS2(
        version=4, usWeightClass=400, usWidthClass=5, fsType=0, fsSelection=0x40,  # Regular
        sTypoAscender=UPM, sTypoDescender=0, sTypoLineGap=0, usWinAscent=UPM, usWinDescent=0,
        sxHeight=UPM // 2, sCapHeight=UPM, usDefaultChar=0, usBreakChar=0x20, usMaxContext=0,
        ulUnicodeRange1=0, ulUnicodeRange2=1 << 28, ulUnicodeRange3=0, ulUnicodeRange4=0,  # bit 60: PUA
        ulCodePageRange1=1, ulCodePageRange2=0, achVendID="SLTE",
        panose=_panose())
    fb.setupPost(keepGlyphNames=True)
    gasp = newTable("gasp")
    gasp.version = 1
    gasp.gaspRange = {0xFFFF: 0x000A}  # unhinted: grayscale + symmetric smoothing at every size
    fb.font["gasp"] = gasp
    return fb


def _panose():
    """All zero ("any"), unlike Segoe MDL2's family type 5 (Latin Pictorial). DirectWrite reads type 5
    as a symbol font that claims every character, so WPF would draw this font's .notdef for any
    codepoint it lacks instead of falling back to MDL2. With 0 the fallback works per character."""
    from fontTools.ttLib.tables.O_S_2f_2 import Panose
    p = Panose()
    for field in ("bFamilyType", "bSerifStyle", "bWeight", "bProportion", "bContrast",
                  "bStrokeVariation", "bArmStyle", "bLetterForm", "bMidline", "bXHeight"):
        setattr(p, field, 0)
    return p


def validate_font(path: str, cmap: dict[int, str]) -> list[str]:
    """Reload the written file and check it is the drop-in it has to be."""
    problems = []
    f = TTFont(path)
    if f["head"].unitsPerEm != UPM:
        problems.append(f"unitsPerEm {f['head'].unitsPerEm}")
    h = f["hhea"]
    if (h.ascent, h.descent, h.lineGap) != (UPM, 0, 0):
        problems.append(f"hhea {h.ascent}/{h.descent}/{h.lineGap}")
    o = f["OS/2"]
    if (o.sTypoAscender, o.sTypoDescender, o.sTypoLineGap, o.usWinAscent, o.usWinDescent) != (UPM, 0, 0, UPM, 0):
        problems.append("OS/2 vertical metrics differ from MDL2")
    best = f.getBestCmap()
    if best != cmap:
        problems.append(f"cmap has {len(best)} entries, expected {len(cmap)}")
    for name, (adv, _) in f["hmtx"].metrics.items():
        if adv != UPM:
            problems.append(f"{name}: advance {adv}")
    glyf = f["glyf"]
    for name in f.getGlyphOrder():
        g = glyf[name]
        if g.numberOfContours and not (0 <= g.xMin and g.xMax <= UPM and 0 <= g.yMin and g.yMax <= UPM):
            problems.append(f"{name}: outside the em box")
    if o.panose.bFamilyType == 5:
        problems.append("panose family type 5 (pictorial): WPF would stop falling back to MDL2")
    if any((t.platformID, t.platEncID) == (3, 0) for t in f["cmap"].tables):
        problems.append("symbol cmap (3,0): WPF would stop falling back to MDL2")
    names = {n.nameID: n.toUnicode() for n in f["name"].names if n.platformID == 3}
    if names.get(1) != FAMILY or names.get(2) != "Regular" or names.get(0) != COPYRIGHT:
        problems.append("name table: family / style / copyright wrong")
    return problems


def check_pixels(path: str, glyphs: dict[str, list[str]], cmap: dict[int, str]) -> list[str]:
    """Render every glyph from the written font at its own grid size (16 px, 10 px for the caption
    glyphs), where a grid pixel is exactly one image pixel, and compare with the art: proves the traced
    outlines cover exactly the '#' pixels, no more and no less."""
    problems = []
    fonts: dict[int, ImageFont.FreeTypeFont] = {}
    first_cp: dict[str, int] = {}
    for cp, name in sorted(cmap.items()):
        first_cp.setdefault(name, cp)
    for name, cp in first_cp.items():
        rows = glyphs[name.upper()]
        n = len(rows)
        font = fonts.setdefault(n, ImageFont.truetype(path, n))
        im = Image.new("L", (n, n), 0)
        ImageDraw.Draw(im).text((0, 0), chr(cp), font=font, fill=255, anchor="la")
        bad = [(x, y) for y, row in enumerate(rows) for x, ch in enumerate(row)
               if abs(im.getpixel((x, y)) - (255 if ch == "#" else 0)) > 16]
        if bad:
            problems.append(f"{name}: {len(bad)} pixel(s) differ from the art at {n} px, first {bad[0]}")
    return problems


# ----------------------------------------------------------------------------------------------
# Client scan: a codepoint the launcher uses but the table does not list gets flagged.
# ----------------------------------------------------------------------------------------------
_SCAN_PATTERNS = [re.compile(r"&#x([0-9A-Fa-f]{4});"), re.compile(r"\\u([0-9A-Fa-f]{4})"),
                  re.compile(r"\b0x([0-9A-Fa-f]{4})\b")]


def scan_client(root: str) -> set[int]:
    found = set()
    if not os.path.isdir(root):
        return found
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in {"bin", "obj", ".vs", "packs", "shots"}]
        for fn in filenames:
            if not fn.endswith((".xaml", ".cs")):
                continue
            with open(os.path.join(dirpath, fn), encoding="utf-8-sig", errors="replace") as fh:
                text = fh.read()
            for pat in _SCAN_PATTERNS:
                for m in pat.finditer(text):
                    cp = int(m.group(1), 16)
                    if 0xE000 <= cp <= 0xF8FF:
                        found.add(cp)
            found.update(ord(ch) for ch in text if 0xE000 <= ord(ch) <= 0xF8FF)
    return found


# ----------------------------------------------------------------------------------------------
# Preview sheet: MDL2 above Slate Icons for every mapped codepoint
# ----------------------------------------------------------------------------------------------
BG = (0x16, 0x16, 0x15)
CELL_BG = (0x22, 0x22, 0x21)
BORDER = (0x33, 0x33, 0x2F)
TEXT = (0xEC, 0xEA, 0xE4)
MUTED = (0xA1, 0x9F, 0x97)
ACCENT = (0xD9, 0x80, 0x5E)
NEW_TINT = (0x7F, 0xB0, 0xE0)


def _ui_font(size: int):
    for candidate in ("segoeui.ttf", "arial.ttf"):
        path = os.path.join(os.environ.get("WINDIR", r"C:\Windows"), "Fonts", candidate)
        if os.path.isfile(path):
            return ImageFont.truetype(path, size)
    try:
        return ImageFont.load_default(size=size)
    except TypeError:
        return ImageFont.load_default()


def _fit(d: ImageDraw.ImageDraw, text: str, font, width: int) -> str:
    if d.textlength(text, font=font) <= width:
        return text
    while text and d.textlength(text + "...", font=font) > width:
        text = text[:-1]
    return text.rstrip() + "..."


def render_preview(rows, font_path: str, new_names: set[str], out_path: str) -> None:
    sizes = (32, 16, 12, 10)
    cols = 4
    card_w, card_h = 330, 118
    pad, title_h = 14, 58
    n_rows = (len(rows) + cols - 1) // cols
    im = Image.new("RGB", (pad * 2 + cols * card_w, title_h + pad + n_rows * card_h), BG)
    d = ImageDraw.Draw(im)
    big, small, tiny = _ui_font(18), _ui_font(12), _ui_font(10)
    glyph_count = len({g for _, g, _, _ in rows if g != "-"})
    d.text((pad, 10), f"Slate Icons - {len(rows)} MDL2 codepoints drawn with {glyph_count} glyphs "
                      f"({len(new_names)} drawn for the launcher, name in blue)", font=big, fill=TEXT)
    d.text((pad, 34), "Each card: Segoe MDL2 Assets on top, Slate Icons below, at 32 / 16 / 12 / 10 px "
                      "(PIL / FreeType, grayscale).", font=small, fill=MUTED)
    mdl2 = {s: ImageFont.truetype(MDL2_PATH, s) for s in sizes}
    slate = {s: ImageFont.truetype(font_path, s) for s in sizes}
    for i, (cp, glyph, meaning, mdl2_name) in enumerate(rows):
        x0 = pad + (i % cols) * card_w
        y0 = title_h + (i // cols) * card_h
        d.rectangle((x0 + 2, y0 + 2, x0 + card_w - 4, y0 + card_h - 4), fill=CELL_BG, outline=BORDER)
        d.text((x0 + 10, y0 + 7), f"{cp:04X}", font=small, fill=ACCENT)
        label = glyph.lower() if glyph != "-" else "(left to MDL2)"
        d.text((x0 + 52, y0 + 7), label, font=small, fill=NEW_TINT if glyph.lower() in new_names else TEXT)
        d.text((x0 + 10, y0 + 24), _fit(d, meaning, tiny, card_w - 22), font=tiny, fill=MUTED)
        gx = x0 + 12
        for fonts, gy in ((mdl2, y0 + 42), (slate, y0 + 79)):
            x = gx
            for s in sizes:
                oy = gy + (32 - s) // 2
                d.rectangle((x - 1, oy - 1, x + s, oy + s), outline=BORDER)
                d.text((x, oy), chr(cp), font=fonts[s], fill=TEXT, anchor="la")
                x += s + 14
        d.text((x0 + card_w - 60, y0 + 50), "MDL2", font=tiny, fill=MUTED)
        d.text((x0 + card_w - 60, y0 + 87), "Slate", font=tiny, fill=MUTED)
    im.save(out_path, optimize=True)


def render_review(glyphs: dict[str, list[str]], font_path: str, cmap: dict[int, str], out_dir: str,
                  names: list[str]) -> None:
    """Per glyph: the pixel grid at 8x, then the font rendered at 32/16/13/12/10 px and blown up
    4x without smoothing, so the anti-aliasing a real UI sees is visible."""
    os.makedirs(out_dir, exist_ok=True)
    first_cp = {}
    for cp, name in sorted(cmap.items()):
        first_cp.setdefault(name, cp)
    sizes = (32, 16, 13, 12, 10)
    per_sheet = 6
    col_w = 16 * 8 + 24 + sum(s * 4 + 12 for s in sizes)
    row_h = 16 * 8 + 30
    label = _ui_font(13)
    fonts = {s: ImageFont.truetype(font_path, s) for s in sizes}
    mfonts = {s: ImageFont.truetype(MDL2_PATH, s) for s in sizes}
    for sheet in range(0, len(names), per_sheet):
        chunk = names[sheet:sheet + per_sheet]
        im = Image.new("RGB", (col_w + 20, 10 + len(chunk) * row_h * 2), BG)
        d = ImageDraw.Draw(im)
        for k, name in enumerate(chunk):
            y0 = 10 + k * row_h * 2
            rows = glyphs[name]
            cp = first_cp[name.lower()]
            d.text((10, y0), f"{name.lower()}  (U+{cp:04X})", font=label, fill=TEXT)
            n = len(rows)
            z = 128 // n
            for r, row in enumerate(rows):
                for c, ch in enumerate(row):
                    fill = TEXT if ch == "#" else (MUTED if ch == "+" else CELL_BG)
                    d.rectangle((10 + c * z, y0 + 20 + r * z, 10 + c * z + z - 2, y0 + 20 + r * z + z - 2), fill=fill)
            x = 10 + 128 + 24
            for fam, yy in ((fonts, y0 + 20), (mfonts, y0 + 20 + row_h)):
                xx = x
                for s in sizes:
                    tile = Image.new("L", (s, s), 0)
                    ImageDraw.Draw(tile).text((0, 0), chr(cp), font=fam[s], fill=255, anchor="la")
                    big = tile.resize((s * 4, s * 4), Image.NEAREST)
                    im.paste(Image.new("RGB", big.size, TEXT), (xx, yy), big)
                    d.rectangle((xx - 1, yy - 1, xx + s * 4, yy + s * 4), outline=BORDER)
                    d.text((xx, yy + s * 4 + 2), f"{s}px", font=label, fill=MUTED)
                    xx += s * 4 + 12
            d.text((10, y0 + 20 + row_h + 40), "MDL2 below", font=label, fill=MUTED)
        im.save(os.path.join(out_dir, f"review_{sheet // per_sheet:02d}.png"))


# ----------------------------------------------------------------------------------------------
def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--slate", default=DEFAULT_SLATE)
    ap.add_argument("--font", default=DEFAULT_FONT)
    ap.add_argument("--preview", default=DEFAULT_PREVIEW)
    ap.add_argument("--review", metavar="DIR")
    ap.add_argument("--check", action="store_true")
    args = ap.parse_args(argv)

    slate = load_slate(args.slate)
    clash = sorted(set(slate) & (set(NEW_GLYPHS) | set(CAPTION_GLYPHS)))
    if clash:
        raise SystemExit(f"Slate now has glyphs named {clash}: rename or drop the local copies")
    glyphs = {**slate, **NEW_GLYPHS, **CAPTION_GLYPHS}

    rows = parse_codepoints(CODEPOINTS)
    cmap: dict[int, str] = {}
    for cp, name, _, _ in rows:
        if name == "-":
            continue
        if name not in glyphs:
            raise SystemExit(f"{cp:04X}: no glyph named {name}")
        cmap[cp] = name.lower()

    used = {name.upper() for name in cmap.values()}
    unused_new = sorted((set(NEW_GLYPHS) | set(CAPTION_GLYPHS)) - used)
    if unused_new:
        raise SystemExit(f"drawn but not mapped: {unused_new}")
    listed = {cp for cp, _, _, _ in rows}
    missing = sorted(scan_client(CLIENT_DIR) - listed)
    if missing:
        print("WARNING: the client uses codepoints this table does not list (they stay MDL2): "
              + " ".join(f"{cp:04X}" for cp in missing))
    stale = sorted(listed - scan_client(CLIENT_DIR)) if os.path.isdir(CLIENT_DIR) else []
    if stale:
        print("note: listed but no longer used by the client: " + " ".join(f"{cp:04X}" for cp in stale))

    order_names = []
    for cp in sorted(cmap):
        if cmap[cp] not in order_names:
            order_names.append(cmap[cp])
    outlines = {".notdef": notdef_contours()}
    for lname in order_names:
        outlines[lname] = glyph_contours(glyphs[lname.upper()])
    new_names = {n.lower() for n in (set(NEW_GLYPHS) | set(CAPTION_GLYPHS))}
    contours = sum(len(v) for k, v in outlines.items() if k != ".notdef")
    print(f"{len(rows)} codepoints -> {len(order_names)} glyphs "
          f"({len(new_names)} drawn here, {len(order_names) - len(new_names)} from Slate), "
          f"{contours} contours")
    if args.check:
        return 0

    fb = build_font(outlines, cmap, [".notdef"] + order_names)
    os.makedirs(os.path.dirname(os.path.abspath(args.font)), exist_ok=True)
    fb.save(args.font)
    problems = validate_font(args.font, cmap) + check_pixels(args.font, glyphs, cmap)
    if problems:
        for p in problems:
            print("FONT PROBLEM:", p)
        return 1
    print("wrote", args.font, f"({os.path.getsize(args.font)} bytes), validated")
    render_preview(rows, args.font, new_names, args.preview)
    print("wrote", args.preview)
    if args.review:
        render_review(glyphs, args.font, cmap, args.review, [n.upper() for n in order_names])
        print("wrote review sheets to", args.review)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
