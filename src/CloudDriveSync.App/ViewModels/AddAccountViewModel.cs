using CloudDriveSync.App.Infrastructure;
using CloudDriveSync.Core;
using CloudDriveSync.Core.Accounts;
using CloudDriveSync.Core.Errors;
using CloudDriveSync.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CloudDriveSync.App.ViewModels;

public enum AddAccountStep
{
    Choose,
    Details,
    Browser,
    Done,
}

/// <summary>
/// Connecting an account (or signing one in again): Nextcloud in the browser or with an app password, IServ and other
/// WebDAV servers with user and password. The sign-in is checked against the server before anything is stored.
/// </summary>
public sealed partial class AddAccountViewModel : ObservableObject, IDisposable
{
    private readonly CloudDriveSyncHost _host;
    private readonly AccountSettings? _relogin;
    private CancellationTokenSource? _cancel;
    private NextcloudPendingLogin? _pending;

    public AddAccountViewModel(CloudDriveSyncHost host, AccountSettings? relogin = null)
    {
        _host = host;
        _relogin = relogin;
        if (relogin is null) return;
        Kind = relogin.Kind;
        Label = relogin.Label;
        Step = AddAccountStep.Details;
    }

    /// <summary>When signing in again: address and user name as they were (the password is asked again).</summary>
    public async Task InitializeAsync()
    {
        if (_relogin is null) return;
        try
        {
            var (url, user) = await _host.Accounts.GetSignInAsync(_relogin.Id);
            if (Address.Length == 0 && url.Length > 0) Address = Kind == WebDavKind.Nextcloud ? WebDavAddresses.NextcloudServerOf(url) : url;
            if (UserName.Length == 0) UserName = user;
        }
        catch (CdException)
        {
            // Then the user types them.
        }
    }

    public bool IsRelogin => _relogin is not null;

