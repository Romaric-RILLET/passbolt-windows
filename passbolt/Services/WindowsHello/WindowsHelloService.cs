/**
 * Passbolt ~ Open source password manager for teams
 * Copyright (c) Passbolt SA (https://www.passbolt.com)
 *
 * Licensed under GNU Affero General Public License version 3 of the or any later version.
 * For full copyright and license information, please see the LICENSE.txt
 * Redistributions of files must retain the above copyright notice.
 *
 * @copyright     Copyright (c) Passbolt SA (https://www.passbolt.com)
 * @license       https://opensource.org/licenses/AGPL-3.0 AGPL License
 * @link          https://www.passbolt.com Passbolt(tm)
 */

using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using passbolt.Services.CredentialLocker;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage.Streams;

namespace passbolt.Services.WindowsHello
{
    /// <summary>
    /// Result of a Windows Hello unlock attempt
    /// </summary>
    public enum HelloUnlockStatus
    {
        Success,
        Cancelled,
        NotEnrolled,
        Failed
    }

    /// <summary>
    /// Protect the user passphrase with a Windows Hello key.
    ///
    /// The Windows Hello key (TPM backed when available) never leaves the device. It is used to sign a random challenge,
    /// RSA PKCS#1 v1.5 signatures being deterministic the same signature is obtained at each unlock and is used to derive
    /// the AES-256-GCM key protecting the passphrase. Signing the challenge requires the user to pass Windows Hello
    /// (face, fingerprint or PIN).
    /// </summary>
    public class WindowsHelloService
    {
        public const string KEY_NAME = "passbolt-windows-hello";
        public const string VAULT_RESOURCE = "windows-hello-passphrase";
        private const string KDF_CONTEXT = "passbolt-windows-hello-v1";
        private const uint CHALLENGE_LENGTH = 32;
        private const uint NONCE_LENGTH = 12;

        private CredentialLockerService credentialLockerService;

        public WindowsHelloService()
        {
            this.credentialLockerService = new CredentialLockerService();
        }

