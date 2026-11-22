using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

public sealed class EncryptedDatabaseProfileStore
{
    private readonly string _path;
    private readonly IDataProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public EncryptedDatabaseProfileStore(string path, IDataProtectionProvider protectionProvider)
    {
        _path = Path.GetFullPath(path);
        _protector = protectionProvider.CreateProtector("OllamaDesk.DatabaseProfiles.v1");
    }

    public async Task<IReadOnlyList<StoredDatabaseProfile>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await LoadUnlockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(StoredDatabaseProfile profile, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await LoadUnlockedAsync(cancellationToken)).ToList();
            if (profiles.Any(existing =>
                    string.Equals(existing.Name, profile.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DatabaseProfileException("A database profile with that name already exists.");
            }

            profiles.Add(profile);
            await SaveUnlockedAsync(profiles, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<StoredDatabaseProfile> GetAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadUnlockedAsync(cancellationToken)).FirstOrDefault(profile =>
                       string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase))
                   ?? throw new ReportDatabaseSelectionException(
                       $"Database '{profileId}' is not available. Choose a database from the list.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(StoredDatabaseProfile profile, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await LoadUnlockedAsync(cancellationToken)).ToList();
            var index = profiles.FindIndex(existing =>
                string.Equals(existing.Id, profile.Id, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new ReportDatabaseSelectionException(
                    $"Database '{profile.Id}' is not available. Choose a database from the list.");
            }

            profiles[index] = profile;
            await SaveUnlockedAsync(profiles, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = (await LoadUnlockedAsync(cancellationToken)).ToList();
            var removed = profiles.RemoveAll(profile =>
                string.Equals(profile.Id, profileId, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                throw new ReportDatabaseSelectionException(
                    $"Database '{profileId}' is not available. Choose a database from the list.");
            }

            await SaveUnlockedAsync(profiles, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<StoredDatabaseProfile>> LoadUnlockedAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        var protectedContents = await File.ReadAllTextAsync(_path, cancellationToken);
        try
        {
            var contents = _protector.Unprotect(protectedContents);
            return JsonSerializer.Deserialize<List<StoredDatabaseProfile>>(contents, JsonOptions)
                ?? throw new InvalidDataException("The saved database profile store is empty or invalid.");
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "Saved database profiles could not be decrypted. The local profile encryption keys may be missing.",
                exception);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The saved database profile store is invalid.", exception);
        }
    }

    private async Task SaveUnlockedAsync(
        IReadOnlyList<StoredDatabaseProfile> profiles,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The database profile store needs a parent directory.");
        Directory.CreateDirectory(directory);
        SetPrivatePermissions(directory, directory: true);

        var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var contents = JsonSerializer.Serialize(profiles, JsonOptions);
            var protectedContents = _protector.Protect(contents);
            await File.WriteAllTextAsync(temporaryPath, protectedContents, cancellationToken);
            SetPrivatePermissions(temporaryPath, directory: false);
            File.Move(temporaryPath, _path, overwrite: true);
            SetPrivatePermissions(_path, directory: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void SetPrivatePermissions(string path, bool directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var permissions = directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        File.SetUnixFileMode(path, permissions);
    }
}
