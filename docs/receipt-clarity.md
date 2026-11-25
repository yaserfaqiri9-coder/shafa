# Patient receipt clarity

Financial printouts now name the billed section and explain what the payment covers. The change is confined to the existing print endpoints and generic print page. Patient records, clinical prescriptions, accounting workflows, permissions and database schema are unchanged.

## Printed content

| Source | Payment title | Section |
| --- | --- | --- |
| Visit/visit token | رسید پرداخت فیس معاینه | پذیرش و معاینه |
| Refraction diagnostic order | رسید پرداخت رفراکشن | رفراکشن / بینایی‌سنجی |
| Optical goods | رسید پرداخت عینک | عینک |
| Surgery charge | رسید پرداخت عملیات | عملیات |
| Pharmacy goods | رسید پرداخت دواخانه | دواخانه |
| Laboratory diagnostic order | رسید پرداخت لابراتوار | لابراتوار / آزمایشگاه |
| Imaging diagnostic order | رسید پرداخت تصویربرداری | تصویربرداری |
| More than one billed section | رسید پرداخت خدمات چند بخش | Every corresponding section |

The payment receipt contains hospital identity, section, receipt number, date, patient name when recorded, the actual invoice item descriptions under `بابت`, and the amount of this individual payment. Queue information remains only on the associated admission-fee receipt. Partial payment, refunded amount, net receipt, outstanding invoice balance and void status remain visible when applicable.

Titles derive from stored invoice sources, diagnostic kinds, sales and revenue accounts rather than guessing from item text. Generic services use their configured department where available, with the cashier as the fallback issuing section. Mixed invoices cannot be mislabeled solely as a visit fee. When every line is void, refund/void papers retain the original section and purpose, with their explicit financial status.

An admission slip is titled `برگه نوبت و پذیرش`; it is identifiable as a queue/admission document. Sales print as `فاکتور دواخانه` or `فاکتور عینک`, with their real paid/unpaid status. Refunds use `رسید برگشت وجه` with the associated section, original payment number, original item descriptions and actual refund amount.

## Removed clutter

- Repeated English branding on financial papers.
- Artificial row-number columns and internal counts of voided items.
- Empty fields and placeholder rows (the existing renderer already excludes empty facts).
- Repeated batch-level sale lines: equivalent items are combined for display using the sums of stored quantities and line totals, without updating records or recalculating prices. Different unit prices or service/source categories remain separate.
- A quantity column containing only ones on service-only invoices. Drug/goods quantities remain visible even when they are one.
- Generic filler subtitles and duplicate service highlights on payment receipts; the actual purpose list supplies the explanation.

The amounts, discounts, refunds, balances, invoice/payment numbers and true financial statuses are retained. Clinical prescription instructions and refraction prescription artwork are not part of this cleanup.

## Verification

Backend ReportTests cover seven individual billed sections, mixed services, excluded void lines, partial payments, grouped batch lines, readonly behavior and refund/void provenance. FinanceTests and ConcurrencyTests also run against a new private loopback-only MySQL instance, never the installed hospital database on port 3308.

Browser rendering is tested separately with synthetic API fixtures: 15 document states, 80/58 mm rolls, an A5 pharmacy bill, long service names, absent optional fields, unpaid/partial/refund/void states, and print-hidden controls. These fixture checks do not claim live hospital integration. PDF inspection verifies all 29 generated documents have the expected dimensions and one page for these fixtures. The 35-item laboratory example uses a longer roll rather than dropping items. The short A5 pharmacy invoice no longer spills onto a second sheet.

Synthetic visual overview and samples: `outputs/receipt-clarity/receipt-overview.png` and the PDFs in that folder. Frontend production build, focused lint, and whitespace checks are also performed; global lint retains the existing 12 unrelated findings in `clipPlayer.ts` and `DossierForm.vue`.

No database migration, production database access, deployment or physical-printer acceptance is included.
