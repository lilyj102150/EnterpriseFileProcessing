using EFP.API.Domain.Jobs;
using EFP.API.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EFP.API.Application.Storage;

public sealed record ArtifactRecord(Guid Id, string FileName, string ObjectKey, long ContentLength, string ContentType, string State, DateTimeOffset ExpiresAt);

public interface IArtifactStore
{
    ValueTask RegisterAsync(Guid artifactId, string fileName, string objectKey, long contentLength, string contentType, CancellationToken cancellationToken);
    ValueTask<ArtifactRecord?> GetAsync(Guid artifactId, CancellationToken cancellationToken);
    ValueTask<bool> DeleteAsync(Guid artifactId, CancellationToken cancellationToken);
}

public sealed class PostgresArtifactStore(EfpDbContext db) : IArtifactStore
{
    public async ValueTask RegisterAsync(Guid artifactId, string fileName, string objectKey, long contentLength, string contentType, CancellationToken cancellationToken)
    {
        db.StorageObjects.Add(new StorageObjectEntity { Id = artifactId, TenantId = 1, FileName = fileName, ObjectKey = objectKey, Provider = CFileStorageProvider.Local, ContentLength = contentLength, ContentType = contentType, Sha256 = string.Empty, CreatedAt = DateTimeOffset.UtcNow, State = "Completed" });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask<ArtifactRecord?> GetAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var item = await db.StorageObjects.AsNoTracking().SingleOrDefaultAsync(record => record.Id == artifactId, cancellationToken);
        return item is null ? null : new ArtifactRecord(item.Id, item.FileName, item.ObjectKey, item.ContentLength, item.ContentType, item.State, item.ExpiresAt ?? item.CreatedAt);
    }

    public async ValueTask<bool> DeleteAsync(Guid artifactId, CancellationToken cancellationToken)
    {
        var item = await db.StorageObjects.SingleOrDefaultAsync(record => record.Id == artifactId, cancellationToken);
        if (item is null) return false;
        db.StorageObjects.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}