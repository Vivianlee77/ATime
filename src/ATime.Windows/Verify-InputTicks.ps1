$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $PSScriptRoot 'InputTickConverter.cs')
$cases = @(
    @{ Name = 'normal idle'; Tick = [uint32]9000; Now = [long]10000; Expected = [long]9000 },
    @{ Name = 'current input'; Tick = [uint32]10000; Now = [long]10000; Expected = [long]10000 },
    @{ Name = 'API rounding'; Tick = [uint32]10016; Now = [long]10000; Expected = [long]10000 },
    @{ Name = 'rounding bound'; Tick = [uint32]10032; Now = [long]10000; Expected = [long]10000 },
    @{ Name = 'future anomaly'; Tick = [uint32]10033; Now = [long]10000; Expected = $null },
    @{ Name = 'large future anomaly'; Tick = [uint32]15000; Now = [long]10000; Expected = $null },
    @{ Name = '32-bit wrap'; Tick = [uint32]4294967280; Now = [long]4294969296; Expected = [long]4294967280 },
    @{ Name = 'after wrap input'; Tick = [uint32]1500; Now = [long]4294969296; Expected = [long]4294968796 },
    @{ Name = 'before boot invalid'; Tick = [uint32]4294967280; Now = [long]1000; Expected = $null }
)
foreach ($case in $cases) {
    $actual = [long]0
    $success = [ATime.Windows.InputTickConverter]::TryMap($case.Tick, $case.Now, [ref]$actual)
    if ($null -eq $case.Expected) {
        if ($success) { throw "Unexpected accepted timestamp: $($case.Name)" }
    } elseif (!$success -or $actual -ne $case.Expected) {
        throw "Timestamp conversion failed: $($case.Name); actual=$actual; accepted=$success"
    }
}
Write-Output "$($cases.Count) input tick conversion checks passed."
