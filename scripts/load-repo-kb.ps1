param(
    [string]$Collection = "repo_code",
    [string]$QdrantUrl = "http://localhost:6333",
    [string]$OllamaUrl = "http://localhost:11434",
    [string]$Model = "nomic-embed-text"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$includeExtensions = @(
    ".cs", ".csproj", ".sln", ".ts", ".html", ".scss", ".css", ".json",
    ".ps1", ".md", ".sql", ".mjs", ".yml", ".yaml"
)

powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'load-kb.ps1') `
    -Collection $Collection `
    -Folder $root `
    -QdrantUrl $QdrantUrl `
    -OllamaUrl $OllamaUrl `
    -Model $Model `
    -IncludeExtensions $includeExtensions `
    -ExcludeDirectories @("node_modules", "dist", "bin", "obj", ".git", ".angular", "logs", "run", "tmp", ".docker", ".dotnet", "backups")

if ($LASTEXITCODE -ne 0) {
    throw "No se pudo cargar la base de conocimiento del repositorio."
}
