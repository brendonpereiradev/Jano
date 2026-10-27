# Gera Assets/Branding/jano-icon.ico e jano-icon.png a partir dos emblemas de Themes/Brand.xaml.
# Roda no Windows PowerShell, que já traz o WPF. Use depois de mudar o emblema:
#   .\scripts\New-JanoIcons.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$taskAppDir = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Vistora.Desktop'
$taskBrandingDir = Join-Path $taskAppDir 'Assets\Branding'

$taskStream = [System.IO.File]::OpenRead((Join-Path $taskAppDir 'Themes\Brand.xaml'))
try { $taskBrand = [System.Windows.Markup.XamlReader]::Load($taskStream) }
finally { $taskStream.Dispose() }

function Get-JanoEmblemPng([int]$size) {
    # Cada tamanho usa o desenho feito para ele: menos detalhe quanto menor o ícone.
    $key = if ($size -le 16) { 'JanoEmblemTinyImage' } elseif ($size -le 32) { 'JanoEmblemSmallImage' } else { 'JanoEmblemImage' }
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    $context.DrawImage($taskBrand[$key], (New-Object System.Windows.Rect 0, 0, $size, $size))
    $context.Close()
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = New-Object System.IO.MemoryStream
    $encoder.Save($memory)
    return ,$memory.ToArray()
}

[System.IO.File]::WriteAllBytes((Join-Path $taskBrandingDir 'jano-icon.png'), (Get-JanoEmblemPng 512))

# ICO com quadros PNG, nos tamanhos do PLANO_ICONE.md (aceito desde o Windows Vista).
$taskSizes = 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256
$taskFrames = $taskSizes | ForEach-Object { ,(Get-JanoEmblemPng $_) }

$taskOutput = New-Object System.IO.MemoryStream
$taskWriter = New-Object System.IO.BinaryWriter $taskOutput
$taskWriter.Write([UInt16]0)
$taskWriter.Write([UInt16]1)
$taskWriter.Write([UInt16]$taskSizes.Count)
$taskOffset = 6 + 16 * $taskSizes.Count
for ($i = 0; $i -lt $taskSizes.Count; $i++) {
    $dimension = if ($taskSizes[$i] -ge 256) { 0 } else { $taskSizes[$i] }
    $taskWriter.Write([byte]$dimension)
    $taskWriter.Write([byte]$dimension)
    $taskWriter.Write([byte]0)
    $taskWriter.Write([byte]0)
    $taskWriter.Write([UInt16]1)
    $taskWriter.Write([UInt16]32)
    $taskWriter.Write([UInt32]$taskFrames[$i].Length)
    $taskWriter.Write([UInt32]$taskOffset)
    $taskOffset += $taskFrames[$i].Length
}
foreach ($taskFrame in $taskFrames) { $taskWriter.Write($taskFrame) }
$taskWriter.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $taskBrandingDir 'jano-icon.ico'), $taskOutput.ToArray())

Write-Output "Ícones gerados em $taskBrandingDir"
