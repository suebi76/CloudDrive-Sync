namespace CloudDriveSync.App.ViewModels;

/// <summary>Something not synchronised on purpose: the filter rule and what it means in words.</summary>
public sealed record IgnoredRule(string Rule, string Text);
