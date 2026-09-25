using CgShop.Application.Common;
using Microsoft.Extensions.Options;

namespace CgShop.Infrastructure.Files;

public sealed class FileStorageOptions
{
    public const string Section = "FileStorage";

    /// <summary>Carpeta raíz (fuera de wwwroot: los archivos se sirven solo por endpoint autorizado).</summary>
    public string RootPath { get; set; } = "App_Data/uploads";
}

/// <summary>
/// Almacenamiento en disco aislado por tenant: <c>{root}/{tenantId}/{folder}/{guid}{ext}</c>.
/// El nombre físico nunca proviene del usuario y toda ruta se valida contra la carpeta del tenant.
/// </summary>
public sealed class LocalFileStorage(IOptions<FileStorageOptions> options) : IFileStorage
{
    private readonly string _root = Path.GetFullPath(options.Value.RootPath);

    public async Task<string> SaveAsync(Guid tenantId, string folder, string extension, Stream content,
        CancellationToken ct = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("TenantId requerido.", nameof(tenantId));
        if (folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folder.Contains("..", StringComparison.Ordinal))
            throw new ArgumentException("Carpeta inválida.", nameof(folder));
        if (extension is not { Length: > 1 } || extension[0] != '.' ||
            extension[1..].Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new ArgumentException("Extensión inválida.", nameof(extension));

        var relative = Path.Combine(folder, $"{Guid.CreateVersion7():N}{extension.ToLowerInvariant()}");
        var full = Resolve(tenantId, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var file = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
        await content.CopyToAsync(file, ct);
        return relative.Replace('\\', '/');
    }

    public Task<Stream?> OpenAsync(Guid tenantId, string storagePath, CancellationToken ct = default)
    {
        var full = Resolve(tenantId, storagePath);
        Stream? stream = File.Exists(full)
            ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true)
            : null;
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(Guid tenantId, string storagePath, CancellationToken ct = default)
    {
        var full = Resolve(tenantId, storagePath);
        if (File.Exists(full))
            File.Delete(full);
        return Task.CompletedTask;
    }

    /// <summary>Resuelve la ruta física impidiendo path traversal fuera de la carpeta del tenant.</summary>
    private string Resolve(Guid tenantId, string relative)
    {
        var tenantRoot = Path.Combine(_root, tenantId.ToString("N")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(tenantRoot, relative));
        if (!full.StartsWith(tenantRoot, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Ruta fuera del almacenamiento del tenant.");
        return full;
    }
}
