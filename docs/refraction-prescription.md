# Alshafa refraction prescription

The refraction editor now opens the supplied prescription directly. `RefractionPrescription.vue` is shared by the editor, the doctor's result panel and the refraction print route. The supplied PNG is unchanged stationery: patient values are actual text inputs, table cells and independent checkboxes, not pixels in the artwork. Readonly and print values are generated from the saved record.

## Storage and compatibility

- Visit ID continues to identify the prescription; patient name and Shamsi date come from that visit, not the machine's current date or the time of an amendment.
- Rows 1–2 map to Subjective OD/OS; rows 3–4 map to Final OD/OS. RE is OD and LE is OS. Existing final-value referral behavior stays in place.
- Vision RE/LE use the existing unaided vision columns, PD uses its existing column, and the nine choices use the existing recognized `Lens:` line in Measurements.
- ADD is the paper's shared final ADD. A deliberate change updates both final eyes. When editing an old record, different existing per-eye ADDs remain intact until the shared field is changed.
- Historic Current/Auto rows, pinhole/near vision, notes, investigations and unrecognized Measurements text are carried through saves. Hiding their former controls does not delete their data.
- Values are text: signs, decimals, Dari digits and other clinical notation are accepted without numeric coercion, rounding or axis-range restrictions. Existing database string capacities remain (10 characters for power/axis/ADD/PD; 30 for vision). Measurements over the existing 1000-character capacity produce an error rather than truncating history.
- Existing backend permissions, payment gate, version conflicts, completion, reasoned amendments, audit and optical correction rules are reused. The transport's online-pending response still cannot be reported as a confirmed save.

There is no schema migration, data conversion or operational-database change. No hospital backup or hospital database connection was needed or performed. Future deployment must follow the existing backup-first hospital update process.

## Printing

The user selected A5 landscape. The 4:3 artwork occupies 196 x 147 mm on a 210 x 148 mm page, centered with 7 mm horizontal and 0.5 mm vertical outer margins. Margins within the supplied artwork are also retained. Print at 100%, with browser headers/footers off. The stationery is an embedded image element, so it prints even with background graphics disabled.

The print route fetches the stored prescription and can print drafts as well as completed records. The editor disables its print link while values are dirty, and freezes inputs during a save. Buttons and application navigation are excluded from the printed page. Filled Vision fields cover the blank fraction mark, so typing `6/12.5` does not print an extra slash.

## Verification

All database/browser verification uses a new loopback-only MySQL instance under `.tools/automation-lab-73e389b18cb64f38bed6bd94c5fbb540`, verified by listener, executable, private configuration, port, data directory and absence of a concurrent testhost. The installed hospital database on port 3308 is never targeted.

- Backend: 22 ClinicalTests/RefractionTests passed, including the added four-row and lens roundtrip test and existing permission, conflict, paid-request, completion and optical correction checks.
- Frontend: four unit tests passed, including hidden-history preservation, distinct old per-eye ADD retention, all nine choices and exact text roundtrip.
- Focused browser run: actual API/MySQL writes, unpaid refusal, queue selection, all 20 text fields, all nine independent selections, save/reload/edit, completion, doctor return, optical copy, reasoned correction, stale-write 409, patient history, readonly doctor view and saved-value/print equality.
- Print: PDF text extraction checks every table value, vision, ADD, PD and eight selected ticks; MediaBox and page count are checked. Row-center geometry is checked against the source. Blank and filled reference comparisons and PDF raster inspection are documented in `refraction-design-qa.md`.
- Production frontend build and focused ESLint passed. The global lint command reports 12 existing errors in `clipPlayer.ts` and `DossierForm.vue`.
- Broader `clinical.spec.ts` and `paper.spec.ts` scenarios reached their downstream optical step, then timed out on their pre-existing `نسخه عینک` button selector; current production source labels that action `درخواست عینک`. These two scenarios are not reported as passed. Their refraction fields/assertions were updated for this redesign; unrelated production workflows were not modified to accommodate stale test selectors.

This is synthetic local verification, not physical-printer or on-site hospital acceptance. No release was deployed to the hospital or online server.
