using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Vistora.Desktop;

// A janela só cuida da navegação e da aparência; o estado e as regras da tela ficam em MainViewModel.
public partial class MainWindow : Window
{
    private bool closing;
    public MainViewModel ViewModel { get; }

    private sealed record PageInfo(string Name, string Title, string Subtitle);
    private static readonly PageInfo[] PageList =
    [
        new("Executar visita", "Executar visita preventiva", "Escolha a unidade e os pavimentos que você visitou."),
        new("Perfis e pavimentos", "Perfis e pavimentos", "Organize os dados de cada solicitante e unidade."),
        new("Histórico", "Histórico de visitas", "Confira os chamados e os resultados de cada visita."),
        new("Configurações", "Configurações", "Defina onde a automação deve abrir os chamados.")
    ];

    public MainWindow(MainViewModel viewModel, int startPage = 0)
    {
        ViewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        VersionLabel.Text = $"Vistora {typeof(App).Assembly.GetName().Version!.ToString(3)}";
        RailItems.ItemsSource = PageList;
        SelectPage(startPage);
        // Colunas de DataGrid não herdam o DataContext, então a coluna de escolha acompanha o ViewModel por aqui.
        viewModel.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.CanChooseFloors)) ShowVisitColumn(); };
        ShowVisitColumn();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.FitStartup(this);
    }

    private void ShowVisitColumn() => VisitColumn.Visibility = ViewModel.CanChooseFloors ? Visibility.Visible : Visibility.Collapsed;
    private void SelectionChanged(object sender, RoutedEventArgs e) => ViewModel.UpdateSelectionSummary();
    private void RailItemLoaded(object sender, RoutedEventArgs e)
    {
        var item = (RadioButton)sender;
        if (ReferenceEquals(item.DataContext, PageList[Pages.SelectedIndex])) item.IsChecked = true;
    }
    private void PageChecked(object sender, RoutedEventArgs e)
    {
        var page = (PageInfo)((RadioButton)sender).DataContext;
        SelectPage(Array.IndexOf(PageList, page));
    }
    internal void SelectPage(int index)
    {
        index = Math.Clamp(index, 0, PageList.Length - 1);
        Pages.SelectedIndex = index;
        var page = PageList[index];
        PageTitle.Text = page.Title; PageSubtitle.Text = page.Subtitle;
        if (RailItems.ItemContainerGenerator.ContainerFromIndex(index) is ContentPresenter container &&
            VisualTreeHelper.GetChildrenCount(container) > 0 && VisualTreeHelper.GetChild(container, 0) is RadioButton item)
            item.IsChecked = true;
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!ViewModel.HasActiveWork || closing) return;
        e.Cancel = true; closing = true;
        await ViewModel.ShutdownAsync();
        Close();
    }
}
