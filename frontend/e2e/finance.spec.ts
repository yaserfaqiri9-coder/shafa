import { call, pick } from './helpers'
import { expect, test, type Browser, type Page } from '@playwright/test'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!
const password = 'E2e-Password-123'

async function login(page: Page, user: string, pass: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(pass)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

async function createUser(admin: Page, userName: string, roleLabel: string) {
  await admin.goto('/users')
  await admin.getByRole('button', { name: 'کاربر جدید' }).click()
  const dialog = admin.getByRole('dialog')
  await dialog.getByLabel('نام کاربری').fill(userName)
  await dialog.getByLabel('نام کامل').fill(`کارمند ${roleLabel}`)
  await dialog.getByLabel(/رمز عبور/).fill(password)
  await dialog.getByLabel(roleLabel, { exact: true }).check()
  await dialog.getByRole('button', { name: 'ذخیره' }).click()
  await expect(dialog).toBeHidden()
}

async function staffPage(browser: Browser, userName: string, errors: string[]) {
  const context = await browser.newContext()
  const page = await context.newPage()
  page.on('pageerror', (e) => errors.push(e.message))
  await login(page, userName, password)
  return page
}

test('stock is bought, sold at the pharmacy, paid at the cashier and reconciled by accounting', async ({ page, browser }) => {
  test.setTimeout(120_000)
  const errors: string[] = []
  const stamp = Date.now()

  await login(page, adminUser, adminPassword)
  await createUser(page, `inv_${stamp}`, 'مسئول گدام')
  await createUser(page, `pha_${stamp}`, 'دواخانه')
  await createUser(page, `cas_${stamp}`, 'صندوق‌دار')
  await createUser(page, `acc_${stamp}`, 'حسابدار')

  const drug = `قطره${stamp}`
  const inventory = await staffPage(browser, `inv_${stamp}`, errors)
  await expect(inventory).toHaveURL(/\/inventory$/)
  await inventory.getByRole('button', { name: 'ثبت محصول' }).click()
  const pd = inventory.getByRole('dialog')
  // The code is given by the system.
  await expect(pd.getByLabel('کد')).toHaveValue('خودکار')
  await pd.getByLabel('نام جنس').fill(drug)
  await pd.getByLabel('نوعیت دوا').selectOption({ index: 1 })
  await pd.getByLabel('قیمت فروش').fill('250')
  await pd.getByRole('button', { name: 'ذخیره' }).click()
  await expect(pd).toBeHidden()

  const supplier = `تأمین${stamp}`
  await call(inventory, 'POST', '/suppliers', { name: supplier })
  // Purchases have their own item in the sidebar, under pharmacy and optical.
  await inventory.getByRole('link', { name: 'خرید', exact: true }).click()
  await expect(inventory).toHaveURL(/\/purchases$/)
  await inventory.getByRole('link', { name: 'ثبت خرید' }).click()
  await pick(inventory, inventory.locator('body'), 'تأمین‌کننده', supplier)
  await pick(inventory, inventory.locator('body'), 'جنس ردیف 1', drug)
  await inventory.getByLabel('محل ورود').selectOption({ label: 'دواخانه' })
  await inventory.getByLabel('مقدار ردیف 1').fill('5')
  await inventory.getByLabel('قیمت خرید واحد ردیف 1').fill('100')
  await inventory.getByRole('button', { name: 'ثبت خرید' }).click()
  await expect(inventory).toHaveURL(/\/purchases$/)
  await inventory.goto('/inventory')
  await inventory.getByRole('tab', { name: 'محصولات' }).click()
  // The list is paged; searching keeps the product in view however many others earlier runs left.
  await inventory.getByLabel('جستجوی نام یا کد جنس').fill(drug)
  await expect(inventory.getByRole('row', { name: new RegExp(drug) })).toContainText('5')

  const pharmacy = await staffPage(browser, `pha_${stamp}`, errors)
  await expect(pharmacy).toHaveURL(/\/pharmacy$/)
  await pharmacy.getByRole('link', { name: 'فروش مستقل' }).click()
  await pick(pharmacy, pharmacy.locator('body'), 'جنس', drug)
  await pharmacy.getByLabel('مقدار ردیف 1').fill('2')
  await expect(pharmacy.locator('form.xr-card')).toContainText('500')
  await pharmacy.getByRole('button', { name: 'ایجاد بل و ارسال به صندوق' }).click()
  const saleDone = pharmacy.locator('.alert-success')
  await expect(saleDone).toContainText('در صندوق مرکزی پرداخت کند')
  const invoiceNumber = (await saleDone.locator('.ltr-num').nth(1).innerText()).trim()
  await pharmacy.getByRole('link', { name: 'بازگشت به دواخانه' }).click()

  // Requesting more than is left is refused with a clear message.
  await pharmacy.getByRole('link', { name: 'فروش مستقل' }).click()
  await pick(pharmacy, pharmacy.locator('body'), 'جنس', drug)
  await pharmacy.getByLabel('مقدار ردیف 1').fill('4')
  await pharmacy.getByRole('button', { name: 'ایجاد بل و ارسال به صندوق' }).click()
  await expect(pharmacy.getByRole('main').getByRole('alert')).toContainText('موجودی این جنس کافی نیست')
  // The typed sale is still open, so leaving asks first.
  await pharmacy.getByRole('link', { name: 'بازگشت به دواخانه' }).click()
  const leave = pharmacy.getByRole('dialog')
  await expect(leave).toContainText('ذخیره نشده')
  await leave.getByRole('button', { name: 'خروج بدون ذخیره' }).click()
  await expect(pharmacy).toHaveURL(/\/pharmacy$/)

  // Cashier has to open a session first, then takes a partial payment.
  // A register of this run's own: a register has one open session at a time, so a shared one could be held by another run.
  const registerName = `صندوق ${stamp}`
  await call(page, 'POST', '/cash/registers', { name: registerName })
  const cashier = await staffPage(browser, `cas_${stamp}`, errors)
  await expect(cashier).toHaveURL(/\/cashier$/)
  await expect(cashier.getByRole('tab')).toHaveCount(2)
  await expect(cashier.getByRole('tab', { name: /فاکتورهای ارجاع‌شده/ })).toHaveAttribute('aria-selected', 'true')
  await expect(cashier.getByRole('table', { name: 'دریافت‌های روز منتخب' })).toBeHidden()
  const openButton = cashier.getByRole('button', { name: 'باز کردن صندوق' })
  await expect(openButton).toBeVisible()
  await cashier.getByRole('combobox', { name: 'صندوق' }).selectOption({ label: registerName })
  await openButton.click()
  await expect(cashier.getByRole('button', { name: 'بستن صندوق' })).toBeVisible()

  await cashier.getByLabel('جستجوی مالی').fill(invoiceNumber)
  // The invoices to collect; the register's own receipts below list the same invoice number once paid.
  const toCollect = cashier.getByRole('table', { name: 'فاکتورهای قابل دریافت' })
  const row = toCollect.getByRole('row').filter({ hasText: `فاکتور ${invoiceNumber}` })
  await expect(row).toContainText('500')
  await row.getByRole('button', { name: 'دریافت' }).click()
  const pay = cashier.getByRole('dialog')
  await pay.getByLabel('مبلغ دریافتی').fill('200')
  await pay.getByRole('button', { name: 'ثبت دریافت' }).click()
  await expect(pay.getByRole('status')).toContainText('شماره رسید')
  await pay.getByRole('button', { name: 'بستن' }).click()
  await expect(row).toContainText('پرداخت ناقص')
  await expect(row).toContainText('300')
  // The payment is now listed under this register, ready to be printed again.
  await cashier.getByRole('tab', { name: /دریافت‌شده‌ها/ }).click()
  const receipts = cashier.getByRole('table', { name: 'دریافت‌های روز منتخب' })
  await expect(receipts.getByRole('row').filter({ hasText: `فاکتور ${invoiceNumber}` })).toContainText('200')

  // Accountant sees the ledger reconciled and can see the same invoice.
  const accounting = await staffPage(browser, `acc_${stamp}`, errors)
  await expect(accounting).toHaveURL(/\/cashier$/)
  await accounting.goto('/accounting')
  await expect(accounting.getByRole('status')).toContainText('تطبیق اسناد دفتر برقرار است')
  await accounting.getByRole('tab', { name: 'فاکتورها' }).click()
  await expect(accounting.getByRole('row').filter({ has: accounting.getByRole('cell', { name: invoiceNumber, exact: true }) })).toContainText('پرداخت ناقص')

  // Corrections remain on the daily desk and affect the accountant's current drawer.
  const accountantRegister = `صندوق حسابدار ${stamp}`
  await call(page, 'POST', '/cash/registers', { name: accountantRegister })
  await accounting.goto('/cashier')
  await accounting.getByRole('combobox', { name: 'صندوق', exact: true }).selectOption({ label: accountantRegister })
  await accounting.getByLabel('موجودی شروع').fill('1000')
  await accounting.getByRole('button', { name: 'باز کردن صندوق', exact: true }).click()
  await accounting.getByLabel('جستجوی مالی').fill(invoiceNumber)
  await accounting.getByRole('tab', { name: /دریافت‌شده‌ها/ }).click()
  const accountantReceipts = accounting.getByRole('table', { name: 'دریافت‌های روز منتخب' })
  const accountantInvoiceReceipts = accountantReceipts.getByRole('row').filter({ has: accounting.getByText(new RegExp(`^رسید .+ · فاکتور ${invoiceNumber}$`)) })
  await accountantInvoiceReceipts.getByRole('button', { name: 'اصلاح', exact: true }).click()
  const correction = accounting.getByRole('dialog')
  await correction.getByLabel('مبلغ درست دریافتی').fill('150')
  await correction.getByLabel('دلیل', { exact: true }).fill('اصلاح دریافت آزمایشی')
  await correction.getByRole('button', { name: 'ثبت اصلاح', exact: true }).click()
  await expect(correction).toBeHidden()
  await accounting.getByRole('tab', { name: /فاکتورهای ارجاع‌شده/ }).click()
  const accountantUnpaid = accounting.getByRole('table', { name: 'فاکتورهای قابل دریافت' })
  await expect(accountantUnpaid.getByRole('row').filter({ hasText: `فاکتور ${invoiceNumber}` })).toContainText('350')
  await accounting.getByRole('tab', { name: /دریافت‌شده‌ها/ }).click()
  await accountantInvoiceReceipts.getByRole('button', { name: 'لغو دریافت', exact: true }).click()
  const cancellation = accounting.getByRole('dialog')
  await cancellation.getByLabel('دلیل', { exact: true }).fill('لغو دریافت آزمایشی')
  await cancellation.getByRole('button', { name: 'تأیید لغو دریافت', exact: true }).click()
  await expect(cancellation).toBeHidden()
  await accounting.getByRole('tab', { name: /فاکتورهای ارجاع‌شده/ }).click()
  await expect(accountantUnpaid.getByRole('row').filter({ hasText: `فاکتور ${invoiceNumber}` })).toContainText('500')
  await accounting.getByRole('tab', { name: /دریافت‌شده‌ها/ }).click()
  await accounting.getByRole('button', { name: 'بستن صندوق', exact: true }).click()
  const accountantClose = accounting.getByRole('dialog')
  await expect(accountantClose.getByLabel('مبلغ شمارش‌شده')).toHaveValue('800')
  await accountantClose.getByRole('button', { name: 'بستن صندوق', exact: true }).click()
  await expect(accountantClose).toBeHidden()
  await expect(accountantInvoiceReceipts).toHaveCount(2)

  // Close the session so the shared register is free for the next run.
  await cashier.getByRole('button', { name: 'بستن صندوق' }).click()
  const closeDialog = cashier.getByRole('dialog')
  await closeDialog.getByLabel('مبلغ شمارش‌شده').fill('200')
  await closeDialog.getByRole('button', { name: 'بستن صندوق' }).click()
  await expect(closeDialog).toBeHidden()
  await expect(cashier.getByRole('button', { name: 'باز کردن صندوق' })).toBeVisible()

  expect(errors).toEqual([])
})
