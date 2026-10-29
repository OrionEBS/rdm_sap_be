# Nén thư mục thành zip đúng chuẩn (đường dẫn trong zip dùng '/').
# Compress-Archive và ZipFile.CreateFromDirectory trên Windows PowerShell 5.1 (.NET Framework) ghi '\' → một số công cụ
# giải nén tạo ra tên file chứa '\' thay vì thư mục.
function New-RdmZip([string]$SourceDir, [string]$ZipPath, [switch]$IncludeBaseDirectory) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $source = (Resolve-Path $SourceDir).Path.TrimEnd('\')
    $prefix = if ($IncludeBaseDirectory) { (Split-Path $source -Leaf) + '/' } else { '' }
    $zip = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $source -Recurse -File) {
            $entry = $prefix + $file.FullName.Substring($source.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $zip.Dispose() }
}
