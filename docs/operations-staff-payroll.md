# Operations, staff payroll and waiting-room display

## Operation catalog

The Operations page has separate tabs for cases, operation definitions, and stocked packages. An operation definition has its own service/fee and zero or more package quantities. The doctor selects that definition from the dossier. Existing legacy cases and their manually recorded supplies retain their original workflow.

A new case copies the operation name, fee into its invoice, and the packages' product quantities and store locations into `SurgerySupplyPlans`. Editing an operation or package later does not rewrite that case. A fully settled payment consumes the plan inside the same invoice transaction, using the existing locked, expiry-aware and reservation-aware stock allocator. A shortage refuses the final payment and rolls back its payment, ledger, audit and stock writes. Partial payments consume nothing. An approved discount that settles the invoice also invokes the same consumption gate.

`SuppliesRecordedAt` prevents another payment/retry or the existing manual-supplies screen from consuming the plan again. Inventory expense is posted at cost using the existing stock-movement ledger convention. A refund does not mean supplies physically returned: actual returns continue through the existing audited stock-reversal workflow. Do not automatically return used surgical supplies merely because money was refunded.

## Staff payroll

The hospital chose Shamsi calendar months, with start/end months prorated by inclusive employment days divided by that Shamsi month's actual length. Only completed calendar months contribute to earned salary. Leap-year Esfand uses its actual length.

`StaffSalaryTerms` preserves salary-rate history. The first rate starts at the employment start date. Later rate changes take effect on the first day of the next Shamsi month; a pending rate can be corrected before it starts. Completed periods and an established employment start date cannot be silently rewritten. Set an explicit employment end date to stop future accrual; disabling an application account alone does not mean terminating employment.

The profile calculates an earned-salary subledger at read time; it needs no scheduler and catches up after server downtime. This projected obligation is separate from the existing cash-basis general ledger. It does not create a cash expense just because a month ended. Salary payments and withdrawals create linked `Expenses` and use the existing expense/cash ledger exactly once. Hospital debt is earned salary minus active payments/withdrawals, floored at zero; excess withdrawals are shown separately as employee advances. Salary payments cannot exceed earned outstanding salary; advances must be explicitly recorded as withdrawals.

`expenses.view` controls profile/history visibility, `expenses.create` controls salary configuration and payouts, and `expenses.cancel` controls reversing a mistaken payout. Existing staff creation/edit permissions remain required for staff changes. Canceled payouts stay visible and no longer reduce salary due. For a correction, cancel the old payout and enter the replacement from the profile; the generic expense replacement route refuses staff-linked payouts so it cannot bypass salary limits or lose their staff link.

## TV

The TV retains today's last started/completed announcement instead of expiring it after 25 seconds. Its separate doctor queue uses today's `InConsultation` and `ReadyForDoctor` visits, scoped to the doctor/department of the latest relevant doctor call, with shift/round/queue ordering for the next visit. The current patient is driven by consultation status. With several doctor rooms, the screen follows the most recently called doctor's room; it does not claim the audio station just to read queue state. The screen shows numbers and the doctor's name, never patient names or file numbers. A network outage retains the last successful display with a disconnected notice. Morning/afternoon text has been removed as requested.

## Migration and deployment boundary

`20261122151006_OperationsAndStaffPayroll` adds nullable columns to existing staff, expenses and cases, and four new tables. It does not reclassify historical cases, backfill salaries, change existing payments, or consume historical stock. Initial salary/start dates must be entered by an authorized financial operator.

Before applying to a real installation, obtain explicit migration authorization and a verified restorable backup of database, uploads, keys and configuration using the guarded deployment workflow. Rehearse restoring that backup to a separate instance and applying this migration there. Check historical patient/payment/stock counts, old expense reversals, and payroll/operation synthetic fixtures before the maintenance window. Migrate matching online/local copies through the existing schema-gated deployment process; a schema mismatch must stop replication rather than reset data.

Rollback before business use can restore the verified pre-upgrade backup and previous binaries. After new operation cases or staff payouts exist, do not run the destructive `Down` migration or restore an older database over new records: retain the added schema/data and deploy a forward correction, or reconcile/export every intervening business transaction before an explicitly authorized full restore. MySQL DDL is not assumed transactional. The source changes and isolated test runs do not constitute installation, real-data migration or physical hospital acceptance.
