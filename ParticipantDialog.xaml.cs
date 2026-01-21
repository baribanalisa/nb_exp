using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
namespace NeuroBureau.Experiment;

public partial class ParticipantDialog : Window
{
    public ParticipantDialogVm Vm { get; } = new();

    public ParticipantDialog()
    {
        InitializeComponent();
        DataContext = Vm;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Vm.Name))
        {
            MessageBox.Show(this, "Нужно заполнить имя.", "Проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }
}

public sealed class ParticipantDialogVm : INotifyPropertyChanged
{
    private string _name = "";
    private string _age = "";
    private string _sex = "";
    private string _comment = "";

    public string Name { get => _name; set { _name = value; OnPropertyChanged(); } }
    public string Age { get => _age; set { _age = value; OnPropertyChanged(); } }
    public string Sex { get => _sex; set { _sex = value; OnPropertyChanged(); } }
    public string Comment { get => _comment; set { _comment = value; OnPropertyChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
