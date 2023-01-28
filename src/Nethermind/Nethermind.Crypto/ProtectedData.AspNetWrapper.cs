// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Nethermind.Crypto
{
    public partial class ProtectedData
    {
        private class AspNetWrapper : IProtector
        {
            private const string AppName = "Nethermind";
            private const string BaseName = AppName + "_";
            private const string ProtectionDir = "protection_keys";

            public byte[] Protect(byte[] userData, byte[] optionalEntropy, DataProtectionScope scope, string keyStoreDir)
            {
                var protector = GetProtector(scope, optionalEntropy, keyStoreDir);
                return protector.Protect(userData);
            }

            public byte[] Unprotect(byte[] encryptedData, byte[] optionalEntropy, DataProtectionScope scope, string keyStoreDir)
            {
                var protector = GetProtector(scope, optionalEntropy, keyStoreDir);
                return protector.Unprotect(encryptedData);
            }

            private IDataProtector GetProtector(DataProtectionScope scope, byte[] optionalEntropy, string keyStoreDir)
            {
                if (scope == DataProtectionScope.CurrentUser)
                {
                    return GetUserProtector(keyStoreDir, optionalEntropy);
                }
                else
                {
                    return GetMachineProtector(optionalEntropy);
                }
            }

            /**
             * Creates data protector with keys located in Environment.SpecialFolder.ApplicationData
             * if we don't have permission to write to this folder keys will be stored at keyStore/protection_keys
             */
            private IDataProtector GetUserProtector(string keyStoreDir, byte[] optionalEntropy)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var path = Path.Combine(appData, AppName);
                try // Check if we have permission to write to directory SpecialFolder.ApplicationData
                {
                    using (FileStream _ = File.Create(Path.Combine(path, Path.GetRandomFileName()), 1,
                               FileOptions.DeleteOnClose)) { }
                }
                catch // Change location of keys to keyStore/protection_keys directory
                {
                    path = Path.Combine(keyStoreDir, ProtectionDir);
                }
                var info = new DirectoryInfo(path);
                var provider = DataProtectionProvider.Create(info);
                var purpose = CreatePurpose(optionalEntropy);

                return provider.CreateProtector(purpose);
            }

            private IDataProtector GetMachineProtector(byte[] optionalEntropy)
            {
                var provider = DataProtectionProvider.Create(AppName);
                var purpose = CreatePurpose(optionalEntropy);
                return provider.CreateProtector(purpose);
            }

            private string CreatePurpose(byte[] optionalEntropy)
            {
                var result = BaseName + Convert.ToBase64String(optionalEntropy);
                return Uri.EscapeDataString(result);
            }
        }
    }
}
