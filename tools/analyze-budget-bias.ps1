# Analyze budget-truncation bias from blind-search result.json pairs (B vs 8B oracle proxy).
param([string]$Root = ".local/budget-bias")

$ErrorActionPreference = "Continue"
foreach ($dir in (Get-ChildItem -Path $Root -Directory -Filter "q-B*" | Sort-Object Name)) {
    $resultPath = Join-Path $dir.FullName "result.json"
    if (-not (Test-Path $resultPath)) { continue }
    $result = Get-Content $resultPath -Raw | ConvertFrom-Json
    $b = $result.blindPolicy
    $chosen = $b.selectedAction.cardId
    if ([string]::IsNullOrEmpty($chosen)) { $chosen = $b.selectedAction.kind }
    Write-Output ("== {0}  selected={1}" -f $dir.Name, $chosen)
    $groups = @{}
    foreach ($e in $b.evaluations) {
        $cmd = $e.actionKey | ConvertFrom-Json
        $card = $cmd.cardId
        if ([string]::IsNullOrEmpty($card)) { $card = $cmd.kind }
        if ($cmd.targetIndex -ge 0) { $card = $card + "#" + $cmd.targetIndex }
        if (-not $groups.ContainsKey($card)) { $groups[$card] = New-Object System.Collections.ArrayList }
        [void]$groups[$card].Add($e)
    }
    foreach ($card in ($groups.Keys | Sort-Object)) {
        $group = @($groups[$card])
        $meanScore = ($group | Measure-Object -Property score -Average).Average
        $meanLoss = ($group | Measure-Object -Property projectedBattleHpLost -Average).Average
        $wins = @($group | Where-Object { $_.completeVictory }).Count
        $stops = (@($group | Group-Object stopReason) | ForEach-Object { "{0}x{1}" -f $_.Name, $_.Count }) -join ","
        $depths = (@($group | ForEach-Object { $_.observedActions.Count }) -join "/")
        Write-Output ("  {0,-22} meanScore={1,8:F1} meanHpLost={2,6:F2} wins={3} stops=[{4}] actions={5}" -f `
            $card, $meanScore, $meanLoss, $wins, $stops, $depths)
    }
}
