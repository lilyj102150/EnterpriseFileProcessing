using EFP.API.Application.Storage;

namespace EFP.API.Infrastructure.Storage;

public sealed class LocalObjectStorage(IConfiguration configuration) : IObjectStorage
{
    private readonly string root = configuration["Storage:LocalRoot"] ?? Path.Combine(AppContext.BaseDirectory, "storage");

    public ValueTask<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken)
    {
        var path = Resolve(objectKey);
        return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public ValueTask<Stream> OpenWriteAsync(string objectKey, CancellationToken cancellationToken)
    {
        var path = Resolve(objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return ValueTask.FromResult<Stream>(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1024 * 64, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public ValueTask<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken) => ValueTask.FromResult(File.Exists(Resolve(objectKey)));

    public ValueTask DeleteAsync(string objectKey, CancellationToken cancellationToken)
    {
        var path = Resolve(objectKey);
        if (File.Exists(path)) File.Delete(path);
        return ValueTask.CompletedTask;
    }

    private string Resolve(string objectKey)
    {
        var normalized = objectKey.Replace('\\', '/').TrimStart('/');
        var path = Path.GetFullPath(Path.Combine(root, normalized));
        var rootPath = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPath, StringComparison.Ordinal)) throw new InvalidOperationException("Storage key is outside the configured storage root.");
        return path;
    }
}