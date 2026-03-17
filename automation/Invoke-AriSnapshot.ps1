<#
.SYNOPSIS
    Azure Resource Inventory (ARI) scheduled runbook for Azure Automation.
    Runs ARI against a specific customer tenant, uploads output to Blob Storage,
    and signals the ingestion pipeline via Service Bus.

.DESCRIPTION
    This runbook is invoked by Azure Automation on a per-tenant schedule.
    It uses either Lighthouse delegation or App Registration credentials
    to authenticate to the customer tenant, runs ARI, and stores the output.

    The runbook does NOT process the output — that is handled by the
    SnapshotIngestionWorker Azure Function triggered by the Service Bus message.

.PARAMETER TenantId
    The AIM internal tenant ID (GUID).

.PARAMETER AzureTenantId
    The customer's Entra ID tenant ID.

.PARAMETER OnboardingMethod
    'lighthouse' or 'app_registration'

.PARAMETER SubscriptionIds
    Comma-separated list of subscription IDs to inventory.
    If empty, inventories all accessible subscriptions.

.PARAMETER StorageAccountName
    Name of the Blob Storage account for output.

.PARAMETER ServiceBusNamespace
    Service Bus namespace for signaling ingestion.

.NOTES
    Requires:
    - AzureResourceInventory PowerShell module (Install-Module AzureResourceInventory)
    - Az.Accounts, Az.Storage, Az.ServiceBus modules
    - Managed Identity with Reader on customer tenant (Lighthouse) or App Reg credentials in Key Vault
#>

param (
    [Parameter(Mandatory = $true)]
    [string]$TenantId,

    [Parameter(Mandatory = $true)]
    [string]$AzureTenantId,

    [Parameter(Mandatory = $true)]
    [ValidateSet('lighthouse', 'app_registration')]
    [string]$OnboardingMethod,

    [Parameter(Mandatory = $false)]
    [string]$SubscriptionIds = "",

    [Parameter(Mandatory = $true)]
    [string]$StorageAccountName,

    [Parameter(Mandatory = $true)]
    [string]$ServiceBusNamespace,

    [Parameter(Mandatory = $false)]
    [string]$KeyVaultName = ""
)

$ErrorActionPreference = "Stop"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outputDir = "$env:TEMP\ari-$TenantId-$timestamp"
$containerName = "snapshots-$TenantId"
$blobPrefix = "$timestamp"

Write-Output "======================================"
Write-Output "ARI Snapshot Runbook"
Write-Output "Tenant ID: $TenantId"
Write-Output "Azure Tenant: $AzureTenantId"
Write-Output "Method: $OnboardingMethod"
Write-Output "Timestamp: $timestamp"
Write-Output "======================================"

