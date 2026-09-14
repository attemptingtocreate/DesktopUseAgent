using SemanticDesktop.Core.Commands;
using SemanticDesktop.Core.Errors;
using SemanticDesktop.Core.Results;

namespace SemanticDesktop.Files;

public sealed class FileService
{
    public Task<ToolResult<object>> WriteTextAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                return Task.FromResult(ToolResult<object>.Failure(
                    new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "path is required.", Retryable = false },
                    ResultMeta.Create(requestId, started)));
            }

            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(full, contents ?? string.Empty);
            return Task.FromResult(ToolResult<object>.Success(
                new { path = full, bytes = (contents ?? string.Empty).Length },
                ResultMeta.Create(requestId, started),
                new PerformanceMeta
                {
                    Operation = CommandNames.FilesystemWriteText,
                    DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                    ElementsInspected = 1,
                    CacheHit = false,
                    Provider = "Filesystem"
                },
                stateChanged: true));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.Internal, Message = ex.Message, Retryable = false },
                ResultMeta.Create(requestId, started)));
        }
    }

    public bool Exists(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var full = Path.GetFullPath(path);
        return File.Exists(full) || Directory.Exists(full);
    }

    public Task<ToolResult<object>> ExistsAsync(string path, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid().ToString("N");
        cancellationToken.ThrowIfCancellationRequested();
        var full = string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(path);
        var exists = Exists(path);
        return Task.FromResult(ToolResult<object>.Success(
            new { path = full, exists },
            ResultMeta.Create(requestId, started),
            new PerformanceMeta
            {
                Operation = CommandNames.FilesystemExists,
                DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                ElementsInspected = 1,
                CacheHit = false,
                Provider = "Filesystem"
            }));
    }

    public Task<ToolResult<object>> ListAsync(string path, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "." : path);
            if (!Directory.Exists(full))
            {
                return Task.FromResult(ToolResult<object>.Failure(
                    new ErrorInfo { Code = ErrorCodes.NotFound, Message = $"Directory not found: {full}", Retryable = false },
                    ResultMeta.Create(requestId, started)));
            }

            var entries = Directory.EnumerateFileSystemEntries(full)
                .Select(p => new
                {
                    path = p,
                    name = Path.GetFileName(p),
                    isDirectory = Directory.Exists(p)
                })
                .OrderBy(e => e.isDirectory ? 0 : 1)
                .ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .ToList();

            return Task.FromResult(ToolResult<object>.Success(
                new { path = full, entries },
                ResultMeta.Create(requestId, started),
                new PerformanceMeta
                {
                    Operation = CommandNames.FilesystemList,
                    DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                    ElementsInspected = entries.Count,
                    CacheHit = false,
                    Provider = "Filesystem"
                }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.Internal, Message = ex.Message, Retryable = false },
                ResultMeta.Create(requestId, started)));
        }
    }

    public Task<ToolResult<object>> ReadTextAsync(string path, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid().ToString("N");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path))
            {
                return Task.FromResult(ToolResult<object>.Failure(
                    new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "path is required.", Retryable = false },
                    ResultMeta.Create(requestId, started)));
            }

            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
            {
                return Task.FromResult(ToolResult<object>.Failure(
                    new ErrorInfo { Code = ErrorCodes.NotFound, Message = $"File not found: {full}", Retryable = false },
                    ResultMeta.Create(requestId, started)));
            }

            var info = new FileInfo(full);
            if (info.Length > 2_000_000)
            {
                return Task.FromResult(ToolResult<object>.Failure(
                    new ErrorInfo { Code = ErrorCodes.InvalidArgument, Message = "File exceeds 2MB read limit.", Retryable = false },
                    ResultMeta.Create(requestId, started)));
            }

            var contents = File.ReadAllText(full);
            return Task.FromResult(ToolResult<object>.Success(
                new { path = full, contents, bytes = contents.Length },
                ResultMeta.Create(requestId, started),
                new PerformanceMeta
                {
                    Operation = CommandNames.FilesystemReadText,
                    DurationMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                    ElementsInspected = 1,
                    CacheHit = false,
                    Provider = "Filesystem"
                }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult<object>.Failure(
                new ErrorInfo { Code = ErrorCodes.Internal, Message = ex.Message, Retryable = false },
                ResultMeta.Create(requestId, started)));
        }
    }
}
