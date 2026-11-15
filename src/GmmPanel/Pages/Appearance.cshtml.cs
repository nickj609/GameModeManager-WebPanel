namespace GmmPanel.Pages;

// Appearance: each person's own look in this browser (accent, opacity, blur, background: appearance.js and panel.js).
// What everyone gets is on Site settings, for panel admins.
public class AppearanceModel : PanelPage
{
    public bool Owner => Viewer?.IsPanelAdmin == true;
    public string SiteTheme => GmmPanel.Data.SiteLook.ThemeOf(HttpContext.RequestServices.GetRequiredService<GmmPanel.Data.PanelStore>().Current.Site.Theme);
    public string? Message { get; private set; }
    public bool Failed { get; private set; }

    public void OnGet()
    {
    }
}
