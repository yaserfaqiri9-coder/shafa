import { readFile } from 'node:fs/promises'
import { expect, test, type Browser, type Page } from '@playwright/test'
import { call } from './helpers'

const adminUser = process.env.ALSHIFA_E2E_USER!
const adminPassword = process.env.ALSHIFA_E2E_PASSWORD!
const password = 'E2e-Password-123'

const overview = 'گزارش عمومی شفاخانه'
const finance = 'گزارش مالی و حسابداری'
const pharmacy = 'گزارش دواخانه و گدام'

test.afterEach(async ({ browser }) => {
  await Promise.all(browser.contexts().map(context => context.close()))
})

async function login(page: Page, user: string, pass: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(user)
  await page.getByLabel('رمز عبور').fill(pass)
  await page.getByRole('button', { name: 'ورود' }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

/** A signed-in page of a new user holding one role. */
async function userWithRole(browser: Browser, role: string, viewport?: { width: number; height: number }) {
  const context = await browser.newContext()
  const admin = await context.newPage()
  await login(admin, adminUser, adminPassword)
  const userName = `rep_${crypto.randomUUID().slice(0, 13)}`
  await call(admin, 'POST', '/users', { userName, fullName: `گزارش ${role}`, password, roles: [role] })
  await context.close()

  const own = await browser.newContext({ acceptDownloads: true, viewport: viewport ?? { width: 1400, height: 900 } })
  const page = await own.newPage()
  await login(page, userName, password)
  return page
}

const tabs = (page: Page) => page.getByRole('tablist', { name: 'دسته گزارش‌ها' })
const tab = (page: Page, name: string) => tabs(page).getByRole('tab', { name })
const bar = (page: Page) => page.getByRole('region', { name: 'فیلترهای گزارش‌ها' })
const heading = (page: Page) => page.getByRole('heading', { level: 2, name: /^گزارش / })
const tiles = (page: Page) => page.locator('.report-tiles .rs')
const reportItems = (page: Page) => page.locator('.report-grid > .report-item')
const item = (page: Page, name: string) => reportItems(page).filter({ has: page.locator('.report-item__name', { hasText: new RegExp(`^${name.replace(/[:()]/g, '.')}$`) }) })
const reportTitle = (page: Page) => page.locator('.fbar__title')

test('the owner opens on the overview with four main figures and each report as one picture that opens alone', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports')
  await expect(page).toHaveURL(/tab=overview.*period=today/)
  for (const name of [overview, finance, pharmacy]) await expect(tab(page, name)).toBeVisible()
  await expect(tab(page, overview)).toHaveAttribute('aria-selected', 'true')
  await expect(heading(page)).toHaveText(overview)
  await expect(tiles(page)).toHaveCount(4)
  for (const label of ['مریضان', 'مریضان معاینه‌شده', 'عواید', 'خالص دریافتی']) {
    await expect(tiles(page).filter({ has: page.locator('.rs__label', { hasText: new RegExp(`^${label}$`) }) })).toHaveCount(1)
  }
  // No folding rows: the reports are pictures, and none of their lists is on the tab.
  await expect(page.locator('[aria-expanded]').filter({ has: page.locator('.report-detail__title') })).toHaveCount(0)
  await expect(reportItems(page)).toHaveCount(2)
  await expect(page.getByRole('columnheader', { name: 'عواید خالص بخش' })).toHaveCount(0)

  // One report opens on its own page, with only its own list; back returns to the tab with the same days.
  await item(page, 'عواید هر بخش').click()
  await expect(page).toHaveURL(/tab=overview.*item=sections/)
  await expect(reportTitle(page)).toHaveText('عواید هر بخش')
  for (const name of ['معاینه داکتر', 'رفرکشن', 'عکس‌برداری', 'لابراتوار', 'جراحی', 'دواخانه', 'عینک']) {
    await expect(page.getByRole('cell', { name, exact: true })).toBeVisible()
  }
  await expect(page.getByRole('columnheader', { name: 'معاینه‌شده' })).toHaveCount(0)
  await expect(tiles(page)).toHaveCount(0)
  for (const name of ['چاپ', 'Excel', 'PDF']) await expect(page.getByRole('button', { name, exact: true })).toBeVisible()
  await page.getByRole('button', { name: 'همه گزارش‌ها' }).click()
  await expect(page).not.toHaveURL(/item=/)
  await expect(reportItems(page)).toHaveCount(2)
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('the three tabs keep the same days, and a tab and its days survive a reload', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports')
  await expect(heading(page)).toHaveText(overview)

  // Only the simple choices: no «ماه گذشته».
  for (const name of ['امروز', 'دیروز', 'این هفته', 'این ماه', 'دوره دلخواه']) await expect(bar(page).getByRole('button', { name, exact: true })).toBeVisible()
  await expect(bar(page).getByRole('button', { name: 'ماه گذشته' })).toHaveCount(0)

  const month = page.waitForResponse((r) => r.url().includes('/api/reports/overview') && r.url().includes('from='))
  await bar(page).getByRole('button', { name: 'این ماه' }).click()
  const asked = new URL((await month).url()).searchParams
  expect(asked.get('from')! <= asked.get('to')!).toBe(true)
  await expect(page).toHaveURL(/period=month/)

  const money = page.waitForResponse((r) => r.url().includes('/api/reports/finance'))
  await tab(page, finance).click()
  const same = new URL((await money).url()).searchParams
  expect([same.get('from'), same.get('to')]).toEqual([asked.get('from'), asked.get('to')])
  await expect(heading(page)).toHaveText(finance)
  await expect(page).toHaveURL(/tab=finance.*period=month/)

  await page.reload()
  await expect(heading(page)).toHaveText(finance)
  await expect(bar(page).getByRole('button', { name: 'این ماه' })).toHaveAttribute('aria-pressed', 'true')

  await bar(page).getByRole('button', { name: 'دوره دلخواه' }).click()
  const custom = page.waitForResponse((r) => r.url().includes('/api/reports/finance') && r.url().includes('from=2026-03-21'))
  await page.getByRole('textbox', { name: 'از تاریخ' }).fill('1405/01/01')
  await page.getByRole('textbox', { name: 'از تاریخ' }).press('Tab')
  await page.getByRole('textbox', { name: 'تا تاریخ' }).fill('1405/01/05')
  await page.getByRole('textbox', { name: 'تا تاریخ' }).press('Tab')
  await bar(page).getByRole('button', { name: 'نمایش', exact: true }).click()
  await custom
  await expect(page).toHaveURL(/period=custom.*from=2026-03-21.*to=2026-03-25/)
  await expect(bar(page).getByText('1 حمل تا 5 حمل 1405')).toBeVisible()
})

test('an accountant reads the books and takes one report alone as Excel and PDF', async ({ browser }) => {
  const page = await userWithRole(browser, 'Accountant')
  await page.goto('/reports?tab=finance')
  await expect(heading(page)).toHaveText(finance)
  await expect(tiles(page)).toHaveCount(4)
  for (const name of ['عواید هر بخش', 'محاسبه خالص دریافتی', 'پول نقد شفاخانه', 'صندوق‌ها', 'مصارف به تفکیک نوع', 'طلبات: فاکتورهای پرداخت‌نشده']) {
    await expect(item(page, name)).toBeVisible()
  }
  // The debt to companies cannot be known, and the page says so instead of showing a number.
  await expect(page.getByRole('note').filter({ hasText: 'بدهی شفاخانه به شرکت‌ها' })).toBeVisible()

  // Each money figure opens the report behind it in one click.
  await page.getByRole('button', { name: 'خالص دریافتی: جزئیات' }).click()
  await expect(page).toHaveURL(/item=net/)
  await expect(reportTitle(page)).toHaveText('محاسبه خالص دریافتی')
  await page.goBack()
  await page.getByRole('button', { name: 'عواید: جزئیات' }).click()
  await expect(page).toHaveURL(/item=sections/)
  await expect(reportTitle(page)).toHaveText('عواید هر بخش')
  await page.goBack()

  await item(page, 'مصارف به تفکیک نوع').click()
  await expect(page).toHaveURL(/item=expenses/)
  await expect(reportTitle(page)).toHaveText('مصارف به تفکیک نوع')
  const [excel] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'Excel', exact: true }).click()])
  expect(excel.suggestedFilename()).toMatch(/^report-finance-expenses-\d{4}-\d{2}-\d{2}\.xlsx$/)
  expect([...(await readFile(await excel.path())).subarray(0, 2)]).toEqual([0x50, 0x4b])

  const [pdf] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name: 'PDF', exact: true }).click()])
  expect(pdf.suggestedFilename()).toMatch(/^report-finance-expenses-\d{4}-\d{2}-\d{2}\.pdf$/)
  expect((await readFile(await pdf.path())).subarray(0, 4).toString('ascii')).toBe('%PDF')
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('a doctor sees the work of the hospital but no money, no other tab and no files', async ({ browser }) => {
  const page = await userWithRole(browser, 'Doctor')
  const asked: string[] = []
  page.on('request', (request) => {
    if (request.url().includes('/api/reports/')) asked.push(new URL(request.url()).pathname)
  })

  await page.goto('/reports?tab=finance&period=month')
  await expect(heading(page)).toHaveText(overview)
  await expect(page).toHaveURL(/tab=overview/)
  await expect(tabs(page)).toHaveCount(0)
  await expect(tiles(page)).toHaveCount(2)
  await expect(page.getByText('خالص دریافتی')).toHaveCount(0)
  await item(page, 'مریضان هر داکتر').click()
  await expect(reportTitle(page)).toHaveText('مریضان هر داکتر')
  for (const name of ['چاپ', 'Excel', 'PDF']) await expect(page.getByRole('button', { name, exact: true })).toHaveCount(0)
  await expect(page.getByRole('alert')).toHaveCount(0)
  expect(asked).not.toContain('/api/reports/finance')
})

