using System;
using FNSyncPlus.Core.Services;

namespace FNSyncPlus.Core.Storage
{
    /// <summary>
    /// NAS 存储提供程序工厂
    /// 遵循飞牛个人空间优先原则：默认首选飞牛官方原生「我的文件」个人空间提供程序，SMB 共享作为高级备用
    /// </summary>
    public static class NasStorageProviderFactory
    {
        /// <summary>
        /// 创建存储提供程序实例。
        /// 默认创建飞牛官方原生个人空间提供程序 (FnOsPersonalStorageProvider)
        /// </summary>
        public static INasStorageProvider CreateProvider(
            StorageProviderType providerType = StorageProviderType.FnOsPersonal,
            string? pinnedCertSha256 = null,
            SmbAuthService? smbService = null,
            string? smbRootUnc = null)
        {
            return providerType switch
            {
                StorageProviderType.FnOsPersonal => new FnOsPersonalStorageProvider(pinnedCertSha256),
                StorageProviderType.SmbShare => new SmbStorageProvider(smbService, smbRootUnc),
                _ => new FnOsPersonalStorageProvider(pinnedCertSha256)
            };
        }
    }
}
