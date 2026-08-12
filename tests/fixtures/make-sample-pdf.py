#!/usr/bin/env python3
"""Generate tests/fixtures/sample.pdf — the integration-test document.

Written by hand rather than with a PDF library so it has no dependencies and
regenerates byte-identically: CI and a developer must get the same file.

The content is chosen to exercise the parts of RailMark that only real pdfium
geometry can test, and nothing else:

  * Two headings on page 1 (Introduction, Methods) with a bookmark each, so
    heading assignment has to order them by position within the page rather
    than by page number alone.
  * A word split across a line break by a wrap hyphen ("inter-" / "pretable"),
    so a quote of the joined word has to resolve across the break.
  * Curly quotes, so a quote written with straight ones has to normalise.
  * A page 2 with its own bookmark, so quotes are page-scoped.

It also carries annotations for the `--images` path, which needs geometry that
renders to something visible:

  * A /Square over the "Introduction" heading — a region with known, non-blank
    content, so a crop of it can be checked for not being uniformly white.
    (Cropping the wrong half of the page produced exactly that; see issue #22.)
  * Three /Ink strokes on page 2: two within the 50pt merge distance of each
    other and one far away, so freehand grouping produces two images, not three.

Usage:  python3 tests/fixtures/make-sample-pdf.py [output.pdf]
"""

import sys
from pathlib import Path

# WinAnsi byte values for the curly quotes we want in the page text.
LDQUO, RDQUO = b"\x93", b"\x94"

FONT_SIZE = 12
LEADING = 18
TOP = 720
LEFT = 72


def escape(text: bytes) -> bytes:
    for ch in (b"\\", b"(", b")"):
        text = text.replace(ch, b"\\" + ch)
    return text


def text_block(lines, start_y):
    """A BT/ET run placing one line per row, top-down from start_y."""
    out = [b"BT", b"/F1 %d Tf" % FONT_SIZE, b"%d %d Td" % (LEFT, start_y)]
    for i, line in enumerate(lines):
        if i:
            out.append(b"0 -%d Td" % LEADING)
        out.append(b"(" + escape(line) + b") Tj")
    out.append(b"ET")
    return b"\n".join(out)


PAGE1_HEADING_1 = b"Introduction"
PAGE1_HEADING_2 = b"Methods"
PAGE2_HEADING = b"Results"

page1_lines = [
    PAGE1_HEADING_1,
    b"",
    b"This document exists so the RailMark test suite has a PDF whose exact",
    b"contents are known. The model described here is inter-",
    b"pretable in practice, which is the point of the exercise.",
    b"",
    b"The reviewer called it " + LDQUO + b"the best available option" + RDQUO + b" at the time.",
    b"",
    PAGE1_HEADING_2,
    b"",
    b"Each sample was measured twice and the results were averaged. This",
    b"paragraph sits under the second heading on the same page.",
]

page2_lines = [
    PAGE2_HEADING,
    b"",
    b"No attempt was made to preserve the original ordering of the samples.",
    b"The effect was small but consistent across every run of the experiment.",
]


def ink_annot(rect, inklist, label):
    return (
        b"<< /Type /Annot /Subtype /Ink /Rect " + rect +
        b" /InkList " + inklist +
        b" /C [0 0 1] /CA 1 /F 4 /T (fixture) /Contents (" + label + b") >>"
    )


def build():
    objects = {}

    objects[1] = b"<< /Type /Catalog /Pages 2 0 R /Outlines 8 0 R >>"
    objects[2] = b"<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"
    objects[3] = (
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Resources << /Font << /F1 7 0 R >> >> /Contents 5 0 R "
        b"/Annots [12 0 R] >>"
    )
    objects[4] = (
        b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        b"/Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R "
        b"/Annots [13 0 R 14 0 R 15 0 R] >>"
    )

    for num, lines in ((5, page1_lines), (6, page2_lines)):
        stream = text_block(lines, TOP)
        objects[num] = b"<< /Length %d >>\nstream\n%s\nendstream" % (len(stream), stream)

    objects[7] = (
        b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
        b"/Encoding /WinAnsiEncoding >>"
    )

    # Outline: Introduction and Methods on page 1, Results on page 2. The /XYZ
    # destinations carry a top coordinate, but RailMark locates headings by
    # searching the page text, so the titles must match the rendered text.
    objects[8] = b"<< /Type /Outlines /First 9 0 R /Last 11 0 R /Count 3 >>"
    objects[9] = (
        b"<< /Title (Introduction) /Parent 8 0 R /Next 10 0 R "
        b"/Dest [3 0 R /XYZ 72 720 0] >>"
    )
    objects[10] = (
        b"<< /Title (Methods) /Parent 8 0 R /Prev 9 0 R /Next 11 0 R "
        b"/Dest [3 0 R /XYZ 72 576 0] >>"
    )
    objects[11] = (
        b"<< /Title (Results) /Parent 8 0 R /Prev 10 0 R "
        b"/Dest [4 0 R /XYZ 72 720 0] >>"
    )

    # Annotations. Coordinates here are PDF-native (bottom-up); the annotation
    # store converts them to top-down when loading, which is the convention
    # ScreenshotService works in.
    #
    # Square over "Introduction" (drawn at y=720) and the first body line.
    objects[12] = (
        b"<< /Type /Annot /Subtype /Square /Rect [66 690 320 732] "
        b"/C [1 0 0] /CA 1 /F 4 /T (fixture) /Contents (heading box) >>"
    )

    # Ink strokes. The renderer draws page content only — annotation appearances
    # are not painted into the bitmap — so a crop shows whatever text lies under
    # the stroke. All three therefore sit over page 2's text, which is what makes
    # "the crop is not blank" a meaningful check on the coordinate mapping.
    #
    # All three sit over page 2's body text, below the "Results" heading, so they
    # file under it. (Placed over the heading instead, they land marginally above
    # its top edge and file under the *previous* section — correct, but a
    # confusing thing for a fixture to assert.)
    #
    # Two strokes ~20pt apart — inside the 50pt merge distance, so one image.
    objects[13] = ink_annot(b"[100 670 140 700]", b"[[100 675 120 695 140 680]]", b"stroke a")
    objects[14] = ink_annot(b"[155 670 195 700]", b"[[160 675 180 695 190 680]]", b"stroke b")
    # Same lines, far to the right — >50pt away horizontally, so its own image.
    objects[15] = ink_annot(b"[395 660 455 695]", b"[[400 665 425 690 450 670]]", b"stroke c")

    out = bytearray(b"%PDF-1.4\n%\xe2\xe3\xcf\xd3\n")
    offsets = {}
    for num in sorted(objects):
        offsets[num] = len(out)
        out += b"%d 0 obj\n" % num + objects[num] + b"\nendobj\n"

    xref_pos = len(out)
    count = len(objects) + 1
    out += b"xref\n0 %d\n" % count
    out += b"0000000000 65535 f \n"
    for num in sorted(objects):
        out += b"%010d 00000 n \n" % offsets[num]
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (
        count,
        xref_pos,
    )
    return bytes(out)


if __name__ == "__main__":
    target = Path(sys.argv[1] if len(sys.argv) > 1 else
                  Path(__file__).parent / "sample.pdf")
    target.write_bytes(build())
    print(f"Wrote {target} ({target.stat().st_size} bytes)")
