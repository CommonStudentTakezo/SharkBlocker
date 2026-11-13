<#
 Verifica tudo do SharkBlocker (PC) com UM comando, sem GitHub:
   1) testes da extensao e da paridade (Node)   2) cases.json em dia   3) versao da extensao subiu se ela mudou
   4) compilar App e Watchdog (Release)         5) testes .NET (paridade, DNS, estatisticas)
 Uso (na pasta do projeto, onde esta SharkBlocker.sln):   .\verificar-tudo.ps1
 Sem o Node.js instalado:  .\verificar-tudo.ps1 -SemNode   (pula as etapas 1 a 3; instale o Node com:  winget install OpenJS.NodeJS.LTS )
 Para na primeira falha e mostra qual etapa falhou. NAO TESTADO pelo autor (sem Windows/.NET no ambiente de desenvolvimento).
#>
param([switch]$SemNode)
$ErrorActionPreference = "Continue"
Set-Location $PSScriptRoot
$steps = @()
function Step($name, [scriptblock]$cmd) {
    Write-Host ""
    Write-Host "=== $name ===" -ForegroundColor Cyan
    & $cmd
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FALHOU: $name" -ForegroundColor Red
        foreach ($s in $script:steps) { Write-Host "  ok: $s" -ForegroundColor DarkGreen }
        Write-Host "Copie a saida acima e mande na conversa." -ForegroundColor Yellow
        exit 1
    }
    $script:steps += $name
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Write-Host "'dotnet' nao encontrado no PATH." -ForegroundColor Red; exit 1 }
if (-not $SemNode -and -not (Get-Command node -ErrorAction SilentlyContinue)) {
    Write-Host "'node' nao encontrado no PATH." -ForegroundColor Red
    Write-Host "Instale com:  winget install OpenJS.NodeJS.LTS   (depois FECHE e abra o PowerShell)" -ForegroundColor Yellow
    Write-Host "Ou rode sem as etapas do Node:  .\verificar-tudo.ps1 -SemNode" -ForegroundColor Yellow
    exit 1
}
if (-not $SemNode) {
    Step "Testes da extensao"                 { node tests/extension/run-tests.js }
    Step "Paridade (JS contra a intencao)"    { node tests/parity/run-parity.js }
    Step "cases.json em dia com o gerador"    { node tests/parity/generate-cases.js }
    Step "Versao da extensao"                 { node tests/ci/check-extension-version-local.js }
} else {
    Write-Host "Etapas do Node PULADAS (-SemNode): a extensao e a paridade JS nao foram verificadas." -ForegroundColor Yellow
}
Step "Compilar App e Watchdog (Release)"  { dotnet build SharkBlocker.sln -c Release }
Step "Testes .NET"                        { dotnet test tests/SharkBlocker.ParityTests -c Release }
Write-Host ""
Write-Host "TUDO OK ($($steps.Count) etapas)." -ForegroundColor Green
Write-Host "Se mudou o cases.json, lembre de recopiar para o Android: tests\parity\cases.json -> app\src\test\resources\parity\cases.json"
