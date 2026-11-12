[CmdletBinding()]
param(
    [ValidateSet('compare', 'local', 'product', 'nodes', 'stabilize', 'monitors', 'fixture')]
    [string]$Command = 'compare',
    [string]$Match = ''
)

$project = Join-Path $PSScriptRoot 'UiaClientProbe'
dotnet run --project $project -c Release -- $Command $Match
