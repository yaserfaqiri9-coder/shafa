# Refraction prescription visual QA

Reference: user-supplied 1448 x 1086 PNG, copied unchanged as the form's stationery.
Implementation: shared interactive/readonly `RefractionPrescription.vue`.

## First comparison

Synthetic browser evidence in `.tools/automation-lab-73e389b18cb64f38bed6bd94c5fbb540/browser`:
`reference-comparison.png`, `saved-sheet.png`, `print-sheet.png`, `pdf-render.png`.
Opened the reference and implementation together; also inspected the filled sheet and rendered PDF.

- Fixed artwork, typography, copy, colors and relative layout match the reference.
- P1: an empty HTML table header collapsed in the readonly rendering. The first data row appeared over the heading and subsequent values were one row too high.
- P2: a long synthetic patient name wrapped into two lines.

Fix: explicitly size all five table rows in container-width units; scale the patient's name to the available single line.
## Final comparison

Final synthetic evidence: `.tools/automation-lab-73e389b18cb64f38bed6bd94c5fbb540/browser-refraction-final`.

- Opened `filled-comparison.png` (reference and populated implementation together) and `pdf-render.png` after the fixes. The reference remains 1448 x 1086; editor capture is 1181 x 887; saved print preview is 742 x 556 CSS pixels at device scale 1. Comparisons normalize both captures to the source dimensions. Raster scaling accounts for the preview's softer lettering; the PDF embeds the original image.
- P1 resolved: each row's center is now in its corresponding printed RE/LE band, with a browser geometry assertion against the reference. All four rows, headings and column positions are aligned in the final PDF raster.
- P2 resolved: the complete synthetic name fits on one line. Very long names use a smaller font; this is an intentional fit rule rather than silent truncation.
- Filled Vision controls supply their own notation and mask the empty fraction mark. The final PDF displays `6/12.5` and `+0.75` exactly once each.
- Fonts/copy/colors/image quality: unchanged supplied stationery preserves the original typography, spelling (including CYLENDAR), blue/red colors, logos, service copy, address and proportions. Filled patient values remain real text. Native checkbox selections print in the nine original red squares.
- Spacing/layout: all 20 fields and nine checkboxes sit within the original spaces; no extra measurement panels appear. Mobile keeps the complete sheet in its own horizontal scroll area, with no page overflow.
- PDF: one A5 landscape page, checked via MediaBox; original artwork embedded; every saved table value plus Vision, ADD, PD and eight selected marks verified in extracted PDF text. No buttons, menus or app chrome appear. Inspected the focused table, vision, checkbox and name regions in the final 1190 x 840 PDF raster.

All P1/P2 findings are resolved. Physical printer output remains outside this synthetic verification.

final result: passed
