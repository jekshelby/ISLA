using CommunityToolkit.WinUI.Notifications;

namespace ISLA.Notifications;

public sealed class WindowsToastService
{
    public void Show(string title, string body)
    {
        new ToastContentBuilder()
            .AddText(title)
            .AddText(body)
            .Show();
    }
}
