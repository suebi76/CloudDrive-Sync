using System.ComponentModel;
using System.Windows;
using CloudDriveSync.App.ViewModels;

namespace CloudDriveSync.App.Views;

/// <summary>Connecting an account. The password goes from the password box straight to the view model.</summary>
public partial class AddAccountWindow : Window
{
    private readonly AddAccountViewModel _viewModel;

    public AddAccountWindow(AddAccountViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += OnViewModelChanged;
        Loaded += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            FocusFirstField();
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AddAccountViewModel.Step) && _viewModel.Step == AddAccountStep.Details) Dispatcher.InvokeAsync(FocusFirstField);
    }

    private void FocusFirstField()
    {
        if (_viewModel.Step != AddAccountStep.Details) return;
        if (_viewModel.Address.Length == 0) AddressBox.Focus();
        else if (_viewModel.NeedsPassword) PasswordBox.Focus();
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) => _viewModel.Password = PasswordBox.Password;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _viewModel.Cancel();
        base.OnClosing(e);
    }
}