        /// <summary>
        /// Check if Windows Hello is configured on the device for the current user
        /// </summary>
        public async Task<bool> IsAvailable()
        {
            try
            {
                return await KeyCredentialManager.IsSupportedAsync();
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Check if a passphrase protected by Windows Hello is stored
        /// </summary>
        public async Task<bool> IsEnrolled()
        {
            return await this.credentialLockerService.Get(VAULT_RESOURCE) != null;
        }

        /// <summary>
        /// Create a new Windows Hello key and store the passphrase encrypted with it.
        /// Prompts Windows Hello twice at most (key creation, then challenge signature).
        /// </summary>
        /// <param name="passphrase">The verified user passphrase</param>
        /// <returns>true if the passphrase has been stored</returns>
        public async Task<bool> Enroll(string passphrase)
        {
            var creation = await KeyCredentialManager.RequestCreateAsync(KEY_NAME, KeyCredentialCreationOption.ReplaceExisting);
            if (creation.Status != KeyCredentialStatus.Success)
            {
                return false;
            }

            var challenge = CryptographicBuffer.GenerateRandom(CHALLENGE_LENGTH);
            var key = await this.DeriveKey(creation.Credential, challenge);
            if (key == null)
            {
                return false;
            }

            var nonce = CryptographicBuffer.GenerateRandom(NONCE_LENGTH);
            var plaintext = CryptographicBuffer.ConvertStringToBinary(passphrase, BinaryStringEncoding.Utf8);
            var encrypted = CryptographicEngine.EncryptAndAuthenticate(key, plaintext, nonce, null);

            var data = new HelloProtectedPassphrase
            {
                challenge = CryptographicBuffer.EncodeToBase64String(challenge),
                nonce = CryptographicBuffer.EncodeToBase64String(nonce),
                ciphertext = CryptographicBuffer.EncodeToBase64String(encrypted.EncryptedData),
                tag = CryptographicBuffer.EncodeToBase64String(encrypted.AuthenticationTag),
            };
            await this.credentialLockerService.Create(VAULT_RESOURCE, JsonConvert.SerializeObject(data));
            return true;
        }

        /// <summary>
        /// Prompt Windows Hello and decrypt the stored passphrase
        /// </summary>
        public async Task<(HelloUnlockStatus status, string passphrase)> Unlock()
        {
            var stored = await this.credentialLockerService.Get(VAULT_RESOURCE);
            if (stored == null)
            {
                return (HelloUnlockStatus.NotEnrolled, null);
            }

            var opening = await KeyCredentialManager.OpenAsync(KEY_NAME);
            if (opening.Status == KeyCredentialStatus.NotFound)
            {
                //The Windows Hello key has been removed (eg. Windows Hello reset), the stored data cannot be decrypted anymore
                await this.Disable();
                return (HelloUnlockStatus.NotEnrolled, null);
            }
            if (opening.Status != KeyCredentialStatus.Success)
            {
                return (HelloUnlockStatus.Failed, null);
            }

            try
            {
                stored.RetrievePassword();
                var data = JsonConvert.DeserializeObject<HelloProtectedPassphrase>(stored.Password);
                var challenge = CryptographicBuffer.DecodeFromBase64String(data.challenge);
                var signature = await opening.Credential.RequestSignAsync(challenge);
                if (signature.Status == KeyCredentialStatus.UserCanceled)
                {
                    return (HelloUnlockStatus.Cancelled, null);
                }
                if (signature.Status != KeyCredentialStatus.Success)
                {
                    return (HelloUnlockStatus.Failed, null);
                }

                var key = this.CreateAesKey(signature.Result);
                var decrypted = CryptographicEngine.DecryptAndAuthenticate(
                    key,
                    CryptographicBuffer.DecodeFromBase64String(data.ciphertext),
                    CryptographicBuffer.DecodeFromBase64String(data.nonce),
                    CryptographicBuffer.DecodeFromBase64String(data.tag),
                    null);
                return (HelloUnlockStatus.Success, CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, decrypted));
            }
            catch
            {
                return (HelloUnlockStatus.Failed, null);
            }
        }

        /// <summary>
        /// Remove the stored passphrase and the Windows Hello key
        /// </summary>
        public async Task Disable()
        {
            try
            {
                if (await this.IsEnrolled())
                {
                    await this.credentialLockerService.Remove(VAULT_RESOURCE);
                }
                await KeyCredentialManager.DeleteAsync(KEY_NAME);
            }
            catch
            {
                //The key may not exist anymore
            }
        }

        /// <summary>
        /// Sign the challenge with Windows Hello and derive the AES key from the signature
        /// </summary>
        private async Task<CryptographicKey> DeriveKey(KeyCredential credential, IBuffer challenge)
        {
            var signature = await credential.RequestSignAsync(challenge);
            return signature.Status == KeyCredentialStatus.Success ? this.CreateAesKey(signature.Result) : null;
        }

        /// <summary>
        /// AES-256-GCM key = SHA-256(context || signature)
        /// </summary>
        private CryptographicKey CreateAesKey(IBuffer signature)
        {
            var hash = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256).CreateHash();
            hash.Append(CryptographicBuffer.ConvertStringToBinary(KDF_CONTEXT, BinaryStringEncoding.Utf8));
            hash.Append(signature);
            var keyMaterial = hash.GetValueAndReset();
            return SymmetricKeyAlgorithmProvider.OpenAlgorithm(SymmetricAlgorithmNames.AesGcm).CreateSymmetricKey(keyMaterial);
        }

        private class HelloProtectedPassphrase
        {
            public string challenge { get; set; }
            public string nonce { get; set; }
            public string ciphertext { get; set; }
            public string tag { get; set; }
        }
    }
}
