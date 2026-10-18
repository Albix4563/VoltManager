param(
    [Parameter(Mandatory = $true)]
    [string]$Source,

    [Parameter(Mandatory = $true)]
    [string]$Destination
)

# Crea lo ZIP con nomi entry canonici '/' anche su Windows PowerShell 5.1: lì
# ZipFile.CreateFromDirectory (.NET Framework) scrive '\', che il setup rifiuta.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "Payload source directory not found: $sourceRoot"
}
$destinationPath = [IO.Path]::GetFullPath($Destination)

# CreateNew: come CreateFromDirectory non sovrascrive un file già esistente.
$stream = [IO.File]::Open($destinationPath, [IO.FileMode]::CreateNew)
$completed = $false
try {
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $items = ([IO.DirectoryInfo]$sourceRoot).EnumerateFileSystemInfos('*', [IO.SearchOption]::AllDirectories)
        foreach ($item in $items) {
            $name = $item.FullName.Substring($sourceRoot.Length + 1).Replace('\', '/')
            if ($item -is [IO.FileInfo]) {
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $item.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
            } elseif (-not $item.EnumerateFileSystemInfos().GetEnumerator().MoveNext()) {
                # Le directory vuote restano nel payload come entry 'nome/'.
                [void]$archive.CreateEntry("$name/")
            }
        }
    } finally {
        $archive.Dispose()
    }
    $completed = $true
} finally {
    $stream.Dispose()
    if (-not $completed) { Remove-Item -LiteralPath $destinationPath -Force -ErrorAction SilentlyContinue }
}
