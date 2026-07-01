using ConveyorInspector.ViewModels;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

namespace ConveyorInspector.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;       
    }

    protected override void OnClosed(EventArgs e)
    {
        (DataContext as MainViewModel)?.Dispose();
        base.OnClosed(e);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (LogBox.Items is INotifyCollectionChanged notifyCollection)
        {
            notifyCollection.CollectionChanged += LogBox_CollectionChanged;
        }
    }

    private void LogBox_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (LogBox.Items.Count == 0)
            return;

        LogBox.Dispatcher.BeginInvoke(new Action(() =>
        {
            var lastItem = LogBox.Items[LogBox.Items.Count - 1];
            LogBox.ScrollIntoView(lastItem);
        }), DispatcherPriority.Background);
    }
}
