using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace sharesTracker
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void Button_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Control button)
            {
                var flyout = new Flyout
                {
                    Content = new TextBlock
                    {
                        Text = "Testing worked",
                        Margin = new Avalonia.Thickness(10)
                    }
                };

                FlyoutBase.SetAttachedFlyout(button, flyout);
                FlyoutBase.ShowAttachedFlyout(button);
            }
        }
    }
}