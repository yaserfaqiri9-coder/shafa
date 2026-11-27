import { expect, test, type Page } from '@playwright/test'
import { mkdir } from 'node:fs/promises'
import { resolve } from 'node:path'
import { call } from './helpers'
import { formatShamsi } from '../src/utils/date'

async function login(page: Page, name: string, password: string) {
  await page.goto('/login')
  await page.getByLabel('نام کاربری').fill(name)
  await page.getByLabel('رمز عبور').fill(password)
  await page.getByRole('button', { name: 'ورود', exact: true }).click()
  await expect(page).not.toHaveURL(/\/login/)
}

for (const kind of ['pharmacy', 'optical'] as const) {
  test(`${kind} staff can search sales and select any historical day without leaving their list`, async ({ page, browser }) => {
    test.setTimeout(90_000)
    const errors: string[] = []
    const stamp = Date.now()
    await login(page, process.env.ALSHIFA_E2E_USER!, process.env.ALSHIFA_E2E_PASSWORD!)
    const userName = `history_${kind}_${stamp}`
    const password = 'E2e-Password-123'
    await call(page, 'POST', '/users', { userName, fullName: 'کارمند ساختگی سوابق', password, roles: [kind === 'pharmacy' ? 'Pharmacist' : 'Optical'] })
    const patient = await call<{ id: number; fileNumber: string }>(page, 'POST', '/patients', { firstName: `آزمایشی ${stamp}`, lastName: 'سوابق', gender: 'Male', estimatedAge: 30 })
    const productName = `قطره آزمایشی ${stamp}`
    let saleNumber: string
    let cancelledNumber = ''
    if (kind === 'pharmacy') {
      const product = await call<{ id: number }>(page, 'POST', '/inventory/products', { name: productName, kind: 'Drug', unit: 'عدد', reorderLevel: 0, salePrice: 100 })
      await call(page, 'POST', '/inventory/purchases', { supplierName: 'تأمین‌کننده ساختگی', locationCode: 'pharmacy', items: [{ productId: product.id, lotNumber: `H${stamp}`, quantity: 5, unitCost: 40 }] })
      const sale = await call<{ saleNumber: string }>(page, 'POST', '/pharmacy/sales', { patientId: patient.id, items: [{ productId: product.id, quantity: 1 }] })
      saleNumber = sale.saleNumber
    } else {
      const body = { patientId: patient.id, description: 'عینک آزمایشی سوابق', customLines: [{ description: 'عدسی سفارشی', amount: 100, isNonStockLens: true }] }
      const order = await call<{ orderNumber: string }>(page, 'POST', '/optical/orders', body)
      saleNumber = order.orderNumber
      const cancelled = await call<{ id: number; orderNumber: string; version: number }>(page, 'POST', '/optical/orders', body)
      await call(page, 'POST', `/optical/orders/${cancelled.id}/status`, { status: 'Cancelled', version: cancelled.version, reason: 'لغو آزمایشی برای سابقه' })
      cancelledNumber = cancelled.orderNumber
    }

    const context = await browser.newContext()
    const staff = await context.newPage()
    staff.on('pageerror', (error) => errors.push(error.message))
    await login(staff, userName, password)
    await expect(staff).toHaveURL(new RegExp(`/${kind}$`))
    await staff.getByRole('tab', { name: kind === 'pharmacy' ? 'سوابق فروش' : 'فروشات', exact: true }).click()
    const list = staff.getByRole('list', { name: kind === 'pharmacy' ? 'سوابق فروش' : 'فروشات عینک', exact: true })
    const number = () => list.locator('.dq__code').filter({ hasText: new RegExp(`^${saleNumber}$`) })
    await expect(number()).toBeVisible()
    if (cancelledNumber) await expect(list.getByText(cancelledNumber, { exact: true })).toBeVisible()

    const period = staff.getByRole('group', { name: kind === 'pharmacy' ? 'تاریخ ثبت فروش دواخانه' : 'تاریخ ثبت فروش عینک', exact: true })
    await period.getByRole('button', { name: 'دیروز', exact: true }).click()
    await expect(number()).toHaveCount(0)
    await expect(staff.getByText('در تاریخ و جستجوی انتخاب‌شده فروشی پیدا نشد.', { exact: true })).toBeVisible()

    await period.getByRole('button', { name: 'دوره دلخواه', exact: true }).click()
    await staff.getByLabel('از تاریخ', { exact: true }).fill(formatShamsi('2024-03-20'))
    await staff.getByLabel('تا تاریخ', { exact: true }).fill(formatShamsi('2024-03-20'))
    const request = staff.waitForResponse((r) => {
      const url = new URL(r.url())
      return url.pathname === `/api/${kind}/${kind === 'pharmacy' ? 'sales' : 'orders'}` && url.searchParams.get('from') === '2024-03-20' && url.searchParams.get('to') === '2024-03-20'
    })
    await staff.getByRole('button', { name: 'نمایش', exact: true }).click()
    expect((await request).ok()).toBeTruthy()
    await expect(number()).toHaveCount(0)
    await period.getByRole('button', { name: 'همه روزها', exact: true }).click()
    await expect(number()).toBeVisible()

    const search = staff.getByLabel(kind === 'pharmacy' ? 'جستجوی سوابق فروش دواخانه' : 'جستجوی مریض، شماره نسخه یا سفارش', { exact: true })
    const fileSearch = staff.waitForResponse((r) => {
      const url = new URL(r.url())
      return url.pathname === `/api/${kind}/${kind === 'pharmacy' ? 'sales' : 'orders'}` && url.searchParams.get('q') === patient.fileNumber
    })
    await search.fill(patient.fileNumber)
    expect((await fileSearch).ok()).toBeTruthy()
    await expect(number()).toBeVisible()
    if (kind === 'pharmacy') {
      await search.fill(productName)
      await expect(number()).toBeVisible()
      // Sales search never replaces the history with the patient/prescription search panel.
      await expect(staff.getByRole('tab', { name: 'سوابق فروش', exact: true })).toHaveAttribute('aria-selected', 'true')
    }
    await period.getByRole('button', { name: 'امروز', exact: true }).click()
    await expect(number()).toBeVisible()
    const artifact = resolve('../.tools/sales-history-verification')
    await mkdir(artifact, { recursive: true })
    await staff.screenshot({ path: resolve(artifact, `${kind}-desktop.png`), fullPage: true, animations: 'disabled' })
    await staff.setViewportSize({ width: 390, height: 844 })
    await staff.reload()
    await staff.getByRole('tab', { name: kind === 'pharmacy' ? 'سوابق فروش' : 'فروشات', exact: true }).click()
    await expect(number()).toBeVisible()
    await staff.screenshot({ path: resolve(artifact, `${kind}-mobile.png`), fullPage: true, animations: 'disabled' })
    expect(await staff.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 2)).toBeTruthy()
    expect(errors).toEqual([])
    await context.close()
  })
}
