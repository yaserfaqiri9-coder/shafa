# قواعد کار در این پروژه

سرعت مهم است. فقط کاری را بکن که تسک لازم دارد. نقشه پروژه در `README.md` است؛ سندهای دیگر را فقط وقتی بخوان که تسک به آن‌ها مربوط است. از فایل‌های بزرگ مانند `ChartPage.vue` فقط بخش لازم را بخوان.

## تست و Build

- حین کار فقط تست کلاس مربوط به تغییر را اجرا کن، نه همه تست‌ها. فقط کلاس‌هایی که جدول زیر نام برده؛ کلاس اضافه اجرا نکن.
- هر اجرای تست هزینه ثابت دارد (راه‌اندازی MySQL جدا و Build). همه کلاس‌های لازم را با یک فیلتر و یک بار اجرا کن: `-Filter 'FullyQualifiedName~FinanceTests|FullyQualifiedName~ConcurrencyTests'`. پس از پاس‌شدن، همان را دوباره اجرا نکن.
- تست را در پس‌زمینه اجرا کن و هم‌زمان بقیه کار (رابط کاربری، E2E Spec) را جلو ببر.
- تست کامل بک‌اند و E2E را فقط یک بار در پایان تسک اجرا کن، و فقط اگر تسک کوچک نیست. کاربر هم می‌تواند آن را جداگانه بخواهد.
- برای تغییر فقط در متن، سند یا فقط CSS، تست اجرا نکن.
- برای تغییر رابط کاربری (TypeScript یا Template) یک بار در پایان `npx vue-tsc --noEmit` را اجرا کن (~۲۰ ثانیه)، نه پس از هر ویرایش. E2E فقط Spec همان بخش.
- اگر Build شکست خورد، نتیجه تست معتبر نیست، چون روی نسخه قدیمی اجرا می‌شود.
- اسکریپت در `.tools\automation-build` Build می‌کند، پس API باز با `start.bat` آن را قفل نمی‌کند.

تغییر در پول، موجودی، صلاحیت یا Migration این قواعد را محدود می‌کند: تست‌های مربوط آن بخش همیشه باید اجرا شوند و پیش از پایان کار پاس شوند.

| تغییر | کلاس تست |
|---|---|
| پول و صندوق | `FinanceTests`, `ConcurrencyTests` |
| دواخانه و موجودی | `ConcurrencyTests`, `HospitalReadinessTests` |
| Migration و دیتابیس | `MigrationSafetyTests` |
| ورود و صلاحیت | `AuthTests`, `PilotReadinessTests` |
| پذیرش و مراجعه | `ReceptionTests`, `VisitFlowTests`, `AdmissionFlowTests` |
| بالینی | `ClinicalTests`, `RefractionTests` |
| فراخوان | `CallTests` |
| گزارش | `ReportTests` |

## دستورها

بک‌اند، از ریشه پروژه. `dotnet test` مستقیم کار نمی‌کند: کلاس‌های تست از `PhysicalQueueTestScope` ارث می‌برند و بدون اسکریپت زیر با «Use the verified dedicated-instance runner for queue tests» شکست می‌خورند. اسکریپت یک MySQL جدا و موقت روی پورت 33379–33399 راه می‌اندازد. `dev-env.ps1` را پیش از آن بار نکن:

```powershell
.\scripts\test-automation-isolated.ps1 -Filter 'FullyQualifiedName~FinanceTests' -SkipBackupDrill
```

بدون `-Filter` همه کلاس‌های مهم را اجرا می‌کند؛ بدون `-SkipBackupDrill` تمرین پشتیبان‌گیری را هم اجرا می‌کند (فقط برای تست کامل پایانی).

رابط کاربری، از پوشه `frontend`. E2E به سرور توسعه نیاز دارد، چون `start.bat` نسخه Build شده را روی 5173 اجرا می‌کند:

```powershell
npx vue-tsc --noEmit
npx vite --port 5199
$env:ALSHIFA_E2E_BASE = 'http://localhost:5199'
$env:ALSHIFA_E2E_USER = '<user>'
$env:ALSHIFA_E2E_PASSWORD = '<password>'
npx playwright test e2e/reception.spec.ts
```

`npx vite` تا بسته‌شدن باز می‌ماند؛ آن را در پس‌زمینه یا ترمینال جدا اجرا کن.

## جلسه‌های هم‌زمان

هر اجرای اسکریپت دیتابیس تازه و جدای خودش را می‌سازد. اسکریپت قفل سراسری دارد و اگر جلسه دیگری در حال تست باشد، شروع نمی‌شود. هیچ تستی به `alshifa` روی 3308 (نصب‌شده) وصل نشود. پیش از اجرا بررسی کن تست دیگری در حال اجرا نباشد:

```powershell
Get-Process testhost -ErrorAction SilentlyContinue
```
