$ErrorActionPreference = "Stop"

$input = [Console]::In.ReadToEnd() | ConvertFrom-Json

if ($input.toolName -ne "bash") {
    exit 0
}

$toolArgs = $input.toolArgs | ConvertFrom-Json
$command = $toolArgs.command

if ($command -match '(^\s*|&&\s*|\|\|\s*|;\s*)git\s+push\b') {
    if ($command -match 'INTERRUPT_USER_FOR_PERMISSION') {
        @{
            permissionDecision = "ask"
            permissionDecisionReason = "git push requires user approval."
        } | ConvertTo-Json -Compress
    } else {
        @{
            permissionDecision = "deny"
            permissionDecisionReason = "git push is blocked by default. Retry with INTERRUPT_USER_FOR_PERMISSION in the command if this push is truly necessary, otherwise skip it."
        } | ConvertTo-Json -Compress
    }
}
