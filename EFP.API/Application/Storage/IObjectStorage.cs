namespace EFP.API.Application.Storage;

public interface IObjectStorage
{
    ValueTask<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken);
    ValueTask<Stream> OpenWriteAsync(string objectKey, CancellationToken cancellationToken);
    ValueTask<bool> ExistsAsync(string objectKey, CancellationToken cancellationToken);
    ValueTask DeleteAsync(string objectKey, CancellationToken cancellationToken);
}