/**
 * Passbolt ~ Open source password manager for teams
 * Copyright (c) 2023 Passbolt SA (https://www.passbolt.com)
 *
 * Licensed under GNU Affero General Public License version 3 of the or any later version.
 * For full copyright and license information, please see the LICENSE.txt
 * Redistributions of files must retain the above copyright notice.
 *
 * @copyright     Copyright (c) 2023 Passbolt SA (https://www.passbolt.com)
 * @license       https://opensource.org/licenses/AGPL-3.0 AGPL License
 * @link          https://www.passbolt.com Passbolt(tm)
 * @since         0.0.1
 */

using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using System;
using Windows.ApplicationModel.Resources;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using passbolt.Controllers;
using passbolt.Services.WindowsHello;

namespace passbolt
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private WebView2 webviewRendered { get => Rendered; }
        private WebView2 webviewBackground { get => Background; }
        private MainController mainController;

        /// <summary>
        /// Constructor for the main page
        /// </summary>
        public MainPage()
        {
            // Initialize the page
            this.InitializeComponent();
            this.mainController = new MainController(webviewRendered, webviewBackground);
            this.InitHelloOverlay();
        }

        private HelloUiState helloUiState = new HelloUiState();

        /// <summary>
        /// Init the Windows Hello overlay labels and bind it to the coordinator state
        /// </summary>
        private void InitHelloOverlay()
        {
            var loader = ResourceLoader.GetForCurrentView();
            HelloDisableItem.Text = loader.GetString("Hello_MenuDisable");
            HelloOfferItem.Text = loader.GetString("Hello_MenuOffer");
            WindowsHelloCoordinator.Instance.UiStateChanged += this.OnHelloUiStateChanged;
        }

        private void OnHelloUiStateChanged(HelloUiState state)
        {
            var loader = ResourceLoader.GetForCurrentView();
            this.helloUiState = state;
            HelloButton.Visibility = state.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            HelloButton.IsEnabled = !state.IsBusy;
            HelloButtonText.Text = loader.GetString(state.IsEnrolled ? "Hello_ButtonUnlock" : "Hello_ButtonNotEnrolled");
        }

        private async void HelloButton_Click(Windows.UI.Xaml.Controls.SplitButton sender, Windows.UI.Xaml.Controls.SplitButtonClickEventArgs args)
        {
            if (this.helloUiState.IsEnrolled)
            {
                await WindowsHelloCoordinator.Instance.RequestUnlock();
                return;
            }
            var loader = ResourceLoader.GetForCurrentView();
            WindowsHelloCoordinator.Instance.ResetEnrollmentDeclined();
            var dialog = new ContentDialog
            {
                Title = loader.GetString("Hello_NotEnrolledTitle"),
                Content = loader.GetString("Hello_NotEnrolledContent"),
                CloseButtonText = loader.GetString("Hello_Ok"),
            };
            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception)
            {
                //Another dialog is already opened
            }
        }

        private void HelloFlyout_Opening(object sender, object e)
        {
            HelloDisableItem.Visibility = this.helloUiState.IsEnrolled ? Visibility.Visible : Visibility.Collapsed;
            HelloOfferItem.Visibility = this.helloUiState.IsEnrolled ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void HelloDisable_Click(object sender, RoutedEventArgs e)
        {
            await WindowsHelloCoordinator.Instance.Disable();
        }

        private void HelloOffer_Click(object sender, RoutedEventArgs e)
        {
            WindowsHelloCoordinator.Instance.ResetEnrollmentDeclined();
        }

        /// <summary>
        /// This method is called when the rendered web view completes navigation.
        /// </summary>
        private void Rendered_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            this.mainController.RenderedNavigationStarting(sender, args);
        }

        /// <summary>
        /// This method is called when the background web view completes navigation.
        /// </summary>
        private async void Background_NavigationStarting(WebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        {
            await this.mainController.BackgroundNavigationStarting(sender, args);
        }

        /// <summary>
        /// This method is called when the background web view completes navigation.
        /// </summary>

        private async void Background_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            await this.mainController.BackgroundNavigationCompleted(sender, args);
        }

        /// <summary>
        /// This method is called when the rendered web view completes navigation.
        /// </summary>
        private void Rendered_NavigationCompleted(WebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            this.mainController.RenderedNavigationCompleted(sender, args);
        }
    }
}
