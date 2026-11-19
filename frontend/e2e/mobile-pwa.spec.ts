import { expect, test, type Page } from '@playwright/test'

test.use({ channel: 'msedge' })

async function mockServer(page: Page, signedIn = false) {
  await page.route(/^https?:\/\/[^/]+\/api\//, route => {
    const path = new URL(route.request().url()).pathname.slice(4)
    if (path === '/auth/me') return signedIn
      ? route.fulfill({ json: { id: 1, userName: 'test', fullName: 'کاربر آزمایشی', roles: ['Manager'], permissions: ['patients.view', 'reports.view', 'ledger.view'] } })
      : route.fulfill({ status: 401, json: { message: 'Unauthorized' } })
    if (path === '/reports/dashboard') return route.fulfill({ json: { today: '2026-11-18', yesterdayVisits: 0, todayByStatus: [], days: [], visitsByDepartment: [], lowStockProducts: 12 } })
    if (path === '/reports/financial') return route.fulfill({ json: { receipts: 1234567, expenses: 12000, outstandingNow: 45678 } })
    return route.fulfill({ json: { items: [], total: 0 } })
  })
  await page.route(/^https?:\/\/[^/]+\/hubs\//, route => route.abort())
}

test('manifest and application icons are available without signing in', async ({ page }) => {
  await mockServer(page)
  await page.goto('/login')
  const manifest = await page.evaluate(async () => {
    const href = document.querySelector<HTMLLinkElement>('link[rel="manifest"]')!.href
    return (await fetch(href)).json()
  })
  expect(manifest.display).toBe('standalone')
  expect(manifest.start_url).toBe('/login')
  expect(manifest.scope).toBe('/')
  for (const icon of manifest.icons) {
    const size = await page.evaluate((src: string) => new Promise<number>((resolve, reject) => {
      const image = new Image()
      image.onload = () => resolve(image.naturalWidth)
      image.onerror = reject
      image.src = src
    }), icon.src)
    expect(icon.sizes).toBe(`${size}x${size}`)
  }
})

test('installation help opens and closing it returns focus to the trigger', async ({ page }) => {
  await mockServer(page)
  await page.goto('/login')
  const install = page.getByRole('button', { name: 'نصب چشم‌الشفا' })
  await install.click()
  await expect(page.getByRole('dialog')).toContainText('Windows')
  await page.keyboard.press('Escape')
  await expect(page.getByRole('dialog')).toHaveCount(0)
  await expect(install).toBeFocused()
})

test('a dismissed install prompt is single use and manual help remains available', async ({ page }) => {
  await mockServer(page)
  await page.goto('/login')
  await page.getByRole('button', { name: 'ورود', exact: true }).waitFor()
  await page.evaluate(() => {
    const state = window as Window & { installAttempts?: number }
    state.installAttempts = 0
    const event = Object.assign(new Event('beforeinstallprompt', { cancelable: true }), {
      prompt: async () => { state.installAttempts!++ },
      userChoice: Promise.resolve({ outcome: 'dismissed' }),
    })
    window.dispatchEvent(event)
  })
  const install = page.getByRole('button', { name: 'نصب چشم‌الشفا' })
  await install.click()
  expect(await page.evaluate(() => (window as Window & { installAttempts: number }).installAttempts)).toBe(1)
  await install.click()
  await expect(page.getByRole('dialog')).toBeVisible()
})

for (const width of [320, 390, 768, 1024, 1440, 1920]) {
  test(`dashboard fits ${width}px and the header never covers the first card`, async ({ page }) => {
    await mockServer(page, true)
    await page.setViewportSize({ width, height: 844 })
    await page.goto('/')
    await expect(page.locator('.home-stats .stat')).toHaveCount(5)
    await page.evaluate(() => document.fonts.ready)
    const metrics = await page.evaluate(() => {
      const main = document.querySelector<HTMLElement>('.layout__main')!
      return {
        rootWidth: document.documentElement.scrollWidth,
        mainWidth: main.clientWidth,
        mainScroll: main.scrollWidth,
        headerBottom: document.querySelector('.header')!.getBoundingClientRect().bottom,
        statsTop: document.querySelector('.home-stats')!.getBoundingClientRect().top,
      }
    })
    expect(metrics.rootWidth).toBeLessThanOrEqual(width + 1)
    expect(metrics.mainScroll).toBeLessThanOrEqual(metrics.mainWidth + 1)
    expect(metrics.headerBottom).toBeLessThanOrEqual(metrics.statsTop)
    const clippedAmounts = await page.locator('.stat__value').evaluateAll(elements =>
      elements.filter(el => el.scrollWidth > el.clientWidth + 1 || el.scrollHeight > el.clientHeight + 1).length,
    )
    expect(clippedAmounts).toBe(0)
    if (width < 768) await expect(page.getByLabel('جستجوی مریض در هدر')).toHaveCSS('font-size', '16px')
  })
}
