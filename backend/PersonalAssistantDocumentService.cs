using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using UglyToad.PdfPig;

namespace backend;

public sealed class PersonalAssistantDocumentService(
    IWebHostEnvironment environment,
    IConfiguration configuration)
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".csv", ".tsv", ".html", ".htm", ".xml", ".yaml", ".yml", ".log",
        ".cs", ".vb", ".fs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".sql", ".ps1", ".py", ".java",
        ".go", ".rs", ".php", ".css", ".scss", ".less", ".sh", ".bat", ".cmd", ".ini", ".cfg", ".conf"
    };

    private static readonly HashSet<string> TechnicalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".fs", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".sql", ".ps1", ".py", ".java",
        ".go", ".rs", ".php", ".css", ".scss", ".less", ".sh", ".bat", ".cmd", ".json", ".xml", ".yaml", ".yml"
    };

    private static readonly Regex WhitespaceRegex = new("\\s{2,}", RegexOptions.Compiled);

    public async Task<IReadOnlyList<PersonalAssistantUploadedDocument>> ProcessUploadsAsync(
        IReadOnlyList<IFormFile> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            throw new InvalidOperationException("Debes subir al menos un documento.");
        }

        var maxFileCount = GetMaxFileCount();
        if (files.Count > maxFileCount)
        {
            throw new InvalidOperationException($"Solo se permiten hasta {maxFileCount} documentos por consulta.");
        }

        var documents = new List<PersonalAssistantUploadedDocument>();
        foreach (var file in files)
        {
            var document = await ProcessSingleAsync(file, cancellationToken);
            if (document is not null)
            {
                documents.Add(document);
            }
        }

        if (documents.Count == 0)
        {
            throw new InvalidOperationException("No se pudo extraer texto util de los documentos cargados.");
        }

        return documents;
    }

    private async Task<PersonalAssistantUploadedDocument?> ProcessSingleAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length <= 0)
        {
            return null;
        }

        var originalFileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            throw new InvalidOperationException("Uno de los archivos no tiene nombre valido.");
        }

        var extension = Path.GetExtension(originalFileName);
        var maxFileSizeBytes = GetMaxFileSizeBytes();
        if (file.Length > maxFileSizeBytes)
        {
            throw new InvalidOperationException($"El archivo {originalFileName} supera el limite permitido de {maxFileSizeBytes / (1024 * 1024)} MB.");
        }

        if (!IsSupportedDocument(extension, file.ContentType))
        {
            throw new InvalidOperationException($"El archivo {originalFileName} no es compatible. Usa txt, md, json, csv, html, xml, docx, pdf o archivos de codigo.");
        }

        var (absolutePath, relativePath) = await SaveUploadAsync(file, originalFileName, cancellationToken);
        var extractedText = await ExtractTextAsync(absolutePath, extension, file.ContentType, cancellationToken);
        if (string.IsNullOrWhiteSpace(extractedText))
        {
            throw new InvalidOperationException($"No se pudo extraer texto util de {originalFileName}.");
        }

        var normalizedText = Truncate(NormalizeText(extractedText), GetMaxExtractedCharacters());
        if (string.IsNullOrWhiteSpace(normalizedText))
        {
            throw new InvalidOperationException($"El contenido extraido de {originalFileName} quedo vacio tras la normalizacion.");
        }

        return new PersonalAssistantUploadedDocument(
            originalFileName,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            file.Length,
            normalizedText.Length,
            $"/api/personal-ai/uploads/{BuildDownloadPath(relativePath)}",
            relativePath.Replace('\\', '/'),
            normalizedText,
            TechnicalExtensions.Contains(extension));
    }

    private async Task<(string AbsolutePath, string RelativePath)> SaveUploadAsync(
        IFormFile file,
        string originalFileName,
        CancellationToken cancellationToken)
    {
        var uploadRoot = GetUploadRoot();
        var datePath = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(originalFileName));
        var extension = Path.GetExtension(originalFileName);
        var stampedName = $"{DateTime.UtcNow:HHmmssfff}_{Guid.NewGuid().ToString("N")[..8]}_{safeName}{extension}";
        var directory = Path.Combine(uploadRoot, datePath);
        Directory.CreateDirectory(directory);

        var absolutePath = Path.Combine(directory, stampedName);
        await using var target = File.Create(absolutePath);
        await file.CopyToAsync(target, cancellationToken);

        var relativePath = Path.GetRelativePath(uploadRoot, absolutePath);
        return (absolutePath, relativePath);
    }

    private async Task<string> ExtractTextAsync(
        string absolutePath,
        string extension,
        string? contentType,
        CancellationToken cancellationToken)
    {
        switch (extension.ToLowerInvariant())
        {
            case ".pdf":
                return await Task.Run(() => ExtractPdfText(absolutePath), cancellationToken);
            case ".docx":
                return await Task.Run(() => ExtractDocxText(absolutePath), cancellationToken);
        }

        var payload = await File.ReadAllTextAsync(absolutePath, cancellationToken);
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractHtmlText(payload);
        }

        if ((contentType ?? string.Empty).Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return ExtractHtmlText(payload);
        }

        return payload;
    }

    private static string ExtractPdfText(string absolutePath)
    {
        var builder = new StringBuilder();
        using var document = PdfDocument.Open(absolutePath);
        foreach (var page in document.GetPages())
        {
            builder.AppendLine(page.Text);
        }

        return builder.ToString();
    }

    private static string ExtractDocxText(string absolutePath)
    {
        using var stream = File.OpenRead(absolutePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var namespaces = XNamespace.Get("http://schemas.openxmlformats.org/wordprocessingml/2006/main");
        var builder = new StringBuilder();

        foreach (var entry in archive.Entries.Where(entry =>
                     Regex.IsMatch(entry.FullName, @"^word/(document|header\d+|footer\d+)\.xml$", RegexOptions.IgnoreCase)))
        {
            using var entryStream = entry.Open();
            var xml = XDocument.Load(entryStream);
            foreach (var paragraph in xml.Descendants(namespaces + "p"))
            {
                var text = string.Concat(paragraph.Descendants(namespaces + "t").Select(node => node.Value));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    builder.AppendLine(text);
                }
            }
        }

        return builder.ToString();
    }

    private static string ExtractHtmlText(string html)
    {
        var cleaned = Regex.Replace(html, "<script[\\s\\S]*?</script>", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "<style[\\s\\S]*?</style>", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "<noscript[\\s\\S]*?</noscript>", " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(cleaned);
    }

    private bool IsSupportedDocument(string extension, string? contentType)
        => TextExtensions.Contains(extension)
            || string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(contentType) && contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

    private string GetUploadRoot()
        => Path.GetFullPath(Path.Combine(environment.ContentRootPath, "backups", "personal-ai", "uploads"));

    private int GetMaxFileCount()
        => int.TryParse(configuration["PERSONAL_AI_UPLOAD_MAX_FILES"], out var value) && value > 0
            ? Math.Min(value, 8)
            : 4;

    private long GetMaxFileSizeBytes()
    {
        var maxMegabytes = int.TryParse(configuration["PERSONAL_AI_UPLOAD_MAX_MB"], out var configured) && configured > 0
            ? Math.Min(configured, 50)
            : 20;
        return maxMegabytes * 1024L * 1024L;
    }

    private int GetMaxExtractedCharacters()
        => int.TryParse(configuration["PERSONAL_AI_UPLOAD_MAX_CHARS"], out var value) && value > 0
            ? Math.Min(value, 18000)
            : 12000;

    private static string NormalizeText(string value)
    {
        var normalized = value
            .Replace('\u0000', ' ')
            .Replace('\r', '\n');
        normalized = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        normalized = WhitespaceRegex.Replace(normalized, " ");
        return normalized.Trim();
    }

    private static string SanitizeFileName(string value)
    {
        var cleaned = value;
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(invalidChar, '_');
        }

        cleaned = cleaned.Replace(' ', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? "documento" : cleaned;
    }

    private static string BuildDownloadPath(string relativePath)
        => string.Join("/", relativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString));

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : $"{value[..maxLength]}...";
}

public sealed record PersonalAssistantUploadedDocument(
    string FileName,
    string ContentType,
    long SizeBytes,
    int CharacterCount,
    string DownloadUrl,
    string RelativePath,
    string ExtractedText,
    bool IsTechnical);
