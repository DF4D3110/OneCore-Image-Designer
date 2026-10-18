param(
    [Parameter(Mandatory = $true)][string] $Dump,
    [Parameter(Mandatory = $true)][string] $Truth,
    [Parameter(Mandatory = $true)][string] $Out,
    [string] $Exe = 'D:\GitHub_Projects\OneCore-Image-Designer\Source\Native\Output\x86\ocid_manifest.exe'
)

# 从 cbs-diff 的差集生成覆盖表：
#   truth 有、我们没（或缺）的值      -> set    <path> <name> <type> <hex>（用真机的字节）
#   我们有、truth 没有同 (path,name)  -> remove <path> <name>
$diff = & $Exe cbs-diff $Dump $Truth --limit 1000000 2>&1
$rows = $diff | Where-Object { $_ -match '^\s+[+-]\s' }

$truthIndex = @{}
foreach ($line in [IO.File]::ReadAllLines($Truth)) {
    if (-not $line.StartsWith("V`t")) { continue }
    $f = $line.Split("`t")
    $truthIndex["$($f[1])|$($f[2])"] = $line
}

$result = New-Object System.Collections.Generic.List[string]
$seen = New-Object 'System.Collections.Generic.HashSet[string]'
$missingKeys = New-Object 'System.Collections.Generic.List[string]'
$coveredByValue = New-Object 'System.Collections.Generic.List[string]'
foreach ($row in $rows) {
    $t = $row.Trim()
    if (-not ($t -match '^([+-])\s+(.*)$')) { continue }
    $sign = $Matches[1]
    $body = $Matches[2]
    $f = $body.Split("`t")
    if ($f[0] -eq 'K') {
        # 我们有、truth 整键都没有 -> remove-key（连同子键/值一起删）
        # truth 有、我们没有 -> 先记下，最后看有没有值覆盖（值会隐式建键）
        if ($sign -eq '-' -and $f[1] -ne '') {
            $line = "remove-key`t$($f[1])"
            if ($seen.Add($line)) { $result.Add($line) }
        }
        elseif ($sign -eq '+' -and $f[1] -ne '') {
            $missingKeys.Add($f[1])
        }
        continue
    }
    if ($f[0] -ne 'V') { continue }
    if ($sign -eq '+') {
        $coveredByValue.Add($f[1])
        $line = "set`t$($f[1])`t$($f[2])`t$($f[3])`t$($f[4])"
        if ($seen.Add($line)) { $result.Add($line) }
    }
    else {
        if ($truthIndex.ContainsKey("$($f[1])|$($f[2])")) {
            $tl = $truthIndex["$($f[1])|$($f[2])"].Split("`t")
            $line = "set`t$($tl[1])`t$($tl[2])`t$($tl[3])`t$($tl[4])"
        }
        else {
            $line = "remove`t$($f[1])`t$($f[2])"
        }
        if ($seen.Add($line)) { $result.Add($line) }
    }
}
# truth 有、我们完全没有的**空键**（没有任何值能隐式建出来）→ add-key。
foreach ($k in $missingKeys) {
    $covered = $false
    foreach ($v in $coveredByValue) {
        if ($v -eq $k -or $v.StartsWith($k + '\')) { $covered = $true; break }
    }
    if (-not $covered) {
        $line = "add-key`t$k"
        if ($seen.Add($line)) { $result.Add($line) }
    }
}
[IO.File]::WriteAllLines($Out, $result)
"overrides lines=$($result.Count)"