    [ObservableProperty] public partial AddAccountStep Step { get; set; }
    [ObservableProperty] public partial WebDavKind Kind { get; set; }
    [ObservableProperty] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial string UserName { get; set; } = "";
    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial bool UseAppPassword { get; set; }
    [ObservableProperty] public partial bool AllowInsecure { get; set; }
    [ObservableProperty] public partial bool ShowInsecureOption { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string BusyText { get; set; } = "";
    [ObservableProperty] public partial string ErrorTitle { get; set; } = "";
    [ObservableProperty] public partial string ErrorText { get; set; } = "";
    [ObservableProperty] public partial string AddressPreview { get; set; } = "";

    /// <summary>Set by the window from its password box; never stored, never shown.</summary>
    public string Password { get; set; } = "";

    public AccountSettings? Result { get; private set; }
    public bool SyncNext { get; private set; }
    public string AccountName => Result?.Label ?? _relogin?.Label ?? "";

    /// <summary>The dialog should close (true: with a result).</summary>
    public event EventHandler<bool>? CloseRequested;

    public string Heading => IsRelogin ? $"„{_relogin!.Label}“ neu anmelden" : Kind switch
    {
        WebDavKind.Nextcloud => "Nextcloud verbinden",
        WebDavKind.IServ => "IServ verbinden",
        _ => "WebDAV-Speicher verbinden",
    };

    public string Hint => Kind switch
    {
        WebDavKind.Nextcloud when UseAppPassword =>
            "Melde dich mit deinem Benutzernamen und einem App-Passwort an. Ein App-Passwort erstellst du in deiner Nextcloud unter Persönliche Einstellungen › Sicherheit.",
        WebDavKind.Nextcloud =>
            "Gib die Adresse ein, unter der du deine Nextcloud im Browser öffnest. Angemeldet wirst du dann im Browser – CloudDrive-Sync sieht dein Passwort nicht.",
        WebDavKind.IServ => "Gib die Adresse deiner Schule und deine IServ-Zugangsdaten ein – dieselben wie bei der Anmeldung im Browser.",
        _ => "Gib die WebDAV-Adresse deines Speichers und deine Zugangsdaten ein. Die Adresse steht in der Hilfe deines Anbieters.",
    };

    public string AddressLabel => Kind switch
    {
        WebDavKind.Nextcloud => "Adresse deiner Nextcloud",
        WebDavKind.IServ => "Adresse deiner Schule",
        _ => "WebDAV-Adresse",
    };

    public string AddressExample => Kind switch
    {
        WebDavKind.Nextcloud => "z. B. cloud.meine-schule.de",
        WebDavKind.IServ => "z. B. meine-schule.de – CloudDrive-Sync nutzt dann webdav.meine-schule.de",
        _ => "z. B. https://speicher.example.org/webdav",
    };

    public string UserLabel => Kind == WebDavKind.IServ ? "IServ-Account" : "Benutzername";
    public string UserExample => Kind == WebDavKind.IServ ? "z. B. max.mustermann" : "";
    public string PasswordLabel => Kind == WebDavKind.Nextcloud ? "App-Passwort" : "Passwort";
    public bool NeedsPassword => Kind != WebDavKind.Nextcloud || UseAppPassword;
    public bool IsNextcloud => Kind == WebDavKind.Nextcloud;
    public string SwitchText => UseAppPassword ? "Stattdessen im Browser anmelden" : "Mit App-Passwort anmelden";
    public bool HasError => ErrorTitle.Length > 0;
    public bool CanGoBack => Step == AddAccountStep.Details && !IsRelogin && !IsBusy;
    public bool CanEdit => !IsBusy;
    public bool ShowLabel => !IsRelogin;
    public string CloseText => Step == AddAccountStep.Done ? "Fertig" : "Abbrechen";

    public string PrimaryText => Step switch
    {
        AddAccountStep.Details when IsNextcloud && !UseAppPassword => "Im Browser anmelden",
        AddAccountStep.Details => "Anmelden",
        AddAccountStep.Done when !IsRelogin => "Ordner synchronisieren …",
        _ => "",
    };

    public string DoneText => IsRelogin
        ? "Die neue Anmeldung funktioniert. Angehaltene Synchronisationen dieses Kontos laufen weiter."
        : $"„{AccountName}“ ist bereit. Als Nächstes wählst du einen Ordner, den CloudDrive-Sync auf diesem PC aktuell hält – in beide Richtungen.";

    partial void OnStepChanged(AddAccountStep value) => NotifyAll();

    partial void OnKindChanged(WebDavKind value) => NotifyAll();

    partial void OnUseAppPasswordChanged(bool value) => NotifyAll();

    partial void OnIsBusyChanged(bool value) => NotifyAll();

    partial void OnErrorTitleChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnAddressChanged(string value)
    {
        try
        {
            var address = WebDavAddresses.Parse(Kind, value, allowInsecure: true);
            AddressPreview = Kind switch
            {
                _ when value.Trim().Length == 0 => "",
                WebDavKind.IServ => $"WebDAV-Adresse: {address.Url}",
                WebDavKind.Nextcloud => $"Server: {address.Server}",
                _ => "",
            };
            if (address.Insecure) AddressPreview = (AddressPreview + "  ·  Achtung: unverschlüsselt (http)").Trim(' ', '·');
        }
        catch (CdException)
        {
            AddressPreview = "";
        }
    }

    private void NotifyAll()
    {
        foreach (var name in new[]
                 {
                     nameof(Heading), nameof(Hint), nameof(AddressLabel), nameof(AddressExample), nameof(UserLabel), nameof(UserExample),
                     nameof(PasswordLabel), nameof(NeedsPassword), nameof(IsNextcloud), nameof(SwitchText), nameof(CanGoBack), nameof(CanEdit),
                     nameof(CloseText), nameof(PrimaryText), nameof(DoneText), nameof(AccountName),
                 })
            OnPropertyChanged(name);
    }

    [RelayCommand]
    private void Choose(WebDavKind kind)
    {
        Kind = kind;
        UseAppPassword = false;
        ClearError();
        Step = AddAccountStep.Details;
        OnAddressChanged(Address);
    }

    [RelayCommand]
    private void Back()
    {
        Cancel();
        ClearError();
        Step = AddAccountStep.Choose;
    }

    [RelayCommand]
    private void ToggleAppPassword()
    {
        UseAppPassword = !UseAppPassword;
        ClearError();
    }

    [RelayCommand]
    private void OpenBrowserAgain()
    {
        if (_pending is not null) Shell.OpenWebPage(_pending.LoginUrl);
    }

    [RelayCommand]
    private void UseAppPasswordInstead()
    {
        Cancel();
        UseAppPassword = true;
        Step = AddAccountStep.Details;
    }

    [RelayCommand]
    private async Task PrimaryAsync()
    {
        if (Step == AddAccountStep.Done)
        {
            SyncNext = true;
            CloseRequested?.Invoke(this, true);
            return;
        }
        if (Step == AddAccountStep.Details && !IsBusy) await SignInAsync();
    }

    /// <summary>Stops a running sign-in (e.g. the wait for the browser).</summary>
    public void Cancel()
    {
        _cancel?.Cancel();
        _pending = null;
    }

    private async Task SignInAsync()
    {
        ClearError();
        if (NeedsPassword && (UserName.Trim().Length == 0 || Password.Length == 0))
        {
            ErrorTitle = "Bitte Benutzername und Passwort eingeben.";
            return;
        }
        _cancel?.Dispose();
        _cancel = new CancellationTokenSource();
        var token = _cancel.Token;
        IsBusy = true;
        try
        {
            WebDavCredential credential;
            if (IsNextcloud && !UseAppPassword)
            {
                var address = WebDavAddresses.Parse(WebDavKind.Nextcloud, Address, AllowInsecure);
                BusyText = "Verbinde mit der Nextcloud …";
                _pending = await _host.Nextcloud.StartBrowserLoginAsync(address.Server, token);
                Shell.OpenWebPage(_pending.LoginUrl);
                Step = AddAccountStep.Browser;
                credential = await _host.Nextcloud.WaitForGrantAsync(_pending, TimeSpan.FromMinutes(15), cancellationToken: token);
                Step = AddAccountStep.Details;
            }
            else if (IsNextcloud)
            {
                var address = WebDavAddresses.Parse(WebDavKind.Nextcloud, Address, AllowInsecure);
                BusyText = "Melde an …";
                credential = await _host.Nextcloud.SignInWithPasswordAsync(address.Server, address.Url.Length > 0 ? address.Url : null, UserName.Trim(), Password, token);
            }
            else
            {
                var address = WebDavAddresses.Parse(Kind, Address, AllowInsecure);
                credential = new WebDavCredential { Url = address.Url, Kind = Kind, User = UserName.Trim(), Password = Password };
            }

            BusyText = "Prüfe die Anmeldung beim Server …";
            if (_relogin is not null) await _host.Accounts.ReloginAsync(_relogin.Id, credential, token);
            else Result = await _host.Accounts.AddWebDavAsync(DefaultLabel(credential), credential, token);
            Step = AddAccountStep.Done;
            NotifyAll();
        }
        catch (OperationCanceledException)
        {
            Step = AddAccountStep.Details;
        }
        catch (CdException e)
        {
            Step = AddAccountStep.Details;
            var entry = ErrorCatalog.Get(e.Code);
            ErrorTitle = entry.Title;
            ErrorText = entry.Fix;
            if (e.Code == "CD-3015") ShowInsecureOption = true;
            // Nextclouds that refuse the browser sign-in for programs still accept an app password.
            if (e.Code == "CD-3014" && IsNextcloud && !UseAppPassword) UseAppPassword = true;
        }
        finally
        {
            _pending = null;
            IsBusy = false;
        }
    }

    /// <summary>The name in CloudDrive-Sync: as typed, else the kind - with the server when the name is taken.</summary>
    private string DefaultLabel(WebDavCredential credential)
    {
        if (Label.Trim().Length > 0) return Label.Trim();
        var host = new Uri(credential.Url).Host;
        if (host.StartsWith("webdav.", StringComparison.OrdinalIgnoreCase)) host = host["webdav.".Length..];
        var name = Kind == WebDavKind.Other ? host : Glyphs.NameOf(Kind);
        return _host.Accounts.Accounts.Any(a => a.Label.Equals(name, StringComparison.CurrentCultureIgnoreCase)) ? $"{name} ({host})" : name;
    }

    private void ClearError()
    {
        ErrorTitle = "";
        ErrorText = "";
    }

    public void Dispose()
    {
        _cancel?.Cancel();
        _cancel?.Dispose();
    }
}
