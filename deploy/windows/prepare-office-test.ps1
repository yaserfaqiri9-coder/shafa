# Prepares only the isolated office-test DNS name. Never connects the office database to production.
#Requires -RunAsAdministrator
param([string]$Root = 'C:\AlShifa', [string]$ReportPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'trusted-lan-common.ps1')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$tlsRoot = Join-Path $Root 'tls'
$settingsPath = Join-Path $tlsRoot 'office-bootstrap.json'
$result = [ordered]@{ Completed = $false; TestHostName = 'test-shafa.novatechsoft.com' }
$tokenPlain = $null
$stage = 'opening_secure_prompt'
try {
    $service = Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
    if ($service.PathName.Trim().Trim('"') -ne (Join-Path $Root 'app\AlShifa.Api.exe')) { throw 'Installation identity did not match.' }
    $form = New-Object Windows.Forms.Form
    $form.Text = 'AlShifa - Secure Cloudflare setup'
    $form.Size = New-Object Drawing.Size(650,335)
    $form.StartPosition = 'CenterScreen'
    $form.TopMost = $true
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $label = New-Object Windows.Forms.Label
    $label.Text = 'توکن Cloudflare را اینجا وارد کنید؛ آن را در چت نفرستید.'
    $label.RightToLeft = 'Yes'
    $label.SetBounds(25,20,590,35)
    $form.Controls.Add($label)
    $inputBox = New-Object Windows.Forms.TextBox
    $inputBox.UseSystemPasswordChar = $true
    $inputBox.SetBounds(25,65,590,30)
    $form.Controls.Add($inputBox)
    $details = New-Object Windows.Forms.Label
    $details.Text = "مقصد آزمایش دفتر: test-shafa.novatechsoft.com`nاطلاعات سایت اصلی و حساب‌های فعلی تغییر نمی‌کنند."
    $details.RightToLeft = 'Yes'
    $details.SetBounds(25,110,590,50)
    $form.Controls.Add($details)
    $terms = New-Object Windows.Forms.CheckBox
    $terms.Text = "شرایط صدور گواهی Let's Encrypt را می‌پذیرم."
    $terms.RightToLeft = 'Yes'
    $terms.SetBounds(25,170,590,30)
    $form.Controls.Add($terms)
    $link = New-Object Windows.Forms.LinkLabel
    $link.Text = 'https://letsencrypt.org/repository/'
    $link.SetBounds(25,205,350,25)
    $link.add_LinkClicked({ Start-Process 'https://letsencrypt.org/repository/' })
    $form.Controls.Add($link)
    $ok = New-Object Windows.Forms.Button
    $ok.Text = 'ادامه'
    $ok.SetBounds(485,245,130,35)
    $ok.add_Click({ if ($inputBox.Text.Trim().Length -ge 20 -and $terms.Checked) { $form.DialogResult = [Windows.Forms.DialogResult]::OK; $form.Close() } })
    $form.Controls.Add($ok)
    $form.AcceptButton = $ok
    if ($form.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { $result.Cancelled = $true; return }
    $tokenPlain = $inputBox.Text.Trim()
    $inputBox.Clear()
    $form.Dispose()
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $headers = @{ Authorization = 'Bearer ' + $tokenPlain }
    $stage = 'verifying_cloudflare_token'
    $verification = Invoke-RestMethod -Uri 'https://api.cloudflare.com/client/v4/user/tokens/verify' -Headers $headers -MaximumRedirection 0
    if (-not $verification.success -or $verification.result.status -ne 'active') { throw 'Cloudflare reports that this API token is not active.' }
    $stage = 'checking_cloudflare_zone_access'
    $zones = Invoke-RestMethod -Uri 'https://api.cloudflare.com/client/v4/zones?name=novatechsoft.com' -Headers $headers -MaximumRedirection 0
    if (-not $zones.success -or @($zones.result).Count -ne 1) { throw 'Zone access could not be verified.' }
    $zoneId = $zones.result[0].id
    $recordUrl = "https://api.cloudflare.com/client/v4/zones/$zoneId/dns_records"
    $stage = 'checking_test_dns_record'
    $records = Invoke-RestMethod -Uri ($recordUrl + '?name=test-shafa.novatechsoft.com') -Headers $headers -MaximumRedirection 0
    if (-not $records.success) { throw 'DNS access could not be verified.' }
    if (@($records.result).Count -eq 0) {
        $stage = 'creating_isolated_test_dns_record'
        $body = @{ type = 'A'; name = 'test-shafa.novatechsoft.com'; content = '157.173.109.255'; ttl = 300; proxied = $false; comment = 'Isolated AlShifa office acceptance test; production data is separate.' } | ConvertTo-Json
        $created = Invoke-RestMethod -Uri $recordUrl -Method Post -Headers $headers -ContentType 'application/json' -Body $body -MaximumRedirection 0
        if (-not $created.success) { throw 'Test DNS creation failed.' }
        $result.DnsCreated = $true
    } elseif (@($records.result).Count -ne 1 -or $records.result[0].type -ne 'A' -or $records.result[0].content -ne '157.173.109.255' -or $records.result[0].proxied) {
        throw 'The test DNS name already belongs to another configuration; nothing was overwritten.'
    }
    $stage = 'saving_encrypted_setup_on_office_server'
    New-Item -ItemType Directory -Path $tlsRoot -Force | Out-Null
    Protect-Path $tlsRoot
    Write-AtomicLanJson $settingsPath ([ordered]@{ CloudflareToken = (Protect-LanSecret $tokenPlain); ContactEmail = 'yaserfaqiri9@gmail.com'; AcceptedAcmeTerms = $true; TestHostName = 'test-shafa.novatechsoft.com' })
    $result.Completed = $true
} catch {
    $result.Error = 'Secure setup did not finish. Check the token permissions or existing test DNS record; no production DNS record was changed.'
    $detail = [string]$_.ErrorDetails.Message
    if ([string]::IsNullOrWhiteSpace($detail)) { $detail = [string]$_.Exception.Message }
    if ($tokenPlain) { $detail = $detail.Replace($tokenPlain, '[hidden]') }
    $result.Stage = $stage
    $result.SafeDetail = $detail
} finally {
    $tokenPlain = $null
    $headers = $null
    if ($ReportPath) { $result | ConvertTo-Json | Set-Content -LiteralPath $ReportPath -Encoding UTF8 }
}
