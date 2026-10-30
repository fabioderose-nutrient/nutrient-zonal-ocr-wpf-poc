"""Generates samples/recorded-document.pdf, a fictitious one-page recorded document with a
typed body and a slanted recording stamp, and samples/lines.json, the two lines that
tools/OcrPreview reads along (in inches, origin at the top-left corner of the page).

All names, numbers and dates are invented.

Requirements: pip install reportlab
Usage:        python generate_sample.py [output-folder]
"""

import json
import math
import sys
from pathlib import Path

from reportlab.lib.pagesizes import letter
from reportlab.pdfgen import canvas

W, H = letter


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).parent
    pdf = out / "recorded-document.pdf"
    c = canvas.Canvas(str(pdf), pagesize=letter)
    c.setTitle("Sample recorded document")
    c.setAuthor("Sample generator")

    c.setFont("Helvetica-Bold", 18)
    c.drawCentredString(W / 2, H - 80, "GRANT OF EASEMENT")
    c.setFont("Helvetica", 9)
    c.drawCentredString(W / 2, H - 96, "Fictitious sample document for the zonal OCR demo")

    body = [
        "This grant is made on March 14, 2026 between Example Holdings LLC (Grantor)",
        "and Sample Utilities Inc. (Grantee) for the land described below.",
        "",
        "Parcel number: 000-123-456-7",
        "County: Example County        Lot 12, Block 4, Sample Subdivision",
        "",
        "The Grantor grants to the Grantee a perpetual easement, ten feet wide,",
        "along the north boundary of the parcel for underground utility lines,",
        "with the right to access, maintain and replace them.",
    ]
    c.setFont("Times-Roman", 12)
    y = H - 150
    lines = []
    for text in body:
        c.drawString(72, y, text)
        if text.startswith("Parcel number"):
            # Line drawn through the middle of the text, left to right.
            mid = (H - y - 4) / 72
            width = c.stringWidth(text, "Times-Roman", 12)
            lines.append({"name": "parcel-number", "x1": 70 / 72, "y1": mid, "x2": (74 + width) / 72, "y2": mid})
        y -= 20

    # Slanted recording stamp, 25 degrees counter-clockwise.
    stamp = "RECORDED 03/14/2026  BOOK 120  PAGE 45"
    angle = 25
    sx, sy = 250, 250
    c.saveState()
    c.translate(sx, sy)
    c.rotate(angle)
    c.setFillColorRGB(0.1, 0.2, 0.55)
    c.setStrokeColorRGB(0.1, 0.2, 0.55)
    c.setFont("Helvetica-Bold", 14)
    width = c.stringWidth(stamp, "Helvetica-Bold", 14)
    c.rect(-8, -8, width + 16, 28, stroke=1, fill=0)
    c.drawString(0, 0, stamp)
    c.restoreState()

    # Line along the middle of the stamp text, in top-left inches.
    rad = math.radians(angle)
    off = 5  # half the cap height, perpendicular to the baseline
    x1 = sx - off * math.sin(rad) - 4 * math.cos(rad)
    y1 = sy + off * math.cos(rad) - 4 * math.sin(rad)
    x2 = x1 + (width + 8) * math.cos(rad)
    y2 = y1 + (width + 8) * math.sin(rad)
    lines.append({"name": "recording-stamp", "x1": x1 / 72, "y1": (H - y1) / 72, "x2": x2 / 72, "y2": (H - y2) / 72})

    c.setFont("Times-Roman", 11)
    c.drawString(72, 120, "Signed: ______________________        Date: ______________")
    c.showPage()
    c.save()

    (out / "lines.json").write_text(json.dumps(lines, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {pdf} and {out / 'lines.json'}")


if __name__ == "__main__":
    main()
