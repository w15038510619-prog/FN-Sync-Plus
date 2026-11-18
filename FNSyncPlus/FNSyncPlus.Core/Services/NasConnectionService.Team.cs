using FNSyncPlus.Core.Models;
using FNSyncPlus.Core.Storage;
using FNSyncPlus.Core.Utils;

namespace FNSyncPlus.Core.Services;

public partial class NasConnectionService
{
    private string? _teamSessionPassword;

    public async Task<TeamSmbStorageProvider> ConnectTeamShareAsync(string path, CancellationToken token = default)
    {
        await _connectionLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var (server, share, root, isRoot) = PathHelper.ParseUncShare(path);
            if (isRoot || string.IsNullOrWhiteSpace(share) || CurrentStorageProvider is not FnOsPersonalStorageProvider fn ||
                !IsConnected || !fn.IsAuthenticated || !fn.MatchesServerAddress(server))
                throw new IOException("请先连接此团队共享所属的 NAS。");
            var saved = SmbAuthService.LoadCredentials(_settings.IsolatedCredentialPath);
            string? password = _teamSessionPassword;
            if (password == null && saved.HasValue && saved.Value.Username == Username && fn.MatchesServerAddress(saved.Value.Server))
                password = saved.Value.Password;
            if (password == null) throw new IOException("请重新登录 NAS 后访问团队文件。");
            var provider = new TeamSmbStorageProvider(root, _smbService);
            var result = await provider.ConnectAndAuthenticateAsync(root, Username, password, ct: token).ConfigureAwait(false);
            if (!result.Success) throw new IOException("无法连接团队共享，请确认 NAS 已开启 SMB，且当前账号拥有访问权限。" + result.ErrorMessage);
            return provider;
        }
        finally { _connectionLock.Release(); }
    }

    public async Task<INasStorageProvider?> GetTaskStorageAsync(SyncTask task, CancellationToken token)
    {
        if (task.ProviderType != StorageProviderType.SmbShare) return task.ProviderType == StorageProviderType.FnOsPersonal ? CurrentStorageProvider : null;
        if (!task.IsExperimentalBackup) return null;
        if (!IsConnected || task.NasUsername != Username || !task.NasAccountUid.HasValue || task.NasAccountUid != CurrentUid ||
            CurrentStorageProvider is not FnOsPersonalStorageProvider fn || string.IsNullOrWhiteSpace(task.NasDeviceId) || !fn.MatchesServerAddress(task.NasDeviceId))
            throw new IOException("当前账号与团队备份任务不一致，请使用创建任务的账号登录。");
        return await ConnectTeamShareAsync(task.DestinationFolder, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetAccessibleTeamSharesAsync(CancellationToken token = default)
    {
        await _connectionLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!IsConnected || CurrentStorageProvider is not FnOsPersonalStorageProvider fn) return Array.Empty<string>();
            string host = Uri.TryCreate(ServerAddress.Contains("://") ? ServerAddress : "https://" + ServerAddress,
                UriKind.Absolute, out var uri) ? uri.Host : ServerAddress;
            var saved = SmbAuthService.LoadCredentials(_settings.IsolatedCredentialPath);
            string? password = _teamSessionPassword ?? (saved.HasValue && saved.Value.Username == Username ? saved.Value.Password : null);
            if (password == null) return Array.Empty<string>();
            var result = await _smbService.ConnectNasAsync(host, Username, password).ConfigureAwait(false);
            if (!result.Success) throw new IOException("团队文件暂不可用，请确认 NAS 已开启 SMB。");
            var shares = await _smbService.EnumerateCategorizedSharesAsync(host, Username).ConfigureAwait(false);
            var accessible = new List<string>();
            foreach (var share in shares.Shares.Where(s => SmbAuthService.IsFnOsTeamShare(s.Name)))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using var entries = Directory.EnumerateFileSystemEntries(share.UncPath).GetEnumerator();
                    entries.MoveNext();
                    accessible.Add(share.UncPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return accessible;
        }
        finally { _connectionLock.Release(); }
    }
}
