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
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Newtonsoft.Json.Linq;
using passbolt.Models;
using passbolt.Models.Messaging;
using passbolt.Models.Messaging.Topics;
using passbolt.Utils;
using Windows.ApplicationModel.Resources;
using Windows.Storage;
using Windows.UI.Xaml.Controls;

namespace passbolt.Services.WindowsHello
{
    /// <summary>
    /// State of the Windows Hello overlay displayed on top of the authentication screen
    /// </summary>
    public class HelloUiState
    {
        public bool IsVisible { get; set; }
        public bool IsEnrolled { get; set; }
        public bool IsBusy { get; set; }
    }

    /// <summary>
    /// Orchestrate Windows Hello unlock and enrollment with the background webview IPC.
    /// </summary>
    public class WindowsHelloCoordinator
    {
        private const string SETTING_ENROLLMENT_DECLINED = "windowsHelloEnrollmentDeclined";
        private const string INVALID_PASSPHRASE_ERROR = "InvalidMasterPasswordError";

        private static WindowsHelloCoordinator instance = null;
        public static WindowsHelloCoordinator Instance
        {
            get
            {
                if (instance == null)
                {
                    instance = new WindowsHelloCoordinator();
                }
                return instance;
            }
        }

        public event Action<HelloUiState> UiStateChanged;

        private readonly WindowsHelloService helloService = new WindowsHelloService();
        private readonly Dictionary<string, Action<IPC>> pendingRequests = new Dictionary<string, Action<IPC>>();
        private WebView2 background;
        private bool onAuthScreen;
        private bool unlockInProgress;
        private bool helloLoginInFlight;
        private bool storedPassphraseIsStale;
        private bool dialogOpen;

        /// <summary>
        /// Attach the background webview used to send the IPC messages
        /// </summary>
        public void Attach(WebView2 background)
        {
            this.background = background;
        }

        /// <summary>
        /// Background authentication application is listening: try to unlock with Windows Hello.
        /// </summary>
        public async Task OnAuthScreenReady()
        {
            this.onAuthScreen = true;
            //A new authentication screen cancels any previous login (eg. MFA abandoned)
            this.helloLoginInFlight = false;
            this.pendingRequests.Clear();
            await this.PublishUiState();
            if (!this.storedPassphraseIsStale && await this.helloService.IsEnrolled())
            {
                await this.TryUnlock();
            }
        }

        /// <summary>
        /// Unlock request coming from the overlay button
        /// </summary>
        public async Task RequestUnlock()
        {
            this.storedPassphraseIsStale = false;
            await this.TryUnlock();
        }

