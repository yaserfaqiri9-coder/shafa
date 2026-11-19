/* Network-only: never store authentication, API responses, clinical data or application bundles. */
const offlineDocument = `<!doctype html><html lang="fa" dir="rtl"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><meta name="theme-color" content="#089bab"><title>چشم‌الشفا — اتصال قطع است</title><style>body{margin:0;background:#eff7f8;color:#263c42;font:18px/1.9 system-ui,sans-serif;display:grid;min-height:100dvh;place-items:center}main{max-width:28rem;padding:32px;text-align:center}h1{font-size:24px}a{display:inline-block;text-decoration:none;font:inherit;background:#089bab;color:white;border:0;border-radius:12px;padding:10px 24px;cursor:pointer}</style><main><h1>اتصال به سیستم برقرار نیست</h1><p>اتصال انترنت یا وای‌فای شفاخانه را بررسی کنید و دوباره تلاش کنید.</p><p>برای مشاهده و ثبت اطلاعات باید به سرور وصل باشید.</p><a href="">تلاش دوباره</a></main></html>`;

self.addEventListener('fetch', (event) => {
  const url = new URL(event.request.url);
  if (event.request.method !== 'GET' || event.request.mode !== 'navigate'
    || url.origin !== self.location.origin || /^\/(api|hubs|print)(\/|$)/.test(url.pathname)) return;
  event.respondWith(fetch(event.request).catch(() => new Response(offlineDocument, {
    status: 503,
    headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' },
  })));
});

