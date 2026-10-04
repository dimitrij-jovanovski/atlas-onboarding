# Walks through every path of the flow against running services (start them with ./scripts/run.ps1).
# The fake providers choose outcomes from the last name: "Match", "Forged", "Flaky".
$ErrorActionPreference = 'Stop'
$api = 'http://localhost:5100'
$office = 'http://localhost:5200'
$jpeg = '/9j/4AAQSkZJRg=='   # tiny JPEG header — enough for the sniffing; the fake IDNow does not look closer
$png = 'iVBORw0KGgoAAA=='

function New-Application([string]$lastName, [string]$country = 'MB', [string]$nationalId = '0403991450016') {
    $body = @{
        firstName = 'Ana'; lastName = $lastName; dateOfBirth = '1991-03-04'; country = $country
        nationalId = $nationalId; email = 'ana@example.com'; phone = '+38970000000'
        documents = @(@{ type = 'PASSPORT'; image = $jpeg }, @{ type = 'SELFIE'; image = $png })
        termsAccepted = $true
    } | ConvertTo-Json -Depth 5
    Invoke-RestMethod -Method Post "$api/applications" -ContentType 'application/json' -Body $body `
        -Headers @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }
}

function Get-Status($app, [int]$wait = 0) {
    Invoke-RestMethod "$api/applications/$($app.applicationId)?waitSeconds=$wait" -Headers @{ 'X-Application-Token' = $app.accessToken }
}

function Show([string]$title, $value) { Write-Host "`n== $title" -ForegroundColor Cyan; $value | ConvertTo-Json -Depth 5 | Write-Host }

$clean = New-Application 'Petrovska'
Show '1. Clean applicant, MB: one call in, one answer out' $clean

$match = New-Application 'Match'
Show '2. Possible sanctions match, MB: referred, not rejected' $match

Show '   MB officer queue (no personal data in the list)' (Invoke-RestMethod "$office/review/queue" -Headers @{ 'X-Staff-Id' = 'officer.mb' })

try {
    Invoke-RestMethod "$office/review/applications/$($match.applicationId)" -Headers @{ 'X-Staff-Id' = 'officer.ma' } | Out-Null
} catch { Write-Host "   MA officer opening an MB application -> HTTP $($_.Exception.Response.StatusCode.value__)" -ForegroundColor Yellow }

$decision = @{ decision = 'APPROVE'; notes = 'False positive: date of birth does not match the listed person' } | ConvertTo-Json
Invoke-RestMethod -Method Post "$office/review/applications/$($match.applicationId)/decision" -ContentType 'application/json' `
    -Body $decision -Headers @{ 'X-Staff-Id' = 'officer.mb' } | Out-Null
Show '   After officer.mb approves, the customer sees' (Get-Status $match)

$md = New-Application 'Petrovska' 'MD' '0403991450'
Show '3. Clean applicant, MD: approved but must sign in branch' $md
$activate = @{ wetSignatureCaptured = $true } | ConvertTo-Json
Invoke-RestMethod -Method Post "$office/branch/applications/$($md.applicationId)/activate" -ContentType 'application/json' `
    -Body $activate -Headers @{ 'X-Staff-Id' = 'branch.md' } | Out-Null
Show '   After branch.md captures the wet signature' (Get-Status $md)

$flaky = New-Application 'Flaky'
Show '4. Each provider returns 503 once: retried with backoff, still answered in the same call' $flaky

$forged = New-Application 'Forged'
Show '5. Document fails verification' $forged

Show '6. Access log for the referred application (as officer.mb)' `
    (Invoke-RestMethod "$office/review/applications/$($match.applicationId)/audit" -Headers @{ 'X-Staff-Id' = 'officer.mb' })
