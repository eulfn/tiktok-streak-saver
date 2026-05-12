using AsyncAwaitBestPractices;
using RandomUserAgent;
using Feener.Services;

namespace Feener;
[Microsoft.Maui.Controls.Internals.Preserve(AllMembers = true)]
public partial class LoginPage : ContentPage
{
    private readonly SessionService _sessionService;
    private bool _isLoggedIn = false;

    public LoginPage()
    {
        InitializeComponent();
        _sessionService = new SessionService();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        LoadTikTok();
    }

    private void LoadTikTok()
    {
        LoadingOverlay.IsVisible = true;
        
#if ANDROID
        // Use a mobile UA for the login flow. TikTok's auth API (especially email/password) 
        // often returns "Internal Server Error" (500) if it detects a desktop UA 
        // running on an Android device (header mismatch).
        var mobileUa = TikTokWebViewHelper.GetDefaultUserAgent();
        var desktopUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
        
        TikTokWebViewHelper.ConfigureWebView(TikTokWebView, mobileUa);
        
        // We save the desktop UA to the session service so the background 
        // StreakService (which requires the desktop site for chat automation) 
        // can use it later.
        _sessionService.SetLoginUserAgent(desktopUa);
#endif

        TikTokWebView.Source = TikTokWebViewHelper.LoginUrl;
    }

    private void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (_isLoggedIn)
        {
            return;
        }
        LoadingOverlay.IsVisible = false;

        // Use direct cookie check instead of URL matching for true confirmation
        bool hasSession = TikTokWebViewHelper.HasValidSessionCookie();

        if (hasSession)
        {
            _isLoggedIn = true;
            Done().SafeFireAndForget();
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        if (TikTokWebView.CanGoBack)
        {
            TikTokWebView.GoBack();
        }
        else
        {
            await Navigation.PopAsync();
        }
    }

    private void OnRefreshClicked(object? sender, EventArgs e)
    {
        LoadingOverlay.IsVisible = true;
        TikTokWebView.Reload();
    }

    private async Task Done()
    {
        // Update session status using helper
        TikTokWebViewHelper.UpdateSessionStatus(_sessionService, _isLoggedIn);
        
        if (_isLoggedIn)
        {
            await DisplayAlert("Logged In", 
                "You're logged in to TikTok! The app will use this session for background messaging.", "OK");
            await Navigation.PopAsync();
        }
        else
        {
            await DisplayAlert("Not Logged In", 
                "Please login to TikTok first before continuing.", "OK");
        }
    }
}



