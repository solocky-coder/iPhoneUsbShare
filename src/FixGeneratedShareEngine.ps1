$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -Raw -LiteralPath $path
$start = $text.IndexOf('    private async Task BindUsbNcmDriverAsync()')
$endMarker = '    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]'
$end = $text.IndexOf($endMarker, $start)
if ($start -lt 0 -or $end -lt 0) { throw 'FixGeneratedShareEngine: generated BindUsbNcmDriverAsync region not found.' }
$region = $text.Substring($start, $end - $start)
$sentinel = '__IUS_ESCAPED_QUOTE__'
# BuildFix4.ps1 is a single-quoted PowerShell here-string. Its C# quotes were
# over-escaped, so normalize the generated region while preserving quotes that
# must remain escaped inside C# command-line string literals.
$region = $region.Replace('\"', $sentinel)
$region = $region.Replace('\"', '"')
$region = $region.Replace($sentinel, '\"')
$text = $text.Substring(0, $start) + $region + $text.Substring($end)
Set-Content -LiteralPath $path -Value $text -Encoding UTF8
