using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Vistora.Core;

namespace Vistora.Desktop;

public partial class ProfileWindow : Window
{
    public VisitProfile Profile { get; }
    private readonly ObservableCollection<Floor> floors;
    private readonly ObservableCollection<string> attachments;
    // Cópia serializada recebida, para saber se Cancelar ou fechar perderia alterações.
    private readonly string original;
    private bool saved;
    internal Func<bool> ConfirmDiscard { get; set; }
    public ProfileWindow(VisitProfile profile)
    {
        InitializeComponent();
        Profile = Serialization.Copy(profile);
        original = JsonSerializer.Serialize(Profile, Serialization.Options);
        ConfirmDiscard = () => MessageBox.Show(this, "Descartar as alterações deste perfil?", "Vistora",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
        Closing += ConfirmClosing;
        DataContext = Profile;
        floors = new(Profile.Floors);
        FloorsGrid.ItemsSource = floors;
        FloorsGrid.SelectedIndex = floors.Count > 0 ? 0 : -1;
        attachments = new(Profile.Attachments);
        AttachmentsList.ItemsSource = attachments;
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowPlacement.FitStartup(this);
    }

    private void SelectedFloorChanged(object sender, SelectionChangedEventArgs e) => ResolutionBox.DataContext = FloorsGrid.SelectedItem;
    internal void AddFloor(object sender, RoutedEventArgs e)
    {
        var floor = new Floor { Name = $"Pavimento {floors.Count + 1}", Resolution = $"Realizada visita preventiva Pavimento {floors.Count + 1}\n\n" };
        floors.Add(floor); FloorsGrid.SelectedItem = floor; FloorsGrid.ScrollIntoView(floor);
    }
    private void RemoveFloor(object sender, RoutedEventArgs e) { if (FloorsGrid.SelectedItem is Floor floor) floors.Remove(floor); }
    private void Move(int offset)
    {
        FloorsGrid.CommitEdit(DataGridEditingUnit.Cell, true); FloorsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        if (FloorsGrid.SelectedItem is not Floor floor) return;
        var index = floors.IndexOf(floor); var target = index + offset;
        if (target >= 0 && target < floors.Count) { floors.Move(index, target); FloorsGrid.SelectedItem = floor; }
    }
    private void MoveUp(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown(object sender, RoutedEventArgs e) => Move(1);
    private void AddAttachments(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = "Anexos para os chamados" };
        if (dialog.ShowDialog(this) == true)
            foreach (var path in dialog.FileNames.Where(p => !attachments.Contains(p))) attachments.Add(path);
    }
    private void RemoveAttachments(object sender, RoutedEventArgs e)
    {
        foreach (var path in AttachmentsList.SelectedItems.Cast<string>().ToList()) attachments.Remove(path);
    }
    internal void Save(object sender, RoutedEventArgs e)
    {
        FloorsGrid.CommitEdit(DataGridEditingUnit.Cell, true); FloorsGrid.CommitEdit(DataGridEditingUnit.Row, true);
        ClearValidation(this, e);
        if (string.IsNullOrWhiteSpace(Profile.Name))
        {
            ShowInvalid(NameBox, 0, "Preencha o nome do perfil.");
            NameBox.Focus(); return;
        }
        var invalid = floors.FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Name) ||
            floors.Count(other => string.Equals(other.Name.Trim(), f.Name.Trim(), StringComparison.OrdinalIgnoreCase)) > 1);
        if (invalid is not null)
        {
            ShowInvalid(FloorsGrid, 1, "Use um nome diferente e preenchido para cada pavimento.");
            FloorsGrid.SelectedItem = invalid; FloorsGrid.ScrollIntoView(invalid); return;
        }
        Profile.Floors = floors.ToList();
        Profile.Attachments = attachments.ToList();
        Profile.Name = Profile.Name.Trim(); Profile.ReporterEmail = Profile.ReporterEmail.Trim(); Profile.Unit = Profile.Unit.Trim();
        saved = true; DialogResult = true;
    }
    // O campo a corrigir fica marcado em vermelho na aba em que está, com o motivo ao lado dos botões.
    private void ShowInvalid(Control field, int tab, string message)
    {
        EditorTabs.SelectedIndex = tab;
        field.SetResourceReference(BorderBrushProperty, "AppDangerBrush");
        ValidationMessage.Text = message;
    }
    private void ClearValidation(object sender, EventArgs e)
    {
        if (ValidationMessage is null || ValidationMessage.Text.Length == 0) return;
        ValidationMessage.Text = "";
        NameBox.ClearValue(BorderBrushProperty); FloorsGrid.ClearValue(BorderBrushProperty);
    }
    internal bool HasChanges
    {
        get
        {
            FloorsGrid.CommitEdit(DataGridEditingUnit.Cell, true); FloorsGrid.CommitEdit(DataGridEditingUnit.Row, true);
            var current = Serialization.Copy(Profile);
            current.Floors = floors.ToList(); current.Attachments = attachments.ToList();
            return JsonSerializer.Serialize(current, Serialization.Options) != original;
        }
    }
    private void ConfirmClosing(object? sender, CancelEventArgs e)
    {
        if (!saved && HasChanges && !ConfirmDiscard()) e.Cancel = true;
    }
    internal void Cancel(object sender, RoutedEventArgs e) => Close();
}