test('the pharmacy tab shows sales, profit and the store in four figures', async ({ browser }) => {
  const page = await userWithRole(browser, 'Pharmacist')
  // A pharmacist also reads the overview's counts, so the pharmacy tab is chosen.
  await page.goto('/reports?tab=pharmacy')
  await expect(heading(page)).toHaveText(pharmacy)
  await expect(tiles(page)).toHaveCount(4)
  for (const label of ['فروشات', 'سود فروشات', 'ارزش موجودی گدام', 'اجناس تمام‌شده یا کمبود']) {
    await expect(tiles(page).filter({ has: page.locator('.rs__label', { hasText: new RegExp(`^${label}$`) }) })).toHaveCount(1)
  }
  for (const name of ['فروشات', 'اجناس تمام‌شده و کمبود', 'اجناس منقضی و نزدیک به انقضا', 'موجودی همه اجناس']) await expect(item(page, name)).toBeVisible()
  await item(page, 'فروشات').click()
  await expect(page.getByRole('cell', { name: 'دواخانه', exact: true })).toBeVisible()
  await expect(page.getByRole('cell', { name: 'عینک', exact: true })).toHaveCount(0)
})

test('a late answer never replaces a newer choice', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.goto('/reports')
  await expect(heading(page)).toHaveText(overview)

  await page.route('**/api/reports/finance*', async (route) => {
    await new Promise((resolve) => setTimeout(resolve, 1500))
    await route.continue().catch(() => {})
  })
  await tab(page, finance).click()
  await tab(page, pharmacy).click()
  await expect(heading(page)).toHaveText(pharmacy)
  await page.waitForTimeout(2000)
  await expect(heading(page)).toHaveText(pharmacy)
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('a failed report says so and retry brings it back', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager')
  await page.route('**/api/reports/overview*', (route) => route.fulfill({ status: 500, json: {} }))
  await page.goto('/reports')
  await expect(page.getByRole('alert')).toBeVisible()
  await page.unroute('**/api/reports/overview*')
  await page.getByRole('button', { name: 'تلاش دوباره' }).click()
  await expect(heading(page)).toHaveText(overview)
  await expect(page.getByRole('alert')).toHaveCount(0)
})

test('at phone width every tab works and the page never scrolls sideways', async ({ browser }) => {
  const page = await userWithRole(browser, 'Manager', { width: 375, height: 760 })
  await page.goto('/reports?period=month')
  for (const name of [overview, finance, pharmacy]) {
    await tab(page, name).click()
    await expect(heading(page)).toHaveText(name)
    const sideways = () => page.evaluate(() => {
      const main = document.querySelector('.layout__main')!
      return Math.max(document.documentElement.scrollWidth - document.documentElement.clientWidth, main.scrollWidth - main.clientWidth)
    })
    expect(await sideways()).toBeLessThanOrEqual(0)
    await reportItems(page).first().click()
    await expect(page).toHaveURL(/item=/)
    expect(await sideways()).toBeLessThanOrEqual(0)
    await page.getByRole('button', { name: 'همه گزارش‌ها' }).click()
  }
})