        /// <summary>
        /// The passphrase has been verified against the user private key and the server, offer to protect it with Windows Hello.
        /// </summary>
        public async Task OnPassphraseVerified(string passphrase)
        {
            this.onAuthScreen = false;
            await this.PublishUiState();

            if (this.helloLoginInFlight || passphrase == null || !await this.helloService.IsAvailable())
            {
                return;
            }

            var enrolled = await this.helloService.IsEnrolled();
            if (enrolled && !this.storedPassphraseIsStale)
            {
                return;
            }
            if (!enrolled && ApplicationData.Current.LocalSettings.Values[SETTING_ENROLLMENT_DECLINED] is bool declined && declined)
            {
                return;
            }

            var loader = ResourceLoader.GetForCurrentView();
            var dialog = new ContentDialog
            {
                Title = loader.GetString(enrolled ? "Hello_UpdateTitle" : "Hello_EnrollTitle"),
                Content = loader.GetString(enrolled ? "Hello_UpdateContent" : "Hello_EnrollContent"),
                PrimaryButtonText = loader.GetString("Hello_EnrollAccept"),
                CloseButtonText = loader.GetString("Hello_EnrollLater"),
                DefaultButton = ContentDialogButton.Primary,
            };
            if (!enrolled)
            {
                dialog.SecondaryButtonText = loader.GetString("Hello_EnrollNever");
            }

            var result = await this.ShowDialog(dialog);
            if (result == ContentDialogResult.Primary)
            {
                if (await this.helloService.Enroll(passphrase))
                {
                    this.storedPassphraseIsStale = false;
                }
                else
                {
                    await this.ShowMessage(loader.GetString("Hello_EnrollFailedTitle"), loader.GetString("Hello_EnrollFailedContent"));
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                ApplicationData.Current.LocalSettings.Values[SETTING_ENROLLMENT_DECLINED] = true;
            }
        }

        /// <summary>
        /// The user reached the workspace
        /// </summary>
        public void OnLoggedIn()
        {
            this.onAuthScreen = false;
            this.helloLoginInFlight = false;
            _ = this.PublishUiState();
        }

        /// <summary>
        /// Intercept the responses of the requests sent by the coordinator to the background webview.
        /// </summary>
        /// <returns>true if the message has been consumed</returns>
        public bool HandleBackgroundResponse(IPC ipc)
        {
            if (ipc?.topic == null || !this.pendingRequests.TryGetValue(ipc.topic, out var callback))
            {
                return false;
            }
            this.pendingRequests.Remove(ipc.topic);
            AllowedTopics.proceedRequestId(ipc.topic);
            callback(ipc);
            return true;
        }

        /// <summary>
        /// Remove the passphrase protected by Windows Hello
        /// </summary>
        public async Task Disable()
        {
            await this.helloService.Disable();
            ApplicationData.Current.LocalSettings.Values[SETTING_ENROLLMENT_DECLINED] = false;
            await this.PublishUiState();
        }

        /// <summary>
        /// Offer the enrollment again at the next passphrase login
        /// </summary>
        public void ResetEnrollmentDeclined()
        {
            ApplicationData.Current.LocalSettings.Values[SETTING_ENROLLMENT_DECLINED] = false;
        }

        private async Task TryUnlock()
        {
            if (this.unlockInProgress || this.helloLoginInFlight || !this.onAuthScreen)
            {
                return;
            }

            this.unlockInProgress = true;
            await this.PublishUiState();
            try
            {
                var (status, passphrase) = await this.helloService.Unlock();
                if (status == HelloUnlockStatus.Success)
                {
                    this.VerifyServerKeyThenLogin(passphrase);
                }
                else if (status == HelloUnlockStatus.Failed)
                {
                    var loader = ResourceLoader.GetForCurrentView();
                    await this.ShowMessage(loader.GetString("Hello_UnlockFailedTitle"), loader.GetString("Hello_UnlockFailedContent"));
                }
            }
            finally
            {
                this.unlockInProgress = false;
                await this.PublishUiState();
            }
        }

        /// <summary>
        /// The server key verification is done by the authentication screen before a passphrase login,
        /// it is replicated here so a Windows Hello login cannot bypass it.
        /// </summary>
        private void VerifyServerKeyThenLogin(string passphrase)
        {
            this.helloLoginInFlight = true;
            this.Send(AuthenticationTopics.VERIFY_SERVER, null, verifyResponse =>
            {
                if (verifyResponse.status != "SUCCESS")
                {
                    //The authentication screen runs the same verification and guides the user through the server key issue
                    this.helloLoginInFlight = false;
                    _ = this.PublishUiState();
                    return;
                }
                this.Send(AuthenticationTopics.DESKTOP_AUTHENTICATE, passphrase, loginResponse =>
                {
                    if (loginResponse.status == "SUCCESS")
                    {
                        return;
                    }
                    this.helloLoginInFlight = false;
                    _ = this.PublishUiState();
                    _ = this.OnHelloLoginError(loginResponse);
                });
            });
        }

        private async Task OnHelloLoginError(IPC response)
        {
            var errorName = (response.message as JObject)?["name"]?.ToString();
            var loader = ResourceLoader.GetForCurrentView();
            if (errorName == INVALID_PASSPHRASE_ERROR)
            {
                //The passphrase has been changed from another device, it will be stored again at the next passphrase login
                this.storedPassphraseIsStale = true;
                await this.ShowMessage(loader.GetString("Hello_StaleTitle"), loader.GetString("Hello_StaleContent"));
            }
            else
            {
                var errorMessage = (response.message as JObject)?["message"]?.ToString();
                await this.ShowMessage(loader.GetString("Hello_LoginFailedTitle"), errorMessage ?? loader.GetString("Hello_LoginFailedContent"));
            }
        }

        /// <summary>
        /// Send a message to the background webview and register the callback of its response
        /// </summary>
        private void Send(string topic, string message, Action<IPC> callback)
        {
            var ipc = new IPC(topic, message);
            //Allow the response topic (the request id) through the IPC guard
            AllowedTopics.AddRequestId(ipc.requestId);
            this.pendingRequests[ipc.requestId] = callback;
            this.background.CoreWebView2.PostWebMessageAsJson(SerializationHelper.SerializeToJson(ipc));
        }

        private async Task PublishUiState()
        {
            var state = new HelloUiState
            {
                IsVisible = this.onAuthScreen && await this.helloService.IsAvailable(),
                IsEnrolled = await this.helloService.IsEnrolled(),
                IsBusy = this.unlockInProgress || this.helloLoginInFlight,
            };
            this.UiStateChanged?.Invoke(state);
        }

        private async Task ShowMessage(string title, string content)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = ResourceLoader.GetForCurrentView().GetString("Hello_Ok"),
            };
            await this.ShowDialog(dialog);
        }

        /// <summary>
        /// Only one ContentDialog can be opened at a time
        /// </summary>
        private async Task<ContentDialogResult> ShowDialog(ContentDialog dialog)
        {
            if (this.dialogOpen)
            {
                return ContentDialogResult.None;
            }
            this.dialogOpen = true;
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                this.dialogOpen = false;
            }
        }
    }
}
