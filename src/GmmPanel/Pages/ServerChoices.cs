using GmmPanel.Data;

namespace GmmPanel.Pages;

// The list of servers the header's switcher and the menu show; Link keeps the page when switching
public sealed record ServerChoices(List<ServerEntry> Servers, string? Selected, Func<string, string> Link);