try {
    # ── Step 1: Authenticate ────────────────────────────────────────────────
    Write-Output "Step 1: Authenticating..."

    if ($OnboardingMethod -eq 'lighthouse') {
        # Use Automation Managed Identity — Lighthouse grants cross-tenant access
        Connect-AzAccount -Identity | Out-Null
        Write-Output "Authenticated via Managed Identity (Lighthouse)"
    }
    else {
        # Retrieve App Registration credentials from Key Vault
        Connect-AzAccount -Identity | Out-Null  # First, connect with MI to access Key Vault
        
        $secret = Get-AzKeyVaultSecret -VaultName $KeyVaultName -Name "tenant-cred-$TenantId" -AsPlainText
        $credentials = $secret | ConvertFrom-Json
        
        $securePassword = ConvertTo-SecureString $credentials.clientSecret -AsPlainText -Force
        $credential = New-Object System.Management.Automation.PSCredential($credentials.clientId, $securePassword)
        
        Connect-AzAccount -ServicePrincipal -Credential $credential -Tenant $AzureTenantId | Out-Null
        Write-Output "Authenticated via App Registration"
    }

    # ── Step 2: Resolve subscriptions ───────────────────────────────────────
    Write-Output "Step 2: Resolving subscriptions..."

    if ($SubscriptionIds) {
        $subs = $SubscriptionIds -split ',' | ForEach-Object { $_.Trim() }
        Write-Output "Using specified subscriptions: $($subs -join ', ')"
    }
    else {
        $subs = Get-AzSubscription -TenantId $AzureTenantId | 
            Where-Object { $_.State -eq 'Enabled' } | 
            Select-Object -ExpandProperty Id
        Write-Output "Discovered $($subs.Count) active subscriptions"
    }

    if ($subs.Count -eq 0) {
        throw "No accessible subscriptions found for tenant $AzureTenantId"
    }

    # ── Step 3: Run ARI ─────────────────────────────────────────────────────
    Write-Output "Step 3: Running Azure Resource Inventory..."

    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

    # ARI parameters
    $ariParams = @{
        TenantID       = $AzureTenantId
        SubscriptionID = $subs
        ReportDir      = $outputDir
        SkipAdvisory   = $false      # Include Advisor data
        SecurityCenter = $true        # Include Defender data
        Online         = $false       # Don't open Excel
        Diagram        = $false       # Skip diagrams for automation
    }

    # Run ARI (this is the core inventory collection)
    Invoke-ARI @ariParams

    # Find the output files
    $ariFiles = Get-ChildItem -Path $outputDir -Recurse -File
    Write-Output "ARI produced $($ariFiles.Count) output files"

    if ($ariFiles.Count -eq 0) {
        throw "ARI produced no output files"
    }

    # ── Step 4: Upload to Blob Storage ──────────────────────────────────────
    Write-Output "Step 4: Uploading to Blob Storage..."

    $storageContext = New-AzStorageContext -StorageAccountName $StorageAccountName -UseConnectedAccount

    # Ensure container exists
    $container = Get-AzStorageContainer -Name $containerName -Context $storageContext -ErrorAction SilentlyContinue
    if (-not $container) {
        New-AzStorageContainer -Name $containerName -Context $storageContext -Permission Off | Out-Null
        Write-Output "Created container: $containerName"
    }

    $uploadedPaths = @()
    foreach ($file in $ariFiles) {
        $blobName = "$blobPrefix/$($file.Name)"
        Set-AzStorageBlobContent `
            -File $file.FullName `
            -Container $containerName `
            -Blob $blobName `
            -Context $storageContext `
            -Force | Out-Null
        
        $uploadedPaths += $blobName
        Write-Output "Uploaded: $blobName"
    }

    $primaryBlobPath = "$containerName/$blobPrefix"

    # ── Step 5: Signal ingestion pipeline ───────────────────────────────────
    Write-Output "Step 5: Sending Service Bus message..."

    $message = @{
        type           = "snapshot-ready"
        tenantId       = $TenantId
        azureTenantId  = $AzureTenantId
        blobPath       = $primaryBlobPath
        timestamp      = (Get-Date).ToUniversalTime().ToString("o")
        resourceFiles  = $uploadedPaths
    } | ConvertTo-Json -Depth 3

    # Send to Service Bus queue
    $sbConnection = Get-AzServiceBusNamespace -ResourceGroupName "rg-aim-platform" -NamespaceName $ServiceBusNamespace
    
    # Using REST API to send Service Bus message (Managed Identity auth)
    $token = (Get-AzAccessToken -ResourceUrl "https://servicebus.azure.net").Token
    $uri = "https://$ServiceBusNamespace.servicebus.windows.net/snapshot-ingestion/messages"
    
    $headers = @{
        "Authorization"  = "Bearer $token"
        "Content-Type"   = "application/json"
        "BrokerProperties" = (@{
            Label = "snapshot-ready"
            ContentType = "application/json"
        } | ConvertTo-Json -Compress)
    }

    Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body $message

    Write-Output "======================================"
    Write-Output "ARI snapshot completed successfully"
    Write-Output "Blob path: $primaryBlobPath"
    Write-Output "Files: $($uploadedPaths.Count)"
    Write-Output "======================================"
}
catch {
    Write-Error "ARI snapshot failed for tenant $TenantId : $_"
    
    # Signal failure to Service Bus
    try {
        $errorMessage = @{
            type      = "snapshot-failed"
            tenantId  = $TenantId
            error     = $_.Exception.Message
            timestamp = (Get-Date).ToUniversalTime().ToString("o")
        } | ConvertTo-Json -Depth 3

        $token = (Get-AzAccessToken -ResourceUrl "https://servicebus.azure.net").Token
        $uri = "https://$ServiceBusNamespace.servicebus.windows.net/snapshot-ingestion/messages"
        $headers = @{
            "Authorization" = "Bearer $token"
            "Content-Type"  = "application/json"
        }
        Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -Body $errorMessage
    }
    catch {
        Write-Warning "Failed to send error notification: $_"
    }

    throw
}
finally {
    # Cleanup temp files
    if (Test-Path $outputDir) {
        Remove-Item -Path $outputDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
