"""
Converts CloudLauncher XAML to the Slate-capable form, idempotently:

  1. Rounded <Border> elements become <PREFIX:SlateBorder> (with their matching end tag and any
     <Border.X> property elements), so the Slate style can draw pixel-stepped corners. A Border
     counts as rounded when it sets CornerRadius or uses one of the rounded keyed styles.
  2. {StaticResource UiFont|HeadingFont|MonoFont|IconFont} become {DynamicResource ...}, so a change
     of look re-fonts the app live.
  3. The xmlns for PREFIX is added to the root element when a conversion needs it.

It is a small tag scanner, not a regex: it tracks nesting (so the right </Border> is renamed),
skips comments and CDATA, and leaves every byte outside the edited tags untouched (line endings
and BOM included). Run with --check to report without writing.

usage: python xaml_slate_convert.py [--check] [--all-borders] <file.xaml>...
"""
import re
import sys

PREFIX = "slate"
XMLNS = 'xmlns:slate="clr-namespace:CloudLauncher.Controls"'
ROUNDED_STYLES = ("Card", "ElevatedCard", "HoverCard", "Pill", "FolderChipBorder")
FONT_KEYS = ("UiFont", "HeadingFont", "MonoFont", "IconFont")

TAG_RE = re.compile(r"<(/?)([A-Za-z_][\w.:-]*)((?:[^>\"']|\"[^\"]*\"|'[^']*')*?)(/?)>", re.S)


def inner_text(text, start):
    """The text between a Border start tag ending at `start` and its matching </Border>."""
    depth = 1
    pos = start
    while True:
        m = TAG_RE.search(text, pos)
        if not m:
            return None
        c = text.find("<!--", pos, m.start())
        if c >= 0:
            e = text.find("-->", c)
            pos = len(text) if e < 0 else e + 3
            continue
        closing, name, selfclose = m.group(1), m.group(2), m.group(4)
        if name == "Border":
            if closing:
                depth -= 1
                if depth == 0:
                    return text[start:m.start()]
            elif not selfclose:
                depth += 1
        pos = m.end()


def convert(text, all_borders=False):
    out = []
    pos = 0
    stack = []  # (name, converted)
    changed = 0
    needs_xmlns = False
    n = len(text)
    while pos < n:
        lt = text.find("<", pos)
        if lt < 0:
            out.append(text[pos:])
            break
        out.append(text[pos:lt])
        if text.startswith("<!--", lt):
            end = text.find("-->", lt + 4)
            end = n if end < 0 else end + 3
            out.append(text[lt:end])
            pos = end
            continue
        if text.startswith("<![CDATA[", lt):
            end = text.find("]]>", lt)
            end = n if end < 0 else end + 3
            out.append(text[lt:end])
            pos = end
            continue
        if text.startswith("<?", lt):
            end = text.find("?>", lt)
            end = n if end < 0 else end + 2
            out.append(text[lt:end])
            pos = end
            continue
        m = TAG_RE.match(text, lt)
        if not m:
            out.append("<")
            pos = lt + 1
            continue
        closing, name, attrs, selfclose = m.group(1), m.group(2), m.group(3), m.group(4)
        tag = m.group(0)
        if not closing:
            if name == "Border":
                rounded = all_borders or re.search(r"\bCornerRadius\s*=", attrs) is not None
                if not rounded:
                    sm = re.search(r"\bStyle\s*=\s*\"\{(?:StaticResource|DynamicResource)\s+(\w+)\}\"", attrs)
                    rounded = sm is not None and sm.group(1) in ROUNDED_STYLES
                if not rounded and not selfclose:
                    # A radius set by the Border's own inline style (<Border.Style> ... CornerRadius),
                    # and only its own: a nested Border's style must not convert the outer one.
                    body = inner_text(text, m.end())
                    if body is not None:
                        own = re.sub(r"<Border(?=[\s/>]).*", "", body, flags=re.S)
                        rounded = re.search(r"<Border\.Style>.*?Property=\"CornerRadius\"", own, re.S) is not None
                if rounded:
                    tag = "<" + PREFIX + ":SlateBorder" + attrs + selfclose + ">"
                    changed += 1
                    needs_xmlns = True
                if not selfclose:
                    stack.append(("Border", rounded))
            elif name.startswith("Border.") and stack and stack[-1][0] == "Border" and stack[-1][1]:
                tag = "<" + PREFIX + ":SlateBorder" + name[len("Border"):] + attrs + selfclose + ">"
                if not selfclose:
                    stack.append((name, True))
            else:
                if not selfclose:
                    stack.append((name, False))
        else:
            # Pop to the matching name (tolerates nothing else: XAML is well-formed).
            if stack and stack[-1][0] == name:
                _, conv = stack.pop()
                if conv and name == "Border":
                    tag = "</" + PREFIX + ":SlateBorder>"
                elif conv and name.startswith("Border."):
                    tag = "</" + PREFIX + ":SlateBorder" + name[len("Border"):] + ">"
            else:
                raise ValueError("unbalanced tag </%s> at offset %d (stack top %r)" % (name, lt, stack[-1] if stack else None))
        out.append(tag)
        pos = m.end()

    result = "".join(out)

    # Fonts: StaticResource -> DynamicResource for the four font keys.
    fonts = 0
    for key in FONT_KEYS:
        pattern = "{StaticResource " + key + "}"
        fonts += result.count(pattern)
        result = result.replace(pattern, "{DynamicResource " + key + "}")

    if needs_xmlns and ("xmlns:" + PREFIX + "=") not in result:
        # Add to the root element's start tag, right after its xmlns:x declaration.
        m = re.search(r'xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"', result)
        if not m:
            raise ValueError("no xmlns:x on the root element")
        nl = "\r\n" if "\r\n" in result[:m.end() + 200] else "\n"
        # Indent to match the xmlns:x line.
        line_start = result.rfind("\n", 0, m.start()) + 1
        indent = re.match(r"\s*", result[line_start:m.start()]).group(0)
        if not indent.strip() and len(indent) > 0:
            insert = nl + indent + XMLNS
        else:
            insert = " " + XMLNS
        result = result[:m.end()] + insert + result[m.end():]

    return result, changed, fonts


def main(argv):
    check = "--check" in argv
    all_borders = "--all-borders" in argv
    files = [a for a in argv if not a.startswith("--")]
    total_b = total_f = 0
    for path in files:
        raw = open(path, "rb").read()
        bom = raw[:3] == b"\xef\xbb\xbf"
        text = raw.decode("utf-8-sig")
        new, b, f = convert(text, all_borders)
        total_b += b
        total_f += f
        if new != text:
            print("%-90s borders=%d fonts=%d" % (path, b, f))
            if not check:
                open(path, "wb").write((b"\xef\xbb\xbf" if bom else b"") + new.encode("utf-8"))
    print("total borders=%d fonts=%d%s" % (total_b, total_f, " (check only)" if check else ""))


if __name__ == "__main__":
    main(sys.argv[1:])
