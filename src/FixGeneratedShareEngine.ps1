$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot 'ShareEngine.cs'
$text = Get-Content -Raw -LiteralPath $path
$start = $text.IndexOf('    private async Task BindUsbNcmDriverAsync()')
$endMarker = '    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)]'
$end = $text.IndexOf($endMarker, $start)
if ($start -lt 0 -or $end -lt 0) { throw 'FixGeneratedShareEngine: generated BindUsbNcmDriverAsync region not found.' }
$region = $text.Substring($start, $end - $start)
# BuildFix4.ps1 currently emits C# quotes with an extra backslash. Normalize
# the generated C# region. The pnputil paths/instance IDs contain no spaces, so
# their command-line quoting is unnecessary after normalization.
$region = $region.Replace('\"', '"')
$region = $region.Replace('$"/enum-devices /instanceid "{target.Id}" /ids /drivers"', '$"/enum-devices /instanceid {target.Id} /ids /drivers"')
$region = $region.Replace('$"/add-driver "{inf}" /install"', '$"/add-driver {inf} /install"')
$text = $text.Substring(0, $start) + $region + $text.Substring($end)
Set-Content -LiteralPath $path -Value $text -Encoding UTF8
