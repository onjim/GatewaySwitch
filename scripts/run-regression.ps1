$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root "bin\Regression"

dotnet build (Join-Path $root "GatewaySwitch.csproj") -c Release --no-restore `
  -p:DefineConstants=GATEWAY_REGRESSION_TESTS `
  -p:StartupObject=YuanxinGateway.RegressionTests `
  -p:OutputType=Exe -p:ApplicationManifest= `
  -p:OutputPath="$output\"

if ($LASTEXITCODE -ne 0) { throw "Regression build failed (exit code $LASTEXITCODE)." }
& (Join-Path $output "GatewaySwitch.exe")
if ($LASTEXITCODE -ne 0) { throw "Regression tests failed (exit code $LASTEXITCODE)." }
